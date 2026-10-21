using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;

namespace SsmsQueryPoller
{
    public partial class QueryPollerControl : UserControl
    {
        private const int MaxRowsPerResultSet = 10000;
        private const int MaxIntervalSeconds = 24 * 60 * 60;

        private static readonly Brush ErrorBrush = CreateFrozenBrush(Color.FromRgb(0xE5, 0x51, 0x4F));
        private static readonly Style RightAlignedCell = CreateRightAlignedCellStyle();

        private readonly QueryPollerToolWindow _owner;
        private readonly DispatcherTimer _countdownTimer;

        private PollConnection _connection;
        private string _connectionMoniker;
        private CancellationTokenSource _pollCts;
        private CancellationTokenSource _delayCts;
        private CancellationTokenSource _runOnceCts;
        private int _executions;
        private int _runCount;
        private int _pollIntervalSeconds;
        private ResultChangeTracker _changeTracker;
        private DateTime? _nextRunAt;
        private IReadOnlyList<ResultSet> _resultSets = Array.Empty<ResultSet>();
        private string _boundSignature;
        private bool _initialized;
        private bool _suppressEditorSelection;
        private bool _suppressResultSetSelection;

        internal QueryPollerControl(QueryPollerToolWindow owner)
        {
            _owner = owner;
            InitializeComponent();

            _countdownTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _countdownTimer.Tick += (s, e) => UpdateCountdown();
        }

        private bool IsPolling => _pollCts != null;

        private bool IsBusy => _executions > 0;

        internal void StopPolling(string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _runOnceCts?.Cancel();
            if (_pollCts == null)
            {
                return;
            }

            // The poll loop owns (and disposes) the token source.
            _pollCts.Cancel();
            _pollCts = null;
            _nextRunAt = null;
            _countdownTimer.Stop();

            UpdateUiState();
            CountdownText.Text = reason ?? "Stopped.";
        }

        internal void Shutdown()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StopPolling(null);
            if (_initialized)
            {
                SaveSettings();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            var settings = PollerSettings.Load();
            QueryBox.Text = settings.Query;
            IntervalBox.Text = settings.IntervalSeconds.ToString(CultureInfo.CurrentCulture);
            TimeoutBox.Text = settings.TimeoutSeconds.ToString(CultureInfo.CurrentCulture);
            AutoStopBox.Text = settings.AutoStopUnchangedSeconds.ToString(CultureInfo.CurrentCulture);
            StopOnErrorCheck.IsChecked = settings.StopOnError;

            RefreshEditors(selectActiveEditor: true);
            UpdateUiState();
        }

        #region Query window / connection selection

        private void RefreshEditors(bool selectActiveEditor)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            IReadOnlyList<QueryEditorItem> items;
            string activeMoniker;
            try
            {
                items = SsmsQueryEditors.ListConnectedEditors(out activeMoniker);
            }
            catch (Exception ex)
            {
                SetStatus("Could not list query windows: " + ex.Message, isError: true);
                return;
            }

            var preferred = selectActiveEditor && _connection == null ? activeMoniker : _connectionMoniker;
            var toSelect = items.FirstOrDefault(i => i.Moniker == preferred);

            _suppressEditorSelection = true;
            try
            {
                EditorCombo.ItemsSource = items;
                EditorCombo.SelectedItem = toSelect;
            }
            finally
            {
                _suppressEditorSelection = false;
            }

            if (items.Count == 0 && _connection == null)
            {
                SetStatus("No connected query windows. Open a query window (Ctrl+N), connect it, then pick it above.", isError: false);
            }
            else if (toSelect != null && _connection == null)
            {
                LoadConnection(toSelect);
            }
        }

        private void OnEditorDropDownOpened(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshEditors(selectActiveEditor: false);
        }

