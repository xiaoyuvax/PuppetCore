using System;

namespace Puppet.Core
{
    /// <summary>
    /// Agent 可读写的运行时提示。用于标记避坑、提示、待办等信息。
    /// 可通过 /agent/hints 查询，或随 /agent/describe 一并返回。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
                    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Event | AttributeTargets.Constructor,
                    AllowMultiple = true, Inherited = false)]
    public sealed class PuppetHintAttribute : Attribute
    {
        /// <summary>提示分类：Tip / Warning / Todo / Gotcha / Info 等</summary>
        public string Category { get; }

        /// <summary>提示正文</summary>
        public string Text { get; }

        /// <summary>可选：关联的 Agent ID（谁写的）</summary>
        public string AgentId { get; set; }

        /// <summary>可选：创建时间（ISO8601），不填自动填充</summary>
        public string CreatedAt { get; set; }

        public PuppetHintAttribute(string category, string text)
        {
            Category = category ?? "Info";
            Text = text ?? "";
            CreatedAt = DateTime.UtcNow.ToString("o");
        }
    }

    /// <summary>
    /// 运行时解析出的 Hint 信息（含声明位置）
    /// </summary>
    public sealed class PuppetHintInfo
    {
        public string TargetType { get; set; }      // 所属类型全名
        public string TargetMember { get; set; }    // 成员名，类级则为空
        public string MemberKind { get; set; }      // Type/Method/Property/Field/Event/Constructor
        public string Category { get; set; }
        public string Text { get; set; }
        public string AgentId { get; set; }
        public string CreatedAt { get; set; }
    }
}