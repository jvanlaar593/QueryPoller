using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsQueryPoller
{
    internal sealed class ResultSet
    {
        public ResultSet(DataTable table, bool truncated)
        {
            Table = table;
            Truncated = truncated;
        }

        public DataTable Table { get; }

        public bool Truncated { get; }

        /// <summary>Column captions joined; used to detect whether the grid layout can be preserved between polls.</summary>
        public string SchemaSignature
        {
            get
            {
                var sb = new StringBuilder();
                foreach (DataColumn c in Table.Columns)
                {
                    sb.Append(c.Caption).Append('\u001f').Append(c.DataType.FullName).Append('\u001e');
                }

                return sb.ToString();
            }
        }
    }

    internal sealed class PollResult
    {
        public PollResult(IReadOnlyList<ResultSet> resultSets, int recordsAffected, TimeSpan duration)
        {
            ResultSets = resultSets;
            RecordsAffected = recordsAffected;
            Duration = duration;
        }

        public IReadOnlyList<ResultSet> ResultSets { get; }

        /// <summary>Total rows affected by INSERT/UPDATE/DELETE statements, or -1 if none ran.</summary>
        public int RecordsAffected { get; }

        public TimeSpan Duration { get; }
    }

    internal static class QueryExecutor
    {
        private const int MaxBinaryBytesShown = 2048;

        private static readonly Regex GoLine = new Regex(@"^\s*GO(?:\s+(?<count>\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Splits a script into batches on SSMS-style "GO" / "GO n" separator lines.</summary>
        public static IReadOnlyList<string> SplitBatches(string script)
        {
            var batches = new List<string>();
            var current = new StringBuilder();

            void Flush(int count)
            {
                var text = current.ToString();
                current.Clear();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                for (var i = 0; i < count; i++)
                {
                    batches.Add(text);
                }
            }

            using (var reader = new StringReader(script ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = GoLine.Match(line);
                    if (match.Success)
                    {
                        var count = 1;
                        if (match.Groups["count"].Success && !int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count))
                        {
                            count = 1;
                        }

                        Flush(Math.Max(count, 1));
                    }
                    else
                    {
                        current.AppendLine(line);
                    }
                }
            }

            Flush(1);
            return batches;
        }

        /// <summary>
        /// Opens <paramref name="connection"/> (and disposes it), runs every batch of <paramref name="script"/>,
        /// and materializes each result set into a <see cref="DataTable"/>, capped at <paramref name="maxRowsPerResultSet"/> rows.
        /// </summary>
        public static async Task<PollResult> ExecuteAsync(DbConnection connection, string script, int commandTimeoutSeconds, int maxRowsPerResultSet, CancellationToken cancellationToken)
        {
            var batches = SplitBatches(script);
            if (batches.Count == 0)
            {
                throw new InvalidOperationException("The query is empty.");
            }

            var stopwatch = Stopwatch.StartNew();
            var resultSets = new List<ResultSet>();
            var recordsAffected = -1;

            using (connection)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                foreach (var batch in batches)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = batch;
                        command.CommandType = CommandType.Text;
                        command.CommandTimeout = Math.Max(commandTimeoutSeconds, 0);

                        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            do
                            {
                                if (reader.FieldCount > 0)
                                {
                                    resultSets.Add(await ReadResultSetAsync(reader, maxRowsPerResultSet, cancellationToken).ConfigureAwait(false));
                                }
                            }
                            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

                            reader.Close();
                            if (reader.RecordsAffected >= 0)
                            {
                                recordsAffected = Math.Max(recordsAffected, 0) + reader.RecordsAffected;
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            return new PollResult(resultSets, recordsAffected, stopwatch.Elapsed);
        }

        private static async Task<ResultSet> ReadResultSetAsync(DbDataReader reader, int maxRows, CancellationToken cancellationToken)
        {
            var fieldCount = reader.FieldCount;
            var table = new DataTable { Locale = CultureInfo.CurrentCulture };
            var passThrough = new bool[fieldCount];

            for (var i = 0; i < fieldCount; i++)
            {
                Type fieldType;
                try
                {
                    fieldType = reader.GetFieldType(i);
                }
                catch (Exception)
                {
                    fieldType = null;
                }

                passThrough[i] = IsGridFriendly(fieldType);
                var name = reader.GetName(i);

                // Internal names avoid WPF binding-path issues and duplicate/empty column names; the caption is what users see.
                table.Columns.Add(new DataColumn("c" + i.ToString(CultureInfo.InvariantCulture), passThrough[i] ? fieldType : typeof(string))
                {
                    Caption = string.IsNullOrEmpty(name) ? "(No column name)" : name,
                });
            }

            var truncated = false;
            var rowCount = 0;
            var values = new object[fieldCount];

            table.BeginLoadData();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rowCount >= maxRows)
                {
                    truncated = true;
                    break;
                }

                for (var i = 0; i < fieldCount; i++)
                {
                    values[i] = ReadValue(reader, i, passThrough[i]);
                }

                table.LoadDataRow(values, true);
                rowCount++;
            }

            table.EndLoadData();
            return new ResultSet(table, truncated);
        }

        private static object ReadValue(DbDataReader reader, int ordinal, bool passThrough)
        {
            object value;
            try
            {
                if (reader.IsDBNull(ordinal))
                {
                    return DBNull.Value;
                }

                value = reader.GetValue(ordinal);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return passThrough ? (object)DBNull.Value : "<" + ex.Message + ">";
            }

            return passThrough ? value : FormatValue(value);
        }

        private static bool IsGridFriendly(Type type)
        {
            if (type == null)
            {
                return false;
            }

            return type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(short)
                || type == typeof(int) || type == typeof(long) || type == typeof(decimal) || type == typeof(double)
                || type == typeof(float) || type == typeof(DateTime) || type == typeof(Guid);
        }

        internal static string FormatValue(object value)
        {
            switch (value)
            {
                case null:
                case DBNull _:
                    return null;
                case byte[] bytes:
                    return ToHex(bytes);
                case DateTimeOffset dto:
                    return dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture);
                case TimeSpan ts:
                    return ts.ToString("c", CultureInfo.InvariantCulture);
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.CurrentCulture);
                default:
                    return value.ToString();
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var shown = Math.Min(bytes.Length, MaxBinaryBytesShown);
            var sb = new StringBuilder(2 + (shown * 2) + 3);
            sb.Append("0x");
            for (var i = 0; i < shown; i++)
            {
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            if (shown < bytes.Length)
            {
                sb.Append("...");
            }

            return sb.ToString();
        }
    }
}