        private void OnEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_suppressEditorSelection && EditorCombo.SelectedItem is QueryEditorItem item)
            {
                LoadConnection(item);
            }
        }

        private void LoadConnection(QueryEditorItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            PollConnection connection;
            string error;
            try
            {
                connection = SsmsQueryEditors.GetConnection(item.Moniker, out error);
            }
            catch (Exception ex)
            {
                connection = null;
                error = ex.Message;
            }

            if (connection == null)
            {
                SetStatus($"Cannot use '{item.Caption}': {error}", isError: true);
                return;
            }

            _connection = connection;
            _connectionMoniker = item.Moniker;
            ServerText.Text = connection.UserName == null ? connection.ServerName : $"{connection.ServerName} ({connection.UserName})";
            DatabaseBox.Text = connection.InitialDatabase ?? string.Empty;
            UpdateCaption();
            SetStatus($"Using the connection of '{item.Caption}'.", isError: false);
        }

        private void OnCopyQueryClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(EditorCombo.SelectedItem is QueryEditorItem item))
            {
                SetStatus("Pick a query window first.", isError: true);
                return;
            }

            string text;
            try
            {
                text = SsmsQueryEditors.GetQueryText(item.Moniker);
            }
            catch (Exception ex)
            {
                SetStatus("Could not read the query window text: " + ex.Message, isError: true);
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus($"'{item.Caption}' has no query text.", isError: true);
                return;
            }

            QueryBox.Text = text.Trim('\r', '\n');
            SetStatus($"Copied query from '{item.Caption}'.", isError: false);
        }

        #endregion

        #region Polling

        private void OnStartStopClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsPolling)
            {
                StopPolling("Stopped.");
            }
            else if (_runOnceCts != null)
            {
                _runOnceCts.Cancel();
            }
            else
            {
                StartPolling();
            }
        }

        private void OnRunNowClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsPolling)
            {
                // Skip the remaining wait; the loop runs the query immediately.
                _delayCts?.Cancel();
                return;
            }

            if (IsBusy || !TryBuildRequest(out var request))
            {
                return;
            }

            SaveSettings();
            var cts = new CancellationTokenSource();
            _runOnceCts = cts;
            UpdateUiState();

            _owner.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ExecuteOnceAsync(request, cts.Token);
                }
                finally
                {
                    if (_runOnceCts == cts)
                    {
                        _runOnceCts = null;
                    }

                    cts.Dispose();
                    UpdateUiState();
                }
            }).FileAndForget("SsmsQueryPoller/RunOnce");
        }

        private void StartPolling()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsBusy || !TryBuildRequest(out var request))
            {
                return;
            }

            SaveSettings();
            var cts = new CancellationTokenSource();
            _pollCts = cts;
            _runCount = 0;
            _pollIntervalSeconds = request.IntervalSeconds;
            _changeTracker = request.AutoStopSeconds > 0 ? new ResultChangeTracker(TimeSpan.FromSeconds(request.AutoStopSeconds)) : null;
            CountdownText.Text = string.Empty;
            _countdownTimer.Start();
            UpdateUiState();

            _owner.JoinableTaskFactory.RunAsync(() => PollLoopAsync(request, cts)).FileAndForget("SsmsQueryPoller/PollLoop");
        }

        private async Task PollLoopAsync(PollRequest request, CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                while (!token.IsCancellationRequested)
                {
                    var startedAt = DateTime.UtcNow;
                    var (succeeded, result) = await ExecuteOnceAsync(request, token);
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!succeeded && request.StopOnError)
                    {
                        StopPolling("Stopped after error.");
                        break;
                    }

                    if (_changeTracker != null)
                    {
                        // Failed runs have no results to compare, so they restart the unchanged period.
                        if (_changeTracker.Observe(succeeded ? ResultFingerprint.Compute(result) : null, startedAt))
                        {
                            StopPolling($"Stopped: results unchanged for {FormatSeconds(_changeTracker.UnchangedFor)}.");
                            break;
                        }
                    }

                    // Interval is measured start-to-start; a slow query just shortens the wait (polls never overlap).
                    var wait = TimeSpan.FromSeconds(request.IntervalSeconds) - (DateTime.UtcNow - startedAt);
                    if (wait < TimeSpan.Zero)
                    {
                        wait = TimeSpan.Zero;
                    }

                    using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        _delayCts = delayCts;
                        _nextRunAt = DateTime.UtcNow + wait;
                        UpdateCountdown();
                        try
                        {
                            await Task.Delay(wait, delayCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        finally
                        {
                            if (_delayCts == delayCts)
                            {
                                _delayCts = null;
                                _nextRunAt = null;
                            }
                        }
                    }
                }
            }
            finally
            {
                cts.Dispose();
            }
        }

        /// <returns>Succeeded = false if the query failed (cancellation counts as success); Result is null unless the query completed.</returns>
        private async Task<(bool Succeeded, PollResult Result)> ExecuteOnceAsync(PollRequest request, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _executions++;
            UpdateUiState();
            UpdateCountdown();
            try
            {
                // Created on the UI thread so any Entra ID sign-in prompt has a proper owner.
                var connection = request.Connection.CreateConnection(request.Database);
                var result = await QueryExecutor.ExecuteAsync(connection, request.Sql, request.TimeoutSeconds, MaxRowsPerResultSet, token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (token.IsCancellationRequested)
                {
                    return (true, null);
                }

                _runCount++;
                ShowResults(result);
                return (true, result);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatus("Query cancelled.", isError: false);
                return (true, null);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _runCount++;
                SetStatus($"Run #{_runCount} failed at {DateTime.Now:T}: {ex.Message}", isError: true);
                return (false, null);
            }
            finally
            {
                _executions--;
                UpdateUiState();
                UpdateCountdown();
            }
        }

        private bool TryBuildRequest(out PollRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            request = null;

            if (_connection == null)
            {
                SetStatus("Pick a connected query window first.", isError: true);
                return false;
            }

            if (string.IsNullOrWhiteSpace(QueryBox.Text))
            {
                SetStatus("Enter a query to poll (or use 'Copy query from window').", isError: true);
                return false;
            }

            if (!int.TryParse(IntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var interval) || interval < 1 || interval > MaxIntervalSeconds)
            {
                SetStatus($"Interval must be a whole number of seconds between 1 and {MaxIntervalSeconds}.", isError: true);
                return false;
            }

            if (!int.TryParse(TimeoutBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var timeout) || timeout < 0)
            {
                SetStatus("Timeout must be a whole number of seconds (0 = no timeout).", isError: true);
                return false;
            }

            if (!TryParseAutoStop(out var autoStop))
            {
                SetStatus($"Auto-stop must be a whole number of seconds between 0 and {MaxIntervalSeconds} (0 = never).", isError: true);
                return false;
            }

            request = new PollRequest(_connection, DatabaseBox.Text.Trim(), QueryBox.Text, interval, timeout, StopOnErrorCheck.IsChecked == true, autoStop);
            return true;
        }

        private bool TryParseAutoStop(out int seconds)
        {
            var text = AutoStopBox.Text.Trim();
            if (text.Length == 0)
            {
                seconds = 0;
                return true;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out seconds) && seconds >= 0 && seconds <= MaxIntervalSeconds;
        }

        private void SaveSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var settings = new PollerSettings
            {
                Query = QueryBox.Text,
                StopOnError = StopOnErrorCheck.IsChecked == true,
            };

            if (int.TryParse(IntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var interval) && interval >= 1)
            {
                settings.IntervalSeconds = interval;
            }

            if (int.TryParse(TimeoutBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var timeout) && timeout >= 0)
            {
                settings.TimeoutSeconds = timeout;
            }

            if (TryParseAutoStop(out var autoStop))
            {
                settings.AutoStopUnchangedSeconds = autoStop;
            }

            settings.Save();
        }

        #endregion

        #region Results

        private void ShowResults(PollResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var previousIndex = Math.Max(ResultSetCombo.SelectedIndex, 0);
            _resultSets = result.ResultSets;

            _suppressResultSetSelection = true;
            try
            {
                ResultSetCombo.ItemsSource = _resultSets
                    .Select((r, i) => $"Result {i + 1} ({FormatRowCount(r)})")
                    .ToList();
                ResultSetCombo.SelectedIndex = _resultSets.Count == 0 ? -1 : Math.Min(previousIndex, _resultSets.Count - 1);
            }
            finally
            {
                _suppressResultSetSelection = false;
            }

            ResultSetPanel.Visibility = _resultSets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            BindResultSet(ResultSetCombo.SelectedIndex);

            var parts = new List<string>
            {
                $"Run #{_runCount} at {DateTime.Now:T}",
                $"{result.Duration.TotalMilliseconds:N0} ms",
            };

            if (_resultSets.Count == 0)
            {
                parts.Add("no result sets");
            }
            else if (_resultSets.Count == 1)
            {
                parts.Add(FormatRowCount(_resultSets[0]));
            }
            else
            {
                parts.Add($"{_resultSets.Count} result sets");
            }

            if (result.RecordsAffected >= 0)
            {
                parts.Add($"{result.RecordsAffected:N0} row(s) affected");
            }

            if (_resultSets.Any(r => r.Truncated))
            {
                parts.Add($"showing first {MaxRowsPerResultSet:N0} rows");
            }

            SetStatus(string.Join("  ·  ", parts), isError: false);
        }

        private static string FormatRowCount(ResultSet resultSet)
        {
            var count = resultSet.Table.Rows.Count;
            return $"{count:N0}{(resultSet.Truncated ? "+" : string.Empty)} row{(count == 1 ? string.Empty : "s")}";
        }

        private void OnResultSetSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_suppressResultSetSelection)
            {
                BindResultSet(ResultSetCombo.SelectedIndex);
            }
        }

        private void BindResultSet(int index)
        {
            if (index < 0 || index >= _resultSets.Count)
            {
                ResultsGrid.ItemsSource = null;
                ResultsGrid.Columns.Clear();
                _boundSignature = null;
                return;
            }

            var resultSet = _resultSets[index];
            var signature = index.ToString(CultureInfo.InvariantCulture) + "|" + resultSet.SchemaSignature;
            var sameLayout = signature == _boundSignature;

            // Keep the user's sort order and scroll position across refreshes when the shape of the result is unchanged.
            var sorts = sameLayout ? ResultsGrid.Items.SortDescriptions.ToList() : new List<SortDescription>();
            var scrollViewer = FindDescendant<ScrollViewer>(ResultsGrid);
            var horizontalOffset = scrollViewer?.HorizontalOffset ?? 0;
            var verticalOffset = scrollViewer?.VerticalOffset ?? 0;

            if (!sameLayout)
            {
                ResultsGrid.Columns.Clear();
                foreach (DataColumn column in resultSet.Table.Columns)
                {
                    ResultsGrid.Columns.Add(CreateColumn(column));
                }
            }

            ResultsGrid.ItemsSource = resultSet.Table.DefaultView;
            _boundSignature = signature;

            if (!sameLayout)
            {
                return;
            }

            foreach (var sort in sorts)
            {
                ResultsGrid.Items.SortDescriptions.Add(sort);
            }

            foreach (var column in ResultsGrid.Columns)
            {
                var sort = sorts.FirstOrDefault(s => s.PropertyName == column.SortMemberPath);
                column.SortDirection = sort.PropertyName == null ? (ListSortDirection?)null : sort.Direction;
            }

            if (scrollViewer != null)
            {
                ResultsGrid.UpdateLayout();
                scrollViewer.ScrollToHorizontalOffset(horizontalOffset);
                scrollViewer.ScrollToVerticalOffset(verticalOffset);
            }
        }

        private static DataGridColumn CreateColumn(DataColumn column)
        {
            var textColumn = new DataGridTextColumn
            {
                // A TextBlock header keeps underscores in column names from being treated as access keys.
                Header = new TextBlock { Text = column.Caption },
                Binding = new Binding(column.ColumnName) { Mode = BindingMode.OneWay, Converter = CellValueConverter.Instance },
                SortMemberPath = column.ColumnName,
                MaxWidth = 600,
            };

            if (IsNumeric(column.DataType))
            {
                textColumn.ElementStyle = RightAlignedCell;
            }

            return textColumn;
        }

        private static bool IsNumeric(Type type) =>
            type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
            || type == typeof(decimal) || type == typeof(double) || type == typeof(float);

        #endregion

        #region UI helpers

        private void UpdateUiState()
        {
            var polling = IsPolling;
            var busy = IsBusy;
            var runningOnce = _runOnceCts != null;
            var editable = !polling && !busy;

            StartStopButton.Content = polling ? "Stop" : runningOnce ? "Cancel" : "Start";
            StartStopButton.IsEnabled = polling || runningOnce || !busy;
            RunNowButton.IsEnabled = !busy;

            EditorCombo.IsEnabled = editable;
            CopyQueryButton.IsEnabled = editable;
            StopOnErrorCheck.IsEnabled = editable;
            DatabaseBox.IsReadOnly = !editable;
            IntervalBox.IsReadOnly = !editable;
            TimeoutBox.IsReadOnly = !editable;
            AutoStopBox.IsReadOnly = !editable;
            QueryBox.IsReadOnly = !editable;

            UpdateCaption();
        }

        private void UpdateCountdown()
        {
            if (IsBusy)
            {
                CountdownText.Text = "Running…";
            }
            else if (IsPolling && _nextRunAt.HasValue)
            {
                var remaining = Math.Max(0, (int)Math.Ceiling((_nextRunAt.Value - DateTime.UtcNow).TotalSeconds));
                var unchanged = _changeTracker != null && _changeTracker.UnchangedFor > TimeSpan.Zero
                    ? $"  ·  unchanged {FormatSeconds(_changeTracker.UnchangedFor)} of {FormatSeconds(_changeTracker.Threshold)}"
                    : string.Empty;
                CountdownText.Text = $"Next run in {remaining}s{unchanged}";
            }
            else if (!IsPolling && CountdownText.Text == "Running…")
            {
                CountdownText.Text = string.Empty;
            }
        }

        private void UpdateCaption()
        {
            if (_connection == null)
            {
                _owner.SetCaptionSuffix(null);
                return;
            }

            _owner.SetCaptionSuffix(IsPolling ? $"{_connection.ServerName} (every {_pollIntervalSeconds}s)" : _connection.ServerName);
        }

        private void SetStatus(string text, bool isError)
        {
            StatusText.Text = text;
            StatusText.ToolTip = text;
            if (isError)
            {
                StatusText.Foreground = ErrorBrush;
            }
            else
            {
                StatusText.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        private static string FormatSeconds(TimeSpan span) => ((int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.CurrentCulture) + "s";

        private static T FindDescendant<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }

                var nested = FindDescendant<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Style CreateRightAlignedCellStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            style.Seal();
            return style;
        }

        #endregion

        private sealed class PollRequest
        {
            public PollRequest(PollConnection connection, string database, string sql, int intervalSeconds, int timeoutSeconds, bool stopOnError, int autoStopSeconds)
            {
                Connection = connection;
                Database = database;
                Sql = sql;
                IntervalSeconds = intervalSeconds;
                TimeoutSeconds = timeoutSeconds;
                StopOnError = stopOnError;
                AutoStopSeconds = autoStopSeconds;
            }

            public PollConnection Connection { get; }

            public string Database { get; }

            public string Sql { get; }

            public int IntervalSeconds { get; }

            public int TimeoutSeconds { get; }

            public bool StopOnError { get; }

            /// <summary>Stop once results are unchanged for this many seconds; 0 = never.</summary>
            public int AutoStopSeconds { get; }
        }
    }

    internal sealed class CellValueConverter : IValueConverter
    {
        public static readonly CellValueConverter Instance = new CellValueConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value)
            {
                case null:
                case DBNull _:
                    return "NULL";
                case DateTime dateTime:
                    return dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                case bool boolean:
                    return boolean ? "1" : "0";
                default:
                    return QueryExecutor.FormatValue(value);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
