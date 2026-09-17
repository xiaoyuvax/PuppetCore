namespace Puppet.Core
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event)]
    public sealed class PuppetTestAttribute : Attribute { }
}
