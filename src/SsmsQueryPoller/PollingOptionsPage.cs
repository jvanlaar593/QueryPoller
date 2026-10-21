using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SsmsQueryPoller
{
    /// <summary>Tools → Options → Query Poller → Execute with Polling.</summary>
    [Guid("e3c5b0a4-6c1d-4a51-9a9c-2f0d6a8b7e41")]
    [ComVisible(true)]
    public sealed class PollingOptionsPage : DialogPage
    {
        public const int MaxIntervalSeconds = 24 * 60 * 60;

        private int _intervalSeconds = 5;
        private int _maxRuns;
        private int _stopWhenUnchangedSeconds;

        [Category("Execute with Polling")]
        [DisplayName("Interval (seconds)")]
        [Description("How often the query window's query is re-executed, measured from the start of one run to the start of the next. Runs never overlap: if a run takes longer than the interval, the next one starts as soon as it finishes. Minimum 1.")]
        [DefaultValue(5)]
        public int IntervalSeconds
        {
            get => _intervalSeconds;
            set => _intervalSeconds = Math.Min(Math.Max(value, 1), MaxIntervalSeconds);
        }

        [Category("Execute with Polling")]
        [DisplayName("Stop on error")]
        [Description("Stop polling when a run finishes with an error (the error stays visible in the Messages tab).")]
        [DefaultValue(false)]
        public bool StopOnError { get; set; }

        [Category("Execute with Polling")]
        [DisplayName("Maximum runs")]
        [Description("Stop polling automatically after this many runs. 0 = run until stopped.")]
        [DefaultValue(0)]
        public int MaxRuns
        {
            get => _maxRuns;
            set => _maxRuns = Math.Max(value, 0);
        }

        [Category("Execute with Polling")]
        [DisplayName("Stop when results unchanged for (seconds)")]
        [Description("Stop polling once the grid results have stayed identical for this many seconds, e.g. when waiting for a job or blocking to clear. Requires Results to Grid. 0 = never.")]
        [DefaultValue(0)]
        public int StopWhenUnchangedSeconds
        {
            get => _stopWhenUnchangedSeconds;
            set => _stopWhenUnchangedSeconds = Math.Min(Math.Max(value, 0), MaxIntervalSeconds);
        }
    }
}
