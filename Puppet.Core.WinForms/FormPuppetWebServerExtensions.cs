using Puppet.Core.Web;
using Wima.Web;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// PuppetWebServer 的 WinForm 扩展：挂接 /agent/control 控件端点。
    /// 模式 B/C（宿主无服务器或双服务器）WinForm 项目用法：
    /// <code>
    /// PuppetKeyVault.SetKey("your-secret");
    /// new PuppetWebServer().UseFormControls().Start("0.0.0.0:9090");
    /// </code>
    /// </summary>
    public static class FormPuppetWebServerExtensions
    {
        /// <summary>
        /// 挂接 WinForm 控件端点（/agent/control）到 PuppetWebServer 扩展处理器链。
        /// 处理器先于内建 /agent/* 端点执行（PuppetWebHandler 会以 404 终结未匹配的 /agent/* 路径）。
        /// </summary>
        /// <param name="server">PuppetWebServer 实例</param>
        public static PuppetWebServer UseFormControls(this PuppetWebServer server)
        {
            // 同时注入 WinForms 的 UI 能力适配器：使框架对 Form/Control 自动 Actionize UI 基础 action
            // （移动/缩放/可见性/窗口态/关闭，面向 B /appagent/*），宿主无需逐窗体声明。
            WinFormsPuppetUiActions.UsePuppetUiActions();
            return server.UseHandler(req => FormPuppetWebHandler.TryHandle(ref req));
        }
    }
}
