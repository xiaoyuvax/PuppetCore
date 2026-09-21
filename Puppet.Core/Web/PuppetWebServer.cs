using Wima.Log;
using Wima.Web;
using Wima.Web.Engine;

namespace Puppet.Core.Web
{
    /// <summary>
    /// 内建 Web 服务器（模式 B/C）。
    /// 继承 WebServerBase，仅处理 /agent/* 端点和 /ping，不提供静态文件服务。
    /// 与 Host 原生 WebServerBase 子类共享 PuppetRegistry（静态字段）。
    /// </summary>
    public sealed class PuppetWebServer : WebServerBase, IPuppet
    {
        /// <summary>默认监听端口（避免与常见 80/8080 冲突）</summary>
        public const string DEFAULT_ENDPOINT = "0.0.0.0:9090";

        /// <summary>扩展端点处理器链（先于内建 /agent/* 端点执行）</summary>
        private readonly List<Func<WebRequest, bool>> _extraHandlers = new();

        /// <summary>面向 B 运行时（UseAppAgent 时创建；null = 未启用，零行为差异）</summary>
        private AppAgent.AppAgentRuntime _appAgentRuntime;

        /// <summary>构造 PuppetWebServer</summary>
        public PuppetWebServer()
        {
            InstanceName = nameof(PuppetWebServer);
            LogMan = new WimaLogger(nameof(PuppetWebServer), logMode: LogMode.Native | LogMode.Console);
            UseCrossDomain().UseHealthCheckPing().UseCompression();
            IsRunning = true;
        }

        /// <summary>启动 Agent Web 服务器（默认端口 9090）。
        /// 已 UseAppAgent 时启动成功后写发现档案（endpoint 含实际端口）。</summary>
        /// <param name="listenEp">监听端点，如 "0.0.0.0:9090"</param>
        /// <returns>是否启动成功</returns>
        public bool Start(string listenEp = DEFAULT_ENDPOINT)
        {
            var ok = StartWebEngine(listenEp, Wima.Web.WebEngine.Kestrel, reuseAddress: true);
            if (ok && _appAgentRuntime != null)
            {
                var idx = listenEp?.LastIndexOf(':') ?? -1;
                var portStr = idx >= 0 ? listenEp[(idx + 1)..] : null;
                _appAgentRuntime.Bind(int.TryParse(portStr, out var p) ? p : 9090);
            }
            return ok;
        }

        /// <summary>停止 Agent Web 服务器（清理发现档案）</summary>
        public void Stop()
        {
            _appAgentRuntime?.Unbind();
            StopWebEngine(dispose: true);
        }

        /// <summary>挂接面向 B 运行时（UseAppAgent 扩展调用；内部 API）。
        /// 端点在 ProcessWebRequest 中先于扩展处理器链执行，保证 BlockAgentEndpoints 拦截不被绕过。</summary>
        internal void AttachAppAgent(AppAgent.AppAgentRuntime runtime) => _appAgentRuntime = runtime;

        /// <summary>
        /// 注册扩展端点处理器（返回 true 表示已处理并终结请求）。
        /// 扩展处理器先于内建 /agent/* 端点执行：PuppetWebHandler 对未匹配的 /agent/* 路径以 404 终结请求，
        /// 拥有更具体前缀的扩展端点（如 WinForms 包的 /agent/control）必须先执行才有机会命中。
        /// 用法：new PuppetWebServer().UseHandler(MyHandler).Start("0.0.0.0:9090")
        /// </summary>
        /// <param name="handler">处理器委托，参数为请求，返回 true 表示已处理</param>
        public PuppetWebServer UseHandler(Func<WebRequest, bool> handler)
        {
            if (handler != null) _extraHandlers.Add(handler);
            return this;
        }

        /// <inheritdoc/>
        public override WebResponse ProcessWebRequest(WebRequest req)
        {
            base.ProcessWebRequest(req);

            if (req.Path == "/ping") { req.Response.SetStatus200(); return req.Response; }

            // 面向 B 端点最先（含 BlockAgentEndpoints 对 /agent/* 的 Paranoid 拦截）
            if (_appAgentRuntime != null && AppAgentWebHandler.TryHandle(ref req, _appAgentRuntime))
            {
                req.Response.OutputCompressed();
                return req.Response;
            }

            // 扩展端点先于内建 /agent/* 端点（见 UseHandler 说明）
            foreach (var handler in _extraHandlers)
                if (handler(req)) { req.Response.OutputCompressed(); return req.Response; }

            if (PuppetWebHandler.TryHandle(ref req))
            {
                req.Response.OutputCompressed();
                return req.Response;
            }

            req.Response.SetStatus404();
            req.Response.OutputCompressed();
            return req.Response;
        }

        // IPuppet 实现（PuppetWebServer 自身也是 IPuppet，可被 Agent 查询）
        string IPuppet.AgentAccessKey => PuppetKeyVault.GlobalKey;
        Common.Logging.ILog IPuppet.AgentLog => LogMan;
        string IPuppet.AgentInstanceName => $"{nameof(PuppetWebServer)}({InstanceName})";
    }
}
