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
        "0.3.7")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(DockToolWindow))]
    [ProvideOptionPage(typeof(DshDockOptions), "DSH Dock", "General", 0, 0, true)]
    [Guid(PackageGuidString)]
    public sealed class DshDockPackage : AsyncPackage
    {
        public const string PackageGuidString = "8a5b0f3c-2d4e-4f6a-9c1d-7e8f9a0b1c2d";

        /// <summary>The loaded package, for the options page and the dock.</summary>
        public static DshDockPackage Instance { get; private set; }

        public DshDockOptions Options => (DshDockOptions)GetDialogPage(typeof(DshDockOptions));

        public const string CommandSetGuidString = "3c9d8e7f-1a2b-4c5d-8e9f-0a1b2c3d4e5f";

        public const int OpenDockCommandId = 0x0100;

        public const int ReloadDockCommandId = 0x0101;

        public const int DockToolbarCommandId = 0x0102;

        public const int ToggleReviewCommandId = 0x0104;

        public const int AddToChatCommandId = 0x0105;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            var commandId = new CommandID(new Guid(CommandSetGuidString), OpenDockCommandId);
            commandService.AddCommand(new MenuCommand(OnOpenDock, commandId));
            var reloadId = new CommandID(new Guid(CommandSetGuidString), ReloadDockCommandId);
            commandService.AddCommand(new MenuCommand(OnReloadDock, reloadId));
            var reviewId = new CommandID(new Guid(CommandSetGuidString), ToggleReviewCommandId);
            commandService.AddCommand(new MenuCommand(OnToggleReview, reviewId));
            var chatId = new CommandID(new Guid(CommandSetGuidString), AddToChatCommandId);
            commandService.AddCommand(new MenuCommand(OnAddToChat, chatId));
        }

        private void OnAddToChat(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                var document = dte?.ActiveDocument;
                if (document == null)
                {
                    return;
                }
                var selection = document.Selection as EnvDTE.TextSelection;
                var hasSelection = selection != null && !string.IsNullOrEmpty(selection.Text);
                var startLine = hasSelection ? selection.TopLine : (selection?.CurrentLine ?? 1);
                var endLine = hasSelection ? selection.BottomLine : startLine;
                var startColumn = hasSelection ? selection.TopPoint.LineCharOffset : (selection?.ActivePoint.LineCharOffset ?? 1);
                var endColumn = hasSelection ? selection.BottomPoint.LineCharOffset : startColumn;
                var window = FindToolWindow(typeof(DockToolWindow), 0, false) as DockToolWindow;
                (window?.Content as DockControl)?.AddToChat(
                    document.FullName, startLine, endLine, startColumn, endColumn);
            }
            catch (Exception error)
            {
                DockLog.Write("chat: cannot read the editor selection: " + error.Message);
            }
        }

        private void OnToggleReview(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var options = Options;
            options.ReviewEdits = !options.ReviewEdits;
            options.SaveSettingsToStorage();
            var window = FindToolWindow(typeof(DockToolWindow), 0, false) as DockToolWindow;
            (window?.Content as DockControl)?.SetReview(options.ReviewEdits);
            var statusBar = GetService(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText("DSH automatic diff: " + (options.ReviewEdits ? "on" : "off"));
        }

        private void OnReloadDock(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = FindToolWindow(typeof(DockToolWindow), 0, false) as DockToolWindow;
            (window?.Content as DockControl)?.Reload();
        }

        /// <summary>Open Tools → Options → DSH Dock (the offline card's Settings).</summary>
        public static void ShowOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Instance?.ShowOptionPage(typeof(DshDockOptions));
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
