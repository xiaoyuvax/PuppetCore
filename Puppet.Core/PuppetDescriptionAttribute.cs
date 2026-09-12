namespace Puppet.Core
{
    /// <summary>
    /// 当程序集无 XML 文档文件时，用此特性携带运行时描述。
    /// 优先级高于 &lt;summary&gt; 注释，但仍属人类维护范畴，Agent 需验证。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class)]
    public sealed class PuppetDescriptionAttribute : Attribute
    {
        /// <summary>描述文本</summary>
        public string Description { get; }

        /// <param name="description">描述文本</param>
        public PuppetDescriptionAttribute(string description) => Description = description;
    }
}
