using System.Linq;
using Newtonsoft.Json.Linq;
using Puppet.Core.Describe;
using Puppet.Core.Extentions;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>
    /// 诊断面锚点：属性名写错时直接给候选（消灭"猜名字再试一轮"），以及 describe?values=1 一次拿到状态现值。
    /// </summary>
    public class PuppetDiagnosticsTests
    {
        [Fact]
        public void Get_unknown_property_returns_candidates()
        {
            var p = new FakePuppet { AgentInstanceName = "Cand#1" };
            var json = JObject.Parse(p.GetProperty("NoSuchProp"));

            Assert.False(json.Value<bool>("ok"));
            var candidates = json.Value<JArray>("candidates");
            Assert.NotNull(candidates);
            Assert.Contains("AgentInstanceName", candidates.Values<string>());
        }

        [Fact]
        public void Get_unknown_nested_property_returns_parent_candidates()
        {
            var p = new FakePuppet { AgentInstanceName = "Cand#2" };
            var json = JObject.Parse(p.GetProperty("AgentInstanceName.NoSuchProp"));

            Assert.False(json.Value<bool>("ok"));
            Assert.Contains("Length", json.Value<JArray>("candidates").Values<string>());
        }

        [Fact]
        public void Get_null_intermediate_value_has_no_candidates_noise()
        {
            var p = new FakePuppet { AgentInstanceName = "Cand#3" };
            var json = JObject.Parse(p.GetProperty("AgentLog.Width"));

            Assert.True(json.Value<bool>("ok"));
            Assert.Null(json["candidates"]);
        }

        [Fact]
        public void Describe_with_values_fills_simple_properties_only()
        {
            var p = new FakePuppet { AgentInstanceName = "Val#1" };
            var json = JObject.Parse(p.Describe(values: true));

            var props = json.Value<JArray>("Properties");
            var name = props.First(x => x.Value<string>("Name") == "AgentInstanceName");
            Assert.Equal("Val#1", name.Value<string>("Value"));

            var log = props.FirstOrDefault(x => x.Value<string>("Name") == "AgentLog");
            if (log != null) Assert.Null(log["Value"]);
        }

        [Fact]
        public void Describe_without_values_has_no_value_fields()
        {
            var json = JObject.Parse(new FakePuppet { AgentInstanceName = "Val#2" }.Describe());
            Assert.All(json.Value<JArray>("Properties"), x => Assert.Null(x["Value"]));
        }
    }
}
