using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace DshDock.VisualStudio
{
    /// <summary>
    /// The editor tools this dock publishes. They run on the UI thread (DTE calls
    /// require it) and are deliberately read-only in this build: the write tools
    /// the other docks have are the next step.
    /// </summary>
    internal static class EditorTools
    {
        private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", "node_modules", "packages", "TestResults"
        };

        public static List<EditorTool> Build()
        {
            return new List<EditorTool>
            {
                new EditorTool
                {
                    Name = "workspace_folders",
                    Description = "The open solution, its directory and its projects.",
                    InputSchema = Schema(),
                    Call = args => WorkspaceFolders()
                },
                new EditorTool
                {
                    Name = "selection",
                    Description = "The active document and the text currently selected in it.",
                    InputSchema = Schema(),
                    Call = args => Selection()
                },
                new EditorTool
                {
                    Name = "read_file",
                    Description = "Read a file, optionally a line range. Relative paths resolve against the solution directory.",
                    InputSchema = Schema(
                        new[] { "path" },
                        ("path", "string", "File path, absolute or relative to the solution."),
                        ("startLine", "integer", "First line to return (1-based)."),
                        ("endLine", "integer", "Last line to return (inclusive).")),
                    Call = args => ReadFile(args)
                },
                new EditorTool
                {
                    Name = "list_files",
                    Description = "List files under the solution (or a directory) matching a glob, skipping build output.",
                    InputSchema = Schema(
                        null,
                        ("path", "string", "Directory to search; defaults to the solution directory."),
                        ("pattern", "string", "Glob such as *.cs; defaults to every file."),
                        ("max", "integer", "Maximum results (default 200).")),
                    Call = args => ListFiles(args)
                },
                new EditorTool
                {
                    Name = "search",
                    Description = "Search the solution's text files for a literal string, returning path, line and the matching line.",
                    InputSchema = Schema(
                        new[] { "query" },
                        ("query", "string", "Text to find."),
                        ("path", "string", "Directory to search; defaults to the solution directory."),
                        ("max", "integer", "Maximum matches (default 50).")),
                    Call = args => Search(args)
                },
                new EditorTool
                {
                    Name = "diagnostics",
                    Description = "Errors and warnings currently in the Error List, with their file and line.",
                    InputSchema = Schema(),
                    Call = args => Diagnostics()
                },
                new EditorTool
                {
                    Name = "symbols",
                    Description = "The outline of the active document (classes, methods, fields) from its code model.",
                    InputSchema = Schema(),
                    Call = args => Symbols()
                },
                new EditorTool
                {
                    Name = "reveal",
                    Description = "Open a file in the editor and move the caret to a line.",
                    InputSchema = Schema(
                        new[] { "path" },
                        ("path", "string", "File path, absolute or relative to the solution."),
                        ("line", "integer", "Line to reveal (1-based).")),
                    Call = args => Reveal(args)
                }
            };
        }

        /* ------------------------------ tools ------------------------------- */

        private static object WorkspaceFolders()
        {
            var dte = Dte();
            var solution = dte.Solution;
            var directory = QuickDockUrls.SolutionDirectory();
            var projects = new List<object>();
            if (!string.IsNullOrEmpty(solution.FullName))
            {
                foreach (Project project in solution.Projects)
                {
                    try
                    {
                        projects.Add(new Dictionary<string, object>
                        {
                            { "name", project.Name },
                            { "path", project.FullName }
                        });
                    }
                    catch (Exception)
                    {
                        // A virtual project has no file.
                    }
                }
            }
            return new Dictionary<string, object>
            {
                { "solution", solution.FullName ?? string.Empty },
                { "folders", directory == null ? new object[0] : new object[] { directory } },
                { "projects", projects }
            };
        }

        private static object Selection()
        {
            var dte = Dte();
            var document = dte.ActiveDocument;
            if (document == null)
            {
                return new Dictionary<string, object> { { "active", false }, { "text", string.Empty } };
            }
            var selection = document.Selection as TextSelection;
            return new Dictionary<string, object>
            {
                { "active", true },
                { "file", document.FullName },
                { "text", selection == null ? string.Empty : selection.Text ?? string.Empty },
                { "line", selection == null ? 0 : selection.CurrentLine },
                { "column", selection == null ? 0 : selection.CurrentColumn }
            };
        }

        private static object ReadFile(Dictionary<string, object> args)
        {
            var path = ResolvePath(Text(args, "path"));
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new InvalidOperationException("file not found: " + Text(args, "path"));
            }
            var lines = File.ReadAllLines(path);
            var start = Math.Max(1, Integer(args, "startLine", 1));
            var end = Math.Min(lines.Length, Integer(args, "endLine", lines.Length));
            if (start > end)
            {
                return new Dictionary<string, object> { { "path", path }, { "content", string.Empty }, { "lines", 0 } };
            }
            var text = string.Join(Environment.NewLine, lines.Skip(start - 1).Take(end - start + 1));
            var truncated = text.Length > 400_000;
            return new Dictionary<string, object>
            {
                { "path", path },
                { "startLine", start },
                { "endLine", end },
                { "lines", end - start + 1 },
                { "truncated", truncated },
                { "content", truncated ? text.Substring(0, 400_000) : text }
            };
        }

        private static object ListFiles(Dictionary<string, object> args)
        {
            var root = ResolvePath(Text(args, "path")) ?? QuickDockUrls.SolutionDirectory();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                throw new InvalidOperationException("directory not found: " + Text(args, "path"));
            }
            var pattern = Text(args, "pattern");
            if (string.IsNullOrEmpty(pattern))
            {
                pattern = "*";
            }
            var max = Math.Max(1, Math.Min(500, Integer(args, "max", 200)));
            var files = new List<object>();
            foreach (var file in EnumerateFiles(root))
            {
                if (!GlobMatch(Path.GetFileName(file), pattern))
                {
                    continue;
                }
                files.Add(file);
                if (files.Count >= max)
                {
                    break;
                }
            }
            return new Dictionary<string, object> { { "root", root }, { "files", files } };
        }

        private static object Search(Dictionary<string, object> args)
        {
            var query = Text(args, "query");
            if (string.IsNullOrEmpty(query))
            {
                throw new InvalidOperationException("query is required");
            }
            var root = ResolvePath(Text(args, "path")) ?? QuickDockUrls.SolutionDirectory();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                throw new InvalidOperationException("directory not found");
            }
            var max = Math.Max(1, Math.Min(200, Integer(args, "max", 50)));
            var matches = new List<object>();
            foreach (var file in EnumerateFiles(root))
            {
                if (matches.Count >= max)
                {
                    break;
                }
                var info = new FileInfo(file);
                if (info.Length > 1024 * 1024)
                {
                    continue;
                }
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (Exception)
                {
                    continue;
                }
                for (var index = 0; index < lines.Length && matches.Count < max; index++)
                {
                    var position = lines[index].IndexOf(query, StringComparison.Ordinal);
                    if (position < 0)
                    {
                        continue;
                    }
                    matches.Add(new Dictionary<string, object>
                    {
                        { "path", file },
                        { "line", index + 1 },
                        { "column", position + 1 },
                        { "text", lines[index].Trim() }
                    });
                }
            }
            return new Dictionary<string, object> { { "query", query }, { "matches", matches } };
        }

        private static object Diagnostics()
        {
            var dte = Dte();
            var items = new List<object>();
            try
            {
                var errorList = dte.ToolWindows.ErrorList;
                var count = errorList.ErrorItems.Count;
                for (var index = 1; index <= count && items.Count < 100; index++)
                {
                    var item = errorList.ErrorItems.Item(index);
                    items.Add(new Dictionary<string, object>
                    {
                        { "description", item.Description },
                        { "file", item.FileName },
                        { "line", item.Line },
                        { "column", item.Column }
                    });
                }
            }
            catch (Exception error)
            {
                return new Dictionary<string, object>
                {
                    { "available", false },
                    { "error", error.Message }
                };
            }
            return new Dictionary<string, object> { { "available", true }, { "items", items } };
        }

        private static object Symbols()
        {
            var dte = Dte();
            var document = dte.ActiveDocument;
            if (document == null)
            {
                throw new InvalidOperationException("no active document");
            }
            FileCodeModel model = null;
            try
            {
                model = document.ProjectItem == null ? null : document.ProjectItem.FileCodeModel;
            }
            catch (Exception)
            {
                model = null;
            }
            if (model == null)
            {
                throw new InvalidOperationException("no code model for " + document.FullName);
            }
            var symbols = new List<object>();
            Collect(model.CodeElements, symbols, 0);
            return new Dictionary<string, object> { { "file", document.FullName }, { "symbols", symbols } };
        }

        private static void Collect(CodeElements elements, List<object> target, int depth)
        {
            if (depth > 6 || target.Count >= 200)
            {
                return;
            }
            foreach (CodeElement element in elements)
            {
                var line = 0;
                try
                {
                    line = element.StartPoint.Line;
                }
                catch (Exception)
                {
                    // Some elements have no text point.
                }
                target.Add(new Dictionary<string, object>
                {
                    { "name", element.Name },
                    { "kind", element.Kind.ToString() },
                    { "line", line }
                });
                try
                {
                    if (element.Children != null && element.Children.Count > 0)
                    {
                        Collect(element.Children, target, depth + 1);
                    }
                }
                catch (Exception)
                {
                    // Not every element exposes children.
                }
            }
        }

        private static object Reveal(Dictionary<string, object> args)
        {
            var path = ResolvePath(Text(args, "path"));
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new InvalidOperationException("file not found: " + Text(args, "path"));
            }
            var dte = Dte();
            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            var line = Integer(args, "line", 0);
            if (line > 0 && dte.ActiveDocument != null && dte.ActiveDocument.Selection is TextSelection selection)
            {
                selection.GotoLine(line, true);
            }
            return new Dictionary<string, object> { { "opened", path }, { "line", line } };
        }

        /* ----------------------------- helpers ------------------------------ */

        private static DTE2 Dte()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte == null)
            {
                throw new InvalidOperationException("Visual Studio automation is not available");
            }
            return dte;
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            if (Path.IsPathRooted(path))
            {
                return path;
            }
            var directory = QuickDockUrls.SolutionDirectory();
            return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, path);
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

        private static bool GlobMatch(string name, string pattern)
        {
            if (pattern == "*" || pattern == "*.*")
            {
                return true;
            }
            var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
            return Regex.IsMatch(name, "^" + escaped + "$", RegexOptions.IgnoreCase);
        }

        private static string Text(Dictionary<string, object> args, string key)
        {
            return args != null && args.ContainsKey(key) && args[key] != null
                ? Convert.ToString(args[key], CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static int Integer(Dictionary<string, object> args, string key, int fallback)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
            {
                return fallback;
            }
            return int.TryParse(Convert.ToString(args[key], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private static Dictionary<string, object> Schema()
        {
            return Schema(null);
        }

        private static Dictionary<string, object> Schema(string[] required, params (string name, string type, string description)[] properties)
        {
            var props = new Dictionary<string, object>();
            foreach (var property in properties)
            {
                props[property.name] = new Dictionary<string, object>
                {
                    { "type", property.type },
                    { "description", property.description }
                };
            }
            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", props }
            };
            if (required != null && required.Length > 0)
            {
                schema["required"] = required;
            }
            return schema;
        }
    }
}
