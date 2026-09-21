using Puppet.Core.AppAgent;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// WinForms 宿主的面向 B 集成扩展：UseAppAgent 时自动注册 L1 UI 文案桥（控件树文案提取）。
    /// </summary>
    public static class FormAppAgentIntegration
    {
        /// <summary>
        /// 启用面向 B 并自动挂接 WinForms 文案桥（manifest 文案与 UI 同源）。
        /// 等价于先 FormAppAgentUiText.Register() 再 UseAppAgent(configure)，保持链式调用。
        /// 用法：<code>new PuppetWebServer().UseFormControls().UseAppAgentWithUiText(o =&gt; o.ProductName = "小步看板").Start("127.0.0.1:9090")</code>
        /// </summary>
        public static Puppet.Core.Web.PuppetWebServer UseAppAgentWithUiText(this Puppet.Core.Web.PuppetWebServer server,
            System.Action<AppAgentOptions> configure = null)
        {
            FormAppAgentUiText.Register();
            return server.UseAppAgent(configure);
        }
    }
}
