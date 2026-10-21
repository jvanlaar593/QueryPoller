using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.Design;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.VisualStudio.Shell;

namespace SsmsQueryPoller
{
    /// <summary>Outcome of one native SSMS execution, mirroring SSMS's internal ScriptExecutionResult flags.</summary>
    [Flags]
    internal enum NativeExecutionResult
    {
        Unknown = 0,
        Success = 1,
        Failure = 2,
        Cancel = 4,
        Timeout = 8,
        Halted = 16,
    }

    /// <summary>
    /// Drives a specific SSMS query window (SQLEditors' SqlScriptEditorControl) without going through the focus-based
    /// command routing, so the poller never executes whichever window happens to have focus.
    /// SSMS doesn't expose these members publicly (and SQLEditors.dll can't be referenced at compile time), so they are
    /// discovered via reflection from the editor's own type hierarchy and validated before polling starts.
    /// </summary>
    internal sealed class SqlEditorAdapter : IDisposable
    {
        public static readonly Guid SqlEditorCommandSet = new Guid("52692960-56bc-4989-b5d3-94c47a513e8d");

        private const string SqlScriptEditorTypeName = "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SqlScriptEditorControl";
        private const string ScriptAndResultsEditorTypeName = "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptAndResultsEditorControl";
        private const string ShellWindowPaneTypeName = "Microsoft.SqlServer.Management.UI.VSIntegration.ShellWindowPaneUserControl";
        private const int ExecuteQueryCommandId = 1;
        private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly MethodInfo CompletedCallback = typeof(SqlEditorAdapter).GetMethod(nameof(OnScriptExecutionCompleted), InstanceAny);

        private readonly Control _editor;
        private readonly MenuCommand _executeCommand;
        private readonly PropertyInfo _isExecutingProperty;
        private object _executionHandler;
        private EventInfo _completedEvent;
        private Delegate _completedDelegate;
        private PropertyInfo _resultProperty;
        private volatile int _lastResult;

        private SqlEditorAdapter(Control editor, MenuCommand executeCommand, PropertyInfo isExecutingProperty)
        {
            _editor = editor;
            _executeCommand = executeCommand;
            _isExecutingProperty = isExecutingProperty;
        }

        public Control Editor => _editor;

        public bool IsClosed => _editor.IsDisposed || _editor.Disposing;

        public bool IsConnected => !IsClosed && IsConnectedEditor(_editor);

        public bool IsExecuting => !IsClosed && (bool)_isExecutingProperty.GetValue(_editor);

        /// <summary>Result of the most recent execution in this window, or Unknown if not observable / not finished.</summary>
        public NativeExecutionResult LastResult => (NativeExecutionResult)_lastResult;

        /// <summary>Returns the document view as an SSMS T-SQL query window, or null if it is anything else.</summary>
        public static Control AsSqlQueryEditor(object docView) =>
            docView is Control control && FindBaseType(control.GetType(), SqlScriptEditorTypeName) != null ? control : null;

        public static bool IsConnectedEditor(Control editor) =>
            editor is ISqlToolsWindowWithConnectionState state && state.IsConnected;

        public static SqlEditorAdapter TryCreate(Control editor, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;

            var type = editor.GetType();
            var menuCommandsField = FindBaseType(type, ShellWindowPaneTypeName)?.GetField("menuCommands", InstanceAny);
            var editorBase = FindBaseType(type, ScriptAndResultsEditorTypeName);
            var isExecuting = editorBase?.GetProperty("IsExecuting", InstanceAny);
            if (menuCommandsField == null || isExecuting == null || isExecuting.PropertyType != typeof(bool))
            {
                error = "this version of SSMS isn't supported (query window internals have changed).";
                return null;
            }

            MenuCommand execute;
            try
            {
                execute = (menuCommandsField.GetValue(editor) as IMenuCommandService)?.FindCommand(new CommandID(SqlEditorCommandSet, ExecuteQueryCommandId));
            }
            catch (Exception ex)
            {
                error = "couldn't access the query window's Execute command: " + ex.Message;
                return null;
            }

            if (execute == null)
            {
                error = "this query window has no Execute command.";
                return null;
            }

            var adapter = new SqlEditorAdapter(editor, execute, isExecuting);
            adapter.HookCompletion(editorBase);
            return adapter;
        }

        /// <summary>Runs SSMS's own Execute on this window (current selection, or the whole script if nothing is selected).</summary>
        public void Execute()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _lastResult = (int)NativeExecutionResult.Unknown;

            // With the default results layout SSMS focuses the executing editor; put focus back where the user had it.
            var focused = NativeMethods.GetFocus();
            try
            {
                _executeCommand.Invoke();
            }
            finally
            {
                if (focused != IntPtr.Zero && NativeMethods.GetFocus() != focused && NativeMethods.IsWindow(focused))
                {
                    NativeMethods.SetFocus(focused);
                }
            }
        }

