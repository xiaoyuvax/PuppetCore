using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Puppet.Core.Usage;
using Puppet.Core.Extentions;

namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// 面向 B 执行器（提案 §4 要点 5/8 + §3.6）：
    /// 按名参数严格绑定（未知参数 400 / 缺必填 400）+ 实例级强制串行（409 busy）+
    /// Task&lt;bool&gt;/Task&lt;OperationResult&gt; 解包（复用内核 invoke 语义）+ 产物提取（PuppetArtifact → AssetStore）。
    /// UI 线程切换经 PuppetUiContext / ISynchronizeInvoke（与 A 共用内核机制）。
    /// </summary>
    internal sealed class AppAgentExecutor
    {
        private static readonly Dictionary<string, SemaphoreSlim> _instanceLocks = new();
        private static readonly object _lockGate = new();

        private readonly AppAgentRuntime _runtime;

        public AppAgentExecutor(AppAgentRuntime runtime) => _runtime = runtime;

        /// <summary>执行一个 action：返回 (statusCode, jsonBody)。
        /// dialogPresets：Agent 随调用预置的模态框答案（DialogBroker 层2兜底；null=无预答，未预置对话框按安全默认自动应答）。</summary>
        public (int Status, string Body) Execute(AppAgentInstance inst, string actionName, JObject argsObj, string callId, List<DialogPreset> dialogPresets = null)
        {
            // ---- 1. 定位 action（反射缓存命中即查；签名单一，与 manifest 一致） ----
            var (actions, _) = ActionizePolicy.Scan(inst.Instance.GetType(), inst.Instance);
            var action = actions.FirstOrDefault(a => string.Equals(a.Name, actionName, StringComparison.Ordinal));
            if (action == null)
                return Error(404, "action-not-found", $"action '{actionName}' not found",
                    "GET /appagent/manifest for available action names");

            // ---- 2. 按名严格绑定参数（UI 基础 action 走参数表；宿主 action 走方法签名） ----
            object[] realArgs = null;
            Dictionary<string, object> baseArgs = null;
            if (action.BaseAction != null)
            {
                var (ba, be) = BindBaseParameters(action, argsObj);
                if (be != null)
                    return Error(400, "parameter-binding", be, "check parameter names/required in /appagent/manifest");
                baseArgs = ba;
            }
            else
            {
                var bind = BindParameters(action, argsObj);
                if (bind.Error != null)
                    return Error(400, "parameter-binding", bind.Error, "check parameter names/required in /appagent/manifest");
                realArgs = bind.Parameters;
            }

            // ---- 3. 实例级强制串行（机制层，不是建议性） ----
            var gate = GetGate(inst.Name);
            if (!gate.Wait(0))
            {
                // 立即失败不排队？——先短暂等待，超时即 409（busyWaitMs 可配）
                if (!gate.Wait(_runtime.Options.BusyWaitMs))
                    return Error(409, "instance-busy", "another action is executing on this instance",
                        "retry after retryAfterMs, or report wait to user", retryAfterMs: _runtime.Options.BusyWaitMs);
            }
            try
            {
                // ---- 4. 执行（UI 线程切换复用内核 + DialogBroker 预答上下文）+ Task 解包 + 产物提取 ----
                using (DialogBroker.Push(dialogPresets))
                {
                    return action.BaseAction != null
                        ? InvokeBase(inst, action, baseArgs, callId)
                        : InvokeCore(inst, action, realArgs, callId);
                }
            }
            finally { gate.Release(); }
        }

        /// <summary>执行 UI 基础 action（框架合成，Method 为 null）：走 PuppetUiActions 能力分发。</summary>
        private (int Status, string Body) InvokeBase(AppAgentInstance inst, ActionizePolicy.ActionMember action, Dictionary<string, object> args, string callId)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            OperationResult result = null;
            object rawErr = RunOnUi(inst.Instance, () =>
            {
                try { result = PuppetUiActions.Execute(inst.Instance, action.BaseAction, args); return null; }
                catch (Exception e) { return e; }
            });

            if (rawErr != null)
            {
                var inner = rawErr is TargetInvocationException tie
                    ? (tie.InnerException ?? (Exception)tie)
                    : (rawErr as Exception ?? new Exception(rawErr.ToString()));
                return Error(500, "action-failed", inner.Message, "check application state via /appagent/state or re-run");
            }

            sw.Stop();
            bool ok = result?.Ok ?? false;
            string message = result?.Message ?? "done";
            var body = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["action"] = action.Name,
                ["message"] = message,
                ["elapsedMs"] = sw.ElapsedMilliseconds
            };
            if (result?.Data != null) body["data"] = result.Data;
            if (callId != null) body["callId"] = callId;

            var answered = DialogBroker.AnsweredSnapshot();
            if (answered.Count > 0)
                body["dialogsAnswered"] = answered.Select(r => new { r.Title, r.Answer, r.FromPreset }).ToArray();

            if (!ok) body["hint"] = "check message and application state; fix inputs and retry";
            return (200, JsonConvert.SerializeObject(body, Formatting.None));
        }

        /// <summary>UI 基础 action 的参数绑定：按 manifest 参数表（名/类型/必填）严格绑定。</summary>
        private (Dictionary<string, object> Args, string Error) BindBaseParameters(ActionizePolicy.ActionMember action, JObject argsObj)
        {
            var result = new Dictionary<string, object>();
            var supplied = argsObj?.Properties().ToDictionary(p => p.Name, p => p.Value)
                        ?? new Dictionary<string, JToken>();

            foreach (var p in action.Params)
            {
                if (supplied.TryGetValue(p.Name, out var token))
                {
                    supplied.Remove(p.Name);
                    try { result[p.Name] = ConvertBase(p.Type, token); }
                    catch (Exception ex) { return (null, $"cannot convert parameter '{p.Name}' to {p.Type}: {ex.Message}"); }
                }
                else if (p.Required)
                {
                    return (null, $"missing required parameter '{p.Name}'");
                }
            }

            if (supplied.Count > 0)
                return (null, $"unknown parameter(s): {string.Join(", ", supplied.Keys)} (manifest names only)");

            return (result, null);
        }

        private static object ConvertBase(string friendlyType, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return friendlyType switch
            {
                "integer" => token.ToObject<int>(),
                "boolean" => token.ToObject<bool>(),
                "number" => token.ToObject<double>(),
                _ => token.ToString()
            };
        }

        private (int Status, string Body) InvokeCore(AppAgentInstance inst, ActionizePolicy.ActionMember action, object[] args, string callId)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                object retVal = null;
                object rawErr = RunOnUi(inst.Instance, () =>
                {
                    try { retVal = action.Method.Invoke(inst.Instance, args); return null; }
                    catch (Exception e) { return e; }
                });

                if (rawErr != null)
                {
                    var inner = rawErr is TargetInvocationException tie
                        ? (tie.InnerException ?? (Exception)tie)
                        : (rawErr as Exception ?? new Exception(rawErr.ToString()));
                    return Error(500, "action-failed", inner.Message, "check application state via /appagent/state or re-run");
                }

                // ---- Task 解包（Task<bool>/Task<OperationResult>：请求-响应本就等待） ----
                if (retVal is System.Threading.Tasks.Task task)
                {
                    task.GetAwaiter().GetResult();
                    if (action.Method.ReturnType.IsGenericType)
                        retVal = action.Method.ReturnType.GetProperty("Result")?.GetValue(retVal);
                    else
                        retVal = null;
                }

                sw.Stop();

                // ---- 产物提取：PuppetArtifact（直接返回或 OperationResult.Data 内嵌） ----
                string artifactUrl = null;
                if (retVal is PuppetArtifact art)
                {
                    artifactUrl = _runtime.Assets.Put(art);
                    retVal = null;
                }
                else if (retVal is OperationResult or && or.Data is PuppetArtifact art2)
                {
                    artifactUrl = _runtime.Assets.Put(art2);
                    retVal = new OperationResult { Ok = or.Ok, Message = or.Message, Data = null };
                }

                // ---- 结果形状（与 A 刻意不同：message 面向用户） ----
                bool ok;
                string message;
                object data = null;
                if (retVal is OperationResult r) { (ok, message, data) = (r.Ok, r.Message, r.Data); }
                else if (retVal is bool b) { ok = b; message = b ? "done" : "rejected"; }
                else if (retVal is null && artifactUrl == null
                         && action.Method.ReturnType != typeof(System.Threading.Tasks.Task))
                {
                    // 范式外成员（[PuppetAction] 收录）返回 null = 未产出结果（如重名守卫 return null），
                    // 谎报 ok:true 会误导用户 Agent；非泛型 Task 解包后的 null 属成功语义，产物已提取亦不在此列
                    ok = false; message = "no result (returned null)";
                }
                else
                {
                    ok = true; message = "done";
                    // 标量返回值（计数/名称等）进 data 供 Agent 核对效果；复杂类型不序列化（防大对象/循环引用）
                    if (retVal != null && IsSimpleScalar(retVal.GetType())) data = retVal;
                }

                var body = new Dictionary<string, object>
                {
                    ["ok"] = ok,
                    ["action"] = action.Name,
                    ["message"] = message,
                    ["elapsedMs"] = sw.ElapsedMilliseconds
                };
                if (data != null) body["data"] = data;
                if (artifactUrl != null) body["asset"] = artifactUrl;
                if (callId != null) body["callId"] = callId;

                // ---- 模态框预答报告（层2兜底）：Agent 上下文内被自动应答的对话框（预答命中或安全默认） ----
                var answered = DialogBroker.AnsweredSnapshot();
                if (answered.Count > 0)
                    body["dialogsAnswered"] = answered.Select(r => new { r.Title, r.Answer, r.FromPreset }).ToArray();

                if (!ok) body["hint"] = "check message and application state; fix inputs and retry";

                return (200, JsonConvert.SerializeObject(body, Formatting.None));
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
                return Error(500, "action-failed", inner?.Message ?? ex.Message, "unexpected execution failure");
            }
        }

        // ---- 按名严格绑定：未知参数 400；缺必填 400；可选参数省略取默认 ----
        private (object[] Parameters, string Error) BindParameters(ActionizePolicy.ActionMember action, JObject argsObj)
        {
            var pis = action.Method.GetParameters();
            var result = new object[pis.Length];
            var supplied = argsObj?.Properties().ToDictionary(p => p.Name, p => (object)p.Value)
                        ?? new Dictionary<string, object>();

            foreach (var pi in pis)
            {
                if (supplied.TryGetValue(pi.Name, out var token))
                {
                    supplied.Remove(pi.Name);
                    try
                    {
                        result[pi.Position] = token is JToken jt ? jt.ToObject(pi.ParameterType) : Convert.ChangeType(token, pi.ParameterType);
                    }
                    catch (Exception ex) { return (null, $"cannot convert parameter '{pi.Name}' to {pi.ParameterType.Name}: {ex.Message}"); }
                }
                else if (pi.IsOptional) result[pi.Position] = pi.DefaultValue ?? (pi.ParameterType.IsValueType ? Activator.CreateInstance(pi.ParameterType) : null);
                else if (pi.ParameterType.IsValueType) result[pi.Position] = Activator.CreateInstance(pi.ParameterType);
                else return (null, $"missing required parameter '{pi.Name}'");
            }

            if (supplied.Count > 0)
                return (null, $"unknown parameter(s): {string.Join(", ", supplied.Keys)} (manifest names only)");

            return (result, null);
        }

        private static (int Status, string Body) Error(int status, string code, string err, string hint, int? retryAfterMs = null)
        {
            var o = new Dictionary<string, object> { ["ok"] = false, ["code"] = code, ["err"] = err, ["hint"] = hint };
            if (retryAfterMs.HasValue) o["retryAfterMs"] = retryAfterMs.Value;
            return (status, JsonConvert.SerializeObject(o));
        }

        // ---- 实例执行锁：每实例一把，进程级共享（多 server 同实例也互斥） ----
        private static SemaphoreSlim GetGate(string instanceName)
        {
            lock (_lockGate)
            {
                if (!_instanceLocks.TryGetValue(instanceName, out var s))
                    _instanceLocks[instanceName] = s = new SemaphoreSlim(1, 1);
                return s;
            }
        }

        /// <summary>UI 线程切换（与内核 RunOnUi 同语义：ISynchronizeInvoke 优先，PuppetUiContext 兜底）</summary>
        private static object RunOnUi(object target, Func<object> action)
        {
            if (target is System.ComponentModel.ISynchronizeInvoke sync && sync.InvokeRequired)
                return sync.Invoke(() => action(), null);
            if (PuppetUiContext.ShouldSwitch())
                return PuppetUiContext.Send(() => action());
            return action();
        }

        /// <summary>可直接进 data 的标量类型（复杂类型不序列化，防大对象/循环引用）。</summary>
        private static bool IsSimpleScalar(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
                || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan)
                || t == typeof(Guid);
        }
    }
}
