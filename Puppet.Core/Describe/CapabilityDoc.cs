namespace Puppet.Core.Describe
{
    /// <summary>能力描述文档（schema，不含运行时值）</summary>
    public class CapabilityDoc
    {
        /// <summary>实例名</summary>
        public string InstanceName { get; set; }
        /// <summary>类型全名</summary>
        public string TypeName { get; set; }
        /// <summary>程序集名</summary>
        public string AssemblyName { get; set; }
        /// <summary>类型级 summary（人类维护，次级参考）</summary>
        public string Summary { get; set; }
        /// <summary>Summary 信息来源标识</summary>
        public InfoSource SummarySource { get; set; }
        /// <summary>属性列表</summary>
        public List<PropertyDesc> Properties { get; set; }
        /// <summary>方法列表</summary>
        public List<MethodDesc> Methods { get; set; }
        /// <summary>字段列表</summary>
        public List<FieldDesc> Fields { get; set; }
    }

    /// <summary>属性描述</summary>
    public class PropertyDesc
    {
        /// <summary>属性名</summary>
        public string Name { get; set; }
        /// <summary>类型全名</summary>
        public string Type { get; set; }
        /// <summary>summary（人类维护，次级参考）</summary>
        public string Summary { get; set; }
        /// <summary>Summary 信息来源</summary>
        public InfoSource? SummarySource { get; set; }
        /// <summary>[PuppetDescription] 补充描述</summary>
        public string Description { get; set; }
        /// <summary>是否可读</summary>
        public bool CanRead { get; set; }
        /// <summary>是否可写</summary>
        public bool CanWrite { get; set; }
    }

    /// <summary>方法描述</summary>
    public class MethodDesc
    {
        /// <summary>方法名</summary>
        public string Name { get; set; }
        /// <summary>返回类型全名</summary>
        public string ReturnType { get; set; }
        /// <summary>summary（人类维护，次级参考）</summary>
        public string Summary { get; set; }
        /// <summary>Summary 信息来源</summary>
        public InfoSource? SummarySource { get; set; }
        /// <summary>[PuppetDescription] 补充描述</summary>
        public string Description { get; set; }
        /// <summary>参数列表</summary>
        public List<ParamDesc> Parameters { get; set; }
    }

    /// <summary>参数描述</summary>
    public class ParamDesc
    {
        /// <summary>参数名</summary>
        public string Name { get; set; }
        /// <summary>参数类型全名</summary>
        public string Type { get; set; }
        /// <summary>summary（人类维护，次级参考）</summary>
        public string Summary { get; set; }
        /// <summary>Summary 信息来源</summary>
        public InfoSource? SummarySource { get; set; }
        /// <summary>是否可选</summary>
        public bool IsOptional { get; set; }
    }

    /// <summary>字段描述</summary>
    public class FieldDesc
    {
        /// <summary>字段名</summary>
        public string Name { get; set; }
        /// <summary>类型全名</summary>
        public string Type { get; set; }
        /// <summary>summary（人类维护，次级参考）</summary>
        public string Summary { get; set; }
        /// <summary>Summary 信息来源</summary>
        public InfoSource? SummarySource { get; set; }
        /// <summary>[PuppetDescription] 补充描述</summary>
        public string Description { get; set; }
        /// <summary>是否只读</summary>
        public bool IsReadOnly { get; set; }
    }
}
