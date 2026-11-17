using System;
using System.IO;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// A tiny append-only log. Visual Studio swallows extension exceptions, so the
    /// dock writes what it is doing to %LOCALAPPDATA%\DshDock\dock.log.
    /// </summary>
    internal static class DockLog
    {
        public static void Write(string message)
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshDock");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "dock.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Diagnostics must never break the dock.
            }
        }
    }
}
