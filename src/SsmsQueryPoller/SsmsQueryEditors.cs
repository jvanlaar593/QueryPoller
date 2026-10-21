using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.ConnectionDlg;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace SsmsQueryPoller
{
    internal sealed class QueryEditorItem
    {
        public QueryEditorItem(string caption, string moniker)
        {
            Caption = caption;
            Moniker = moniker;
        }

        public string Caption { get; }

        public string Moniker { get; }

        public override string ToString() => Caption;
    }

    /// <summary>A snapshot of a query window's connection settings, used to open fresh connections for each poll.</summary>
    internal sealed class PollConnection
    {
        private const string ApplicationName = "SSMS Query Poller";
        private readonly UIConnectionInfo _connectionInfo;

        public PollConnection(UIConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo.Copy();
        }

        public string ServerName => _connectionInfo.ServerName;

        public string UserName => string.IsNullOrEmpty(_connectionInfo.UserName) ? null : _connectionInfo.UserName;

        public string InitialDatabase => _connectionInfo.AdvancedOptions?["DATABASE"];

        public DbConnection CreateConnection(string database)
        {
            var info = _connectionInfo.Copy();
            if (!string.IsNullOrWhiteSpace(database))
            {
                info.AdvancedOptions["DATABASE"] = database.Trim();
            }

            info.ApplicationName = ApplicationName;

            // Same factory SSMS uses for query windows, so Windows, SQL and all Entra ID auth modes behave identically.
            var connection = ((IServerTypePoolable)new SqlServerType()).GetConnectionObject(info, true);
            return connection as DbConnection
                ?? throw new NotSupportedException("SSMS returned an unsupported connection type: " + connection?.GetType().FullName);
        }
    }

    internal static class SsmsQueryEditors
    {
        public static ISqlEditorService GetEditorService()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Package.GetGlobalService(typeof(SSqlEditorService)) as ISqlEditorService;
        }

        public static IReadOnlyList<QueryEditorItem> ListConnectedEditors(out string activeMoniker)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var items = new List<QueryEditorItem>();
            activeMoniker = null;

            var service = GetEditorService();
            if (service == null)
            {
                return items;
            }

            activeMoniker = service.GetActiveEditorMoniker();
            foreach (var (caption, moniker) in service.ListOpenedQueryEditorCaptionsWithMonikers(true))
            {
                if (!string.IsNullOrEmpty(moniker))
                {
                    items.Add(new QueryEditorItem(string.IsNullOrEmpty(caption) ? moniker : caption, moniker));
                }
            }

            return items;
        }

        /// <summary>Returns a connection snapshot for the given query window, or null if it isn't connected to a SQL Server database engine.</summary>
        public static PollConnection GetConnection(string moniker, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;

            var service = GetEditorService();
            if (service == null)
            {
                error = "The SSMS SQL editor service is not available.";
                return null;
            }

            var info = service.GetUIConnectionInfoForSpecificQueryEditor(moniker);
            if (info == null)
            {
                error = "That query window is not connected.";
                return null;
            }

            if (info.ServerType != SqlServerType.ServerType)
            {
                error = "Only SQL Server Database Engine connections are supported.";
                return null;
            }

            return new PollConnection(info);
        }

        /// <summary>Returns the selected text of the query window, or all of its text when nothing is selected.</summary>
        public static string GetQueryText(string moniker)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Package.GetGlobalService(typeof(SDTE)) is DTE dte)
            {
                foreach (Document document in dte.Documents)
                {
                    if (!string.Equals(document.FullName, moniker, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (document.Object("TextDocument") is TextDocument textDocument)
                    {
                        var selection = textDocument.Selection;
                        if (selection != null && !selection.IsEmpty && !string.IsNullOrWhiteSpace(selection.Text))
                        {
                            return selection.Text;
                        }

                        return textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
                    }
                }
            }

            return GetTextFromRunningDocumentTable(moniker);
        }

        private static string GetTextFromRunningDocumentTable(string moniker)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable rdt))
            {
                return null;
            }

            var docData = IntPtr.Zero;
            try
            {
                var hr = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, moniker, out _, out _, out docData, out _);
                if (ErrorHandler.Failed(hr) || docData == IntPtr.Zero)
                {
                    return null;
                }

                if (Marshal.GetObjectForIUnknown(docData) is IVsTextLines lines
                    && ErrorHandler.Succeeded(lines.GetLastLineIndex(out var lastLine, out var lastIndex))
                    && ErrorHandler.Succeeded(lines.GetLineText(0, 0, lastLine, lastIndex, out var text)))
                {
                    return text;
                }

                return null;
            }
            finally
            {
                if (docData != IntPtr.Zero)
                {
                    Marshal.Release(docData);
                }
            }
        }
    }
}
