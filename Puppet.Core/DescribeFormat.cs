namespace Puppet.Core
{
    /// <summary>能力描述输出格式</summary>
    public enum DescribeFormat : byte
    {
        /// <summary>JSON 格式（默认，minify 降低流量）</summary>
        Json = 0,
        /// <summary>Markdown 格式（人类可读）</summary>
        Markdown = 1
    }

    /// <summary>
    /// 信息来源标识，区分 Agent 维护的可靠信息与人类维护的次级参考信息。
    /// &lt;summary&gt; 由人类开发者维护，可能陈旧或失真，仅作次级参考。
    /// </summary>
    public enum InfoSource : byte
    {
        /// <summary>反射推断（类型/名称/签名等，可靠）</summary>
        Reflection = 0,
        /// <summary>人类开发者维护的 XML &lt;summary&gt; 注释（可能陈旧失真，仅作次级参考）</summary>
        HumanSummary = 1,
        /// <summary>[PuppetDescription] 特性补充（源码端显式标注，比 summary 更新）</summary>
        PuppetDescription = 2
    }
}
