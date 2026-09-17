using System.Reflection;
using System.Text;
using Newtonsoft.Json;
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
    /// /agent/* 端点处理器（静态类）。
    /// 模式 A：在 WebServerBase 子类 ProcessWebRequest 中调用 TryHandle。
    /// 模式 B/C：由 PuppetWebServer 内部调用。
    /// </summary>
    public static class PuppetWebHandler
    {
        /// <summary>端点前缀</summary>
        public const string PREFIX = "/agent";

        /// <summary>尝试处理 /agent/* 请求。返回 true 表示已处理。</summary>
        public static bool TryHandle(ref WebRequest req)
        {
            if (!req.Path.StartsWith(PREFIX)) return false;

            PuppetUsage.Track("endpoint", $"{req.Method} {req.Path} name={req.Queries?["name"]}");
            try
            {
                return HandleCore(ref req);
            }
            catch (Exception ex)
            {
                PuppetUsage.Error("endpoint", $"{req.Method} {req.Path}", ex);
                req.Response.StatusCode = 500;
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = false, err = ex.Message }));
                req.Response.SetJsonContent();
                return true;
            }
        }

        /// <summary>端点分发核心逻辑（经 TryHandle 包一层使用日志 + 意外上报）</summary>
        private static bool HandleCore(ref WebRequest req)
        {
            // 提取 Agent 协调参数（可选，向后兼容）
            var agentId = req.Queries?["agentId"];
            var purpose = req.Queries?["agentPurpose"];
            var operation = req.Queries?["agentOp"] ?? req.Path;
            var waitEst = ParseInt(req.Queries?["agentWaitEst"], 0);
            var site = req.Queries?["site"];
            var lockModeStr = req.Queries?["lockMode"];
            LockMode lockMode = LockMode.None;
            if (!string.IsNullOrEmpty(lockModeStr))
                Enum.TryParse(lockModeStr, true, out lockMode);

            var key = ExtractKey(req);
            var name = req.Queries?["name"];
            var target = PuppetRegistry.Resolve(name);

            // 自动心跳（所有 /agent/* 调用均更新 AgentBook）
            if (!string.IsNullOrEmpty(agentId))
                AgentBook.Heartbeat(agentId, purpose ?? "", operation, waitEst, name ?? "unknown", site, lockMode);

            // 元端点：条件查询已注册实例（仅全局密钥）
            if (req.Path == "/agent/registry" && req.Method == "GET")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                var result = PuppetRegistry.Query(
                    typeFilter: req.Queries["type"],
                    tagFilter: SplitCsv(req.Queries["tags"]),
                    includeInternal: req.Queries["includeInternal"] == "true",
                    activeWithinMinutes: ParseInt(req.Queries["activeWithin"], 0),
                    keyword: req.Queries["keyword"],
                    offset: ParseInt(req.Queries["offset"], 0),
                    limit: ParseInt(req.Queries["limit"], 0));
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(result));
                req.Response.SetJsonContent();
                return true;
            }

            // 刷新密钥端点（仅全局密钥）
            if (req.Path == "/agent/key/refresh" && req.Method == "POST")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                var newKey = PuppetKeyVault.RefreshKey();
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, newKey }));
                req.Response.SetJsonContent();
                return true;
            }

            // 使用率摘要端点（仅全局密钥）：返回各功能调用次数，供 Agent 做利用率与迭代清理由
            if (req.Path == "/agent/usage" && req.Method == "GET")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                var counts = PuppetUsage.Counts();
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
                {
                    ok = true,
                    total = PuppetUsage.Total(),
                    enabled = PuppetUsage.Enabled,
                    counts
                }));
                req.Response.SetJsonContent();
                return true;
            }

            if (req.Path == "/agent/docs" && req.Method == "GET")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                using var stream = typeof(PuppetWebHandler).Assembly.GetManifestResourceStream("Puppet.Core.DEVELOPMENT.md")
                    ?? throw new InvalidOperationException("Embedded DEVELOPMENT.md was not found.");
                using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                req.Response.Buffer = Encoding.UTF8.GetBytes(reader.ReadToEnd());
                req.Response.Headers.Set("Content-Type", "text/markdown; charset=utf-8");
                return true;
            }

            // PuppetCore 能力端点（仅全局密钥）
            if (req.Path == "/agent/capabilities" && req.Method == "GET")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                req.Response.Buffer = Encoding.UTF8.GetBytes(PuppetCoreCapabilities.ToJson());
                req.Response.SetJsonContent();
                return true;
            }

            // Notes 端点（需实例密钥或全局密钥）
            if (req.Path == "/agent/hints" && req.Method == "GET")
            {
                if (string.IsNullOrEmpty(name))
                {
                    if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                    // 返回所有实例的 notes
                    var allNotes = new List<object>();
                    foreach (var instance in PuppetRegistry.AllAlive())
                    {
                        var notes = ExtractNotes(instance);
                        if (notes.Count > 0)
                            allNotes.Add(new { instance = instance.AgentInstanceName, notes });
                    }
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, instances = allNotes }));
                }
                else
                {
                    if (target != null && !PuppetKeyVault.Authorize(target, key)) { req.Response.SetStatus404(); return true; }
                    else if (target == null && !PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }

                    var notes = target != null ? ExtractNotes(target) : new List<object>();
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, instance = name, notes }));
                }
                req.Response.SetJsonContent();
                return true;
            }

            // AgentBook 端点（仅全局密钥）
            if (req.Path.StartsWith("/agent/book") && PuppetKeyVault.AuthorizeGlobal(key))
                return HandleAgentBook(ref req);

            // SiteLock 端点（仅全局密钥）
            if (req.Path.StartsWith("/agent/lock") && PuppetKeyVault.AuthorizeGlobal(key))
                return HandleSiteLock(ref req);

            // 通用日志端点
            if (req.Path == "/agent/logs" && req.Method == "GET")
            {
                if (string.IsNullOrEmpty(name))
                {
                    if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                    var names = WimaLogger.LogBook.Keys.OrderBy(k => k).ToArray();
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(names));
                }
                else
                {
                    if (target != null && !PuppetKeyVault.Authorize(target, key)) { req.Response.SetStatus404(); return true; }
                    else if (target == null && !PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }

                    if (WimaLogger.LogBook.TryGetValue(name, out var logger))
                    {
                        var buf = logger.LogBuf ?? "";
                        var maxLen = ParseInt(req.Queries["maxLen"], 131072);
                        if (maxLen > 0 && buf.Length > maxLen) buf = buf[^maxLen..];
                        req.Response.Buffer = Encoding.UTF8.GetBytes(buf);
                    }
                    else { req.Response.SetStatus404(); return true; }
                }
                req.Response.SetJsonContent();
                return true;
            }

            // 以下端点需要实例 + 密钥校验
            if (target == null || !PuppetKeyVault.Authorize(target, key))
            { req.Response.SetStatus404(); return true; }

            switch (req.Path)
            {
                case "/agent/describe" when req.Method == "GET":
                    {
                        var fmt = req.Queries["format"] == "md" ? DescribeFormat.Markdown : DescribeFormat.Json;
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.Describe(fmt));
                        break;
                    }
                case "/agent/state" when req.Method == "GET":
                    {
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.GetState(
                            req.Queries["path"],
                            SplitCsv(req.Queries["include"]),
                            SplitCsv(req.Queries["exclude"])));
                        break;
                    }
                case "/agent/get" when req.Method == "GET":
                    {
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.GetProperty(req.Queries["path"]));
                        break;
                    }
                case "/agent/set" when req.Method == "POST":
                    {
                        // Body: { "path": "txtName.Text", "value": <any> }
                        var parsed = ParseSetBody(req.InputStream);
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.SetProperty(parsed.path, parsed.value));
                        break;
                    }
                case "/agent/invoke" when req.Method == "POST":
                    {
                        var args = ParseJsonArgs(req.InputStream);
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.Invoke(req.Queries["method"], args));
                        break;
                    }
                default:
                    req.Response.SetStatus404();
                    return true;
            }

            req.Response.SetJsonContent();
            return true;
        }

        private static bool HandleAgentBook(ref WebRequest req)
        {
            if (req.Path == "/agent/book" && req.Method == "GET")
            {
                var includeStale = req.Queries["includeStale"] == "true";
                var agents = AgentBook.GetAll(includeStale);
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, agents }));
                req.Response.SetJsonContent();
                return true;
            }

            if (req.Path == "/agent/book/heartbeat" && req.Method == "POST")
            {
                var body = ParseJsonBody(req.InputStream);
                var id = body?["id"]?.ToString();
                var purp = body?["purpose"]?.ToString();
                var op = body?["op"]?.ToString();
                var wait = ParseInt(body?["waitEst"]?.ToString(), 0);
                var site = body?["site"]?.ToString();
                var lmStr = body?["lockMode"]?.ToString();
                LockMode lm = LockMode.None;
                if (!string.IsNullOrEmpty(lmStr)) Enum.TryParse(lmStr, true, out lm);

                if (!string.IsNullOrEmpty(id))
                    AgentBook.Heartbeat(id, purp ?? "", op ?? "", wait, "unknown", site, lm);

                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true }));
                req.Response.SetJsonContent();
                return true;
            }

            if (req.Path == "/agent/book/release" && req.Method == "POST")
            {
                var body = ParseJsonBody(req.InputStream);
                var id = body?["id"]?.ToString();
                var waitOthers = body?["waitForOthers"]?.ToString() == "true";
                var timeout = ParseInt(body?["timeoutSec"]?.ToString(), 30);

                if (string.IsNullOrEmpty(id)) { req.Response.StatusCode = 400; return true; }

                bool canShutdown = true;
                if (waitOthers) canShutdown = AgentBook.CanShutdown(id, timeout);
                if (canShutdown) AgentBook.Release(id);

                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, canShutdown }));
                req.Response.SetJsonContent();
                return true;
            }

            return false;
        }

        private static bool HandleSiteLock(ref WebRequest req)
        {
            if (req.Path == "/agent/lock/acquire" && req.Method == "POST")
            {
                var body = ParseJsonBody(req.InputStream);
                var site = body?["site"]?.ToString();
                var modeStr = body?["mode"]?.ToString() ?? "Write";
                var timeout = ParseInt(body?["timeoutMs"]?.ToString(), 5000);
                var agentId = req.Queries?["agentId"] ?? body?["agentId"]?.ToString();

                if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(agentId))
                { req.Response.StatusCode = 400; return true; }

                Enum.TryParse(modeStr, true, out LockMode mode);
                var ok = SiteLock.TryAcquire(site, agentId, mode, timeout);
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok }));
                req.Response.SetJsonContent();
                return true;
            }

            if (req.Path == "/agent/lock/release" && req.Method == "POST")
            {
                var body = ParseJsonBody(req.InputStream);
                var site = body?["site"]?.ToString();
                var agentId = req.Queries?["agentId"] ?? body?["agentId"]?.ToString();

                if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(agentId))
                { req.Response.StatusCode = 400; return true; }

                SiteLock.Release(site, agentId);
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true }));
                req.Response.SetJsonContent();
                return true;
            }

            if (req.Path == "/agent/lock/status" && req.Method == "GET")
            {
                var site = req.Queries["site"];
                if (site == "*" || string.IsNullOrEmpty(site))
                {
                    var all = SiteLock.GetAll();
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, locks = all }));
                }
                else
                {
                    var status = SiteLock.GetStatus(site);
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, @lock = status }));
                }
                req.Response.SetJsonContent();
                return true;
            }

            return false;
        }

        private static JObject ParseJsonBody(Stream input)
        {
            if (input == null || !input.CanRead) return null;
            using var reader = new StreamReader(input, Encoding.UTF8, false);
            var body = reader.ReadToEnd();
            if (string.IsNullOrEmpty(body)) return null;
            try { return JObject.Parse(body); } catch { return null; }
        }

        private static string ExtractKey(WebRequest req)
        {
            var auth = req.Headers?.Get("Authorization");
            if (string.IsNullOrEmpty(auth)) return null;
            return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth["Bearer ".Length..] : auth;
        }

        private static string[] SplitCsv(string s) =>
            string.IsNullOrEmpty(s) ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries);

        private static int ParseInt(string s, int defaultValue) =>
            int.TryParse(s, out var v) ? v : defaultValue;

        private static object[] ParseJsonArgs(Stream input)
        {
            if (input == null || !input.CanRead) return Array.Empty<object>();
            using var reader = new StreamReader(input, Encoding.UTF8, false);
            var body = reader.ReadToEnd();
            if (string.IsNullOrEmpty(body)) return Array.Empty<object>();
            try
            {
                var arr = JArray.Parse(body);
                return arr.Cast<object>().ToArray();
            }
            catch
            {
                try
                {
                    var token = JToken.Parse(body);
                    return new object[] { token };
                }
                catch { return Array.Empty<object>(); }
            }
        }

        private static (string path, object value) ParseSetBody(Stream input)
        {
            if (input == null || !input.CanRead) return (null, null);
            using var reader = new StreamReader(input, Encoding.UTF8, false);
            var body = reader.ReadToEnd();
            if (string.IsNullOrEmpty(body)) return (null, null);
            try
            {
                var obj = JObject.Parse(body);
                var path = obj["path"]?.ToString();
                var value = obj["value"];
                return (path, value);
            }
            catch
            {
                return (null, null);
            }
        }

        private static List<object> ExtractNotes(IPuppet target)
        {
            var type = target.GetType();
            var notes = new List<object>();

            // Type level
            foreach (var n in type.GetCustomAttributes<PuppetHintAttribute>(false))
                notes.Add(new { target = type.FullName, member = "", kind = "Type", n.Category, n.Text, n.AgentId, n.CreatedAt });

            // Methods
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                foreach (var n in m.GetCustomAttributes<PuppetHintAttribute>(false))
                    notes.Add(new { target = type.FullName, member = m.Name, kind = "Method", n.Category, n.Text, n.AgentId, n.CreatedAt });
            }

            // Properties
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var n in p.GetCustomAttributes<PuppetHintAttribute>(false))
                    notes.Add(new { target = type.FullName, member = p.Name, kind = "Property", n.Category, n.Text, n.AgentId, n.CreatedAt });
            }

            // Fields
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var n in f.GetCustomAttributes<PuppetHintAttribute>(false))
                    notes.Add(new { target = type.FullName, member = f.Name, kind = "Field", n.Category, n.Text, n.AgentId, n.CreatedAt });
            }

            // Events
            foreach (var e in type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var n in e.GetCustomAttributes<PuppetHintAttribute>(false))
                    notes.Add(new { target = type.FullName, member = e.Name, kind = "Event", n.Category, n.Text, n.AgentId, n.CreatedAt });
            }

            // Constructors
            foreach (var c in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var n in c.GetCustomAttributes<PuppetHintAttribute>(false))
                    notes.Add(new { target = type.FullName, member = ".ctor", kind = "Constructor", n.Category, n.Text, n.AgentId, n.CreatedAt });
            }

            return notes;
        }
    }
}