namespace Puppet.Core
{
    /// <summary>标记不允许暴露给 Agent 的方法/属性/字段/事件。反射描述时自动跳过。</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event)]
    public sealed class PuppetIgnoreAttribute : Attribute { }
}
