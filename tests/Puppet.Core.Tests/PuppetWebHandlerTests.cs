using System.Text;
using Newtonsoft.Json.Linq;
using Puppet.Core.Web;
using Wima.Web;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>PuppetWebHandler 核心路径：路由/鉴权/registry/invoke Broker 行为锚（1.2.8）。</summary>
    public class PuppetWebHandlerTests
    {
        private static WebRequest MakeReq(string method, string path, string authKey = null, string query = null)
        {
            var req = new WebRequest
            {
                Method = method,
                InputStream = new MemoryStream(),
            };
            req.Path = path;
            if (!string.IsNullOrEmpty(query))
                foreach (var kv in query.Split('&'))
                {
                    var i = kv.IndexOf('=');
                    req.Queries[kv[..i]] = Uri.UnescapeDataString(kv[(i + 1)..]);
                }
            if (authKey != null)
                req.Headers["Authorization"] = "Bearer " + authKey;
            req.Response = new WebResponse();
            return req;
        }

        private static string Body(WebRequest req) => req.Response.Buffer == null ? null : Encoding.UTF8.GetString(req.Response.Buffer);

        /// <summary>协议约定：处理器只置 Buffer（StatusCode 留 0 待引擎 ConcludeStatusCode 收尾），显式 404 才直接置码。
        /// 本方法模拟引擎收尾后返回状态码。</summary>
        private static int Concluded(WebRequest req) => req.Response.ConcludeStatusCode().StatusCode;

        [Fact]
        public void Non_agent_path_is_ignored()
        {
            var req = MakeReq("GET", "/other/path");
            Assert.False(PuppetWebHandler.TryHandle(ref req));
            Assert.Null(req.Response.Buffer);
        }

        [Fact]
        public void Registry_requires_global_key()
        {
            var bad = MakeReq("GET", "/agent/registry", "wrong-key");
            Assert.True(PuppetWebHandler.TryHandle(ref bad));
            Assert.Equal(404, bad.Response.StatusCode);

            var good = MakeReq("GET", "/agent/registry", PuppetKeyVault.GlobalKey);
            Assert.True(PuppetWebHandler.TryHandle(ref good));
            Assert.Equal(200, Concluded(good));
            var json = JObject.Parse(Body(good));
            Assert.NotNull(json["Instances"]);
        }

        [Fact]
        public void Instance_endpoint_without_valid_key_returns_404()
        {
            var req = MakeReq("GET", "/agent/describe", "wrong-key", "name=NoPuppet");
            Assert.True(PuppetWebHandler.TryHandle(ref req));
            Assert.Equal(404, req.Response.StatusCode);
        }

        [Fact]
        public void Unknown_instance_returns_404()
        {
            var req = MakeReq("GET", "/agent/describe", PuppetKeyVault.GlobalKey, "name=NoSuchInstance");
            Assert.True(PuppetWebHandler.TryHandle(ref req));
            Assert.Equal(404, req.Response.StatusCode);
        }

        [Fact]
        public void Describe_with_registered_instance_returns_200()
        {
            var puppet = new FakePuppet { AgentInstanceName = "DescribeTest#1" };
            PuppetRegistry.Register(puppet);
            try
            {
                var req = MakeReq("GET", "/agent/describe", PuppetKeyVault.GlobalKey, "name=DescribeTest#1");
                Assert.True(PuppetWebHandler.TryHandle(ref req));
                Assert.Equal(200, Concluded(req));
                var json = JObject.Parse(Body(req));
                // describe 响应体 = CapabilityDoc 直接序列化（无 ok 包裹）
                Assert.Equal("DescribeTest#1", json.Value<string>("InstanceName"));
                Assert.Contains("FakePuppet", json.Value<string>("TypeName"));
                Assert.NotNull(json["Methods"]);
            }
            finally { PuppetRegistry.Unregister("DescribeTest#1"); }
        }

        [Fact]
        public void Get_returns_property_value()
        {
            var puppet = new FakePuppet { AgentInstanceName = "GetTest#1", AgentAccessKey = "get-key" };
            PuppetRegistry.Register(puppet);
            try
            {
                var req = MakeReq("GET", "/agent/get", "get-key", "name=GetTest%231&path=AgentInstanceName");
                Assert.True(PuppetWebHandler.TryHandle(ref req));
                Assert.Equal(200, Concluded(req));
                var json = JObject.Parse(Body(req));
                Assert.True(json.Value<bool>("ok"));
                Assert.Equal("GetTest#1", json.Value<string>("value"));
            }
            finally { PuppetRegistry.Unregister("GetTest#1"); }
        }

        /// <summary>1.2.8 行为锚点：A 通道 invoke 在 DialogBroker 作用域内执行宿主方法——
        /// 宿主代码路径上的 PuppetDialog.Ask 自动按安全默认应答，永不阻塞。</summary>
        [Fact]
        public void Invoke_runs_host_method_inside_DialogBroker_scope()
        {
            var puppet = new FakePuppet { AgentInstanceName = "InvokeTest#1", AgentAccessKey = "inv-key" };
            PuppetRegistry.Register(puppet);
            try
            {
                var payload = Encoding.UTF8.GetBytes("[]");
                var req = MakeReq("POST", "/agent/invoke", "inv-key", "name=InvokeTest%231&method=ProbeBrokerActive");
                req.InputStream = new MemoryStream(payload);
                req.ContentLength = payload.Length;
                Assert.True(PuppetWebHandler.TryHandle(ref req));
                Assert.Equal(200, Concluded(req));

                var json = JObject.Parse(Body(req));
                Assert.True(json.Value<bool>("ok"));
                Assert.True(json.Value<bool>("result"), "invoke 应在 DialogBroker 激活上下文中执行（1.2.8 A 通道根修行为锚）");
                Assert.True(puppet.BrokerActiveDuringInvoke);
            }
            finally { PuppetRegistry.Unregister("InvokeTest#1"); }
        }

        [Fact]
        public void Invoke_returns_error_shape_for_missing_method()
        {
            var puppet = new FakePuppet { AgentInstanceName = "InvokeMiss#1", AgentAccessKey = "k" };
            PuppetRegistry.Register(puppet);
            try
            {
                var payload = Encoding.UTF8.GetBytes("[]");
                var req = MakeReq("POST", "/agent/invoke", "k", "name=InvokeMiss%231&method=NoSuchMethod");
                req.InputStream = new MemoryStream(payload);
                req.ContentLength = payload.Length;
                Assert.True(PuppetWebHandler.TryHandle(ref req));
                var json = JObject.Parse(Body(req));
                Assert.False(json.Value<bool>("ok"));   // 未知方法 → ok:false 错误形状（不抛 500）
            }
            finally { PuppetRegistry.Unregister("InvokeMiss#1"); }
        }
    }
}
