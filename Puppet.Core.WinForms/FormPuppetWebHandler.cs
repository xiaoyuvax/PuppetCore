using System.Text;
using Wima.Web;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// WinForm 专用 /agent/control 端点处理器。
    /// 消费方在 ProcessWebRequest 中链式调用。
    /// 顺序重要：本处理器前缀（/agent/control）更具体，须先于 PuppetWebHandler 执行，
    /// 否则请求会被 PuppetWebHandler 的 default 分支以 404 终结（它对任何 /agent/* 前缀都返回 true）：
    /// <code>
    /// if (FormPuppetWebHandler.TryHandle(ref req)) { ... }
    /// else if (PuppetWebHandler.TryHandle(ref req)) { ... }
    /// </code>
    /// PuppetWebServer（模式 B/C）请直接用 UseFormControls() 扩展方法挂接，无需手写链。
    /// </summary>
    public static class FormPuppetWebHandler
    {
        /// <summary>端点前缀</summary>
        public const string CONTROL_PREFIX = "/agent/control";

        /// <summary>尝试处理 /agent/control 请求。返回 true 表示已处理。</summary>
        public static bool TryHandle(ref WebRequest req)
        {
            if (!req.Path.StartsWith(CONTROL_PREFIX)) return false;

            var key = ExtractKey(req);
            var name = req.Queries?["name"];
            var target = PuppetRegistry.Resolve(name);

            if (target == null || !PuppetKeyVault.Authorize(target, key))
            { req.Response.SetStatus404(); return true; }

            if (target is not System.Windows.Forms.Control)
            { req.Response.SetStatus404(); return true; }

            var ctrl = req.Queries?["ctrl"];

            switch (req.Path)
            {
                case "/agent/control" when req.Method == "GET":
                    {
                        if (string.IsNullOrEmpty(ctrl))
                            req.Response.Buffer = Encoding.UTF8.GetBytes(target.DescribeControls());
                        else
                            req.Response.Buffer = Encoding.UTF8.GetBytes(target.GetControlState(ctrl));
                        break;
                    }
                case "/agent/control" when req.Method == "POST":
                    {
                        var action = req.Queries?["action"];
                        var value = req.Queries?["value"];
                        req.Response.Buffer = Encoding.UTF8.GetBytes(
                            target.InvokeControl(ctrl, action, value));
                        break;
                    }
                default:
                    req.Response.SetStatus404();
                    return true;
            }

            req.Response.SetJsonContent();
            return true;
        }

        private static string ExtractKey(WebRequest req)
        {
            var auth = req.Headers?.Get("Authorization");
            if (string.IsNullOrEmpty(auth)) return null;
            return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth["Bearer ".Length..] : auth;
        }
    }
}
