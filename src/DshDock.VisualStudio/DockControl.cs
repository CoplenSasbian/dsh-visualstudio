using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// The dock: QuickDock's shell page in a WebView2, plus a local card when the
    /// listener cannot be reached.
    ///
    /// This is a WPF control on purpose: a Visual Studio tool window hosts a
    /// FrameworkElement as its content, and a WinForms UserControl would be wrapped
    /// in a ContentControl and never rendered (the first build's blank window).
    ///
    /// The shell page is embedded in a one-line host page rather than loaded as
    /// the document: that is what lets the shell's `quickdock:open-file` message
    /// reach this extension (its parent), which then opens the file in Visual
    /// Studio. The same shape the VS Code extension's webview uses.
    /// </summary>
    public sealed class DockControl : System.Windows.Controls.UserControl, IDisposable
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        private readonly WebView2 view;
        private readonly TextBlock problem;
        private int port;
        private string shellUrl;
        private string origin;
        private bool started;
        private bool offline;
        private DockPairing pairing;
        private readonly System.Web.Script.Serialization.JavaScriptSerializer json =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        public DockControl()
        {
            DockLog.Write("control created");

            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 21, 23));

            var grid = new Grid();
            view = new WebView2 { Visibility = Visibility.Collapsed };
            problem = new TextBlock
            {
                Text = "Starting DSH Dock…",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(223, 225, 229)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
                Visibility = Visibility.Visible
            };
            grid.Children.Add(view);
            grid.Children.Add(problem);
            Content = grid;

            Loaded += (sender, args) => EnsureStarted();
            IsVisibleChanged += (sender, args) => EnsureStarted();
        }

        /// <summary>
        /// Start WebView2 once. A tool window may load the control before it is
        /// shown; either event is a fine trigger, and the first one wins.
        /// </summary>
        private void EnsureStarted()
        {
            if (started)
            {
                return;
            }
            started = true;
            DockLog.Write("ensure started");
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            DockLog.Write("init");
            try
            {
                port = QuickDockUrls.ResolvePort();
                origin = port > 0 ? "http://127.0.0.1:" + port : string.Empty;
                shellUrl = port > 0 ? QuickDockUrls.ShellUrl(port) : string.Empty;
                DockLog.Write("port=" + port + " url=" + shellUrl);

                var dataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshDock",
                    "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
                DockLog.Write("environment ok: " + environment.BrowserVersionString);
                await view.EnsureCoreWebView2Async(environment);
                DockLog.Write("webview ok");

                view.CoreWebView2.Settings.AreDevToolsEnabled = true;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                view.CoreWebView2.WebMessageReceived += OnWebMessage;
                view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                pairing = new DockPairing("0.2.0");
                pairing.StateChanged += OnPairingState;

                await ProbeAndShowAsync();
            }
            catch (Exception error)
            {
                DockLog.Write("FAIL " + error);
                ShowProblem("DSH Dock could not start." + Environment.NewLine + error.Message);
            }
        }

        /// <summary>Ask the listener whether it is there, then show the shell or the card.</summary>
        private async Task ProbeAndShowAsync()
        {
            if (view.CoreWebView2 == null)
            {
                ShowProblem("WebView2 did not initialise.");
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

            DockLog.Write("probe ok; loading shell");
            offline = false;
            problem.Visibility = Visibility.Collapsed;
            view.Visibility = Visibility.Visible;
            view.CoreWebView2.Navigate(shellUrl);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (offline)
            {
                return;
            }
            if (e.IsSuccess)
            {
                // The shell asks for the real state once it is up; our pairing may
                // already be on (this window was reopened), so answer every load.
                PostState();
                return;
            }
            var source = view.Source == null ? string.Empty : view.Source.AbsoluteUri;
            if (source.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
            {
                ShowOffline("QuickDock answered at " + source + " (" + e.WebErrorStatus + ").");
            }
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
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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
            if (message == "retry")
            {
                _ = ProbeAndShowAsync();
                return;
            }
            if (message == "browser")
            {
                if (!string.IsNullOrEmpty(shellUrl))
                {
                    System.Diagnostics.Process.Start(new ProcessStartInfo(shellUrl) { UseShellExecute = true });
                }
                return;
            }

            Dictionary<string, object> data;
            try
            {
                data = json.DeserializeObject(message) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return;
            }
            if (data == null)
            {
                return;
            }

            var type = Value(data, "type");
            switch (type)
            {
                case "quickdock:open-file":
                    OpenFile(Value(data, "path"));
                    break;
                case "quickdock:mcp-toggle":
                    pairing?.Toggle();
                    break;
            }
        }

        private void OnPairingState(bool on, string detail)
        {
            Dispatcher.BeginInvoke(new Action(() => PostState(on, detail)));
        }

        private void PostState()
        {
            PostState(pairing != null && pairing.On, pairing == null ? string.Empty : pairing.Detail);
        }

        private void PostState(bool on, string detail)
        {
            if (view.CoreWebView2 == null)
            {
                return;
            }
            var text = json.Serialize(new Dictionary<string, object>
            {
                { "type", "quickdock:mcp-state" },
                { "on", on },
                { "detail", detail ?? string.Empty }
            });
            try
            {
                view.CoreWebView2.PostWebMessageAsString(text);
            }
            catch (Exception error)
            {
                DockLog.Write("post state failed: " + error.Message);
            }
        }

        private static string Value(Dictionary<string, object> data, string key)
        {
            return data != null && data.ContainsKey(key) && data[key] != null
                ? Convert.ToString(data[key], System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
        }

        public void Dispose()
        {
            pairing?.Dispose();
            pairing = null;
        }

        private void ShowOffline(string reason)
        {
            if (view.CoreWebView2 == null)
            {
                ShowProblem(reason);
                return;
            }
            offline = true;
            problem.Visibility = Visibility.Collapsed;
            view.Visibility = Visibility.Visible;
            view.CoreWebView2.NavigateToString(OfflineHtml(reason));
        }

        private void ShowProblem(string text)
        {
            DockLog.Write("problem: " + text);
            view.Visibility = Visibility.Collapsed;
            problem.Text = text;
            problem.Visibility = Visibility.Visible;
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
