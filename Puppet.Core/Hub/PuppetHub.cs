using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Puppet.Core.Web;
using Wima.Web;

namespace Puppet.Core.Hub
{
    /// <summary>Hub 调用结果：与外部 HTTP 响应逐字同构（Status 与 Body 均来自同一条瀑布）。</summary>
    public sealed class HubResponse
    {
        /// <summary>HTTP 状态码语义：200/204 成功；400 参数；401 凭证；404 未找到；409 busy；500 执行失败。</summary>
        public int Status { get; init; }

        /// <summary>响应体（JSON）。无输出时为 null（如 204）。</summary>
        public string Body { get; init; }

        /// <summary>响应 Content-Type（含 charset）。</summary>
        public string ContentType { get; init; }
    }

    /// <summary>
    /// 进程内能力入口（ActionHub）：内部 Agent / 宿主服务直接消费 Puppet 能力，不经过 WebEngine。
    /// <para>
    /// 它只是 <see cref="PuppetWebServer.ProcessWebRequest"/> 的 typed 门面——A 面（/agent/*）与
    /// B 面（/appagent/*）走的是与外部 HTTP 完全相同的路由、鉴权、实例级串行、DialogBroker 与审计，
    /// 不存在第二套执行器。WebEngine 仅是外部 transport。
    /// </para>
    /// <para>
    /// 不依赖 <c>Start</c>/<c>Bind</c>：无需端口、回环网络、档案文件。凭证由 Hub 自动注入
    /// （/appagent/* 用进程内档案凭证，其余用全局密钥），因此本地调用与外部调用同权、同语义。
    /// </para>
    /// <para>
    /// 调用一律异步：<c>AppAgentExecutor</c> 对 Task 返回值同步等待，在 UI 线程直接调用会死锁，
    /// 故 Hub 内部统一切到线程池执行。
    /// </para>
    /// <para>
    /// 安全底线不变：未预置的模态框仍按 DialogBroker 安全默认（Cancel/No）自动应答，
    /// 本地调用不等于跳过用户确认。
    /// </para>
    /// </summary>
    public sealed class PuppetHub
    {
        /// <summary>本地调用标记头（审计区分内部/外部；不参与鉴权）。</summary>
        public const string LocalHeader = "X-Puppet-Hub";

        private readonly PuppetWebServer _server;

        internal PuppetHub(PuppetWebServer server) => _server = server;

        /// <summary>GET 请求。</summary>
        public Task<HubResponse> GetAsync(string path, object query = null, string bearerKey = null)
            => CallAsync("GET", path, query, null, bearerKey);

        /// <summary>POST 请求（body 序列化为 JSON）。</summary>
        public Task<HubResponse> PostAsync(string path, object query = null, object body = null, string bearerKey = null)
            => CallAsync("POST", path, query, body, bearerKey);

        /// <summary>
        /// 调用 B 面 action：<c>POST /appagent/actions/{action}?name={instance}</c>。
        /// <paramref name="instance"/> 省略时由运行时按单实例默认解析（多实例必须显式给名）。
        /// </summary>
        public Task<HubResponse> ActionAsync(string action, string instance = null, object args = null, string bearerKey = null)
        {
            if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("action is required", nameof(action));
            var query = string.IsNullOrEmpty(instance)
                ? null
                : new Dictionary<string, object> { ["name"] = instance };
            var body = new Dictionary<string, object>
            {
                ["args"] = (object)args ?? new Dictionary<string, object>(),
                ["callId"] = Guid.NewGuid().ToString("N")
            };
            return CallAsync("POST", "/appagent/actions/" + action, query, body, bearerKey);
        }

        /// <summary>任意方法/路径的瀑布调用（path 保留原始大小写）。</summary>
        public Task<HubResponse> CallAsync(string method, string path, object query = null,
            object body = null, string bearerKey = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required", nameof(path));
            var req = BuildRequest(method, path, query, body, bearerKey);

            // 必须离开调用线程：Executor 对 Task 返回值做 GetAwaiter().GetResult()，
            // 在 UI 线程同步等待回 UI 的续体会死锁。
            return Task.Run(() => ToResponse(_server.ProcessWebRequest(req)));
        }

        private WebRequest BuildRequest(string method, string path, object query, object body, string bearerKey)
        {
            var req = new WebRequest
            {
                Method = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant(),
                InputStream = new MemoryStream(),
                Response = new WebResponse()
            };

            // 原样保留大小写：B 面 action 名从 PathCaseSensitive 取（Path getter 才返回小写视图）
            req.Path = path;

            FillQuery(req.Queries, query);

            if (body != null)
            {
                // System.Text.Json：与宿主既有 HTTP payload 路径一致（JsonElement 等类型可直接序列化）
                var json = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(body));
                req.InputStream = new MemoryStream(json);
                req.Headers["Content-Type"] = "application/json; charset=utf-8";
            }

            // 不设 Accept-Encoding：避免 OutputCompressed() 压缩内部响应体
            // 本地调用标记（审计用；不参与鉴权——外部伪造只影响统计标签）
            req.Headers[LocalHeader] = "1";

            var key = bearerKey ?? (path.StartsWith(AppAgentWebHandler.PREFIX, StringComparison.OrdinalIgnoreCase)
                ? _server.AppAgent?.LocalKey
                : PuppetKeyVault.GlobalKey);
            if (!string.IsNullOrEmpty(key)) req.Headers["Authorization"] = "Bearer " + key;

            return req;
        }

        private static void FillQuery(NameValueCollection queries, object query)
        {
            if (query == null) return;
            foreach (var p in JObject.FromObject(query).Properties())
            {
                if (p.Value.Type is JTokenType.Null or JTokenType.Undefined) continue;
                queries[p.Name] = p.Value.Type == JTokenType.String
                    ? p.Value.Value<string>()
                    : p.Value.ToString(Formatting.None);
            }
        }

        private static HubResponse ToResponse(WebResponse resp) => new()
        {
            // 瀑布未显式设状态码时按 Buffer 补 200/404（与 HTTP transport 的 ConcludeStatusCode 同语义）
            Status = resp.ConcludeStatusCode().StatusCode,
            Body = resp.Buffer == null ? null : Encoding.UTF8.GetString(resp.Buffer),
            ContentType = resp.Headers?["Content-Type"]
        };
    }
}
