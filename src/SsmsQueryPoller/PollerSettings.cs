using System;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace SsmsQueryPoller
{
    internal sealed class PollerSettings
    {
        private const string Collection = "SsmsQueryPoller";

        public string Query { get; set; } = string.Empty;

        public int IntervalSeconds { get; set; } = 5;

        public int TimeoutSeconds { get; set; } = 30;

        public int AutoStopUnchangedSeconds { get; set; } = 30;

        public bool StopOnError { get; set; }

        public static PollerSettings Load()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var settings = new PollerSettings();
            try
            {
                var store = GetStore();
                if (store.CollectionExists(Collection))
                {
                    settings.Query = store.GetString(Collection, nameof(Query), settings.Query);
                    settings.IntervalSeconds = store.GetInt32(Collection, nameof(IntervalSeconds), settings.IntervalSeconds);
                    settings.TimeoutSeconds = store.GetInt32(Collection, nameof(TimeoutSeconds), settings.TimeoutSeconds);
                    settings.AutoStopUnchangedSeconds = store.GetInt32(Collection, nameof(AutoStopUnchangedSeconds), settings.AutoStopUnchangedSeconds);
                    settings.StopOnError = store.GetBoolean(Collection, nameof(StopOnError), settings.StopOnError);
                }
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogError("SsmsQueryPoller", "Failed to load settings: " + ex);
            }

            return settings;
        }

        public void Save()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var store = GetStore();
                store.CreateCollection(Collection);
                store.SetString(Collection, nameof(Query), Query ?? string.Empty);
                store.SetInt32(Collection, nameof(IntervalSeconds), IntervalSeconds);
                store.SetInt32(Collection, nameof(TimeoutSeconds), TimeoutSeconds);
                store.SetInt32(Collection, nameof(AutoStopUnchangedSeconds), AutoStopUnchangedSeconds);
                store.SetBoolean(Collection, nameof(StopOnError), StopOnError);
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogError("SsmsQueryPoller", "Failed to save settings: " + ex);
            }
        }

        private static WritableSettingsStore GetStore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
        }
    }
}