        public void Dispose()
        {
            if (_completedDelegate == null)
            {
                return;
            }

            try
            {
                _completedEvent.GetRemoveMethod(true)?.Invoke(_executionHandler, new object[] { _completedDelegate });
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogWarning("SsmsQueryPoller", "Failed to unhook execution events: " + ex);
            }

            _completedDelegate = null;
            _executionHandler = null;
        }

        private void HookCompletion(Type editorBase)
        {
            try
            {
                var getHandler = editorBase.GetMethod("GetQueryExecutionHandler", InstanceAny, null, Type.EmptyTypes, null);
                _executionHandler = getHandler?.Invoke(_editor, null);
                _completedEvent = getHandler?.ReturnType.GetEvent("ScriptExecutionCompleted", InstanceAny);
                var add = _completedEvent?.GetAddMethod(true);
                if (_executionHandler == null || add == null || CompletedCallback == null)
                {
                    return;
                }

                var handler = Delegate.CreateDelegate(_completedEvent.EventHandlerType, this, CompletedCallback, throwOnBindFailure: false);
                if (handler == null)
                {
                    return;
                }

                add.Invoke(_executionHandler, new object[] { handler });
                _completedDelegate = handler;
            }
            catch (Exception ex)
            {
                // Polling still works; only stop-on-error / stop-on-cancel detection is lost.
                ActivityLog.TryLogWarning("SsmsQueryPoller", "Couldn't observe execution results: " + ex);
                _completedDelegate = null;
            }
        }

        // Bound (contravariantly) to SSMS's internal ScriptExecutionCompletedEventHandler(object, ScriptExecutionCompletedEventArgs).
        private void OnScriptExecutionCompleted(object sender, EventArgs e)
        {
            try
            {
                _resultProperty = _resultProperty ?? e?.GetType().GetProperty("ExecutionResult", InstanceAny);
                var value = _resultProperty?.GetValue(e);
                if (value != null)
                {
                    _lastResult = Convert.ToInt32(value);
                }
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogWarning("SsmsQueryPoller", "Couldn't read execution result: " + ex);
            }
        }

