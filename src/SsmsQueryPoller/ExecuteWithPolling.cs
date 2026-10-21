using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace SsmsQueryPoller
{
    /// <summary>"Execute with Polling": repeatedly runs SSMS's native Execute in a query window.</summary>
    internal sealed class ExecuteWithPollingCommand
    {
        private const string StartText = "Execute with Polling";
        private const string StopText = "Stop Polling";

        private readonly AsyncPackage _package;
        private readonly Dictionary<Control, PollingSession> _sessions = new Dictionary<Control, PollingSession>();

        private ExecuteWithPollingCommand(AsyncPackage package)
        {
            _package = package;
        }

        public static void Initialize(AsyncPackage package, OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var instance = new ExecuteWithPollingCommand(package);
            var command = new OleMenuCommand((s, e) => instance.Toggle(), new CommandID(PackageGuids.CmdSet, PackageIds.ExecuteWithPollingCommandId));
            command.BeforeQueryStatus += instance.OnBeforeQueryStatus;
            commandService.AddCommand(command);
        }

        internal JoinableTaskFactory JoinableTaskFactory => _package.JoinableTaskFactory;

        internal void OnSessionEnded(PollingSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_sessions.TryGetValue(session.Editor, out var current) && current == session)
            {
                _sessions.Remove(session.Editor);
            }

            RefreshCommandUi();
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            var editor = GetActiveSqlEditor(out _);
            var polling = editor != null && _sessions.ContainsKey(editor);

            command.Visible = true;
            command.Checked = polling;
            command.Text = polling ? StopText : StartText;
            command.Enabled = polling || (editor != null && IsConnected(editor));
        }

        private void Toggle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var editor = GetActiveSqlEditor(out var frame);
            if (editor == null)
            {
                PollingStatus.Show("Query Poller: activate a connected query window first.");
                return;
            }

            if (_sessions.TryGetValue(editor, out var existing))
            {
                existing.Stop("stopped");
                return;
            }

            if (!IsConnected(editor))
            {
                PollingStatus.Show("Query Poller: connect the query window first.");
                return;
            }

            var adapter = SqlEditorAdapter.TryCreate(editor, out var error);
            if (adapter == null)
            {
                PollingStatus.Show("Query Poller can't poll this window: " + error);
                return;
            }

            var options = (PollingOptionsPage)_package.GetDialogPage(typeof(PollingOptionsPage));
            var session = new PollingSession(this, adapter, frame, options.IntervalSeconds, options.StopOnError, options.MaxRuns, options.StopWhenUnchangedSeconds);
            _sessions[editor] = session;
            RefreshCommandUi();

            JoinableTaskFactory.RunAsync(session.RunAsync).FileAndForget("SsmsQueryPoller/ExecuteWithPolling");
        }

        private static bool IsConnected(Control editor) => SqlEditorAdapter.IsConnectedEditor(editor);

        private static Control GetActiveSqlEditor(out IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            frame = null;

            if (!(Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection)
                || ErrorHandler.Failed(selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var frameObject)))
            {
                return null;
            }

            frame = frameObject as IVsWindowFrame;
            if (frame == null || ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView)))
            {
                return null;
            }

            return SqlEditorAdapter.AsSqlQueryEditor(docView);
        }

        private static void RefreshCommandUi()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell)?.UpdateCommandUI(0);
        }
    }

    /// <summary>One polling loop bound to one query window.</summary>
    internal sealed class PollingSession
    {
        private static readonly TimeSpan ExecutionCheckInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan MaxWaitForGridData = TimeSpan.FromSeconds(10);

        private readonly ExecuteWithPollingCommand _owner;
        private readonly SqlEditorAdapter _adapter;
        private readonly string _caption;
        private readonly int _intervalSeconds;
        private readonly bool _stopOnError;
        private readonly int _maxRuns;
        private readonly ResultChangeTracker _changeTracker;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private IWpfTextView _textView;
        private ITrackingSpan _selection;
        private string _selectionText;
        private string _stopReason;
        private string _autoStopUnavailable;
        private int _runs;

        public PollingSession(ExecuteWithPollingCommand owner, SqlEditorAdapter adapter, IVsWindowFrame frame, int intervalSeconds, bool stopOnError, int maxRuns, int stopWhenUnchangedSeconds)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _owner = owner;
            _adapter = adapter;
            _intervalSeconds = Math.Max(intervalSeconds, 1);
            _stopOnError = stopOnError;
            _maxRuns = Math.Max(maxRuns, 0);
            _changeTracker = stopWhenUnchangedSeconds > 0 ? new ResultChangeTracker(TimeSpan.FromSeconds(stopWhenUnchangedSeconds)) : null;
            _caption = GetCaption(frame);
            CaptureSelection();

            adapter.Editor.Disposed += OnEditorDisposed;
        }

        public Control Editor => _adapter.Editor;

        public void Stop(string reason)
        {
            _stopReason = _stopReason ?? reason;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Session already ended.
            }
        }

        public async Task RunAsync()
        {
            await _owner.JoinableTaskFactory.SwitchToMainThreadAsync();
            var token = _cts.Token;
            var scope = _selection != null ? "the selected text" : "the whole script";
            PollingStatus.Show($"Query Poller: polling '{_caption}' ({scope}) every {_intervalSeconds}s…");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Don't interleave with a run the user started themselves.
                    await WaitWhileExecutingAsync(token);
                    if (token.IsCancellationRequested || !EnsureCanRun())
                    {
                        break;
                    }

                    var startedAt = DateTime.UtcNow;
                    _adapter.Execute();
                    if (!_adapter.IsExecuting)
                    {
                        Stop("the query didn't start (empty script, connection problem, or SSMS showed an error)");
                        break;
                    }

                    _runs++;
                    PollingStatus.Show($"Query Poller: '{_caption}' run #{_runs} executing…");

                    await WaitWhileExecutingAsync(token);
                    if (token.IsCancellationRequested || !CheckAfterRun())
                    {
                        break;
                    }

                    if (_changeTracker != null && !await CheckResultsChangedAsync(startedAt, token))
                    {
                        break;
                    }

                    var wait = TimeSpan.FromSeconds(_intervalSeconds) - (DateTime.UtcNow - startedAt);
                    if (wait < TimeSpan.Zero)
                    {
                        wait = TimeSpan.Zero;
                    }

                    var nextAt = DateTime.Now + wait;
                    PollingStatus.Show($"Query Poller: '{_caption}' run #{_runs} finished at {DateTime.Now:T}; next run at {nextAt:T} (every {_intervalSeconds}s){DescribeAutoStop()}.");
                    await DelayAsync(wait, token);
                }
            }
            finally
            {
                await _owner.JoinableTaskFactory.SwitchToMainThreadAsync();
                _adapter.Editor.Disposed -= OnEditorDisposed;
                _adapter.Dispose();
                _cts.Dispose();
                _owner.OnSessionEnded(this);

                var runs = _runs == 1 ? "1 run" : _runs.ToString(CultureInfo.CurrentCulture) + " runs";
                PollingStatus.Show($"Query Poller: stopped polling '{_caption}' after {runs} — {_stopReason ?? "stopped"}.");
            }
        }

        private bool EnsureCanRun()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_adapter.IsClosed)
            {
                Stop("the query window was closed");
                return false;
            }

            if (!_adapter.IsConnected)
            {
                Stop("the query window is disconnected");
                return false;
            }

            return RestoreSelection();
        }

        private bool CheckAfterRun()
        {
            if (_adapter.IsClosed)
            {
                Stop("the query window was closed");
                return false;
            }

            var result = _adapter.LastResult;
            if ((result & NativeExecutionResult.Cancel) != 0)
            {
                Stop($"run #{_runs} was cancelled");
                return false;
            }

            if (_stopOnError && (result & (NativeExecutionResult.Failure | NativeExecutionResult.Timeout | NativeExecutionResult.Halted)) != 0)
            {
                Stop($"run #{_runs} failed (see the Messages tab)");
                return false;
            }

            if (_maxRuns > 0 && _runs >= _maxRuns)
            {
                Stop($"reached the maximum of {_maxRuns} runs");
                return false;
            }

            return true;
        }

        /// <returns>False if polling should stop because the results have been unchanged long enough.</returns>
        private async Task<bool> CheckResultsChangedAsync(DateTime runStartedUtc, CancellationToken token)
        {
            await _owner.JoinableTaskFactory.SwitchToMainThreadAsync();

            // A failed run's results aren't comparable; it restarts the unchanged period.
            if ((_adapter.LastResult & (NativeExecutionResult.Failure | NativeExecutionResult.Timeout | NativeExecutionResult.Halted)) != 0)
            {
                _changeTracker.Reset();
                return true;
            }

            var waitUntil = DateTime.UtcNow + MaxWaitForGridData;
            while (!token.IsCancellationRequested && !_adapter.IsClosed && !_adapter.AreGridResultsLoaded() && DateTime.UtcNow < waitUntil)
            {
                await DelayAsync(ExecutionCheckInterval, token);
            }

            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (_adapter.IsClosed)
            {
                Stop("the query window was closed");
                return false;
            }

            var fingerprint = _adapter.AreGridResultsLoaded()
                ? _adapter.TryComputeGridResultsFingerprint(out _autoStopUnavailable)
                : null;
            if (fingerprint == null && _autoStopUnavailable == null)
            {
                _autoStopUnavailable = "results were still loading";
            }
            else if (fingerprint != null)
            {
                _autoStopUnavailable = null;
            }

            if (_changeTracker.Observe(fingerprint, runStartedUtc))
            {
                Stop($"results unchanged for {(int)Math.Round(_changeTracker.UnchangedFor.TotalSeconds)}s");
                return false;
            }

            return true;
        }

        private string DescribeAutoStop()
        {
            if (_changeTracker == null)
            {
                return string.Empty;
            }

            if (_autoStopUnavailable != null)
            {
                return $"; auto-stop inactive: {_autoStopUnavailable}";
            }

            return _changeTracker.UnchangedFor > TimeSpan.Zero
                ? $"; unchanged {(int)Math.Round(_changeTracker.UnchangedFor.TotalSeconds)}s of {(int)_changeTracker.Threshold.TotalSeconds}s"
                : string.Empty;
        }

        /// <summary>
        /// SSMS executes whatever is selected at the moment Execute runs. Pin that to what was selected when polling started:
        /// re-select the original selection (tracked through edits elsewhere), or clear any new selection when the whole script was polled.
        /// </summary>
        private void CaptureSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _textView = GetTextView(_adapter.Editor);
            if (_textView == null || _textView.Selection.IsEmpty)
            {
                return;
            }

            var span = _textView.Selection.StreamSelectionSpan.SnapshotSpan;
            if (span.Length == 0)
            {
                return;
            }

            _selection = span.Snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive);
            _selectionText = span.GetText();
        }

        private bool RestoreSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_textView == null || _textView.IsClosed)
            {
                return true;
            }

            var selection = _textView.Selection;
            if (_selection == null)
            {
                if (!selection.IsEmpty)
                {
                    selection.Clear();
                }

                return true;
            }

            var span = _selection.GetSpan(_textView.TextSnapshot);
            if (!string.Equals(span.GetText(), _selectionText, StringComparison.Ordinal))
            {
                Stop("the selected query text was edited");
                return false;
            }

            if (selection.Mode != TextSelectionMode.Stream || selection.StreamSelectionSpan.SnapshotSpan != span)
            {
                selection.Select(span, isReversed: false);
            }

            return true;
        }

        private async Task WaitWhileExecutingAsync(CancellationToken token)
        {
            await _owner.JoinableTaskFactory.SwitchToMainThreadAsync();
            while (!token.IsCancellationRequested && _adapter.IsExecuting)
            {
                await DelayAsync(ExecutionCheckInterval, token);
            }
        }

        private async Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
            }

            await _owner.JoinableTaskFactory.SwitchToMainThreadAsync();
        }

        private void OnEditorDisposed(object sender, EventArgs e) => Stop("the query window was closed");

        private static IWpfTextView GetTextView(Control editor)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(editor is IVsCodeWindow codeWindow))
                {
                    return null;
                }

                if (ErrorHandler.Failed(codeWindow.GetLastActiveView(out var view)) || view == null)
                {
                    codeWindow.GetPrimaryView(out view);
                }

                var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
                return view == null ? null : adapters?.GetWpfTextView(view);
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogWarning("SsmsQueryPoller", "Couldn't get the query window's text view: " + ex);
                return null;
            }
        }

        private static string GetCaption(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame != null && ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var caption)) && caption is string text && text.Length > 0)
            {
                // Query window captions look like "SQLQuery1.sql - SERVER.db (user (55))"; the file name is enough.
                var dash = text.IndexOf(" - ", StringComparison.Ordinal);
                return dash > 0 ? text.Substring(0, dash) : text;
            }

            return "query window";
        }
    }

    internal static class PollingStatus
    {
        public static void Show(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar statusBar
                && ErrorHandler.Succeeded(statusBar.IsFrozen(out var frozen)) && frozen == 0)
            {
                statusBar.SetText(text);
            }
        }
    }
}
