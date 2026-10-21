using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace SsmsQueryPoller
{
    [Guid(PackageGuids.ToolWindowString)]
    public sealed class QueryPollerToolWindow : ToolWindowPane, IVsWindowFrameNotify3
    {
        private readonly QueryPollerControl _control;

        public QueryPollerToolWindow() : base(null)
        {
            Caption = "Query Poller";
            _control = new QueryPollerControl(this);
            Content = _control;
        }

        public override void OnToolWindowCreated()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            base.OnToolWindowCreated();

            // Lets us stop polling when the user closes (hides) the tool window.
            if (Frame is IVsWindowFrame frame)
            {
                frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, this);
            }
        }

        internal JoinableTaskFactory JoinableTaskFactory => (Package as AsyncPackage)?.JoinableTaskFactory ?? ThreadHelper.JoinableTaskFactory;

        internal void SetCaptionSuffix(string suffix)
        {
            Caption = string.IsNullOrEmpty(suffix) ? "Query Poller" : "Query Poller - " + suffix;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
#pragma warning disable VSTHRD108 // Dispose(true) only runs on the UI thread; Dispose(false) must not assert.
                ThreadHelper.ThrowIfNotOnUIThread();
#pragma warning restore VSTHRD108
                _control.Shutdown();
            }

            base.Dispose(disposing);
        }

        int IVsWindowFrameNotify3.OnClose(ref uint pgrfSaveOptions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _control.StopPolling("Stopped (window closed).");
            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify3.OnShow(int fShow) => VSConstants.S_OK;

        int IVsWindowFrameNotify3.OnMove(int x, int y, int w, int h) => VSConstants.S_OK;

        int IVsWindowFrameNotify3.OnSize(int x, int y, int w, int h) => VSConstants.S_OK;

        int IVsWindowFrameNotify3.OnDockableChange(int fDockable, int x, int y, int w, int h) => VSConstants.S_OK;
    }
}
