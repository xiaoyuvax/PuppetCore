using System;

namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// 将成员收录为用户 Agent 可执行的操作（覆盖标注，用于补漏与特例）。
    /// 范式内的成员（public + bool/OperationResult/Task&lt;bool&gt; 等成败返回，见 ActionizePolicy）
    /// 无需此特性即自动收录；本特性用于：1) 改 action name；2) 收录范式外成员；3) 补充文案。
    /// [PuppetIgnore] 永远优先：打了 Ignore 的成员即使带此特性也不暴露。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class PuppetActionAttribute : Attribute
    {
        /// <summary>操作名（稳定契约，默认 = 方法名）。发布后改名 = 破坏性变更。</summary>
        public string Name { get; }

        /// <summary>面向最终用户的操作描述（缺省时走三层文案解析，无需手写）。</summary>
        public string Desc { get; set; }

        /// <summary>风险级别（Low/Medium/High），输出到 manifest 供用户 Agent 自行判断；缺省不输出。</summary>
        public string Risk { get; set; }

        /// <summary>操作分组名（可选，对应类级 [PuppetActionGroup] 声明）。</summary>
        public string Group { get; set; }

        public PuppetActionAttribute() { }

        public PuppetActionAttribute(string name) => Name = name;
    }

    /// <summary>
    /// 将属性收录为用户 Agent 可读取的状态（覆盖标注）。
    /// 范式内成员（public + 简单类型）无需此特性；本特性用于收录复杂类型属性或改名。
    /// [PuppetIgnore] 永远优先。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class PuppetStateAttribute : Attribute
    {
        /// <summary>状态键（稳定契约，默认 = 属性名）。</summary>
        public string Name { get; }

        /// <summary>面向最终用户的状态描述（缺省时走三层文案解析）。</summary>
        public string Desc { get; set; }

        public PuppetStateAttribute() { }

        public PuppetStateAttribute(string name) => Name = name;
    }

    /// <summary>参数级说明（可选）：面向用户的参数描述与敏感标记（审计脱敏用）。</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class PuppetParamAttribute : Attribute
    {
        /// <summary>参数名（标在方法上时用于按名匹配参数；标在参数上时忽略此项）。</summary>
        public string Name { get; set; }

        /// <summary>面向最终用户的参数说明（取值范围、格式约束等）。</summary>
        public string Desc { get; set; }

        /// <summary>敏感参数：审计/日志中脱敏，不出现在明细里。</summary>
        public bool Sensitive { get; set; }

        public PuppetParamAttribute() { }
    }

    /// <summary>操作分组声明（类级，可选）：描述器按组组织 manifest，方便用户 Agent 呈现菜单。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class PuppetActionGroupAttribute : Attribute
    {
        public string Name { get; }
        public string Desc { get; set; }

        public PuppetActionGroupAttribute(string name) => Name = name;
    }

    /// <summary>
    /// 应用信息声明（程序集级或实例类级，可选）：填充 manifest 的 app 段。
    /// 不自动收集程序集信息——manifest 只含显式声明（安全默认）。
    /// 缺省时 manifest.app 为 null，用户 Agent 以发现档案中的 productName 兜底。
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class PuppetAppInfoAttribute : Attribute
    {
        public string Name { get; }
        public string Version { get; set; }
        public string Vendor { get; set; }

        public PuppetAppInfoAttribute(string name) => Name = name;
    }
}
