using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// The dock: QuickDock's shell page in a WebView2, plus a local card when the
    /// listener cannot be reached.
    ///
    /// The shell page is embedded in a one-line host page rather than loaded as
    /// the document: that is what lets the shell's `quickdock:open-file` message
    /// reach this extension (its parent), which then opens the file in Visual
    /// Studio. The same shape the VS Code extension's webview uses.
    /// </summary>
    public sealed class DockControl : UserControl
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        private readonly WebView2 view;
        private readonly int port;
        private readonly string shellUrl;
        private readonly string origin;

        public DockControl()
        {
            port = QuickDockUrls.ResolvePort();
            shellUrl = port > 0 ? QuickDockUrls.ShellUrl(port) : string.Empty;
            origin = port > 0 ? "http://127.0.0.1:" + port : string.Empty;

            view = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(21, 21, 23)
            };
            Controls.Add(view);
            Load += OnLoaded;
        }

        private async void OnLoaded(object sender, EventArgs e)
        {
            try
            {
                var dataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshDock",
                    "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
                await view.EnsureCoreWebView2Async(environment);

                view.CoreWebView2.Settings.AreDevToolsEnabled = true;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                view.CoreWebView2.WebMessageReceived += OnWebMessage;

                await ProbeAndShowAsync();
            }
            catch (Exception error)
            {
                ShowOffline("WebView2 could not start: " + error.Message);
            }
        }

        /// <summary>Ask the listener whether it is there, then show the shell or the card.</summary>
        private async Task ProbeAndShowAsync()
        {
            if (view.CoreWebView2 == null)
            {
                return;
            }
            if (port <= 0 || string.IsNullOrEmpty(shellUrl))
            {
                ShowOffline("QuickDock's listener is turned off (port 0). Start DSH and enable the listener in DSH → Settings → QuickDock → Connection.");
                return;
            }

            try
            {
                using (var response = await Http.GetAsync(origin + "/quickdock/api/meta"))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        ShowOffline("QuickDock answered " + (int)response.StatusCode + " at " + origin + ".");
                        return;
                    }
                }
            }
            catch (Exception error)
            {
                ShowOffline("QuickDock is not reachable at " + origin + " (" + error.Message + "). Is DSH running with the dsh-quickdock plugin?");
                return;
            }

            view.CoreWebView2.NavigateToString(WrapperHtml());
        }

        private void OpenFile(string raw)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = raw ?? string.Empty;
            var nul = path.IndexOf('\u0000');
            if (nul >= 0)
            {
                path = path.Substring(0, nul);
            }
            path = path.Trim();
            if (path.Length == 0)
            {
                return;
            }

            if (!Path.IsPathRooted(path))
            {
                var directory = QuickDockUrls.SolutionDirectory();
                if (!string.IsNullOrEmpty(directory))
                {
                    path = Path.Combine(directory, path);
                }
            }
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                dte?.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    "Could not open " + path + Environment.NewLine + error.Message,
                    "DSH Dock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }

            if (message.StartsWith("open-file:", StringComparison.Ordinal))
            {
                OpenFile(message.Substring("open-file:".Length));
                return;
            }
            switch (message)
            {
                case "retry":
                    _ = ProbeAndShowAsync();
                    break;
                case "browser":
                    if (!string.IsNullOrEmpty(shellUrl))
                    {
                        System.Diagnostics.Process.Start(new ProcessStartInfo(shellUrl) { UseShellExecute = true });
                    }
                    break;
            }
        }

        private void ShowOffline(string reason)
        {
            if (view.CoreWebView2 == null)
            {
                return;
            }
            view.CoreWebView2.NavigateToString(OfflineHtml(reason));
        }

        /// <summary>
        /// The one-line host page: the shell fills it, and its `open-file` message
        /// (posted to its parent) is forwarded to this extension. The origin check
        /// keeps any other page in the webview from driving the host.
        /// </summary>
        private string WrapperHtml()
        {
            string src = shellUrl.Replace("&", "&amp;").Replace("\"", "&quot;");
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<style>
  html, body { height: 100%; margin: 0; background: #151517; }
  iframe { display: block; width: 100%; height: 100%; border: 0; }
</style>
</head>
<body>
<iframe src=""" + src + @""" allow=""clipboard-read; clipboard-write""></iframe>
<script>
(function () {
  var origin = '" + origin + @"';
  window.addEventListener('message', function (event) {
    if (event.origin !== origin) return;
    var data = event.data || {};
    if (data.type === 'quickdock:open-file' && typeof data.path === 'string' && data.path) {
      try { chrome.webview.postMessage('open-file:' + data.path); } catch (error) { /* ignore */ }
    }
  });
})();
</script>
</body>
</html>";
        }

        private string OfflineHtml(string reason)
        {
            string escaped = reason
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
            string address = port > 0 ? origin : "no listener";
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<style>
  html, body { height: 100%; margin: 0; }
  body {
    background: #151517; color: #dfe1e5;
    font: 13px/1.5 system-ui, 'Segoe UI', Roboto, sans-serif;
    display: flex; align-items: center; justify-content: center;
  }
  main { max-width: 460px; padding: 28px; }
  h1 { font-size: 18px; font-weight: 600; margin: 0 0 8px; }
  p { color: #8b8f96; margin: 0 0 10px; }
  code {
    background: rgba(128,128,128,.18); border-radius: 4px; padding: 1px 5px;
    font-family: ui-monospace, Consolas, monospace; font-size: 12px;
  }
  .row { display: flex; gap: 8px; margin-top: 18px; }
  button {
    height: 28px; padding: 0 14px; border-radius: 4px; border: 1px solid #393b40;
    background: transparent; color: #dfe1e5; font: inherit; cursor: pointer;
  }
  button.primary { background: #4a88c7; border-color: transparent; color: #fff; }
</style>
</head>
<body>
<main>
  <h1>DSH Dock cannot reach QuickDock</h1>
  <p>Tried <code>" + address + @"</code>.</p>
  <p>" + escaped + @"</p>
  <div class=""row"">
    <button class=""primary"" onclick=""chrome.webview.postMessage('retry')"">Retry</button>
    <button onclick=""chrome.webview.postMessage('browser')"">Open in browser</button>
  </div>
</main>
</body>
</html>";
        }
    }
}
