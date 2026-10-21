using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SsmsQueryPoller.Tests
{
    public class SplitBatchesTests
    {
        [Fact]
        public void SingleBatchWithoutSeparator()
        {
            var batches = QueryExecutor.SplitBatches("SELECT 1;\r\nSELECT 2;");
            Assert.Single(batches);
        }

        [Fact]
        public void SplitsOnGoLinesCaseInsensitive()
        {
            var batches = QueryExecutor.SplitBatches("SELECT 1\r\nGO\r\nSELECT 2\r\n  go  \r\nSELECT 3\r\nGo -- trailing comment\r\n");
            Assert.Equal(3, batches.Count);
            Assert.Contains("SELECT 2", batches[1]);
        }

        [Fact]
        public void GoWithCountRepeatsBatch()
        {
            var batches = QueryExecutor.SplitBatches("PRINT 'x'\nGO 3\nSELECT 1");
            Assert.Equal(4, batches.Count);
            Assert.All(new[] { batches[0], batches[1], batches[2] }, b => Assert.Contains("PRINT", b));
        }

        [Fact]
        public void IgnoresEmptyBatchesAndGoInsideIdentifiers()
        {
            var batches = QueryExecutor.SplitBatches("GO\r\n\r\nGO\r\nSELECT 1 AS GOAL\r\nGO\r\n");
            Assert.Single(batches);
            Assert.Contains("GOAL", batches[0]);
        }

        [Fact]
        public void EmptyScriptHasNoBatches()
        {
            Assert.Empty(QueryExecutor.SplitBatches("  \r\n "));
            Assert.Empty(QueryExecutor.SplitBatches(null));
        }
    }

    public class ExecuteAsyncTests
    {
        private const string LocalDb = @"Data Source=(localdb)\MSSQLLocalDB;Integrated Security=true;Initial Catalog=master;Encrypt=false";

        private static SqlConnection Connect() => new SqlConnection(LocalDb);

        [Fact]
        public async Task ReturnsMultipleResultSetsAcrossBatches()
        {
            var result = await QueryExecutor.ExecuteAsync(Connect(), "SELECT 1 AS a, N'x' AS b;\r\nSELECT 2 AS a UNION ALL SELECT 3;\r\nGO\r\nSELECT GETDATE() AS now;", 30, 100, CancellationToken.None);

            Assert.Equal(3, result.ResultSets.Count);
            Assert.Equal(1, result.ResultSets[0].Table.Rows.Count);
            Assert.Equal(2, result.ResultSets[1].Table.Rows.Count);
            Assert.Equal("a", result.ResultSets[0].Table.Columns[0].Caption);
            Assert.Equal(typeof(int), result.ResultSets[0].Table.Columns[0].DataType);
            Assert.Equal(typeof(DateTime), result.ResultSets[2].Table.Columns[0].DataType);
        }

        [Fact]
        public async Task HandlesDuplicateEmptyAndAwkwardColumnNames()
        {
            var result = await QueryExecutor.ExecuteAsync(Connect(), "SELECT 1, 2 AS [x], 3 AS [x], 4 AS [a.b], 5 AS [we]]ird], 6 AS [under_score]", 30, 100, CancellationToken.None);

            var columns = result.ResultSets[0].Table.Columns;
            Assert.Equal(6, columns.Count);
            Assert.Equal("(No column name)", columns[0].Caption);
            Assert.Equal("x", columns[1].Caption);
            Assert.Equal("x", columns[2].Caption);
            Assert.Equal("a.b", columns[3].Caption);
            Assert.Equal("we]ird", columns[4].Caption);
        }

        [Fact]
        public async Task CapsRowsAndFlagsTruncation()
        {
            var result = await QueryExecutor.ExecuteAsync(Connect(), "SELECT TOP (50) object_id FROM sys.all_objects; SELECT 1 AS after_cap;", 30, 10, CancellationToken.None);

            Assert.Equal(2, result.ResultSets.Count);
            Assert.Equal(10, result.ResultSets[0].Table.Rows.Count);
            Assert.True(result.ResultSets[0].Truncated);
            Assert.False(result.ResultSets[1].Truncated);
        }

        [Fact]
        public async Task FormatsNonGridTypesAndNulls()
        {
            var result = await QueryExecutor.ExecuteAsync(
                Connect(),
                "SELECT CAST(NULL AS int) AS n, 0xDEADBEEF AS bin, SYSDATETIMEOFFSET() AS dto, CAST('01:02:03' AS time) AS t, CAST(1.5 AS decimal(5,2)) AS d, CAST(1 AS bit) AS flag, NEWID() AS g, CAST('<a/>' AS xml) AS x",
                30,
                10,
                CancellationToken.None);

            var row = result.ResultSets[0].Table.Rows[0];
            Assert.Equal(DBNull.Value, row[0]);
            Assert.Equal("0xDEADBEEF", row[1]);
            Assert.IsType<string>(row[2]);
            Assert.Equal("01:02:03", row[3]);
            Assert.Equal(1.5m, row[4]);
            Assert.Equal(true, row[5]);
            Assert.IsType<Guid>(row[6]);
            Assert.Equal("<a />", row[7]);
        }

        [Fact]
        public async Task ReportsRecordsAffected()
        {
            var result = await QueryExecutor.ExecuteAsync(
                Connect(),
                "DECLARE @t TABLE (i int); INSERT @t VALUES (1),(2),(3); UPDATE @t SET i = i + 1 WHERE i > 1; SELECT * FROM @t;",
                30,
                10,
                CancellationToken.None);

            Assert.Equal(5, result.RecordsAffected);
            Assert.Single(result.ResultSets);
        }

        [Fact]
        public async Task SurfacesSqlErrors()
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => QueryExecutor.ExecuteAsync(Connect(), "SELECT * FROM dbo.does_not_exist_xyz", 30, 10, CancellationToken.None));
            Assert.Contains("does_not_exist_xyz", ex.Message);
        }

        [Fact]
        public async Task CancellationStopsLongRunningQuery()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            {
                var started = DateTime.UtcNow;
                await Assert.ThrowsAnyAsync<Exception>(() => QueryExecutor.ExecuteAsync(Connect(), "WAITFOR DELAY '00:00:30'; SELECT 1;", 60, 10, cts.Token));
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
            }
        }

        [Fact]
        public async Task CommandTimeoutIsApplied()
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => QueryExecutor.ExecuteAsync(Connect(), "WAITFOR DELAY '00:00:10'; SELECT 1;", 1, 10, CancellationToken.None));
            Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task EmptyQueryThrows()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => QueryExecutor.ExecuteAsync(Connect(), "GO", 30, 10, CancellationToken.None));
        }
    }
}
