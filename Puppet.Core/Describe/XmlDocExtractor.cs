using System.Collections.Concurrent;
using System.Reflection;
using System.Xml;

namespace Puppet.Core.Describe
{
    /// <summary>
    /// 从 XML 文档文件提取 &lt;summary&gt; 注释。
    /// 注意：&lt;summary&gt; 由人类开发者维护，可能陈旧或失真，仅作次级参考。
    /// </summary>
    public static class XmlDocExtractor
    {
        private static readonly ConcurrentDictionary<string, XmlDocument> _cache = new();

        /// <summary>获取成员的 summary 注释及来源</summary>
        /// <param name="member">成员信息</param>
        /// <param name="source">输出：信息来源</param>
        /// <returns>summary 文本，null 表示无</returns>
        public static string GetSummary(MemberInfo member, out InfoSource source)
        {
            source = InfoSource.Reflection;

            // 优先：[PuppetDescription] 特性（源码端显式标注）
            var descAttr = member.GetCustomAttribute<PuppetDescriptionAttribute>();
            if (descAttr != null && !string.IsNullOrEmpty(descAttr.Description))
            {
                source = InfoSource.PuppetDescription;
                return descAttr.Description;
            }

            // 次级参考：XML <summary> 注释（人类维护，可能陈旧）
            var asm = member.DeclaringType?.Assembly ?? (member as Type)?.Assembly;
            if (asm == null) return null;

            var doc = _cache.GetOrAdd(asm.Location, LoadXmlDoc);
            if (doc == null) return null;

            var memberId = BuildXmlDocId(member);
            if (memberId == null) return null;

            var node = doc.SelectSingleNode($"//member[@name='{memberId}']/summary");
            if (node == null) return null;

            var text = node.InnerText?.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            source = InfoSource.HumanSummary;
            return text;
        }

        /// <summary>获取成员的 summary 注释（不返回来源，向后兼容）</summary>
        public static string GetSummary(MemberInfo member)
            => GetSummary(member, out _);

        /// <summary>获取参数的 summary 注释（从 [PuppetDescription] 特性提取，参数级 XML doc 较复杂暂不支持）</summary>
        public static string GetSummary(ParameterInfo param, out InfoSource source)
        {
            source = InfoSource.Reflection;
            // 参数级 XML doc 提取较复杂（需完整方法签名匹配），暂仅支持 [PuppetDescription]
            // ParameterInfo 本身不支持特性？实际可以：param.GetCustomAttribute
            var descAttr = param.GetCustomAttribute<PuppetDescriptionAttribute>();
            if (descAttr != null && !string.IsNullOrEmpty(descAttr.Description))
            {
                source = InfoSource.PuppetDescription;
                return descAttr.Description;
            }
            return null;
        }

        private static XmlDocument LoadXmlDoc(string assemblyPath)
        {
            var xmlPath = Path.ChangeExtension(assemblyPath, ".xml");
            if (!File.Exists(xmlPath)) return null;
            try
            {
                var doc = new XmlDocument { PreserveWhitespace = false };
                doc.Load(xmlPath);
                return doc;
            }
            catch { return null; }
        }

        private static string BuildXmlDocId(MemberInfo member)
        {
            return member switch
            {
                Type t => $"T:{t.FullName}",
                MethodInfo m => $"M:{m.DeclaringType?.FullName}.{m.Name}",
                PropertyInfo p => $"P:{p.DeclaringType?.FullName}.{p.Name}",
                FieldInfo f => $"F:{f.DeclaringType?.FullName}.{f.Name}",
                _ => null
            };
        }
    }
}
