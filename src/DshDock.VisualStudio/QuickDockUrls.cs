using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// Where the dock page lives: the loopback listener QuickDock serves, and the
    /// query a Visual Studio dock identifies itself with.
    /// </summary>
    internal static class QuickDockUrls
    {
        public const int DefaultPort = 47910;

        /// <summary>The QuickDock view name this client asks the shell page for.</summary>
        public const string ViewName = "visualstudio";

        /// <summary>
        /// The listener port: environment, then QuickDock's own config.json, then
        /// the default. Zero (or a busy port) means the listener is off.
        /// </summary>
        public static int ResolvePort()
        {
            var fromEnv = Environment.GetEnvironmentVariable("DSH_QUICKDOCK_PORT");
            if (int.TryParse(fromEnv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort)
                && envPort >= 0 && envPort <= 65535)
            {
                return envPort;
            }

            var config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dsh-quickdock",
                "config.json");
            try
            {
                if (File.Exists(config))
                {
                    var text = File.ReadAllText(config);
                    var match = Regex.Match(text, "\"port\"\\s*:\\s*(\\d+)");
                    if (match.Success
                        && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var filePort)
                        && filePort >= 0 && filePort <= 65535)
                    {
                        return filePort;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return DefaultPort;
        }

        /// <summary>The shell page URL, carrying this dock's id and workspace folders.</summary>
        public static string ShellUrl(int port)
        {
            var url = new StringBuilder();
            url.Append("http://127.0.0.1:")
                .Append(port.ToString(CultureInfo.InvariantCulture))
                .Append("/quickdock?view=")
                .Append(ViewName)
                .Append("&dock=")
                .Append(Uri.EscapeDataString(DockId()))
                .Append("&mcp=0");
            foreach (var folder in WorkspaceFolders())
            {
                url.Append("&workspace=").Append(Uri.EscapeDataString(folder));
            }
            return url.ToString();
        }

        /// <summary>
        /// This window's dock id: stable for a solution, per process when no
        /// solution is open (two VS windows on one solution would otherwise share
        /// a context).
        /// </summary>
        public static string DockId()
        {
            var seed = SolutionDirectory()
                ?? Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                var hex = BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
                return "vs-" + hex;
            }
        }

        /// <summary>The directory of the open solution, or null when none is open.</summary>
        public static string SolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var solution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;
                if (solution == null)
                {
                    return null;
                }
                if (solution.GetSolutionInfo(out var directory, out var file, out _) != 0)
                {
                    return null;
                }
                if (!string.IsNullOrEmpty(directory))
                {
                    return directory.TrimEnd('\\', '/');
                }
                if (!string.IsNullOrEmpty(file))
                {
                    return Path.GetDirectoryName(file);
                }
            }
            catch (Exception)
            {
                // No solution service (first launch, experimental hive): treat as none.
            }
            return null;
        }

        public static IEnumerable<string> WorkspaceFolders()
        {
            var directory = SolutionDirectory();
            if (!string.IsNullOrEmpty(directory))
            {
                yield return directory;
            }
        }
    }
}
