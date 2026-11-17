using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// DSH's reported writes, reviewed in Visual Studio.
    ///
    /// QuickDock forwards the filesystem service's observed writes
    /// (`GET /quickdock/api/file-events`); nothing a shell command does reaches
    /// this feed, so a build or a formatter never opens a diff. What remains is
    /// bookkeeping: keep the text each file had before DSH wrote it, skip what a
    /// diff cannot show (binaries, huge files, a buffer the user is typing in),
    /// and open the before/after comparison.
    /// </summary>
    internal sealed class ReviewWatcher : IDisposable
    {
        private const int MaxFileBytes = 512 * 1024;
        private const int MaxCachedFiles = 2_000;
        private const long MaxCachedBytes = 32 * 1024 * 1024;
        private const int PollWaitMs = 15_000;
        private const int RetryMs = 5_000;

        private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", "node_modules", "packages", "TestResults"
        };

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(PollWaitMs + 10_000) };
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        private readonly int port;
        private readonly string[] roots;
        private readonly DateTime startedAt = DateTime.Now;
        private readonly Dictionary<string, string> baseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private long cursor = -1;
        private string noted = string.Empty;
        private bool disposed;

        public ReviewWatcher(int port, string[] roots)
        {
            this.port = port;
            this.roots = roots ?? new string[0];
        }

        public void Start()
        {
            CleanupTemporaryFiles();
            Task.Run((Func<Task>)CacheWorkspaceAsync);
            Task.Run((Func<Task>)FollowAsync);
        }

        public void Dispose()
        {
            disposed = true;
            stop.Cancel();
        }

        /* ------------------------------ baseline ------------------------------ */

        private async Task CacheWorkspaceAsync()
        {
            var files = 0;
            long bytes = 0;
            try
            {
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }
                    foreach (var path in EnumerateFiles(root))
                    {
                        if (stop.IsCancellationRequested || files >= MaxCachedFiles || bytes >= MaxCachedBytes)
                        {
                            break;
                        }
                        var text = await ReadTextAsync(path);
                        if (text == null)
                        {
                            continue;
                        }
                        lock (gate)
                        {
                            if (!baseline.ContainsKey(path))
                            {
                                baseline[path] = text;
                                files += 1;
                                bytes += text.Length;
                            }
                        }
                    }
                }
                DockLog.Write("review: cached " + files + " file(s), " + (bytes / 1024) + " KB, for automatic diffs");
            }
            catch (Exception error)
            {
                DockLog.Write("review: cannot cache the workspace: " + error.Message);
            }
        }

        /* -------------------------------- feed -------------------------------- */

        private async Task FollowAsync()
        {
            var backoff = RetryMs;
            while (!disposed)
            {
                try
                {
                    var since = cursor < 0 ? long.MaxValue : cursor;
                    var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)
                        + "/quickdock/api/file-events?since=" + since.ToString(CultureInfo.InvariantCulture)
                        + "&wait=" + PollWaitMs.ToString(CultureInfo.InvariantCulture);
                    var text = await Http.GetStringAsync(url);
                    if (disposed)
                    {
                        return;
                    }
                    var body = Json.DeserializeObject(text) as Dictionary<string, object>;
                    if (body == null)
                    {
                        continue;
                    }
                    if (cursor < 0)
                    {
                        cursor = Long(body, "seq");
                        DockLog.Write("review: following DSH's reported writes from sequence " + cursor);
                        continue;
                    }
                    var dropped = Long(body, "dropped");
                    if (dropped > 0)
                    {
                        DockLog.Write("review: " + dropped + " write(s) went by while DSH writes were not followed");
                    }
                    if (body.ContainsKey("events") && body["events"] is object[] events)
                    {
                        foreach (var entry in events)
                        {
                            var item = entry as Dictionary<string, object>;
                            if (item == null)
                            {
                                continue;
                            }
                            var kind = Text(item, "kind");
                            var path = Text(item, "path");
                            if (kind == "delete")
                            {
                                lock (gate)
                                {
                                    baseline.Remove(Resolve(path) ?? path);
                                }
                                continue;
                            }
                            await ReviewAsync(path);
                        }
                    }
                    cursor = Long(body, "seq");
                    noted = string.Empty;
                    backoff = RetryMs;
                }
                catch (Exception error)
                {
                    var message = error.Message;
                    if (message != noted)
                    {
                        noted = message;
                        DockLog.Write("review: cannot read DSH writes from QuickDock: " + message);
                    }
                    try
                    {
                        await Task.Delay(backoff, stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    backoff = Math.Min(30_000, backoff * 2);
                }
            }
        }

        /* ------------------------------- review ------------------------------- */

        private async Task ReviewAsync(string reported)
        {
            if (disposed || string.IsNullOrEmpty(reported))
            {
                return;
            }
            var path = Resolve(reported);
            if (path == null || !File.Exists(path))
            {
                return;
            }
            var text = await ReadTextAsync(path);
            if (text == null)
            {
                return;
            }

            string before;
            bool hasBefore;
            lock (gate)
            {
                hasBefore = baseline.TryGetValue(path, out before);
            }
            if (!hasBefore)
            {
                // A file created after this dock opened starts empty; anything
                // older is uncached history and has no honest "before".
                var created = File.GetCreationTime(path);
                if (created < startedAt)
                {
                    DockLog.Write("review: no baseline for " + path + ", not opening a diff");
                    return;
                }
                before = string.Empty;
            }
            if (string.Equals(before, text, StringComparison.Ordinal))
            {
                lock (gate)
                {
                    baseline[path] = text;
                }
                return;
            }

            var opened = await OpenDiffAsync(path, before);
            lock (gate)
            {
                baseline[path] = text;
            }
            DockLog.Write("review: " + path + (opened ? " opened" : " not opened"));
        }

        private async Task<bool> OpenDiffAsync(string path, string before)
        {
            var temp = Path.Combine(
                Path.GetTempPath(),
                "DshDock",
                "review",
                "before-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temp) ?? Path.GetTempPath());
                File.WriteAllText(temp, before ?? string.Empty);

                return ThreadHelper.JoinableTaskFactory.Run(
                    async delegate
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (IsDirty(path))
                        {
                            DockLog.Write("review: " + path + " has unsaved edits; skipping");
                            return false;
                        }
                        var service = Package.GetGlobalService(typeof(SVsDifferenceService)) as IVsDifferenceService;
                        if (service == null)
                        {
                            DockLog.Write("review: no difference service in this Visual Studio");
                            return false;
                        }
                        var name = Path.GetFileName(path);
                        var frame = service.OpenComparisonWindow2(
                            temp,
                            path,
                            name + " (DSH)",
                            "DSH wrote this file",
                            "before",
                            "after",
                            string.Empty,
                            string.Empty,
                            0);
                        frame?.Show();
                        return true;
                    });
            }
            catch (Exception error)
            {
                DockLog.Write("review: cannot open " + path + ": " + error.Message);
                return false;
            }
        }

        private static bool IsDirty(string path)
        {
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                if (dte == null)
                {
                    return false;
                }
                foreach (EnvDTE.Document document in dte.Documents)
                {
                    if (document.FullName == path)
                    {
                        return !document.Saved;
                    }
                }
            }
            catch (Exception)
            {
                // No automation: assume the buffer is clean.
            }
            return false;
        }

        /* ------------------------------- helpers ------------------------------ */

        private string Resolve(string reported)
        {
            if (string.IsNullOrWhiteSpace(reported))
            {
                return null;
            }
            if (Path.IsPathRooted(reported))
            {
                return reported;
            }
            foreach (var root in roots)
            {
                var candidate = Path.Combine(root, reported);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var directory = queue.Dequeue();
                string[] files;
                string[] children;
                try
                {
                    files = Directory.GetFiles(directory);
                    children = Directory.GetDirectories(directory);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var file in files)
                {
                    yield return file;
                }
                foreach (var child in children)
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                    {
                        queue.Enqueue(child);
                    }
                }
            }
        }

        private static async Task<string> ReadTextAsync(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes)
                {
                    return null;
                }
                var text = await Task.Run(() => File.ReadAllText(path));
                return text.IndexOf('\u0000') >= 0 ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void CleanupTemporaryFiles()
        {
            try
            {
                var directory = Path.Combine(Path.GetTempPath(), "DshDock", "review");
                if (!Directory.Exists(directory))
                {
                    return;
                }
                foreach (var file in Directory.GetFiles(directory))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-1))
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception)
                    {
                        // In use; next time.
                    }
                }
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        private static long Long(Dictionary<string, object> body, string key)
        {
            if (body == null || !body.ContainsKey(key) || body[key] == null)
            {
                return 0;
            }
            return long.TryParse(Convert.ToString(body[key], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        private static string Text(Dictionary<string, object> body, string key)
        {
            return body != null && body.ContainsKey(key) && body[key] != null
                ? Convert.ToString(body[key], CultureInfo.InvariantCulture)
                : string.Empty;
        }
    }
}
