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
            => server.UseHandler(req => FormPuppetWebHandler.TryHandle(ref req));
    }
}
