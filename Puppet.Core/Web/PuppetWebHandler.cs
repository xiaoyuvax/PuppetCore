using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Puppet.Core.AppAgent;
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

        /// <summary>已知路由清单（未知路由 404 时回列，省去"是不是路由写错了？"的排查往返）</summary>
        private static readonly string[] KnownRoutes =
        {
            "/mcp",
            "GET  /agent/registry", "GET  /agent/capabilities", "GET  /agent/docs", "GET  /agent/usage",
            "GET  /agent/hints", "GET  /agent/logs", "GET  /agent/describe", "GET  /agent/state",
            "GET  /agent/get", "POST /agent/set", "POST /agent/invoke", "POST /agent/key/refresh",
            "GET  /agent/chat", "GET/POST /agent/book*", "GET/POST /agent/lock*", "/appagent/*"
        };

        /// <summary>尝试处理 /agent/* 与 /mcp 请求。返回 true 表示已处理。</summary>
        public static bool TryHandle(ref WebRequest req)
        {
            if (req.Path == PuppetMcpHandler.PATH)
            {
                try
                {
                    var mcpHandled = PuppetMcpHandler.TryHandle(ref req);
                    if (mcpHandled) EnsureUtf8JsonContentType(ref req);
                    return mcpHandled;
                }
                catch (Exception ex)
                {
                    PuppetUsage.Error("mcp", $"{req.Method} {req.Path}", ex);
                    req.Response.StatusCode = 500;
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = false, err = ex.Message }));
                    req.Response.SetJsonContent();
                    EnsureUtf8JsonContentType(ref req);
                    return true;
                }
            }

            if (!req.Path.StartsWith(PREFIX)) return false;

            PuppetUsage.Track("endpoint", $"{req.Method} {req.Path} name={req.Queries?["name"]}");
            bool handled;
            try
            {
                handled = HandleCore(ref req);
            }
            catch (Exception ex)
            {
                PuppetUsage.Error("endpoint", $"{req.Method} {req.Path}", ex);
                req.Response.StatusCode = 500;
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = false, err = ex.Message }));
                req.Response.SetJsonContent();
                EnsureUtf8JsonContentType(ref req);
                return true;
            }

            if (handled)
            {
                FinishAgentCall(ref req);
                EnsureUtf8JsonContentType(ref req);
            }
            return handled;
        }

        /// <summary>端点分发核心逻辑（经 TryHandle 包一层使用日志 + 意外上报）</summary>
        private static bool HandleCore(ref WebRequest req)
        {
            var key = ExtractKey(req);
            var name = req.Queries?["name"];
            var target = PuppetRegistry.Resolve(name);

            // 自动心跳（所有 /agent/* 调用均更新 AgentBook）
            NoteAgentCall(ref req, name);

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

            // 对话历史（仅全局密钥）
            if (req.Path == "/agent/chat" && req.Method == "GET" && PuppetKeyVault.AuthorizeGlobal(key))
                return HandleChat(ref req);

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

            // 以下端点需要实例 + 密钥校验。
            // 与 B 面 AppAgentWebHandler 统一错误信封：名缺/名错 → 400/404 + availableInstances（Agent 据此直接选名，无需猜）；
            // 仅密钥错保留裸 404（隐藏实例存在性，沿用旧语义）。实例名属敏感信息，只有全局密钥通过后才回列可用实例。
            // 只有这几个路由需要实例；未知路由交给下面 default 返回 unknown-route，避免被"实例错误"劫持。
            var needsInstance = req.Path is "/agent/describe" or "/agent/state" or "/agent/get" or "/agent/set" or "/agent/invoke";
            if (needsInstance && target == null)
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }

                var available = PuppetRegistry.Query(includeInternal: true).Instances
                    .Select(i => i.InstanceName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

                if (string.IsNullOrEmpty(name) && available.Length == 1)
                    target = PuppetRegistry.Resolve(available[0]); // 单实例默认（与 B 面一致）

                if (target == null)
                {
                    if (string.IsNullOrEmpty(name) && available.Length > 1)
                        AppAgentWebHandler.WriteError(req, 400, "instance-name-required",
                            "multiple puppet instances are bound; ?name= is required",
                            "pass ?name=<instance> from availableInstances; omit ?name= only when a single instance is bound",
                            availableInstances: available);
                    else
                        AppAgentWebHandler.WriteError(req, 404, "instance-not-found",
                            string.IsNullOrEmpty(name)
                                ? "no puppet instance is currently bound"
                                : $"instance '{name}' is not bound (closed or not yet created)",
                            available.Length == 0
                                ? "no instances are bound yet; wait for one to open, then retry"
                                : "use ?name= from availableInstances; omit ?name= only when a single instance is bound",
                            availableInstances: available);
                    return true;
                }
            }
            if (needsInstance && !PuppetKeyVault.Authorize(target, key))
            { req.Response.SetStatus404(); return true; }

            // 模态框代理（A 通道，2026-09-28）：与 B 通道 AppAgentExecutor 同语义——agent 触发的
            // PuppetDialog.Ask 按安全默认（Cancel/No）自动应答，永不阻塞 UI/Web 线程。
            // 旧根因：A 通道无 DialogBroker 上下文，宿主代码里的原生模态会挂死调用线程
            // （VDH SaveV3dDocument 弹 OK 框挂 HTTP 线程、TE 退出菜单确认框挂死——均此根因）。
            // AsyncLocal 作用域随 RunOnUi 的 UI 线程编组流动（Invoke/Set/control 均经此路径），
            // 请求结束即还原；人工界面操作不走 /agent/*，原生模态行为不变。
            using (DialogBroker.Push(null))
            switch (req.Path)
            {
                case "/agent/describe" when req.Method == "GET":
                    {
                        var fmt = req.Queries["format"] == "md" ? DescribeFormat.Markdown : DescribeFormat.Json;
                        // own=true：只列本类型自身成员，滤掉 WinForms 基类继承属性/字段（大窗口体积可省一个量级）
                        var own = req.Queries["own"] is "1" or "true" or "own";
                        // values=1：属性附带当前值（仅简单类型），一次拿到"有哪些状态 + 现值"
                        var values = req.Queries["values"] is "1" or "true" or "values";
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.Describe(fmt, req.Queries["section"], own, values));
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
                        // 批量：paths=A,B,C（逗号分隔，一次往返取多个属性）；未给 paths 维持单 path 旧行为
                        var paths = SplitCsv(req.Queries["paths"]);
                        if (paths is not { Length: > 0 } && string.IsNullOrEmpty(req.Queries["path"]))
                        {
                            req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
                            {
                                ok = false,
                                code = "path-required",
                                err = "path is empty",
                                hint = "GET /agent/get 需 ?path=<属性路径>（点分，如 FileMan.CurrentScene）；批量用 ?paths=A,B；取整实例状态用 /agent/state",
                            }));
                            req.Response.SetJsonContent();
                            break;
                        }
                        if (paths is { Length: > 0 })
                        {
                            var results = new JArray(paths.Select(p => JToken.Parse(target.GetProperty(p))));
                            var batch = new JObject { ["ok"] = true, ["results"] = results };
                            req.Response.Buffer = Encoding.UTF8.GetBytes(batch.ToString(Formatting.None));
                            break;
                        }
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.GetProperty(req.Queries["path"]));
                        break;
                    }
                case "/agent/set" when req.Method == "POST":
                    {
                        // Body: { "path": "txtName.Text", "value": <any> }
                        var parsed = ParseSetBody(req.InputStream);
                        if (string.IsNullOrEmpty(parsed.path))
                        {
                            req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
                            {
                                ok = false,
                                code = "path-required",
                                err = "path is empty",
                                hint = "POST /agent/set body 需 {\"path\":\"<属性路径>\",\"value\":<任意>}，如 {\"path\":\"txtName.Text\",\"value\":\"hi\"}",
                            }));
                            req.Response.SetJsonContent();
                            break;
                        }
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
                    req.Response.StatusCode = 404;
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
                    {
                        ok = false,
                        code = "unknown-route",
                        err = $"no handler for {req.Method} {req.Path}",
                        hint = "对照 routes 列表核对路径与 method；describe/state/get 需 ?name=<instance>",
                        routes = KnownRoutes
                    }));
                    req.Response.SetJsonContent();
                    return true;
            }

            req.Response.SetJsonContent();
            return true;
        }

        /// <summary>统一解析 agentId/agentPurpose/agentOp/agentWaitEst/site/lockMode/agentNote/agentHelp 并写 AgentBook 心跳。
        /// A 面（/agent/*）与 B 面（/appagent/*）共用；B 面未显式给 site 时按动作/状态名推断站点。</summary>
        internal static void NoteAgentCall(ref WebRequest req, string instanceName)
        {
            var agentId = req.Queries?["agentId"];
            if (string.IsNullOrEmpty(agentId)) return;

            var site = req.Queries?["site"];
            if (string.IsNullOrEmpty(site))
            {
                var p = req.PathCaseSensitive ?? req.Path ?? "";
                const string actions = "/appagent/actions/";
                const string state = "/appagent/state/";
                if (p.StartsWith(actions, StringComparison.OrdinalIgnoreCase)) site = p[actions.Length..];
                else if (p.StartsWith(state, StringComparison.OrdinalIgnoreCase)) site = p[state.Length..];
            }

            LockMode lockMode = LockMode.None;
            var lockModeStr = req.Queries?["lockMode"];
            if (!string.IsNullOrEmpty(lockModeStr)) Enum.TryParse(lockModeStr, true, out lockMode);

            var operation = req.Queries?["agentOp"];
            if (string.IsNullOrEmpty(operation))
                operation = IsCoordinationPath(req.Path) ? null : req.Path;

            AgentBook.Heartbeat(agentId, req.Queries?["agentPurpose"] ?? "",
                operation, ParseInt(req.Queries?["agentWaitEst"], 0),
                instanceName ?? "unknown", site, lockMode, req.Queries?["agentNote"],
                req.Queries?["agentSay"] ?? req.Queries?["agentHelp"],
                req.Queries?["agentSayTo"] ?? req.Queries?["agentHelpTo"] ?? req.Queries?["helpTo"]);
        }

        /// <summary>只读协调/轮询端点：未显式提供 agentOp 时保留上一次真实操作，避免 doing 被轮询覆盖。</summary>
        private static bool IsCoordinationPath(string path) => path is
            "/agent/book" or "/agent/book/heartbeat" or "/agent/book/release" or
            "/agent/chat" or
            "/agent/lock/status" or "/agent/registry" or "/agent/usage" or "/agent/hints" or
            "/agent/logs" or "/agent/capabilities" or "/agent/probe" or
            "/appagent/probe" or "/appagent/manifest";

        /// <summary>JSON 响应补 charset=utf-8，避免客户端按 Latin-1 解码中文。</summary>
        internal static void EnsureUtf8JsonContentType(ref WebRequest req)
        {
            var contentType = req.Response.Headers?.Get("Content-Type");
            if (string.IsNullOrEmpty(contentType)) return;
            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                && contentType.IndexOf("charset", StringComparison.OrdinalIgnoreCase) < 0)
                req.Response.Headers.Set("Content-Type", contentType + "; charset=utf-8");
        }

        /// <summary>响应收尾：附加在场同伴与对话概况，并处理 agentDone（本次即最后一次调用）。</summary>
        internal static void FinishAgentCall(ref WebRequest req)
        {
            AnnotatePresence(ref req);
            var selfId = req.Queries?["agentId"];
            if (!string.IsNullOrEmpty(selfId) && req.Queries?["agentDone"] is "1" or "true")
                AgentBook.Release(selfId);
        }

        /// <summary>同伴提示文案（附在每个含同伴的回复里，告诉 Agent 下一步查什么）。</summary>
        private const string PeerHint =
            "Other agents are active. Use idleSeconds to judge liveness (large = possibly dropped). " +
            "Coordinating APIs: GET /agent/book (full sessions + notes), GET /agent/lock/status?site=* (locks), " +
            "POST /agent/lock/acquire {site,mode} before touching a shared site. " +
            "Talk to them: add ?agentSay=<your message> to any call; add ?agentSayTo=<agentId> to target one peer, or omit to broadcast. " +
            "Peers see it as lastMessage and the shared log at /agent/chat. " +
            "When done: call any endpoint with agentDone=true (or POST /agent/book/release {id}) so others stop seeing you.";

        /// <summary>对话概况提示（随每个响应返回，指向查询端点）。</summary>
        private const string ChatHint =
            "Conversation is kept in memory for this app run. GET /agent/chat?tail=20 reads the last 20; " +
            "filter with &q=<keyword>, &since=<yyyy-MM-dd HH:mm:ss>, &until=<...>, &from=<agentId>, &to=<agentId>; tail=0 returns all. " +
            "Send with agentSay=<text> (alias agentHelp), optionally agentSayTo=<agentId>.";

        /// <summary>单个同伴视图：数值字段名自解释（idleSeconds/estimatedWaitSeconds），状态语义用自然语言表达。</summary>
        private sealed record PeerView(string Id, string Instance, string Site, string Doing, string Purpose,
            int IdleSeconds, string LastActive, int EstimatedWaitSeconds, bool ShuttingDown,
            string Lock, string Note, string LastMessage, DateTime? LastMessageAt, string MessageTo, string Summary);

        private static PeerView ToPeerView(AgentInfo a, DateTime now)
        {
            var idle = (int)Math.Max(0, (now - a.LastHeartbeatAt).TotalSeconds);
            var lockText = AgentBook.DescribeReportedLock(a.CurrentLockMode);
            var note = string.IsNullOrEmpty(a.Note) ? null : a.Note;
            var message = string.IsNullOrEmpty(a.LastMessage) ? null : a.LastMessage;
            var messageTo = string.IsNullOrEmpty(a.MessageTo) ? null : a.MessageTo;
            var shuttingDown = a.Status == AgentStatus.ShuttingDown;

            var parts = new List<string> { $"{a.Id} is on instance {a.PuppetInstanceName}" };
            if (!string.IsNullOrEmpty(a.CurrentSite)) parts.Add($"at site {a.CurrentSite}");
            if (!string.IsNullOrEmpty(a.Operation)) parts.Add($"doing {a.Operation}");
            if (!string.IsNullOrEmpty(a.Purpose)) parts.Add($"for {a.Purpose}");
            parts.Add($"last called {idle} seconds ago");
            if (a.EstimatedWaitSeconds > 0) parts.Add($"expects about {a.EstimatedWaitSeconds} more seconds");
            parts.Add(lockText);
            if (shuttingDown) parts.Add("is shutting down");
            if (note != null) parts.Add($"note: {note}");
            if (message != null) parts.Add(messageTo != null ? $"last said to {messageTo}: {message}" : $"last said: {message}");

            return new PeerView(a.Id, a.PuppetInstanceName, a.CurrentSite, a.Operation, a.Purpose,
                idle, a.LastHeartbeatAt.ToString("yyyy-MM-dd HH:mm:ss"), a.EstimatedWaitSeconds, shuttingDown,
                lockText, note, message, a.LastMessageAt, messageTo, string.Join("; ", parts));
        }

        /// <summary>对话概况（最后一条 + 计数/时间概况），供各响应与 /agent/book 复用。</summary>
        private static JObject ToChatJson(ChatOverview chat) => chat.Last == null ? null : new JObject
        {
            ["messages"] = chat.Messages,
            ["agents"] = chat.Agents,
            ["firstAt"] = chat.FirstAt,
            ["lastAt"] = chat.LastAt,
            ["last"] = new JObject
            {
                ["seq"] = chat.Last.Seq,
                ["agentId"] = chat.Last.AgentId,
                ["text"] = chat.Last.Text,
                ["to"] = chat.Last.To,
                ["at"] = chat.Last.At
            },
            ["summary"] = chat.Summary
        };

        /// <summary>把在场同伴（排除自己）与对话概况附加到响应；两者都没有则不动响应体。</summary>
        internal static void AnnotatePresence(ref WebRequest req)
        {
            if (req.Response.StatusCode >= 400) return;   // 错误/未认证响应不披露其他会话
            if (req.Path == "/agent/chat") return;         // 查询端点自带完整历史
            var ct = req.Response.Headers?.Get("Content-Type") ?? "";
            if (ct.Length > 0 && !ct.Contains("json") && !ct.Contains("text") && !ct.Contains("markdown"))
                return;                                    // 二进制产物（如 /appagent/assets/*）绝不改写

            var selfId = req.Queries?["agentId"];
            var now = DateTime.Now;
            var peers = AgentBook.GetAll()
                .Where(a => a.Id != selfId)
                .OrderByDescending(a => a.LastHeartbeatAt)
                .Select(a => ToPeerView(a, now))
                .ToArray();
            var chat = AgentBook.GetChatOverview();
            if (peers.Length == 0 && chat.Last == null) return;
            if (req.Response.Buffer is not { Length: > 0 } raw) return;

            var hint = string.IsNullOrEmpty(selfId)
                ? "You are not registered (no agentId): add ?agentId=<unique-id>&site=<your-site>&agentNote=<your-note> to your calls so other agents can see you. " + PeerHint
                : PeerHint;
            var text = Encoding.UTF8.GetString(raw);
            var trimmed = text.TrimStart();
            // JSON 对象：加 peers / chat 字段（保持原形状）
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    var obj = JObject.Parse(text);
                    if (peers.Length > 0)
                    {
                        obj["peers"] = new JArray(peers.Select(p => new JObject
                        {
                            ["id"] = p.Id,
                            ["instance"] = p.Instance,
                            ["site"] = p.Site,
                            ["doing"] = p.Doing,
                            ["purpose"] = p.Purpose,
                            ["idleSeconds"] = p.IdleSeconds,
                            ["lastActive"] = p.LastActive,
                            ["estimatedWaitSeconds"] = p.EstimatedWaitSeconds,
                            ["shuttingDown"] = p.ShuttingDown,
                            ["lock"] = p.Lock,
                            ["note"] = p.Note,
                            ["lastMessage"] = p.LastMessage,
                            ["lastMessageAt"] = p.LastMessageAt,
                            ["messageTo"] = p.MessageTo,
                            ["summary"] = p.Summary,
                            // 旧字段保留以兼容旧调用方，均等价于 lastMessage
                            ["seeksHelp"] = p.LastMessage,
                            ["helpUpdatedAt"] = p.LastMessageAt,
                            ["helpTo"] = p.MessageTo
                        }));
                        obj["peersHint"] = hint;
                    }
                    if (chat.Last != null)
                    {
                        obj["chat"] = ToChatJson(chat);
                        obj["chatHint"] = ChatHint;
                    }
                    req.Response.Buffer = Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
                    return;
                }
                catch { /* 非合法 JSON，走文本追加 */ }
            }
            // 顶层数组契约（如 /agent/logs 列表）不破坏
            if (trimmed.StartsWith("[", StringComparison.Ordinal)) return;

            // Markdown / 纯文本响应：追加可读区块（describe?format=md、/agent/docs、/agent/logs）
            var sb = new StringBuilder(text);
            if (peers.Length > 0)
            {
                sb.AppendLine().AppendLine().AppendLine("---").AppendLine("### Other agents active on this puppet");
                foreach (var p in peers)
                    sb.Append("- ").AppendLine(p.Summary);
                sb.AppendLine().Append(hint);
            }
            if (chat.Last != null)
            {
                sb.AppendLine().AppendLine("---").AppendLine("### Conversation").AppendLine(chat.Summary);
                sb.Append("- last from ").Append(chat.Last.AgentId).Append(": ").AppendLine(chat.Last.Text);
                sb.AppendLine(ChatHint);
            }
            req.Response.Buffer = Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static bool HandleAgentBook(ref WebRequest req)
        {
            if (req.Path == "/agent/book" && req.Method == "GET")
            {
                var includeStale = req.Queries["includeStale"] == "true";
                var now = DateTime.Now;
                var agents = AgentBook.GetAll(includeStale).Select(a => new
                {
                    a.Id, a.Purpose, a.Operation, a.PuppetInstanceName, a.CurrentSite,
                    idleSeconds = (int)Math.Max(0, (now - a.LastHeartbeatAt).TotalSeconds),
                    lastActive = a.LastHeartbeatAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    a.EstimatedWaitSeconds,
                    shuttingDown = a.Status == AgentStatus.ShuttingDown,
                    @lock = AgentBook.DescribeReportedLock(a.CurrentLockMode),
                    a.Note,
                    lastMessage = string.IsNullOrEmpty(a.LastMessage) ? null : a.LastMessage,
                    lastMessageAt = a.LastMessageAt,
                    messageTo = string.IsNullOrEmpty(a.MessageTo) ? null : a.MessageTo,
                    // 旧字段保留以兼容既有调用方，等价于 lastMessage
                    seeksHelp = string.IsNullOrEmpty(a.LastMessage) ? null : a.LastMessage,
                    helpUpdatedAt = a.LastMessageAt,
                    helpTo = string.IsNullOrEmpty(a.MessageTo) ? null : a.MessageTo,
                    // 旧枚举字段保留以兼容既有调用方；新调用方请使用上面的自解释字段
                    a.Status, a.CurrentLockMode, a.RegisteredAt, a.LastHeartbeatAt
                });
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
                {
                    ok = true,
                    agents,
                    chat = ToChatJson(AgentBook.GetChatOverview()),
                    chatHint = ChatHint
                }));
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
                var note = body?["note"]?.ToString();
                var message = body?["say"]?.ToString() ?? body?["message"]?.ToString() ?? body?["help"]?.ToString();
                var messageTo = body?["sayTo"]?.ToString() ?? body?["messageTo"]?.ToString() ?? body?["helpTo"]?.ToString() ?? body?["agentHelpTo"]?.ToString();
                var lmStr = body?["lockMode"]?.ToString();
                LockMode lm = LockMode.None;
                if (!string.IsNullOrEmpty(lmStr)) Enum.TryParse(lmStr, true, out lm);

                if (!string.IsNullOrEmpty(id))
                {
                    req.Queries["agentId"] = id;
                    AgentBook.Heartbeat(id, purp, op, wait, "unknown", site, lm, note, message, messageTo);
                }

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

                req.Queries["agentId"] = id;
                bool canShutdown = true;
                if (waitOthers) canShutdown = AgentBook.CanShutdown(id, timeout);
                if (canShutdown) AgentBook.Release(id);

                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, canShutdown }));
                req.Response.SetJsonContent();
                return true;
            }

            return false;
        }

        /// <summary>对话历史查询：tail / since / until / q / from / to 过滤，结果按时间正序。</summary>
        private static bool HandleChat(ref WebRequest req)
        {
            if (!TryParseTime(req.Queries?["since"], out var since))
            {
                AppAgentWebHandler.WriteError(req, 400, "invalid-time", $"since '{req.Queries?["since"]}' is not a date/time",
                    "use yyyy-MM-dd HH:mm:ss or ISO 8601, or drop since");
                return true;
            }
            if (!TryParseTime(req.Queries?["until"], out var until))
            {
                AppAgentWebHandler.WriteError(req, 400, "invalid-time", $"until '{req.Queries?["until"]}' is not a date/time",
                    "use yyyy-MM-dd HH:mm:ss or ISO 8601, or drop until");
                return true;
            }

            var tail = ParseInt(req.Queries?["tail"], 20);
            if (tail < 0) tail = 0;

            var matched = AgentBook.GetChat(since, until, req.Queries?["q"], req.Queries?["from"], req.Queries?["to"]);
            var shown = tail > 0 && matched.Count > tail ? matched.GetRange(matched.Count - tail, tail) : matched;
            req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
            {
                ok = true,
                total = matched.Count,
                returned = shown.Count,
                messages = shown.Select(m => new { seq = m.Seq, agentId = m.AgentId, text = m.Text, to = m.To, at = m.At })
            }));
            req.Response.SetJsonContent();
            return true;
        }

        private static bool TryParseTime(string text, out DateTime? value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return true;
            if (!DateTime.TryParse(text, out var parsed)) return false;
            value = parsed;
            return true;
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

                req.Queries["agentId"] = agentId;
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

                req.Queries["agentId"] = agentId;
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
                    var all = SiteLock.GetAll().Select(status => new
                    {
                        status.Site, status.OwnerAgentId, status.ReadCount, status.Since,
                        @lock = AgentBook.DescribeLock(status.Mode),
                        status.Mode
                    });
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, locks = all }));
                }
                else
                {
                    var status = SiteLock.GetStatus(site);
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
                    {
                        ok = true,
                        @lock = status,
                        lockText = AgentBook.DescribeLock(status.Mode)
                    }));
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