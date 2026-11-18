using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private ReviewWatcher review;
        private readonly System.Web.Script.Serialization.JavaScriptSerializer json =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        public DockControl()
        {
            DockLog.Write(
                "=== DshDock 0.2.4 pid="
                + System.Diagnostics.Process.GetCurrentProcess().Id
                + " ui=" + ThreadHelper.CheckAccess());

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
                // A loopback DevTools endpoint so the dock can be inspected live
                // (and so `playwright`/CDP can drive it) without a debugger.
                var environmentOptions = new CoreWebView2EnvironmentOptions("--remote-debugging-port=9222");
                var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder, environmentOptions);
                DockLog.Write("environment ok: " + environment.BrowserVersionString + " devtools=http://127.0.0.1:9222");
                await view.EnsureCoreWebView2Async(environment);
                DockLog.Write("webview ok");

                view.CoreWebView2.Settings.AreDevToolsEnabled = true;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                view.CoreWebView2.WebMessageReceived += OnWebMessage;
                view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                pairing = new DockPairing(
                    "0.3.5",
                    DshDockPackage.Instance == null ? DockPairing.PreferredPort : DshDockPackage.Instance.Options.McpPort);
                pairing.StateChanged += OnPairingState;
                if (DshDockPackage.Instance != null && DshDockPackage.Instance.Options.EditorTools)
                {
                    pairing.Start();
                }
                StartReview();

                // A click bridge inside the DSH app frame: the app's own routing
                // depends on its dock context, which can be missing in a WebView2
                // host; this listens in the frame itself and posts the path
                // straight to the extension. Frames only: the shell is top-level.
                var clickBridge = ClickBridgeScript();
                if (!string.IsNullOrEmpty(clickBridge))
                {
                    await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(clickBridge);
                }

                await ProbeAndShowAsync();
            }
            catch (Exception error)
            {
                DockLog.Write("FAIL " + error);
                ShowProblem("DSH Dock could not start." + Environment.NewLine + error.Message);
            }
        }

        /// <summary>Reload the shell page (tool window toolbar), picking a new port if DSH moved.</summary>
        public void Reload()
        {
            DockLog.Write("reload requested");
            _ = ProbeAndShowAsync();
        }

        /// <summary>Ask the listener whether it is there, then show the shell or the card.</summary>
        private async Task ProbeAndShowAsync()
        {
            if (view.CoreWebView2 == null)
            {
                ShowProblem("WebView2 did not initialise.");
                return;
            }
            // Re-resolve on every probe: a DSH restart gives the app proxy a new
            // port, and Reload must not keep the URL this window booted with.
            port = QuickDockUrls.ResolvePort();
            origin = port > 0 ? "http://127.0.0.1:" + port : string.Empty;
            shellUrl = port > 0 ? QuickDockUrls.ShellUrl(port) : string.Empty;
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
            catch (TaskCanceledException)
            {
                ShowOffline("No answer from " + origin + " within 2 seconds. Is DSH running with the dsh-quickdock plugin, and is its listener on?");
                return;
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
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OpenFile(raw)));
                return;
            }
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

            // A chip may carry file:line; split it only when the file part exists.
            var line = 0;
            var match = System.Text.RegularExpressions.Regex.Match(path, @"^(.*?):(\d+)$");
            if (match.Success && File.Exists(match.Groups[1].Value))
            {
                path = match.Groups[1].Value;
                int.TryParse(match.Groups[2].Value, out line);
            }

            if (!Path.IsPathRooted(path))
            {
                // Same rule as the VS Code / JetBrains docks: try every open root.
                var roots = QuickDockUrls.SearchRoots().ToList();
                var candidates = roots.Select(root => Path.Combine(root, path)).ToList();
                var found = candidates.FirstOrDefault(File.Exists);
                DockLog.Write(
                    "open file: " + path
                    + " candidates=" + string.Join(" | ", candidates)
                    + " found=" + (found ?? "none"));
                if (found == null)
                {
                    return;
                }
                path = found;
            }
            else
            {
                DockLog.Write("open file: " + path + " exists=" + File.Exists(path));
                if (!File.Exists(path))
                {
                    return;
                }
            }
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte != null)
                {
                    dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
                    if (line > 0 && dte.ActiveDocument != null && dte.ActiveDocument.Selection is TextSelection selection)
                    {
                        selection.GotoLine(line, true);
                    }
                    DockLog.Write("open file: via DTE");
                    return;
                }
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
                DockLog.Write("open file: via VsShellUtilities");
            }
            catch (Exception error)
            {
                DockLog.Write("open file failed: " + error);
                MessageBox.Show(
                    "Could not open " + path + Environment.NewLine + error.Message,
                    "DSH Dock",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try
            {
                raw = e.WebMessageAsJson ?? string.Empty;
            }
            catch (Exception)
            {
                raw = "<unavailable>";
            }
            DockLog.Write("web message raw: " + (raw.Length > 200 ? raw.Substring(0, 200) : raw));

            string message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                try
                {
                    var value = json.DeserializeObject(e.WebMessageAsJson);
                    message = value as string ?? e.WebMessageAsJson;
                }
                catch (Exception)
                {
                    DockLog.Write("web message: unreadable (" + e.WebMessageAsJson + ")");
                    return;
                }
            }
            DockLog.Write("web message: " + (message.Length > 160 ? message.Substring(0, 160) : message));

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => HandleMessage(message)));
                return;
            }
            HandleMessage(message);
        }

        private void HandleMessage(string message)
        {
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
            if (message == "start-dsh")
            {
                StartDsh();
                return;
            }
            if (message == "settings")
            {
                DshDockPackage.ShowOptions();
                return;
            }
            if (message == "log")
            {
                OpenLog();
                return;
            }
            if (message == "adopt")
            {
                AdoptPort();
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
                case "quickdock:debug-click":
                    DockLog.Write("debug click: host=" + Value(data, "host") + " path=" + Value(data, "path"));
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

        /// <summary>Ask the dock page to insert the selection into its composer.</summary>
        public void AddToChat(string path, int startLine, int endLine, int startColumn, int endColumn)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view?.CoreWebView2 == null)
            {
                DockLog.Write("chat: webview is not ready");
                return;
            }
            var payload = new Dictionary<string, object>
            {
                ["type"] = "quickdock:add-to-chat",
                ["path"] = path,
                ["line"] = startLine,
                ["endLine"] = endLine,
                ["column"] = startColumn,
                ["endColumn"] = endColumn
            };
            try
            {
                view.CoreWebView2.PostWebMessageAsJson(json.Serialize(payload));
            }
            catch (Exception error)
            {
                DockLog.Write("chat: cannot post the selection: " + error.Message);
            }
        }

        /// <summary>Turn the automatic review on or off without reopening the dock.</summary>
        public void SetReview(bool on)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (on)
            {
                StartReview();
            }
            else
            {
                review?.Dispose();
                review = null;
            }
        }

        private void StartReview()
        {
            if (review != null)
            {
                return;
            }
            if (DshDockPackage.Instance == null || !DshDockPackage.Instance.Options.ReviewEdits)
            {
                return;
            }
            var quickDockPort = QuickDockUrls.ResolvePort();
            if (quickDockPort <= 0)
            {
                return;
            }
            review = new ReviewWatcher(quickDockPort, QuickDockUrls.SearchRoots().ToArray());
            review.Start();
            DockLog.Write("review: following DSH writes on port " + quickDockPort);
        }

        public void Dispose()
        {
            review?.Dispose();
            review = null;
            pairing?.Dispose();
            pairing = null;
        }

        private static string ClickBridgeScript()
        {
            var path = Path.Combine(
                Path.GetDirectoryName(typeof(DockControl).Assembly.Location) ?? string.Empty,
                "click-bridge.js");
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception error)
            {
                DockLog.Write("click bridge unreadable: " + error.Message);
                return string.Empty;
            }
        }

        /// <summary>Start DSH in a console window (the offline card's Start DSH).</summary>
        private void StartDsh()
        {
            try
            {
                DockLog.Write("start-dsh requested");
                System.Diagnostics.Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"DSH\" dsh web")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception error)
            {
                ShowProblem("Could not start DSH: " + error.Message);
            }
        }

        private void OpenLog()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var host = Path.Combine(local, "dsh-quickdock", "host.log");
            var dock = Path.Combine(local, "DshDock", "dock.log");
            var path = File.Exists(host) ? host : dock;
            if (!File.Exists(path))
            {
                ShowOffline("No QuickDock or DSH Dock log exists yet.");
                return;
            }
            System.Diagnostics.Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        /// <summary>Take the port QuickDock's config.json declares, then retry.</summary>
        private void AdoptPort()
        {
            var config = QuickDockUrls.ReadConfigPort();
            var options = DshDockPackage.Instance == null ? null : DshDockPackage.Instance.Options;
            if (!config.HasValue)
            {
                ShowOffline("QuickDock's config.json does not declare a port yet. Set it in DSH → Settings → QuickDock → Connection, or pick the port below.");
                return;
            }
            if (options != null)
            {
                options.Port = config.Value;
                options.SaveSettingsToStorage();
                DockLog.Write("adopted config.json port " + config.Value);
            }
            Reload();
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
            string source = QuickDockUrls.PortSource();
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
  main { max-width: 500px; padding: 28px; }
  h1 { font-size: 18px; font-weight: 600; margin: 0 0 8px; }
  p { color: #8b8f96; margin: 0 0 10px; }
  code {
    background: rgba(128,128,128,.18); border-radius: 4px; padding: 1px 5px;
    font-family: ui-monospace, Consolas, monospace; font-size: 12px;
  }
  .row { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 18px; }
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
  <p>Tried <code>" + address + @"</code> (" + source + @").</p>
  <p>" + escaped + @"</p>
  <div class=""row"">
    <button class=""primary"" onclick=""chrome.webview.postMessage('retry')"">Retry</button>
    <button onclick=""chrome.webview.postMessage('start-dsh')"">Start DSH</button>
    <button onclick=""chrome.webview.postMessage('browser')"">Open in browser</button>
  </div>
  <div class=""row"">
    <button onclick=""chrome.webview.postMessage('settings')"">Connection settings</button>
    <button onclick=""chrome.webview.postMessage('log')"">QuickDock log</button>
    <button onclick=""chrome.webview.postMessage('adopt')"">Use config.json port</button>
  </div>
</main>
</body>
</html>";
        }
    }
}
