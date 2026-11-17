using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// Tools → Options → DSH Dock. The defaults follow QuickDock's own config so
    /// a normal install needs no setup; the fields exist for the cases where it
    /// does.
    /// </summary>
    public sealed class DshDockOptions : DialogPage
    {
        [Category("Connection")]
        [DisplayName("QuickDock port")]
        [Description("The loopback port QuickDock listens on. 0 follows DSH_QUICKDOCK_PORT, then QuickDock's config.json, then 47910.")]
        public int Port { get; set; }

        [Category("Connection")]
        [DisplayName("Follow QuickDock's config.json")]
        [Description("When on, the port is read from QuickDock's config.json (and host.log); a non-zero port above always wins.")]
        public bool FollowQuickDockConfig { get; set; } = true;

        [Category("Editor tools")]
        [DisplayName("Start editor tools with the dock")]
        [Description("Publish the editor MCP server when the dock opens. The plug in the dock's title bar can still turn it off.")]
        public bool EditorTools { get; set; } = true;

        [Category("Editor tools")]
        [DisplayName("Editor tools port")]
        [Description("Preferred port for the editor MCP server; a busy port falls back to a free one.")]
        public int McpPort { get; set; } = DockPairing.PreferredPort;

        [Category("Editor tools")]
        [DisplayName("Allow write tools")]
        [Description("Reserved for the write tools (edit, save, run, debug); the current build publishes read-only tools only.")]
        public bool WriteTools { get; set; }
    }
}
