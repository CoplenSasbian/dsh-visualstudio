using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.VisualStudio.Shell;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// The editor-tools half: starts the local MCP server, pairs it with QuickDock
    /// for every open workspace folder, and re-pairs on every beat so a DSH restart
    /// does not need a manual reconnect. The namespace is <c>visualstudio</c>, so
    /// DSH mounts the tools as <c>mcp__visualstudio__*</c>.
    /// </summary>
    internal sealed class DockPairing : IDisposable
    {
        public const string Namespace = "visualstudio";
        public const int PreferredPort = 47920;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        private readonly object gate = new object();
        private readonly string version;
        private readonly string token;
        private readonly List<EditorTool> tools;
        private readonly int preferredPort;

        private EditorMcpServer server;
        private Timer timer;
        private bool on;
        private string detail = string.Empty;

        /// <summary>Raised whenever the switch or its detail changes.</summary>
        public event Action<bool, string> StateChanged;

        public DockPairing(string version, int preferredPort)
        {
            this.version = version;
            this.preferredPort = preferredPort > 0 ? preferredPort : PreferredPort;
            token = NewToken();
            tools = EditorTools.Build();
        }

        public bool On
        {
            get { lock (gate) { return on; } }
        }

        public string Detail
        {
            get { lock (gate) { return detail; } }
        }

        public IReadOnlyList<string> ToolNames => tools.Select(tool => tool.Name).ToList();

        public void Toggle()
        {
            if (On)
            {
                Stop();
            }
            else
            {
                Start();
            }
        }

        public void Start()
        {
            lock (gate)
            {
                if (on)
                {
                    return;
                }
                try
                {
                    server = Bind(preferredPort) ?? Bind(0);
                }
                catch (Exception error)
                {
                    detail = "could not start the MCP server: " + error.Message;
                    Raise();
                    return;
                }
                on = true;
                detail = string.Empty;
                timer = new Timer(_ => Beat(), null, 0, 20_000);
            }
            DockLog.Write("pairing: on (" + server.Url + ")");
            Raise();
        }

        public void Stop()
        {
            EditorMcpServer stopped;
            lock (gate)
            {
                if (!on)
                {
                    return;
                }
                on = false;
                detail = string.Empty;
                timer?.Dispose();
                timer = null;
                stopped = server;
                server = null;
            }
            DockLog.Write("pairing: off");
            Task.Run(() =>
            {
                foreach (var workspace in Workspaces())
                {
                    Unregister(workspace);
                }
                stopped?.Dispose();
            });
            Raise();
        }

        public void Dispose()
        {
            Stop();
        }

        private EditorMcpServer Bind(int port)
        {
            try
            {
                return new EditorMcpServer(port, token, version, tools, Invoke);
            }
            catch (System.Net.Sockets.SocketException)
            {
                return null;
            }
        }

        private object Invoke(string name, Dictionary<string, object> arguments)
        {
            return ThreadHelper.JoinableTaskFactory.Run(
                async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var tool = tools.Find(candidate => candidate.Name == name);
                    if (tool == null)
                    {
                        throw new InvalidOperationException("unknown tool: " + name);
                    }
                    return tool.Call(arguments);
                });
        }

        /// <summary>Register every open folder that is not mounted yet.</summary>
        private void Beat()
        {
            EditorMcpServer current;
            lock (gate)
            {
                if (!on || server == null)
                {
                    return;
                }
                current = server;
            }

            var port = ResolvePortOnUi();
            if (port <= 0) 
            {
                Note("QuickDock's listener is off (port 0)");
                return;
            }

            var workspaces = Workspaces();
            if (workspaces.Count == 0)
            {
                Note("no solution directory to scope the editor tools to");
                return;
            }

            foreach (var workspace in workspaces)
            {
                try
                {
                    if (!NeedsRegistration(port, workspace, current.Url))
                    {
                        Note(string.Empty);
                        continue;
                    }
                    var answer = Register(port, workspace, current.Url);
                    if (answer)
                    {
                        Note(string.Empty);
                        DockLog.Write("pairing: registered " + workspace);
                    }
                }
                catch (Exception error)
                {
                    Note(error.Message);
                }
            }
        }

        private bool NeedsRegistration(int port, string workspace, string url)
        {
            var listed = Get(port, "/quickdock/api/mcp-list?workspace=" + Uri.EscapeDataString(workspace));
            if (listed == null)
            {
                return false;
            }
            var root = listed as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("workspaces"))
            {
                return true;
            }
            var entries = root["workspaces"] as object[];
            if (entries == null)
            {
                return true;
            }
            foreach (var entry in entries)
            {
                var item = entry as Dictionary<string, object>;
                if (item == null || !item.ContainsKey("workspace"))
                {
                    continue;
                }
                if (!SamePath(Convert.ToString(item["workspace"], CultureInfo.InvariantCulture), workspace))
                {
                    continue;
                }
                var servers = item.ContainsKey("servers") ? item["servers"] as object[] : null;
                if (servers == null)
                {
                    continue;
                }
                foreach (var candidate in servers)
                {
                    var serverEntry = candidate as Dictionary<string, object>;
                    if (serverEntry == null)
                    {
                        continue;
                    }
                    if (Convert.ToString(serverEntry.ContainsKey("name") ? serverEntry["name"] : null, CultureInfo.InvariantCulture) != Namespace)
                    {
                        continue;
                    }
                    var currentUrl = Convert.ToString(serverEntry.ContainsKey("url") ? serverEntry["url"] : null, CultureInfo.InvariantCulture);
                    return currentUrl != url;
                }
            }
            return true;
        }

        private bool Register(int port, string workspace, string url)
        {
            var body = new Dictionary<string, object>
            {
                { "workspace", workspace },
                { "url", url },
                { "name", Namespace },
                { "headers", new Dictionary<string, string> { { "Authorization", "Bearer " + token } } },
                { "version", version },
                { "tools", tools.Select(tool => tool.Name).ToArray() },
                { "waitMs", 3000 }
            };
            var answer = Post(port, "/quickdock/api/mcp-register", body);
            if (answer == null)
            {
                return false;
            }
            var root = answer as Dictionary<string, object>;
            if (root != null && root.ContainsKey("ok") && root["ok"] is bool ok && !ok)
            {
                Note("QuickDock refused the editor tools: " + Convert.ToString(root.ContainsKey("error") ? root["error"] : "unknown error", CultureInfo.InvariantCulture));
                return false;
            }
            return true;
        }

        private void Unregister(string workspace)
        {
            try
            {
                var port = ResolvePortOnUi();
                if (port > 0)
                {
                    Post(port, "/quickdock/api/mcp-unregister", new Dictionary<string, object>
                    {
                        { "workspace", workspace },
                        { "name", Namespace }
                    });
                }
            }
            catch (Exception error)
            {
                DockLog.Write("pairing: unregister failed: " + error.Message);
            }
        }

        private object Get(int port, string path)
        {
            try
            {
                var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + path;
                var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
                return Json.DeserializeObject(text);
            }
            catch (Exception error)
            {
                DockLog.Write("pairing: " + path + " failed: " + error.Message);
                return null;
            }
        }

        private object Post(int port, string path, object body)
        {
            var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + path;
            var payload = Encoding.UTF8.GetBytes(Json.Serialize(body));
            using (var content = new ByteArrayContent(payload))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                var response = Http.PostAsync(url, content).GetAwaiter().GetResult();
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    DockLog.Write("pairing: " + path + " answered " + (int)response.StatusCode + ": " + text);
                    return null;
                }
                return string.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : Json.DeserializeObject(text);
            }
        }

        private void Note(string message)
        {
            lock (gate)
            {
                if (detail == message)
                {
                    return;
                }
                detail = message;
            }
            if (message.Length > 0)
            {
                DockLog.Write("pairing: " + message);
            }
            Raise();
        }

        private void Raise()
        {
            var handler = StateChanged;
            if (handler != null)
            {
                handler(On, Detail);
            }
        }

        private List<string> Workspaces()
        {
            return ThreadHelper.JoinableTaskFactory.Run(
                async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return QuickDockUrls.WorkspaceFolders().ToList();
                });
        }

        private static int ResolvePortOnUi()
        {
            return ThreadHelper.JoinableTaskFactory.Run(
                async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return QuickDockUrls.ResolvePort();
                });
        }

        private static bool SamePath(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd('\\', '/'),
                    Path.GetFullPath(right).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string NewToken()
        {
            var bytes = new byte[32];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }
    }
}
