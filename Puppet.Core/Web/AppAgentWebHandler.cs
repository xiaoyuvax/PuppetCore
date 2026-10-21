using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Puppet.Core.AppAgent;
using Wima.Web;

namespace Puppet.Core.Web
{
    /// <summary>
    /// /appagent/* 端点处理器（面向 B：用户 Agent 操作接口，提案 §4 / §4.5）。
    /// 模式 B：UseAppAgent(...) 自动挂接（扩展处理器链，先于内建 /agent/* 执行）。
    /// 模式 A：宿主在 ProcessWebRequest 中调用 AppAgentWebHandler.TryHandle(ref req, runtime)。
    ///
    /// 凭证分层（CLI 类比）：probe/help = 看见工具 + 读手册（无凭证）；manifest/actions/state/assets = 能力目录（需档案凭证）。
    /// 错误契约：统一 {ok:false, code, err, hint, retryAfterMs?}，每条错误自带行动指引。
    /// </summary>
    public static class AppAgentWebHandler
    {
        /// <summary>端点前缀</summary>
        public const string PREFIX = "/appagent";

        /// <summary>尝试处理 /appagent/* 请求。返回 true 表示已处理。</summary>
        public static bool TryHandle(ref WebRequest req, AppAgent.AppAgentRuntime runtime)
        {
            if (runtime == null) return false;
            var path = req.Path ?? "";
            if (runtime.Options.BlockAgentEndpoints && path.StartsWith("/agent", StringComparison.Ordinal)
                && !path.StartsWith(PREFIX, StringComparison.Ordinal))
            {
                req.Response.SetStatus404();
                return true;
            }
            if (!path.StartsWith(PREFIX, StringComparison.Ordinal)) return false;

            // 与 A 面同一套多 Agent 协调语义：心跳 + 响应尾附同伴摘要 + agentDone 清理
            PuppetWebHandler.NoteAgentCall(ref req, req.Queries?["name"]);
            var handled = TryHandleCore(ref req, runtime);
            if (handled)
            {
                PuppetWebHandler.FinishAgentCall(ref req);
                PuppetWebHandler.EnsureUtf8JsonContentType(ref req);
            }
            return handled;
        }

        private static bool TryHandleCore(ref WebRequest req, AppAgent.AppAgentRuntime runtime)
        {
            var path = req.Path ?? "";                          // 小写化路由视图（端点分流，Wima 契约）
            var pathCs = req.PathCaseSensitive ?? "";            // 原始大小写：action/state 契约名提取（manifest 逐字一致）

            if (!path.StartsWith(PREFIX, StringComparison.Ordinal)) return false;

            var key = ExtractKey(req);

            try
            {
                // ---- 无凭证：发现 L1 探针 + L2 手册（静态最小披露） ----
                if (path == "/appagent/probe" && req.Method == "GET")
                {
                    req.Response.Buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                    {
                        puppet = true,
                        protocol = "appagent/1.0",
                        auth = "profile-file",
                        authHint = "%LOCALAPPDATA%\\Puppet.AppAgents\\<app>.<pid>.json",
                        help = "/appagent/help"
                    }, Formatting.None));
                    req.Response.SetJsonContent();
                    return true;
                }

                if (path == "/appagent/help" && req.Method == "GET")
                {
                    req.Response.Buffer = Encoding.UTF8.GetBytes(HelpText);
                    req.Response.Headers.Set("Content-Type", "text/markdown; charset=utf-8");
                    return true;
                }

                // ---- 以下全部需要档案凭证 ----
                if (!runtime.Authorize(key))
                {
                    WriteError(req, 401, "credential-invalid",
                        "missing or invalid bearer key",
                        "read key from %LOCALAPPDATA%\\Puppet.AppAgents\\<app>.<pid>.json (glob <app>.*.json; pick the live pid)");
                    return true;
                }

                if (path == "/appagent/manifest" && req.Method == "GET")
                {
                    var inst = ResolveInstance(req, runtime, req.Queries?["name"]);
                    if (inst == null) return true;
                    req.Response.Buffer = Encoding.UTF8.GetBytes(AppAgent.ManifestBuilder.Build(inst));
                    req.Response.SetJsonContent();
                    return true;
                }

                if (path.StartsWith("/appagent/actions/", StringComparison.Ordinal) && req.Method == "POST")
                {
                    var inst = ResolveInstance(req, runtime, req.Queries?["name"]);
                    if (inst == null) return true;
                    var actionName = pathCs["/appagent/actions/".Length..];
                    var (argsObj, callId, dialogs) = ParseActionBody(req.InputStream);
                    var (status, body) = runtime.Executor.Execute(inst, actionName, argsObj, callId, dialogs);
                    req.Response.StatusCode = status;
                    req.Response.Buffer = Encoding.UTF8.GetBytes(body);
                    req.Response.SetJsonContent();
                    return true;
                }

