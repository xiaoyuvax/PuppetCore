using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Puppet.Core.Describe;
using Puppet.Core.Extentions;
using Puppet.Core.Usage;
using Wima.Core;
using Wima.Log;
using Wima.Web;

namespace Puppet.Core.Web
{
    /// <summary>
    /// /mcp —— MCP (Model Context Protocol) Streamable HTTP 无状态端点。
    /// 让任何 MCP 客户端（Claude/Cursor/opencode 等）零学习成本接入本框架：
    /// - tools/list 由 describe 动态生成（实例×方法扁平化，含框架合成方法）
    /// - tools/call 转发既有 invoke 路径（鉴权/usage/DialogBroker 安全默认全部继承）
    /// - 另提供 puppet_registry/describe/get/usage/logs 五个固定工具
    /// 仅支持 POST JSON-RPC 2.0（tools-only 场景无需 SSE/会话管理）；GET 返回 405。
    /// 鉴权：仅全局密钥（Bearer），与 /agent/registry 同级。
    /// </summary>
    public static class PuppetMcpHandler
    {
        /// <summary>端点路径</summary>
        public const string PATH = "/mcp";

        private const string ProtocolVersion = "2025-06-18";
        private static readonly Regex Sanitizer = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

        /// <summary>尝试处理 /mcp 请求。返回 true 表示已处理。</summary>
        public static bool TryHandle(ref WebRequest req)
        {
            if (req.Path != PATH) return false;

            PuppetUsage.Track("endpoint", $"{req.Method} {req.Path}");
            if (req.Method != "POST") { req.Response.StatusCode = 405; return true; }

            if (!PuppetKeyVault.AuthorizeGlobal(ExtractKey(req))) { req.Response.SetStatus404(); return true; }

            JObject rpc;
            try { rpc = JObject.Parse(ReadBody(req.InputStream)); }
            catch
            {
                WriteParseError(req);
                return true;
            }

            var id = rpc["id"];
            var method = rpc["method"]?.ToString();

            // 通知（无 id）：202 空体
            if (id == null) { req.Response.StatusCode = 202; return true; }

            switch (method)
            {
                case "initialize":
                    WriteResult(req, id, new
                    {
                        protocolVersion = ProtocolVersion,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "Puppet", version = typeof(PuppetMcpHandler).Assembly.GetName().Version?.ToString() ?? "unknown" }
                    });
                    return true;
                case "ping":
                    WriteResult(req, id, new { });
                    return true;
                case "tools/list":
                    WriteResult(req, id, new { tools = BuildToolList() });
                    return true;
                case "tools/call":
                    HandleToolCall(req, id, rpc["params"]);
                    return true;
                default:
                    WriteError(req, id, -32601, $"Method not found: {method}");
                    return true;
            }
        }

        private static void HandleToolCall(WebRequest req, JToken id, JToken parameters)
        {
            var name = parameters?["name"]?.ToString();
            var args = parameters?["arguments"];
            if (string.IsNullOrEmpty(name)) { WriteError(req, id, -32602, "missing tool name"); return; }

            try
            {
                switch (name)
                {
                    case "puppet_registry":
                        WriteToolText(req, id, ToText(PuppetRegistry.Query(includeInternal: true)));
                        return;
                    case "puppet_describe":
                        {
                            var target = PuppetRegistry.Resolve(args?["instance"]?.ToString());
                            if (target == null) { WriteToolText(req, id, err: "instance not found"); return; }
                            WriteToolText(req, id, target.Describe(DescribeFormat.Json));
                            return;
                        }
                    case "puppet_get":
                        {
                            var target = PuppetRegistry.Resolve(args?["instance"]?.ToString());
                            if (target == null) { WriteToolText(req, id, err: "instance not found"); return; }
                            WriteToolText(req, id, target.GetProperty(args?["path"]?.ToString()));
                            return;
                        }
                    case "puppet_usage":
                        WriteToolText(req, id, ToText(new { ok = true, total = PuppetUsage.Total(), enabled = PuppetUsage.Enabled, counts = PuppetUsage.Counts() }));
                        return;
                    case "puppet_logs":
                        {
                            var loggerName = args?["name"]?.ToString();
                            if (string.IsNullOrEmpty(loggerName))
                            { WriteToolText(req, id, ToText(WimaLogger.LogBook.Keys.OrderBy(k => k).ToArray())); return; }
                            if (!WimaLogger.LogBook.TryGetValue(loggerName, out var logger))
                            { WriteToolText(req, id, err: "logger not found"); return; }
                            var buf = logger.LogBuf ?? "";
                            var maxLen = int.TryParse(args?["maxLen"]?.ToString(), out var m) && m > 0 ? m : 131072;
                            if (buf.Length > maxLen) buf = buf[^maxLen..];
                            WriteToolText(req, id, buf);
                            return;
                        }
                }

                var map = BuildToolMap();
                if (!map.TryGetValue(name, out var pair))
                { WriteToolText(req, id, err: $"unknown tool: {name}"); return; }

                var target2 = PuppetRegistry.Resolve(pair.Instance);
                if (target2 == null) { WriteToolText(req, id, err: "instance not found (may have unregistered)"); return; }

                var invokeArgs = args?["args"] as JArray;
                object[] argObjects = invokeArgs == null
                    ? Array.Empty<object>()
                    : invokeArgs.Cast<object>().ToArray();

                string result;
                using (AppAgent.DialogBroker.Push(null))
                    result = target2.Invoke(pair.Method, argObjects);

                var ok = false;
                try { ok = JObject.Parse(result)["ok"]?.Value<bool>() == true; } catch { }
                WriteToolText(req, id, result, isError: !ok);
            }
            catch (Exception ex)
            {
                WriteToolText(req, id, err: ex.Message, isError: true);
            }
        }

