using System.Text;
using Newtonsoft.Json.Linq;
using Puppet.Core.Extentions;
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

        /// <summary>批量读取：paths=A,B,C 一次往返取多个属性；结果数组与单 path 顺序一致。</summary>
        [Fact]
        public void Get_supports_batch_paths_in_one_round_trip()
        {
            var puppet = new FakePuppet { AgentInstanceName = "BatchGet#1", AgentAccessKey = "batch-key" };
            PuppetRegistry.Register(puppet);
            try
            {
                var req = MakeReq("GET", "/agent/get", "batch-key",
                    "name=BatchGet%231&paths=AgentInstanceName,AgentAccessKey");
                Assert.True(PuppetWebHandler.TryHandle(ref req));
                Assert.Equal(200, Concluded(req));

                var json = JObject.Parse(Body(req));
                Assert.True(json.Value<bool>("ok"));
                var results = (JArray)json["results"];
                Assert.Equal(2, results.Count);
                Assert.Equal("BatchGet#1", results[0].Value<string>("value"));
                Assert.Equal("batch-key", results[1].Value<string>("value"));
            }
            finally { PuppetRegistry.Unregister("BatchGet#1"); }
        }

        /// <summary>未给 paths 时维持单 path 旧契约（本测试即旧行为锚点）。</summary>
        [Fact]
        public void Get_without_paths_keeps_single_path_contract()
        {
            var puppet = new FakePuppet { AgentInstanceName = "SingleGet#1", AgentAccessKey = "s" };
            PuppetRegistry.Register(puppet);
            try
            {
                var req = MakeReq("GET", "/agent/get", "s", "name=SingleGet%231&path=AgentInstanceName");
                Assert.True(PuppetWebHandler.TryHandle(ref req));
                var json = JObject.Parse(Body(req));
                Assert.True(json.Value<bool>("ok"));
                Assert.Equal("SingleGet#1", json.Value<string>("value"));
                Assert.Null(json["results"]);   // 不是批量形状
            }
            finally { PuppetRegistry.Unregister("SingleGet#1"); }
        }

        /// <summary>ListView 条目的复杂对象 note 必须点名 .Tag —— 实测曾把 .Text 的列宽截断值当真数据用。</summary>
        [Fact]
        public void Get_list_view_item_note_points_to_Tag_not_truncated_Text()
        {
            var host = new ListViewHost();
            var json = JObject.Parse(host.GetProperty("Item"));
            Assert.True(json.Value<bool>("ok"));
            // Newtonsoft 对 JSON null 返回 JValue(Null) 而非 C# null，判据看 TokenType
            Assert.Equal(JTokenType.Null, json["value"].Type);
            Assert.EndsWith("ListViewItem", json.Value<string>("type"));
            var note = json.Value<string>("note");
            Assert.Contains("Tag", note);
            Assert.Contains("truncated", note);
        }

        /// <summary>非 ListViewItem 的复杂对象仍用通用 note（不误伤其它类型）。</summary>
        [Fact]
        public void Get_other_complex_object_keeps_generic_note()
        {
            var host = new ListViewHost();
            var json = JObject.Parse(host.GetProperty("Other"));
            var note = json.Value<string>("note");
            Assert.Contains("complex object", note);
            Assert.DoesNotContain("Tag", note);
        }

        /// <summary>访问/可见度解耦：非 public 成员无 [PuppetExpose] 也可读/调；[PuppetIgnore] 仍拦截。</summary>
        [Fact]
        public void Non_public_members_accessible_without_PuppetExpose()
        {
            var puppet = new FakePuppet { AgentInstanceName = "ExposeDecouple#1", AgentAccessKey = "x" };
            PuppetRegistry.Register(puppet);
            try
            {
                var get = MakeReq("GET", "/agent/get", "x", "name=ExposeDecouple%231&path=SecretNumber");
                Assert.True(PuppetWebHandler.TryHandle(ref get));
                var gj = JObject.Parse(Body(get));
                Assert.True(gj.Value<bool>("ok"));
                Assert.Equal(42, gj.Value<int>("value"));

                var payload = Encoding.UTF8.GetBytes("[]");
                var inv = MakeReq("POST", "/agent/invoke", "x", "name=ExposeDecouple%231&method=SecretText");
                inv.InputStream = new MemoryStream(payload);
                inv.ContentLength = payload.Length;
                Assert.True(PuppetWebHandler.TryHandle(ref inv));
                Assert.Equal("secret-ok", JObject.Parse(Body(inv)).Value<string>("result"));

                var hidden = MakeReq("GET", "/agent/get", "x", "name=ExposeDecouple%231&path=HiddenNumber");
                Assert.True(PuppetWebHandler.TryHandle(ref hidden));
                Assert.False(JObject.Parse(Body(hidden)).Value<bool>("ok"));
            }
            finally { PuppetRegistry.Unregister("ExposeDecouple#1"); }
        }

        /// <summary>可见度仍解耦：无 [PuppetExpose] 的非 public 成员不进 describe 广告。</summary>
        [Fact]
        public void Describe_omits_non_public_members_without_PuppetExpose()
        {
            var puppet = new FakePuppet { AgentInstanceName = "ExposeDescribe#1" };
            PuppetRegistry.Register(puppet);
            try
            {
                var req = MakeReq("GET", "/agent/describe", PuppetKeyVault.GlobalKey, "name=ExposeDescribe%231");
                Assert.True(PuppetWebHandler.TryHandle(ref req));
                var body = Body(req);
                Assert.DoesNotContain("SecretNumber", body);
                Assert.DoesNotContain("SecretText", body);
            }
            finally { PuppetRegistry.Unregister("ExposeDescribe#1"); }
        }

        /// <summary>宿主：一个名为 ListViewItem 的类（Puppet.Core 不引用 WinForms，note 判定按类型名）。</summary>
        private sealed class ListViewHost : IPuppet
        {
            public string AgentAccessKey { get; set; } = "k";
            public Common.Logging.ILog AgentLog { get; set; }
            public string AgentInstanceName { get; set; } = "LvHost#1";

            public ListViewItem Item { get; } = new ListViewItem
            {
                Text = "【标准脱衣】一个女孩，赛博…",     // 列宽截断的显示值
                Tag = new { Prompt = "【标准脱衣】一个女孩，赛博朋克，8K，细节丰富" }
            };

            public NotListViewItem Other { get; } = new NotListViewItem();
        }

        private sealed class ListViewItem
        {
            public string Text { get; set; }
            public object Tag { get; set; }
        }

        private sealed class NotListViewItem
        {
            public int Value { get; set; } = 42;
        }
    }
}
