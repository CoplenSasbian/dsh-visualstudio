using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DshDock.VisualStudio
{
    /// <summary>One tool published by the editor's MCP server.</summary>
    internal sealed class EditorTool
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> InputSchema { get; set; }
        public Func<Dictionary<string, object>, object> Call { get; set; }
    }

    /// <summary>
    /// The editor-side MCP server: a loopback HTTP endpoint speaking JSON-RPC
    /// (streamable HTTP, plain JSON answers), the same contract the VS Code
    /// extension's `mcp.ts` implements. Tool calls are marshalled to the UI
    /// thread by the callback the server is constructed with.
    /// </summary>
    internal sealed class EditorMcpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private readonly string token;
        private readonly string version;
        private readonly Func<string, Dictionary<string, object>, object> invoke;
        private readonly List<EditorTool> tools;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private volatile bool running = true;
        private int session;

        public int Port { get; private set; }
        public string Url { get; private set; }

        public EditorMcpServer(
            int port,
            string token,
            string version,
            List<EditorTool> tools,
            Func<string, Dictionary<string, object>, object> invoke)
        {
            this.token = token;
            this.version = version;
            this.tools = tools;
            this.invoke = invoke;

            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/mcp";
            thread = new Thread(AcceptLoop) { IsBackground = true, Name = "DshDock MCP" };
            thread.Start();
        }

        public void Dispose()
        {
            running = false;
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!running)
                    {
                        return;
                    }
                    continue;
                }

                ThreadPool.QueueUserWorkItem(
                    state =>
                    {
                        using (var connection = (TcpClient)state)
                        {
                            try
                            {
                                Handle(connection);
                            }
                            catch (Exception error)
                            {
                                DockLog.Write("mcp: request failed: " + error.Message);
                            }
                        }
                    },
                    client);
            }
        }

        private void Handle(TcpClient client)
        {
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 15000;
                stream.WriteTimeout = 15000;
                var request = ReadRequest(stream);
                if (request == null)
                {
                    return;
                }

                if (!string.Equals(request.Value.Authorization, "Bearer " + token, StringComparison.Ordinal))
                {
                    Write(stream, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }

                if (request.Value.Method == "GET" && request.Value.Path == "/health")
                {
                    Write(stream, 200, json.Serialize(new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "version", version },
                        { "tools", tools.Count }
                    }));
                    return;
                }
                if (request.Value.Method == "POST" && request.Value.Path == "/mcp")
                {
                    object answer = Answer(request.Value.Body);
                    if (answer == null)
                    {
                        Write(stream, 202, string.Empty);
                        return;
                    }
                    Write(stream, 200, json.Serialize(answer));
                    return;
                }

                Write(stream, 404, "{\"error\":\"not found\"}");
            }
        }

        private object Answer(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }
            object parsed;
            try
            {
                parsed = json.DeserializeObject(body);
            }
            catch (Exception)
            {
                return Error(null, -32700, "parse error");
            }

            var batch = parsed as object[];
            var messages = batch != null ? batch : new[] { parsed };
            var responses = new List<object>();
            foreach (var message in messages)
            {
                var call = message as Dictionary<string, object>;
                if (call == null)
                {
                    continue;
                }
                var response = AnswerOne(call);
                if (response != null)
                {
                    responses.Add(response);
                }
            }
            if (responses.Count == 0)
            {
                return null;
            }
            return batch != null ? (object)responses.ToArray() : responses[0];
        }

        private object AnswerOne(Dictionary<string, object> call)
        {
            var hasId = call.ContainsKey("id");
            var id = hasId ? call["id"] : null;
            var method = call.ContainsKey("method") ? Convert.ToString(call["method"], CultureInfo.InvariantCulture) : null;

            switch (method)
            {
                case "initialize":
                    session = Environment.TickCount;
                    return new Dictionary<string, object>
                    {
                        { "jsonrpc", "2.0" },
                        { "id", id },
                        {
                            "result", new Dictionary<string, object>
                            {
                                { "protocolVersion", "2025-06-18" },
                                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object> { { "listChanged", false } } } } },
                                {
                                    "serverInfo", new Dictionary<string, object>
                                    {
                                        { "name", "dsh-visualstudio" },
                                        { "version", version }
                                    }
                                },
                                {
                                    "instructions",
                                    "Editor tools from Visual Studio: the open solution and its files, the active selection, diagnostics, a file's symbols, and opening a file at a line."
                                }
                            }
                        }
                    };

                case "ping":
                    return Result(id, new Dictionary<string, object>());

                case "tools/list":
                {
                    var list = new List<object>();
                    foreach (var tool in tools)
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            { "name", tool.Name },
                            { "description", tool.Description },
                            { "inputSchema", tool.InputSchema }
                        });
                    }
                    return Result(id, new Dictionary<string, object> { { "tools", list } });
                }

                case "tools/call":
                {
                    var parameters = call.ContainsKey("params") ? call["params"] as Dictionary<string, object> : null;
                    var name = parameters != null && parameters.ContainsKey("name")
                        ? Convert.ToString(parameters["name"], CultureInfo.InvariantCulture)
                        : string.Empty;
                    var tool = tools.Find(candidate => candidate.Name == name);
                    if (tool == null)
                    {
                        return Error(id, -32602, "unknown tool: " + name);
                    }
                    var arguments = parameters != null && parameters.ContainsKey("arguments")
                        ? parameters["arguments"] as Dictionary<string, object>
                        : null;
                    try
                    {
                        var value = invoke(name, arguments ?? new Dictionary<string, object>());
                        var text = value is string ? (string)value : json.Serialize(value);
                        return Result(id, new Dictionary<string, object>
                        {
                            {
                                "content", new object[]
                                {
                                    new Dictionary<string, object> { { "type", "text" }, { "text", text } }
                                }
                            }
                        });
                    }
                    catch (Exception error)
                    {
                        var message = error.InnerException != null ? error.InnerException.Message : error.Message;
                        return Result(id, new Dictionary<string, object>
                        {
                            {
                                "content", new object[]
                                {
                                    new Dictionary<string, object> { { "type", "text" }, { "text", "error: " + message } }
                                }
                            },
                            { "isError", true }
                        });
                    }
                }

                default:
                    if (!hasId)
                    {
                        return null;
                    }
                    return Error(id, -32601, "method not found: " + method);
            }
        }

        private static object Result(object id, object result)
        {
            return new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } };
        }

        private static object Error(object id, int code, string message)
        {
            return new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
            };
        }

        private struct Request
        {
            public string Method;
            public string Path;
            public string Authorization;
            public string Body;
        }

        private static Request? ReadRequest(Stream stream)
        {
            var buffer = new MemoryStream();
            var one = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = stream.Read(one, 0, one.Length);
                if (read <= 0)
                {
                    return null;
                }
                buffer.Write(one, 0, read);
                var bytes = buffer.GetBuffer();
                headerEnd = IndexOf(bytes, (int)buffer.Length, Encoding.ASCII.GetBytes("\r\n\r\n"));
                if (buffer.Length > 64 * 1024)
                {
                    return null;
                }
            }

            var head = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }
            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < lines.Length; index++)
            {
                var separator = lines[index].IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }
                headers[lines[index].Substring(0, separator).Trim()] = lines[index].Substring(separator + 1).Trim();
            }

            var length = 0;
            if (headers.ContainsKey("Content-Length"))
            {
                int.TryParse(headers["Content-Length"], NumberStyles.Integer, CultureInfo.InvariantCulture, out length);
            }
            var bodyStart = headerEnd + 4;
            var available = (int)buffer.Length - bodyStart;
            while (available < length)
            {
                var read = stream.Read(one, 0, Math.Min(one.Length, length - available));
                if (read <= 0)
                {
                    break;
                }
                buffer.Write(one, 0, read);
                available += read;
            }

            var body = available > 0
                ? Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, Math.Min(available, length))
                : string.Empty;

            return new Request
            {
                Method = parts[0],
                Path = parts[1],
                Authorization = headers.ContainsKey("Authorization") ? headers["Authorization"] : string.Empty,
                Body = body
            };
        }

        private void Write(Stream stream, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(status == 200 ? " OK" : status == 202 ? " Accepted" : status == 401 ? " Unauthorized" : status == 404 ? " Not Found" : "")
                .Append("\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\n")
                .Append("Content-Length: ").Append(bytes.Length).Append("\r\nConnection: close\r\n\r\n");
            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (bytes.Length > 0)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static int IndexOf(byte[] haystack, int count, byte[] needle)
        {
            for (var index = 0; index <= count - needle.Length; index++)
            {
                var match = true;
                for (var offset = 0; offset < needle.Length; offset++)
                {
                    if (haystack[index + offset] != needle[offset])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return index;
                }
            }
            return -1;
        }
    }
}
