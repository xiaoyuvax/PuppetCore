namespace Puppet.Core
{
    /// <summary>
    /// 广告可见度标记：把非 public 成员（方法/属性/字段/事件）列入 /agent/describe 的广告清单，
    /// 让 Agent 在“不读源码”时也能发现它。值可选填 <see cref="Reason"/> 说明用途。
    ///
    /// 与访问裁决<b>解耦</b>：
    /// - <b>访问</b>：public 优先；非 public 同样可访问，唯 <see cref="PuppetIgnoreAttribute"/> 能阻止访问。
    ///   即 Agent 只要知道成员名/路径，无需任何标记即可读/写/调用。
    /// - <b>可见度</b>：/agent/describe 只广告 public + [PuppetExpose] 成员，避免内部成员噪声淹没信号。
    /// - <see cref="PuppetIgnoreAttribute"/> 优先级最高：标记后既不广告、也不可访问。
    ///
    /// 设计哲学（来自架构约束）：
    /// - 不修改成员的可访问性修饰符（public/protected/private 均保持原状），保留最终产品的封装形态。
    /// - 访问安全边界 = 密钥（PuppetKeyVault），而非成员标注；开发期 Agent 与宿主同一信任域。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event)]
    public sealed class PuppetExposeAttribute : Attribute
    {
        /// <summary>
        /// 暴露的语义说明（可选）。用于 /agent/describe 时展示此成员为何被暴露（如 "测试链路" "调试入口"）。
        /// </summary>
        public string Reason { get; set; }
    }
}
