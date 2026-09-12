namespace Puppet.Core
{
    /// <summary>
    /// 标记需要暴露给 Agent 的非 public 成员（方法/属性/字段/事件）。
    /// 与 <see cref="PuppetIgnoreAttribute"/> 互斥：[PuppetIgnore] 优先级最高，标记后无论如何都不暴露。
    ///
    /// 设计哲学（来自架构约束）：
    /// - 不修改成员的可访问性修饰符（public/protected/private 均保持原状），保留最终产品的封装形态。
    /// - 通过特性"声明式暴露"：开发者显式声明某个非 public 成员可被 Agent 测试/调用，语义清晰。
    /// - 框架统一规则：Puppet 反射时，public 成员默认可访问；NonPublic 成员需带 [PuppetExpose] 才可访问。
    /// - 跨项目复用：作为 Puppet.Core 框架的一部分，所有引用此库的项目共享同一套暴露规范。
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
