using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Puppet.Core;
using Puppet.Core.AppAgent;
using Puppet.Core.Web;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>
    /// ActionHub 锚点：内部调用直接落在 ProcessWebRequest 瀑布上——不需要 Start/Bind、不依赖端口与档案文件，
    /// 但 A/B 两面的鉴权、实例解析、action 名大小写与串行语义与外部 HTTP 逐字一致。
    /// </summary>
    public class PuppetHubTests
    {
        private static string Unique(string prefix) => $"HubT.{prefix}.{Guid.NewGuid():N}";

        private static PuppetWebServer MakeServer(params string[] names) => new PuppetWebServer()
            .UseAppAgent(o =>
            {
                o.ProductName = "PuppetHubTests." + Guid.NewGuid().ToString("N");
                o.InstanceNames = names;
            });

        [Fact]
        public async Task Hub_works_without_start_or_bind()
        {
            var name = Unique("NoStart");
            PuppetRegistry.Register(new FakePuppet { AgentInstanceName = name });
            var server = MakeServer(name);
            try
            {
                Assert.Null(server.WebEngine);                 // 未启动任何传输层
                var before = CountOf("hub-local");
                var r = await server.Hub.GetAsync("/agent/registry");
                Assert.Equal(200, r.Status);
                Assert.Contains(name, r.Body);
                Assert.True(CountOf("hub-local") > before);    // 内部调用单独计数，不影响 endpoint 口径
            }
            finally { PuppetRegistry.Unregister(name); }
        }

        private static long CountOf(string category)
            => Puppet.Core.Usage.PuppetUsage.Counts().TryGetValue(category, out var v) ? v : 0;

        [Fact]
        public async Task Hub_b_channel_works_without_bind_or_profile_file()
        {
            var name = Unique("B");
            PuppetRegistry.Register(new FakePuppet { AgentInstanceName = name });
            var server = MakeServer(name);
            try
            {
                var r = await server.Hub.GetAsync("/appagent/manifest", new { name });
                Assert.Equal(200, r.Status);
                Assert.Equal(name, JObject.Parse(r.Body).Value<string>("instance"));
            }
            finally { PuppetRegistry.Unregister(name); }
        }

        [Fact]
        public async Task Hub_action_keeps_case_and_runs_actionize_member()
        {
            var name = Unique("Action");
            var p = new FakePuppet { AgentInstanceName = name };
            PuppetRegistry.Register(p);
            var server = MakeServer(name);
            try
            {
                var ok = await server.Hub.ActionAsync("ProbeBrokerActive", name);
                Assert.Equal(200, ok.Status);
                Assert.True(JObject.Parse(ok.Body).Value<bool>("ok"));
                Assert.True(p.BrokerActiveDuringInvoke);       // 与 HTTP 同一 DialogBroker 上下文

                // action 名区分大小写：path 不能被小写化
                var lower = await server.Hub.ActionAsync("probebrokeractive", name);
                Assert.Equal(404, lower.Status);
                Assert.Equal("action-not-found", JObject.Parse(lower.Body).Value<string>("code"));
            }
            finally { PuppetRegistry.Unregister(name); }
        }

        [Fact]
        public async Task Hub_b_channel_still_requires_valid_key()
        {
            var name = Unique("BadKey");
            PuppetRegistry.Register(new FakePuppet { AgentInstanceName = name });
            var server = MakeServer(name);
            try
            {
                var r = await server.Hub.GetAsync("/appagent/manifest", new { name }, bearerKey: "wrong");
                Assert.Equal(401, r.Status);
                Assert.Equal("credential-invalid", JObject.Parse(r.Body).Value<string>("code"));
            }
            finally { PuppetRegistry.Unregister(name); }
        }

        [Fact]
        public async Task Hub_unknown_path_returns_404()
        {
            var server = MakeServer();
            var r = await server.Hub.GetAsync("/nothing-here");
            Assert.Equal(404, r.Status);
        }

        [Fact]
        public void Profile_key_is_stable_across_bind_cycles()
        {
            var runtime = new AppAgentRuntime(new AppAgentOptions
            {
                ProductName = "PuppetHubTests." + Guid.NewGuid().ToString("N")
            });
            try
            {
                runtime.Bind(0);
                var first = ReadKey(runtime.ProfileFile);
                runtime.Unbind();

                runtime.Bind(0);                              // key 在构造时生成，Bind 不重置
                var second = ReadKey(runtime.ProfileFile);
                Assert.Equal(first, second);
            }
            finally { runtime.Unbind(); }
        }

        private static string ReadKey(string profileFile)
            => JObject.Parse(File.ReadAllText(profileFile)).Value<string>("key");
    }
}