                if (path.StartsWith("/appagent/state/", StringComparison.Ordinal) && req.Method == "GET")
                {
                    var inst = ResolveInstance(req, runtime, req.Queries?["name"]);
                    if (inst == null) return true;
                    var stateKey = pathCs["/appagent/state/".Length..];
                    return HandleState(req, inst, stateKey);
                }

                if (path.StartsWith("/appagent/assets/", StringComparison.Ordinal) && req.Method == "GET")
                {
                    var id = pathCs["/appagent/assets/".Length..];
                    if (runtime.Assets.TryGet(id, out var artifact, out var fileName))
                    {
                        req.Response.Buffer = artifact.Content ?? Array.Empty<byte>();
                        req.Response.Headers.Set("Content-Type",
                            string.IsNullOrEmpty(artifact.ContentType) ? "application/octet-stream" : artifact.ContentType);
                        req.Response.Headers.Set("Content-Disposition", $"attachment; filename=\"{SanitizeFileName(fileName)}\"");
                        return true;
                    }
                    WriteError(req, 404, "asset-expired", "asset not found or expired",
                        "re-run the producing action to get a new asset");
                    return true;
                }

                WriteError(req, 404, "endpoint-not-found", $"unknown endpoint {path}",
                    "GET /appagent/help for the endpoint list");
                return true;
            }
            catch (Exception ex)
            {
                WriteError(req, 500, "internal", ex.Message, "retry or inspect /appagent/help");
                return true;
            }
        }

        // ---- state：简单类型属性直读（文本聊天视图；复杂类型不序列化对象图，与内核 GetProperty 同哲学） ----
        private static bool HandleState(WebRequest req, AppAgent.AppAgentInstance inst, string key)
        {
            var (_, states) = AppAgent.ActionizePolicy.Scan(inst.Instance.GetType(), inst.Instance);
            var state = states.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.Ordinal));
            if (state == null)
            {
                WriteError(req, 404, "state-not-found", $"state key '{key}' not found",
                    "GET /appagent/manifest for the state key list");
                return true;
            }

            object value = null;
            object rawErr = RunOnUi(inst.Instance, () =>
            {
                try
                {
                    value = state.Property != null
                        ? state.Property.GetValue(inst.Instance)
                        : state.ValueProvider?.Invoke(inst.Instance);
                    return null;
                }
                catch (Exception e) { return e; }
            });
            if (rawErr != null)
            {
                var inner = rawErr is System.Reflection.TargetInvocationException tie
                    ? (tie.InnerException ?? (Exception)tie)
                    : (rawErr as Exception ?? new Exception(rawErr.ToString()));
                WriteError(req, 500, "state-read-failed", inner.Message, "retry later");
                return true;
            }

            req.Response.Buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                ok = true,
                key = state.Name,
                value,
                type = state.Property != null ? state.Property.PropertyType.Name : state.TypeName
            }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Include }));
            req.Response.SetJsonContent();
            return true;
        }

        /// <summary>实例解析（错误在此写回，null = 已写错误）。单实例时 name 可省略；
        /// 多实例省略 name → 400 instance-name-required（静默默认 Instances[0] 会操作错窗口）。
        /// 任何未命中错误均携带 availableInstances，B 面 Agent 无需读档案即可枚举实例名。</summary>
        private static AppAgent.AppAgentInstance ResolveInstance(WebRequest req, AppAgent.AppAgentRuntime runtime, string name)
        {
            var list = runtime.Instances;
            if (list.Count == 0) { WriteNoInstance(req, name, Array.Empty<string>()); return null; }
            if (string.IsNullOrEmpty(name))
            {
                if (list.Count == 1) return list[0];
                WriteError(req, 400, "instance-name-required",
                    "multiple bound instances; ?name= is required",
                    "pass ?name=<instance> from availableInstances; omit ?name= only when a single instance is bound",
                    availableInstances: list.Select(i => i.Name).ToArray());
                return null;
            }
            var inst = list.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
            if (inst == null) WriteNoInstance(req, name, list.Select(i => i.Name).ToArray());
            return inst;
        }

        private static (JObject args, string callId, System.Collections.Generic.List<DialogPreset> dialogs) ParseActionBody(Stream input)
        {
            if (input == null || !input.CanRead) return (null, null, null);
            using var reader = new StreamReader(input, Encoding.UTF8, false);
            var raw = reader.ReadToEnd();
            if (string.IsNullOrEmpty(raw)) return (null, null, null);
            try
            {
                var obj = JObject.Parse(raw);
                // dialogs：模态框预答表（层2兜底），形如 [{"title":"未保存","answer":"No"}]
                var dialogs = obj["dialogs"]?
                    .Where(d => d is JObject)
                    .Select(d => new DialogPreset { Title = d["title"]?.ToString(), Answer = d["answer"]?.ToString() })
                    .ToList();
                return (obj["args"] as JObject, obj["callId"]?.ToString(), dialogs);
            }
            catch { return (null, null, null); }
        }

        private static void WriteNoInstance(WebRequest req, string name, string[] available) =>
            WriteError(req, 404, "instance-not-found",
                string.IsNullOrEmpty(name)
                    ? "no puppet instance is currently bound"
                    : $"instance '{name}' is not bound (closed or not yet created)",
                available.Length == 0
                    ? "no instances are bound yet; wait for one to open, then retry"
                    : "use ?name= from availableInstances; omit ?name= only when a single instance is bound",
                availableInstances: available);

        internal static void WriteError(WebRequest req, int status, string code, string err, string hint, int? retryAfterMs = null, string[] availableInstances = null)
        {
            var o = new System.Collections.Generic.Dictionary<string, object>
            {
                ["ok"] = false, ["code"] = code, ["err"] = err, ["hint"] = hint
            };
            if (retryAfterMs.HasValue) o["retryAfterMs"] = retryAfterMs.Value;
            if (availableInstances != null) o["availableInstances"] = availableInstances;
            req.Response.StatusCode = status;
            req.Response.Buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(o));
            req.Response.SetJsonContent();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace("\"", "");
        }

        private static string ExtractKey(WebRequest req)
        {
            var auth = req.Headers?.Get("Authorization");
            if (string.IsNullOrEmpty(auth)) return null;
            return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..] : auth;
        }

        private static object RunOnUi(object target, Func<object> action)
        {
            // 与内核 RunOnUi 同语义：ISynchronizeInvoke 优先，PuppetUiContext 兜底（同程序集 internal 直用）
            if (target is System.ComponentModel.ISynchronizeInvoke sync && sync.InvokeRequired)
                return sync.Invoke(() => action(), null);
            if (Puppet.Core.PuppetUiContext.ShouldSwitch())
                return Puppet.Core.PuppetUiContext.Send(action);
            return action();
        }

        private const string HelpText = """
# AppAgent 使用手册（appagent/1.0）

面向用户个人 Agent 的应用操作接口。四步上手：

1. **发现**：枚举 `%LOCALAPPDATA%\Puppet.AppAgents\*.json` 取 {endpoint, key}；或扫描端口后 `GET /appagent/probe` 确认。
2. **能力目录**：`GET /appagent/manifest`（Bearer key；多实例宿主必须带 `?name=`，省略时单实例默认、多实例返回 400 `instance-name-required` 并在 `availableInstances` 列出可用实例名）→ actions[]（name/desc/parameters）与 state[]。
3. **执行操作**：`POST /appagent/actions/{name}`，body `{"args":{按名传参}, "callId":"uuid", "dialogs":[{"title":"对话框标题","answer":"No"}]}`。
   - 参数严格按名绑定：未知参数名 400、缺必填 400。
   - 实例级强制串行：busy 时返回 `409 {code:"instance-busy", retryAfterMs}`，按提示重试。
   - 模态框预答（层2兜底）：动作执行期间弹出的确认框按 dialogs 表按标题精确匹配自动应答
     （answer: "Yes"/"No"/"OK"/"Cancel"）；未预置的对话框按安全默认（Cancel/No）自动应答，动作永不阻塞；
     响应 `dialogsAnswered[]` 报告每个被自动应答的对话框（fromPreset=false 表示走了安全默认）。
4. **读状态**：`GET /appagent/state/{key}`。

**错误契约**：所有错误统一 `{ok:false, code, err, hint, retryAfterMs?}`——按 hint 行动即可恢复。
常见 code：credential-invalid / instance-name-required / instance-not-found / instance-busy / action-not-found / state-not-found / asset-expired / site-locked。
实例相关错误（instance-*）均携带 `availableInstances[]`——直接据此选名，无需读档案。

**产物下载**：action 响应携带 `asset` URL 时立即 `GET`（Bearer key）下载；TTL 默认 10 分钟，过期重跑 action。

**多步原子序列**（先选中再操作）：请求可带 agentId/agentPurpose/agentOp/agentWaitEst/site/lockMode/agentNote/agentHelp
透传参数进入 AgentBook 心跳（未给 site 时按 `actions/<name>`、`state/<key>` 自动推断站点），
并用 /agent/lock/acquire 建议性锁保护序列（协议与调试面向一致，manifest.concurrency 已声明串行语义）。

**多 Agent 在场提示**：若存在其他 Agent，本接口的 JSON 响应会自动附带 `peers`（id/instance/site/doing/purpose/idleSeconds/lastActive/estimatedWaitSeconds/shuttingDown/lock/note/seeksHelp/summary）与 `peersHint`；
数值字段名自解释可直接计算，语义用自然语言表达；不要依赖预定义枚举码（跨版本不保证含义一致）。
详情用 `GET /agent/book`（全局密钥，含 `helpLog` 最近求助历史），锁状态用 `GET /agent/lock/status?site=*`（仅 A 面全局密钥）。
需要其他 Agent 协助时在任意请求加 `agentHelp=<需求>`，定向加 `agentHelpTo=<agentId>`（空值广播）；对方会在 `seeksHelp`/`helpTo` 看到。完事带 `agentDone=true`（任意请求）或 `POST /agent/book/release` 清除自己在场状态。
""";
    }

}
