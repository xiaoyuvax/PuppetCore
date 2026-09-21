using System;

namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// UseAppAgent 扩展入口（模式 B 内建服务器）。
    /// 与 PuppetWebServer 同程序集，故可调用 internal AttachAppAgent。
    /// 模式 A 注入：宿主自建 AppAgentRuntime 后在 ProcessWebRequest 中调 AppAgentWebHandler.TryHandle(ref req, runtime)。
    /// </summary>
    public static class AppAgentServerExtensions
    {
        /// <summary>
        /// 启用面向 B（用户 Agent 操作接口）：注册 /appagent/* 端点、Start 时写发现档案、启用实例级串行。
        /// 不影响 /agent/* 现有行为；options.BlockAgentEndpoints = true 时 /agent/* 一律 404（Paranoid，B-only 成品建议开启）。
        /// 注意：如需 Paranoid 拦截生效，UseAppAgent 的处理器先于其他扩展执行（框架内部已保证次序）。
        /// </summary>
        public static Web.PuppetWebServer UseAppAgent(this Web.PuppetWebServer server, Action<AppAgentOptions> configure = null)
        {
            var options = new AppAgentOptions();
            configure?.Invoke(options);
            var runtime = new AppAgentRuntime(options);
            server.AttachAppAgent(runtime);
            return server;
        }
    }
}