        private static List<object> BuildToolList()
        {
            var tools = new List<object>
            {
                Tool("puppet_registry", "List all registered Puppet instances (name/type/tags).", new()),
                Tool("puppet_describe", "Get capability schema (methods/properties) of one instance.", new { instance = Prop("string", "Instance name from puppet_registry") }),
                Tool("puppet_get", "Read a single property by dotted path (safer than full state dump).", new { instance = Prop("string", "Instance name"), path = Prop("string", "Dotted path, e.g. TotalCount or list.SelectedItem.Text") }),
                Tool("puppet_usage", "Long-term usage counters of framework capabilities.", new()),
                Tool("puppet_logs", "Read a logger buffer by name; omit name to list loggers.", new { name = Prop("string", "Logger name"), maxLen = Prop("integer", "Max chars returned (default 131072)") })
            };
            foreach (var kv in BuildToolMap())
                tools.Add(Tool(kv.Key, kv.Value.Description, new { args = new { type = "array", items = new { }, description = "Positional arguments in documented order; omit for no-arg calls" } }));
            return tools;
        }

        private static Dictionary<string, (string Instance, string Method, string Description)> BuildToolMap()
        {
            var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
            foreach (var instance in PuppetRegistry.AllAlive())
            {
                var instanceName = instance.AgentInstanceName ?? instance.GetType().Name;
                CapabilityDoc doc;
                try { doc = JSN.JsonConvert.DeserializeObject<CapabilityDoc>(instance.Describe(DescribeFormat.Json)); }
                catch { continue; }
                if (doc?.Methods == null) continue;
                foreach (var m in doc.Methods)
                {
                    var parms = m.Parameters == null || m.Parameters.Count == 0
                        ? "no parameters"
                        : "parameters: " + string.Join(", ", m.Parameters.Select(p => $"{ShortType(p.Type)} {p.Name}{(p.IsOptional ? "?" : "")}"));
                    var summary = m.Description ?? m.Summary ?? m.Name;
                    var toolName = ToolName(instanceName, m.Name);
                    if (map.ContainsKey(toolName)) continue;
                    map[toolName] = (instanceName, m.Name, $"{summary} ({parms}) -> {ShortType(m.ReturnType)}. Instance: {instanceName}.");
                }
            }
            return map;
        }

        private static string ToolName(string instance, string method)
        {
            var i = Sanitizer.Replace(instance, "_").Trim('_');
            var m = Sanitizer.Replace(method, "_").Trim('_');
            if (i.Length == 0) i = "inst";
            if (m.Length == 0) m = "m";
            if (i.Length > 40) i = i[..40];
            if (m.Length > 30) m = m[..30];
            return $"{i}_{m}_{Fnv6(instance + "|" + method)}";
        }

        private static string Fnv6(string s)
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return (h & 0xFFFFFF).ToString("x6");
        }

        private static string ShortType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "?";
            var dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName[(dot + 1)..] : fullName;
        }

        private static object Tool(string name, string description, object schema) =>
            new { name, description, inputSchema = schema };

        private static object Prop(string type, string description) => new { type, description };

        private static string ToText(object o) => JSN.JsonConvert.SerializeObject(o);

        private static void WriteResult(WebRequest req, JToken id, object result)
        {
            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = JToken.FromObject(result, JSN.JsonSerializer.CreateDefault())
            };
            WriteJson(req, payload);
        }

        private static void WriteError(WebRequest req, JToken id, int code, string message)
        {
            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            };
            WriteJson(req, payload);
        }

        private static void WriteParseError(WebRequest req)
        {
            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = -32700, ["message"] = "Parse error" }
            };
            WriteJson(req, payload);
        }

        private static void WriteToolText(WebRequest req, JToken id, string text = null, string err = null, bool isError = false)
        {
            var content = text != null
                ? new JArray(new JObject { ["type"] = "text", ["text"] = text })
                : new JArray(new JObject { ["type"] = "text", ["text"] = err ?? "unknown error" });
            if (err != null) isError = true;
            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = new JObject { ["content"] = content, ["isError"] = isError }
            };
            WriteJson(req, payload);
        }

        private static void WriteJson(WebRequest req, JObject payload)
        {
            req.Response.Buffer = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
            req.Response.SetJsonContent();
        }

        private static string ReadBody(System.IO.Stream input)
        {
            if (input == null || !input.CanRead) return "";
            using var reader = new System.IO.StreamReader(input, Encoding.UTF8, false);
            return reader.ReadToEnd();
        }

        private static string ExtractKey(WebRequest req)
        {
            var auth = req.Headers?.Get("Authorization");
            if (string.IsNullOrEmpty(auth)) return null;
            return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth["Bearer ".Length..] : auth;
        }
    }
}
