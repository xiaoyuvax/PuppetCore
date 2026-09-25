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
            var path = req.Path ?? "";                          // 小写化路由视图（端点分流，Wima 契约）
            var pathCs = req.PathCaseSensitive ?? "";            // 原始大小写：action/state 契约名提取（manifest 逐字一致）

            // Paranoid 开关（v0.5.4）：BlockAgentEndpoints 时 /agent/* 一律 404。
            // 本处理器在扩展链中先于内建 /agent/* 执行，因此可拦截（UseAppAgent 应先于 UseFormControls 调用）。
            if (runtime.Options.BlockAgentEndpoints && path.StartsWith("/agent", StringComparison.Ordinal)
                && !path.StartsWith(PREFIX, StringComparison.Ordinal))
            {
                req.Response.SetStatus404();
                return true;
            }

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
                        authHint = "%LOCALAPPDATA%\\Puppet.AppAgents\\<app>.json",
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
                        "read key from %LOCALAPPDATA%\\Puppet.AppAgents\\<app>.json");
                    return true;
                }

                if (path == "/appagent/manifest" && req.Method == "GET")
                {
                    var inst = ResolveInstance(runtime, req.Queries?["name"]);
                    if (inst == null) { WriteNoInstance(req); return true; }
                    req.Response.Buffer = Encoding.UTF8.GetBytes(AppAgent.ManifestBuilder.Build(inst));
                    req.Response.SetJsonContent();
                    return true;
                }

                if (path.StartsWith("/appagent/actions/", StringComparison.Ordinal) && req.Method == "POST")
                {
                    var inst = ResolveInstance(runtime, req.Queries?["name"]);
                    if (inst == null) { WriteNoInstance(req); return true; }
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
                    var inst = ResolveInstance(runtime, req.Queries?["name"]);
                    if (inst == null) { WriteNoInstance(req); return true; }
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

        private static AppAgent.AppAgentInstance ResolveInstance(AppAgent.AppAgentRuntime runtime, string name)
        {
            if (runtime.Instances.Count == 0) return null;
            if (string.IsNullOrEmpty(name)) return runtime.Instances[0]; // 单实例默认；多实例必须带 name
            return runtime.Instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
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

        private static void WriteNoInstance(WebRequest req) =>
            WriteError(req, 404, "instance-not-found", "no bound puppet instance for this request",
                "check instance names in the discovery profile or omit ?name= for the single instance");

        internal static void WriteError(WebRequest req, int status, string code, string err, string hint, int? retryAfterMs = null)
        {
            var o = new System.Collections.Generic.Dictionary<string, object>
            {
                ["ok"] = false, ["code"] = code, ["err"] = err, ["hint"] = hint
            };
            if (retryAfterMs.HasValue) o["retryAfterMs"] = retryAfterMs.Value;
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
2. **能力目录**：`GET /appagent/manifest`（Bearer key）→ actions[]（name/desc/parameters）与 state[]。
3. **执行操作**：`POST /appagent/actions/{name}`，body `{"args":{按名传参}, "callId":"uuid", "dialogs":[{"title":"对话框标题","answer":"No"}]}`。
   - 参数严格按名绑定：未知参数名 400、缺必填 400。
   - 实例级强制串行：busy 时返回 `409 {code:"instance-busy", retryAfterMs}`，按提示重试。
   - 模态框预答（层2兜底）：动作执行期间弹出的确认框按 dialogs 表按标题精确匹配自动应答
     （answer: "Yes"/"No"/"OK"/"Cancel"）；未预置的对话框按安全默认（Cancel/No）自动应答，动作永不阻塞；
     响应 `dialogsAnswered[]` 报告每个被自动应答的对话框（fromPreset=false 表示走了安全默认）。
4. **读状态**：`GET /appagent/state/{key}`。

**错误契约**：所有错误统一 `{ok:false, code, err, hint, retryAfterMs?}`——按 hint 行动即可恢复。
常见 code：credential-invalid / instance-busy / action-not-found / state-not-found / asset-expired / site-locked。

**产物下载**：action 响应携带 `asset` URL 时立即 `GET`（Bearer key）下载；TTL 默认 10 分钟，过期重跑 action。

**多步原子序列**（先选中再操作）：请求可带 agentId/agentOp/site/lockMode 透传参数进入 AgentBook 心跳，
并用 /agent/lock/acquire 建议性锁保护序列（协议与调试面向一致，manifest.concurrency 已声明串行语义）。
""";
    }

}