        private static Type FindBaseType(Type type, string fullName)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.FullName == fullName)
                {
                    return t;
                }
            }

            return null;
        }

        #region Grid results

        /// <summary>Upper bound on cells hashed per run, to keep the UI thread responsive.</summary>
        public const int MaxCellsToCompare = 250_000;

        private const string GridResultSetInterfaceName = "Microsoft.SqlServer.Management.QueryExecution.IGridResultSet";
        private static readonly Dictionary<Type, PropertyInfo> GridStorageProperties = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, GridResultSetAccessor> GridAccessors = new Dictionary<Type, GridResultSetAccessor>();

        /// <summary>True when every grid result set of the last run has finished loading into SSMS's grid storage.</summary>
        public bool AreGridResultsLoaded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return FindGridResultSets().All(rs => GetAccessor(rs.GetType())?.IsFullyLoaded(rs) ?? true);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Hashes the result grids SSMS is showing for this window (column names and every cell as displayed).
        /// Returns null with a reason when results can't be compared.
        /// </summary>
        public string TryComputeGridResultsFingerprint(out string unavailableReason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            unavailableReason = null;
            try
            {
                var mode = GetResultsMode();
                if (mode == null)
                {
                    unavailableReason = "this version of SSMS isn't supported";
                    return null;
                }

                if (!string.Equals(mode, "ResultsToGrid", StringComparison.Ordinal))
                {
                    unavailableReason = "it needs Results to Grid";
                    return null;
                }

                var resultSets = FindGridResultSets();
                using (var fingerprint = new ResultFingerprint())
                {
                    fingerprint.Add(resultSets.Count);
                    long cells = 0;
                    foreach (var resultSet in resultSets)
                    {
                        var accessor = GetAccessor(resultSet.GetType());
                        if (accessor == null)
                        {
                            unavailableReason = "this version of SSMS isn't supported";
                            return null;
                        }

                        var columns = accessor.ColumnCount(resultSet);
                        var rows = accessor.RowCount(resultSet);
                        cells += rows * columns;
                        if (cells > MaxCellsToCompare)
                        {
                            unavailableReason = $"results are too large to compare (over {MaxCellsToCompare:N0} cells)";
                            return null;
                        }

                        fingerprint.Add(columns);
                        foreach (var name in accessor.ColumnNames(resultSet) ?? new StringCollection())
                        {
                            fingerprint.Add(name);
                        }

                        // In grid mode column 0 is SSMS's row-number column; data columns start at 1.
                        var offset = accessor.IsGridMode(resultSet) ? 1 : 0;
                        fingerprint.Add(rows);
                        for (long row = 0; row < rows; row++)
                        {
                            for (var column = 0; column < columns; column++)
                            {
                                fingerprint.Add(accessor.GetCell(resultSet, row, column + offset));
                            }
                        }
                    }

                    return fingerprint.Finish();
                }
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogWarning("SsmsQueryPoller", "Couldn't read grid results: " + ex);
                unavailableReason = "the results couldn't be read";
                return null;
            }
        }

        private string GetResultsMode()
        {
            for (var t = _editor.GetType(); t != null; t = t.BaseType)
            {
                var field = t.GetField("m_sqlResultsControl", InstanceAny | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    var resultsControl = field.GetValue(_editor);
                    return resultsControl?.GetType().GetProperty("SqlExecutionMode", InstanceAny)?.GetValue(resultsControl)?.ToString();
                }
            }

            return null;
        }

        /// <summary>SSMS's result grids are WinForms GridControls whose public GridStorage is the result set; find them in display order.</summary>
        private List<object> FindGridResultSets()
        {
            var resultSets = new List<object>();
            CollectGridResultSets(_editor, resultSets);
            return resultSets;
        }

        private static void CollectGridResultSets(Control parent, List<object> resultSets)
        {
            foreach (Control child in parent.Controls)
            {
                var storageProperty = GetGridStorageProperty(child.GetType());
                if (storageProperty != null
                    && storageProperty.GetValue(child) is object storage
                    && storage.GetType().GetInterface(GridResultSetInterfaceName) != null
                    && !resultSets.Any(existing => ReferenceEquals(existing, storage)))
                {
                    resultSets.Add(storage);
                }

                CollectGridResultSets(child, resultSets);
            }
        }

        private static PropertyInfo GetGridStorageProperty(Type controlType)
        {
            if (!GridStorageProperties.TryGetValue(controlType, out var property))
            {
                property = controlType.GetProperty("GridStorage", BindingFlags.Instance | BindingFlags.Public);
                GridStorageProperties[controlType] = property;
            }

            return property;
        }

        private static GridResultSetAccessor GetAccessor(Type resultSetType)
        {
            if (!GridAccessors.TryGetValue(resultSetType, out var accessor))
            {
                accessor = GridResultSetAccessor.TryCreate(resultSetType);
                GridAccessors[resultSetType] = accessor;
            }

            return accessor;
        }

        /// <summary>Fast access to SSMS's public IGridResultSet interface (compiled once per type; the interface can't be referenced at compile time).</summary>
        private sealed class GridResultSetAccessor
        {
            private PropertyInfo _gridMode;
            private PropertyInfo _storedAllData;

            public Func<object, int> ColumnCount { get; private set; }

            public Func<object, long> RowCount { get; private set; }

            public Func<object, StringCollection> ColumnNames { get; private set; }

            public Func<object, long, int, string> GetCell { get; private set; }

            public bool IsGridMode(object resultSet) => _gridMode == null || (bool)_gridMode.GetValue(resultSet);

            public bool IsFullyLoaded(object resultSet) => _storedAllData == null || (bool)_storedAllData.GetValue(resultSet);

            public static GridResultSetAccessor TryCreate(Type resultSetType)
            {
                var contract = resultSetType.GetInterface(GridResultSetInterfaceName);
                var columns = contract?.GetProperty("NumberOfDataColumns");
                var rows = contract?.GetProperty("TotalNumberOfRows");
                var names = contract?.GetProperty("ColumnNames");
                var cell = contract?.GetMethod("GetCellDataAsString", new[] { typeof(long), typeof(int) });
                if (columns?.PropertyType != typeof(int) || rows?.PropertyType != typeof(long) || names?.PropertyType != typeof(StringCollection) || cell?.ReturnType != typeof(string))
                {
                    return null;
                }

                var instance = Expression.Parameter(typeof(object), "resultSet");
                var typed = Expression.Convert(instance, contract);
                var row = Expression.Parameter(typeof(long), "row");
                var column = Expression.Parameter(typeof(int), "column");

                var gridMode = resultSetType.GetProperty("GridMode", InstanceAny);
                var storedAllData = resultSetType.GetProperty("StoredAllData", InstanceAny);
                return new GridResultSetAccessor
                {
                    ColumnCount = Expression.Lambda<Func<object, int>>(Expression.Property(typed, columns), instance).Compile(),
                    RowCount = Expression.Lambda<Func<object, long>>(Expression.Property(typed, rows), instance).Compile(),
                    ColumnNames = Expression.Lambda<Func<object, StringCollection>>(Expression.Property(typed, names), instance).Compile(),
                    GetCell = Expression.Lambda<Func<object, long, int, string>>(Expression.Call(typed, cell, row, column), instance, row, column).Compile(),
                    _gridMode = gridMode?.PropertyType == typeof(bool) ? gridMode : null,
                    _storedAllData = storedAllData?.PropertyType == typeof(bool) ? storedAllData : null,
                };
            }
        }

        #endregion

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern IntPtr GetFocus();

            [DllImport("user32.dll")]
            public static extern IntPtr SetFocus(IntPtr hWnd);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsWindow(IntPtr hWnd);
        }
    }
}
