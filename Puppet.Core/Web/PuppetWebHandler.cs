using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Puppet.Core.Describe;
using Puppet.Core.Extentions;
using Puppet.Core.Usage;
using Wima.Core;
using Wima.Log;
using Wima.Web;

namespace Puppet.Core.Web
{
    /// <summary>
    /// /agent/* 端点处理器（静态类）。
    /// 模式 A：在 WebServerBase 子类 ProcessWebRequest 中调用 TryHandle。
    /// 模式 B/C：由 PuppetWebServer 内部调用。
    /// </summary>
    public static class PuppetWebHandler
    {
        /// <summary>端点前缀</summary>
        public const string PREFIX = "/agent";

        /// <summary>尝试处理 /agent/* 请求。返回 true 表示已处理。</summary>
        public static bool TryHandle(ref WebRequest req)
        {
            if (!req.Path.StartsWith(PREFIX)) return false;

            PuppetUsage.Track("endpoint", $"{req.Method} {req.Path} name={req.Queries?["name"]}");
            try
            {
                return HandleCore(ref req);
            }
            catch (Exception ex)
            {
                PuppetUsage.Error("endpoint", $"{req.Method} {req.Path}", ex);
                req.Response.StatusCode = 500;
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = false, err = ex.Message }));
                req.Response.SetJsonContent();
                return true;
            }
        }

        /// <summary>端点分发核心逻辑（经 TryHandle 包一层使用日志 + 意外上报）</summary>
        private static bool HandleCore(ref WebRequest req)
        {
            var key = ExtractKey(req);
            var name = req.Queries?["name"];
            var target = PuppetRegistry.Resolve(name);

            // 元端点：条件查询已注册实例（仅全局密钥）
            if (req.Path == "/agent/registry" && req.Method == "GET")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                var result = PuppetRegistry.Query(
                    typeFilter: req.Queries["type"],
                    tagFilter: SplitCsv(req.Queries["tags"]),
                    includeInternal: req.Queries["includeInternal"] == "true",
                    activeWithinMinutes: ParseInt(req.Queries["activeWithin"], 0),
                    keyword: req.Queries["keyword"],
                    offset: ParseInt(req.Queries["offset"], 0),
                    limit: ParseInt(req.Queries["limit"], 0));
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(result));
                req.Response.SetJsonContent();
                return true;
            }

            // 刷新密钥端点（仅全局密钥）
            if (req.Path == "/agent/key/refresh" && req.Method == "POST")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                var newKey = PuppetKeyVault.RefreshKey();
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new { ok = true, newKey }));
                req.Response.SetJsonContent();
                return true;
            }

            // 使用率摘要端点（仅全局密钥）：返回各功能调用次数，供 Agent 做利用率与迭代清理由
            if (req.Path == "/agent/usage" && req.Method == "GET")
            {
                if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                var counts = PuppetUsage.Counts();
                req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(new
                {
                    ok = true,
                    total = PuppetUsage.Total(),
                    enabled = PuppetUsage.Enabled,
                    counts
                }));
                req.Response.SetJsonContent();
                return true;
            }

            // 通用日志端点
            if (req.Path == "/agent/logs" && req.Method == "GET")
            {
                if (string.IsNullOrEmpty(name))
                {
                    if (!PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }
                    var names = WimaLogger.LogBook.Keys.OrderBy(k => k).ToArray();
                    req.Response.Buffer = Encoding.UTF8.GetBytes(Utils.ToJson(names));
                }
                else
                {
                    if (target != null && !PuppetKeyVault.Authorize(target, key)) { req.Response.SetStatus404(); return true; }
                    else if (target == null && !PuppetKeyVault.AuthorizeGlobal(key)) { req.Response.SetStatus404(); return true; }

                    if (WimaLogger.LogBook.TryGetValue(name, out var logger))
                    {
                        var buf = logger.LogBuf ?? "";
                        var maxLen = ParseInt(req.Queries["maxLen"], 131072);
                        if (maxLen > 0 && buf.Length > maxLen) buf = buf[^maxLen..];
                        req.Response.Buffer = Encoding.UTF8.GetBytes(buf);
                    }
                    else { req.Response.SetStatus404(); return true; }
                }
                req.Response.SetJsonContent();
                return true;
            }

            // 以下端点需要实例 + 密钥校验
            if (target == null || !PuppetKeyVault.Authorize(target, key))
            { req.Response.SetStatus404(); return true; }

            switch (req.Path)
            {
                case "/agent/describe" when req.Method == "GET":
                    {
                        var fmt = req.Queries["format"] == "md" ? DescribeFormat.Markdown : DescribeFormat.Json;
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.Describe(fmt));
                        break;
                    }
                case "/agent/state" when req.Method == "GET":
                    {
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.GetState(
                            req.Queries["path"],
                            SplitCsv(req.Queries["include"]),
                            SplitCsv(req.Queries["exclude"])));
                        break;
                    }
                case "/agent/get" when req.Method == "GET":
                    {
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.GetProperty(req.Queries["path"]));
                        break;
                    }
                case "/agent/set" when req.Method == "POST":
                    {
                        // Body: { "path": "txtName.Text", "value": <any> }
                        var parsed = ParseSetBody(req.InputStream);
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.SetProperty(parsed.path, parsed.value));
                        break;
                    }
                case "/agent/invoke" when req.Method == "POST":
                    {
                        var args = ParseJsonArgs(req.InputStream);
                        req.Response.Buffer = Encoding.UTF8.GetBytes(target.Invoke(req.Queries["method"], args));
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

        private static string[] SplitCsv(string s) =>
            string.IsNullOrEmpty(s) ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries);

        private static int ParseInt(string s, int defaultValue) =>
            int.TryParse(s, out var v) ? v : defaultValue;

        private static object[] ParseJsonArgs(Stream input)
        {
            if (input == null || !input.CanRead) return Array.Empty<object>();
            using var reader = new StreamReader(input, Encoding.UTF8, false);
            var body = reader.ReadToEnd();
            if (string.IsNullOrEmpty(body)) return Array.Empty<object>();
            try
            {
                var arr = JArray.Parse(body);
                return arr.Cast<object>().ToArray();
            }
            catch
            {
                // 非 JSON 数组，尝试单值
                try
                {
                    var token = JToken.Parse(body);
                    return new object[] { token };
                }
                catch { return Array.Empty<object>(); }
            }
        }

        /// <summary>
        /// 解析 /agent/set 的请求体，格式 { "path": "txtName.Text", "value": 任意值 }。
        /// value 可为标量/对象/数组，保持 JToken 形态由 SetProperty 按目标类型反序列化。
        /// </summary>
        private static (string path, object value) ParseSetBody(Stream input)
        {
            if (input == null || !input.CanRead) return (null, null);
            using var reader = new StreamReader(input, Encoding.UTF8, false);
            var body = reader.ReadToEnd();
            if (string.IsNullOrEmpty(body)) return (null, null);
            try
            {
                var obj = JObject.Parse(body);
                var path = obj["path"]?.ToString();
                var value = obj["value"];
                return (path, value);
            }
            catch
            {
                return (null, null);
            }
        }
    }
}
