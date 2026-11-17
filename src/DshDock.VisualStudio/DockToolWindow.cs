using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// The dock tool window: one per Visual Studio window, showing QuickDock's
    /// shell page for this solution.
    /// </summary>
    [Guid(ToolWindowGuidString)]
    public sealed class DockToolWindow : ToolWindowPane
    {
        public const string ToolWindowGuidString = "d4e5f6a7-b8c9-4d0e-9f1a-2b3c4d5e6f70";

        public DockToolWindow()
            : base(null)
        {
            DockLog.Write("pane ctor; ui=" + ThreadHelper.CheckAccess());
            Caption = "DSH Dock";
            // The Reload button lives in the tool window's own toolbar, which VS
            // draws in the pane's caption bar.
            ToolBar = new CommandID(
                new Guid(DshDockPackage.CommandSetGuidString),
                DshDockPackage.DockToolbarCommandId);
            Content = new DockControl();
            DockLog.Write("pane ctor done; content=" + (Content == null ? "null" : Content.GetType().Name));
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            DockLog.Write("pane created; content=" + (Content == null ? "null" : Content.GetType().FullName));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Content is IDisposable disposable)
            {
                disposable.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
