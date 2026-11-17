using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// The extension entry point: one command that opens the dock tool window.
    /// The window itself is a WebView2 showing QuickDock's shell page.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(
        "DSH Dock for Visual Studio",
        "DeepSeek Harness's conversation in a Visual Studio tool window, served by QuickDock.",
        "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(DockToolWindow))]
    [Guid(PackageGuidString)]
    public sealed class DshDockPackage : AsyncPackage
    {
        public const string PackageGuidString = "8a5b0f3c-2d4e-4f6a-9c1d-7e8f9a0b1c2d";

        public const string CommandSetGuidString = "3c9d8e7f-1a2b-4c5d-8e9f-0a1b2c3d4e5f";

        public const int OpenDockCommandId = 0x0100;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            var commandId = new CommandID(new Guid(CommandSetGuidString), OpenDockCommandId);
            commandService.AddCommand(new MenuCommand(OnOpenDock, commandId));
        }

        private void OnOpenDock(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = FindToolWindow(typeof(DockToolWindow), 0, true) as DockToolWindow;
            if (window?.Frame == null)
            {
                return;
            }
            var frame = (IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
        }
    }
}
