using System;
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
            Caption = "DSH Dock";
            Content = new DockControl();
        }
    }
}
