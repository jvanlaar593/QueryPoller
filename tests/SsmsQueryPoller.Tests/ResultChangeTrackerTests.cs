using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SsmsQueryPoller.Tests
{
    public class ResultChangeTrackerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void StopsOnlyAfterResultsUnchangedForThreshold()
        {
            var tracker = new ResultChangeTracker(TimeSpan.FromSeconds(10));

            Assert.False(tracker.Observe("A", T0));
            Assert.False(tracker.Observe("A", T0.AddSeconds(5)));
            Assert.Equal(TimeSpan.FromSeconds(5), tracker.UnchangedFor);
            Assert.True(tracker.Observe("A", T0.AddSeconds(10)));
        }

        [Fact]
        public void ChangeRestartsTheUnchangedPeriod()
        {
            var tracker = new ResultChangeTracker(TimeSpan.FromSeconds(10));

            Assert.False(tracker.Observe("A", T0));
            Assert.False(tracker.Observe("A", T0.AddSeconds(8)));
            Assert.False(tracker.Observe("B", T0.AddSeconds(9)));
            Assert.Equal(TimeSpan.Zero, tracker.UnchangedFor);
            Assert.False(tracker.Observe("B", T0.AddSeconds(18)));
            Assert.True(tracker.Observe("B", T0.AddSeconds(19)));
        }

        [Fact]
        public void ReturningToEarlierResultsCountsAsAChange()
        {
            var tracker = new ResultChangeTracker(TimeSpan.FromSeconds(10));

            tracker.Observe("A", T0);
            tracker.Observe("B", T0.AddSeconds(5));
            Assert.False(tracker.Observe("A", T0.AddSeconds(10)));
            Assert.Equal(TimeSpan.Zero, tracker.UnchangedFor);
        }

        [Fact]
        public void UnknownResultsResetAndNeverStop()
        {
            var tracker = new ResultChangeTracker(TimeSpan.FromSeconds(5));

            tracker.Observe("A", T0);
            Assert.False(tracker.Observe(null, T0.AddSeconds(10)));
            Assert.False(tracker.Observe("A", T0.AddSeconds(11)));
            Assert.True(tracker.Observe("A", T0.AddSeconds(16)));
        }

        [Fact]
        public void ThresholdShorterThanIntervalStopsOnFirstRepeat()
        {
            var tracker = new ResultChangeTracker(TimeSpan.FromSeconds(1));

            Assert.False(tracker.Observe("A", T0));
            Assert.True(tracker.Observe("A", T0.AddSeconds(5)));
        }
    }

    public class ResultFingerprintTests
    {
        private const string LocalDb = @"Data Source=(localdb)\MSSQLLocalDB;Integrated Security=true;Initial Catalog=master;Encrypt=false";

        private static Task<PollResult> RunAsync(string sql) =>
            QueryExecutor.ExecuteAsync(new SqlConnection(LocalDb), sql, 30, 1000, CancellationToken.None);

        [Fact]
        public async Task IdenticalResultsHaveTheSameFingerprint()
        {
            const string sql = "SELECT name, object_id FROM sys.objects WHERE object_id < 100 ORDER BY object_id; SELECT 1 AS x;";
            var first = ResultFingerprint.Compute(await RunAsync(sql));
            var second = ResultFingerprint.Compute(await RunAsync(sql));
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task DifferentValuesColumnsOrShapesChangeTheFingerprint()
        {
            var baseline = ResultFingerprint.Compute(await RunAsync("SELECT 1 AS a, 'x' AS b"));

            Assert.NotEqual(baseline, ResultFingerprint.Compute(await RunAsync("SELECT 2 AS a, 'x' AS b")));
            Assert.NotEqual(baseline, ResultFingerprint.Compute(await RunAsync("SELECT 1 AS c, 'x' AS b")));
            Assert.NotEqual(baseline, ResultFingerprint.Compute(await RunAsync("SELECT 1 AS a, 'x' AS b; SELECT 1 AS a, 'x' AS b")));
            Assert.NotEqual(baseline, ResultFingerprint.Compute(await RunAsync("SELECT 1 AS a, 'x' AS b UNION ALL SELECT 1, 'x'")));
        }

        [Fact]
        public async Task NullDiffersFromEmptyAndValueBoundariesMatter()
        {
            var nullValue = ResultFingerprint.Compute(await RunAsync("SELECT CAST(NULL AS varchar(5)) AS a"));
            var emptyValue = ResultFingerprint.Compute(await RunAsync("SELECT CAST('' AS varchar(5)) AS a"));
            Assert.NotEqual(nullValue, emptyValue);

            var split1 = ResultFingerprint.Compute(await RunAsync("SELECT 'ab' AS a, 'c' AS b"));
            var split2 = ResultFingerprint.Compute(await RunAsync("SELECT 'a' AS a, 'bc' AS b"));
            Assert.NotEqual(split1, split2);
        }

        [Fact]
        public async Task TimeVaryingResultsChange()
        {
            var first = ResultFingerprint.Compute(await RunAsync("SELECT SYSDATETIME() AS now"));
            await Task.Delay(20);
            var second = ResultFingerprint.Compute(await RunAsync("SELECT SYSDATETIME() AS now"));
            Assert.NotEqual(first, second);
        }
    }
}
