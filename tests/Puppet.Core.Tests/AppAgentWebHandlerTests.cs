using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Puppet.Core.AppAgent;
using Puppet.Core.Web;
using Wima.Web;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>
    /// AppAgentWebHandler 实例解析与错误契约锚：单实例省略 ?name= 默认、
    /// 多实例省略 ?name= 拒绝（400 instance-name-required）、未命中错误携带 availableInstances 供 B 面枚举。
    /// </summary>
    public class AppAgentWebHandlerTests
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

        private static int Concluded(WebRequest req) => req.Response.ConcludeStatusCode().StatusCode;

        private sealed class Host : IPuppet
        {
            public string AgentAccessKey { get; set; } = "k";
            public Common.Logging.ILog AgentLog { get; set; }
            public string AgentInstanceName { get; set; }
        }

        /// <summary>绑定并返回档案 key（每次用例独立产品名，档案文件互不干扰；用例须在 finally Unbind）。</summary>
        private static (AppAgentRuntime runtime, string key) StartRuntime(params string[] instanceNames)
        {
            var runtime = new AppAgentRuntime(new AppAgentOptions
            {
                ProductName = $"AppAgentTests.{Guid.NewGuid():N}",
                InstanceNames = instanceNames
            });
            runtime.Bind(0);
            Assert.NotNull(runtime.ProfileFile);
            var key = JObject.Parse(File.ReadAllText(runtime.ProfileFile)).Value<string>("key");
            Assert.False(string.IsNullOrEmpty(key));
            return (runtime, key);
        }

        private static Host Register(string name)
        {
            var p = new Host { AgentInstanceName = name };
            PuppetRegistry.Register(p);
            return p;
        }

        [Fact]
        public void Manifest_without_name_defaults_to_single_instance()
        {
            const string n = "AppAgentT.Single";
            Register(n);
            var (runtime, key) = StartRuntime(n);
            try
            {
                var req = MakeReq("GET", "/appagent/manifest", key);
                Assert.True(AppAgentWebHandler.TryHandle(ref req, runtime));
                Assert.Equal(200, Concluded(req));
                var json = JObject.Parse(Body(req));
                Assert.Equal(n, json.Value<string>("instance"));
            }
            finally { runtime.Unbind(); PuppetRegistry.Unregister(n); }
        }

        [Fact]
        public void Multiple_instances_without_name_return_400_with_available_names()
        {
            const string a = "AppAgentT.MultiA";
            const string b = "AppAgentT.MultiB";
            Register(a); Register(b);
            var (runtime, key) = StartRuntime(a, b);
            try
            {
                var req = MakeReq("GET", "/appagent/manifest", key); // 省略 ?name=
                Assert.True(AppAgentWebHandler.TryHandle(ref req, runtime));
                Assert.Equal(400, Concluded(req));
                var json = JObject.Parse(Body(req));
                Assert.False(json.Value<bool>("ok"));
                Assert.Equal("instance-name-required", json.Value<string>("code"));
                var names = ((JArray)json["availableInstances"]).Values<string>().ToArray();
                Assert.Equal(2, names.Length);
                Assert.Contains(a, names);
                Assert.Contains(b, names);
            }
            finally { runtime.Unbind(); PuppetRegistry.Unregister(a); PuppetRegistry.Unregister(b); }
        }

        [Fact]
        public void Unknown_instance_name_returns_404_with_available_names()
        {
            const string known = "AppAgentT.Known";
            const string missing = "AppAgentT.Missing";
            Register(known);
            var (runtime, key) = StartRuntime(known);
            try
            {
                var req = MakeReq("GET", "/appagent/manifest", key, "name=" + missing);
                Assert.True(AppAgentWebHandler.TryHandle(ref req, runtime));
                Assert.Equal(404, Concluded(req));
                var json = JObject.Parse(Body(req));
                Assert.False(json.Value<bool>("ok"));
                Assert.Equal("instance-not-found", json.Value<string>("code"));
                Assert.Contains(missing, json.Value<string>("err"));
                var names = ((JArray)json["availableInstances"]).Values<string>().ToArray();
                Assert.Equal(new[] { known }, names);
            }
            finally { runtime.Unbind(); PuppetRegistry.Unregister(known); }
        }

        [Fact]
        public void No_bound_instances_returns_404_with_empty_available()
        {
            var (runtime, key) = StartRuntime(); // 零实例绑定
            try
            {
                var req = MakeReq("GET", "/appagent/manifest", key);
                Assert.True(AppAgentWebHandler.TryHandle(ref req, runtime));
                Assert.Equal(404, Concluded(req));
                var json = JObject.Parse(Body(req));
                Assert.Equal("instance-not-found", json.Value<string>("code"));
                Assert.NotNull(json["availableInstances"]);
                Assert.Empty(((JArray)json["availableInstances"]).Values<string>());
            }
            finally { runtime.Unbind(); }
        }

        [Fact]
        public void Invalid_key_error_hint_points_to_pid_suffixed_profile_file()
        {
            const string n = "AppAgentT.KeyHint";
            Register(n);
            var (runtime, _) = StartRuntime(n);
            try
            {
                var req = MakeReq("GET", "/appagent/manifest", "wrong-key");
                Assert.True(AppAgentWebHandler.TryHandle(ref req, runtime));
                Assert.Equal(401, Concluded(req));
                var json = JObject.Parse(Body(req));
                Assert.Equal("credential-invalid", json.Value<string>("code"));
                // 档案文件名带 pid 后缀（<app>.<pid>.json），hint 必须指对——历史文案曾漏 .<pid> 导致 agent 找不到文件
                Assert.Contains("<app>.<pid>.json", json.Value<string>("hint"));
            }
            finally { runtime.Unbind(); PuppetRegistry.Unregister(n); }
        }
    }
}
