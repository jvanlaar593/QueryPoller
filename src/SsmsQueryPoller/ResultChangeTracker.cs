using System;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SsmsQueryPoller
{
    /// <summary>Builds a compact hash of query results so consecutive polls can be compared cheaply.</summary>
    internal sealed class ResultFingerprint : IDisposable
    {
        private static readonly byte[] NullMarker = { 0xFF, 0x00, 0xFF };

        private readonly SHA256 _sha = SHA256.Create();
        private readonly byte[] _lengthBuffer = new byte[4];

        public void Add(string value)
        {
            if (value == null)
            {
                _sha.TransformBlock(NullMarker, 0, NullMarker.Length, null, 0);
                return;
            }

            // Length-prefix every value so ("ab","c") and ("a","bc") hash differently.
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteLength(bytes.Length);
            _sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        public void Add(long value) => Add(value.ToString(CultureInfo.InvariantCulture));

        public string Finish()
        {
            _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToBase64String(_sha.Hash);
        }

        public void Dispose() => _sha.Dispose();

        /// <summary>Fingerprint of a tool-window poll: every result set's columns and cell values, plus rows affected.</summary>
        public static string Compute(PollResult result)
        {
            using (var fingerprint = new ResultFingerprint())
            {
                fingerprint.Add(result.RecordsAffected);
                fingerprint.Add(result.ResultSets.Count);
                foreach (var resultSet in result.ResultSets)
                {
                    var table = resultSet.Table;
                    fingerprint.Add(table.Columns.Count);
                    foreach (DataColumn column in table.Columns)
                    {
                        fingerprint.Add(column.Caption);
                    }

                    fingerprint.Add(table.Rows.Count);
                    fingerprint.Add(resultSet.Truncated ? "truncated" : "complete");
                    foreach (DataRow row in table.Rows)
                    {
                        foreach (var value in row.ItemArray)
                        {
                            fingerprint.Add(ToInvariantString(value));
                        }
                    }
                }

                return fingerprint.Finish();
            }
        }

        private static string ToInvariantString(object value)
        {
            switch (value)
            {
                case null:
                case DBNull _:
                    return null;
                case DateTime dateTime:
                    return dateTime.ToString("O", CultureInfo.InvariantCulture);
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString();
            }
        }

        private void WriteLength(int length)
        {
            _lengthBuffer[0] = (byte)length;
            _lengthBuffer[1] = (byte)(length >> 8);
            _lengthBuffer[2] = (byte)(length >> 16);
            _lengthBuffer[3] = (byte)(length >> 24);
            _sha.TransformBlock(_lengthBuffer, 0, 4, null, 0);
        }
    }

    /// <summary>Decides when results have stayed identical long enough to auto-stop polling.</summary>
    internal sealed class ResultChangeTracker
    {
        private string _fingerprint;
        private DateTime _unchangedSince;

        public ResultChangeTracker(TimeSpan threshold)
        {
            Threshold = threshold;
        }

        public TimeSpan Threshold { get; }

        /// <summary>How long the results have been identical, measured between run start times.</summary>
        public TimeSpan UnchangedFor { get; private set; }

        /// <summary>
        /// Records the results of a run that started at <paramref name="runStartedUtc"/>. Returns true once the same results have been
        /// seen for at least <see cref="Threshold"/>. A null fingerprint (results unknown) resets tracking and never stops.
        /// </summary>
        public bool Observe(string fingerprint, DateTime runStartedUtc)
        {
            if (fingerprint == null)
            {
                Reset();
                return false;
            }

            if (!string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                _fingerprint = fingerprint;
                _unchangedSince = runStartedUtc;
                UnchangedFor = TimeSpan.Zero;
                return false;
            }

            UnchangedFor = runStartedUtc - _unchangedSince;
            return UnchangedFor >= Threshold;
        }

        public void Reset()
        {
            _fingerprint = null;
            UnchangedFor = TimeSpan.Zero;
        }
    }
}
