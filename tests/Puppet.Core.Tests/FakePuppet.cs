using Puppet.Core;
using Common.Logging;

namespace Puppet.Core.Tests
{
    /// <summary>最小 IPuppet 测试桩：仅实现三属性，供 Registry/KeyVault/Handler 测试。</summary>
    public sealed class FakePuppet : IPuppet
    {
        public string AgentAccessKey { get; set; } = "test-key";
        public ILog AgentLog { get; set; }
        public string AgentInstanceName { get; set; } = "Fake#1";

        /// <summary>invoke 测试用：记录调用时 DialogBroker 是否激活（1.2.8 行为锚点）。</summary>
        public bool BrokerActiveDuringInvoke { get; private set; }

        /// <summary>A 通道 invoke 目标方法：探测调用瞬间 DialogBroker 上下文是否在。</summary>
        public bool ProbeBrokerActive() => DialogBrokerActiveProbe();

        private bool DialogBrokerActiveProbe()
        {
            BrokerActiveDuringInvoke = Puppet.Core.AppAgent.DialogBroker.IsActive;
            return BrokerActiveDuringInvoke;
        }
    }
}
