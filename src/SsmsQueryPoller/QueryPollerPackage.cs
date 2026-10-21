using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SsmsQueryPoller
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Query Poller", "Re-runs a query every N seconds and shows the latest results.", "1.0.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(QueryPollerToolWindow), Style = VsDockStyle.Tabbed, Window = "34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3")]
    [Guid(PackageGuids.PackageString)]
    public sealed class QueryPollerPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var id = new CommandID(PackageGuids.CmdSet, PackageIds.ShowQueryPollerCommandId);
                commandService.AddCommand(new MenuCommand((s, e) => JoinableTaskFactory.RunAsync(ShowToolWindowAsync).FileAndForget("SsmsQueryPoller/ShowToolWindow"), id));
            }
        }

        private async Task ShowToolWindowAsync()
        {
            var window = await ShowToolWindowAsync(typeof(QueryPollerToolWindow), 0, create: true, DisposalToken);
            if (window?.Frame == null)
            {
                throw new NotSupportedException("Cannot create the Query Poller tool window.");
            }
        }
    }
}
