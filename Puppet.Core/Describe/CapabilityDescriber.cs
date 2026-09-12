using System.Reflection;
using System.Text;
using Wima.Core;

namespace Puppet.Core.Describe
{
    /// <summary>
    /// 能力描述生成器。通过反射枚举可被 Agent 访问的成员，提取 &lt;summary&gt;。
    /// 规则：public 成员默认可见；非 public 成员需带 [PuppetExpose]；所有成员带 [PuppetIgnore] 后不可见。
    /// &lt;summary&gt; 由人类维护，可能陈旧失真，仅作次级参考（通过 SummarySource 字段标识）。
    /// </summary>
    public static class CapabilityDescriber
    {
        /// <summary>生成完整能力描述（JSON 或 Markdown）</summary>
        /// <param name="target">目标实例</param>
        /// <param name="format">输出格式</param>
        public static string Describe(this IPuppet target, DescribeFormat format = DescribeFormat.Json)
        {
            var type = target.GetType();
            var doc = new CapabilityDoc
            {
                InstanceName = target.AgentInstanceName,
                TypeName = type.FullName,
                AssemblyName = type.Assembly.GetName().Name,
                Summary = XmlDocExtractor.GetSummary(type, out var typeSource),
                SummarySource = typeSource,
                Properties = EnumerateProperties(type),
                Methods = EnumerateMethods(type),
                Fields = EnumerateFields(type)
            };
            return format == DescribeFormat.Json
                ? Utils.ToJson(doc, compact: true)
                : ToMarkdown(doc);
        }

        private static List<PropertyDesc> EnumerateProperties(Type type)
        {
            var publicProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            var exposedProps = type.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<PuppetExposeAttribute>() != null
                         && p.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            return publicProps.Concat(exposedProps).Select(p =>
                {
                    var summary = XmlDocExtractor.GetSummary(p, out var source);
                    return new PropertyDesc
                    {
                        Name = p.Name,
                        Type = p.PropertyType.FullName,
                        CanRead = p.CanRead,
                        CanWrite = p.CanWrite,
                        Summary = summary,
                        SummarySource = summary != null ? source : null,
                        Description = p.GetCustomAttribute<PuppetDescriptionAttribute>()?.Description
                    };
                }).ToList();
        }

        private static List<MethodDesc> EnumerateMethods(Type type)
        {
            var publicMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            var exposedMethods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName
                          && m.GetCustomAttribute<PuppetExposeAttribute>() != null
                          && m.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            return publicMethods.Concat(exposedMethods).Select(m =>
                {
                    var summary = XmlDocExtractor.GetSummary(m, out var source);
                    return new MethodDesc
                    {
                        Name = m.Name,
                        ReturnType = m.ReturnType?.FullName,
                        Summary = summary,
                        SummarySource = summary != null ? source : null,
                        Description = m.GetCustomAttribute<PuppetDescriptionAttribute>()?.Description,
                        Parameters = m.GetParameters().Select(p =>
                        {
                            var pSummary = XmlDocExtractor.GetSummary(p, out var pSource);
                            return new ParamDesc
                            {
                                Name = p.Name,
                                Type = p.ParameterType.FullName,
                                Summary = pSummary,
                                SummarySource = pSummary != null ? pSource : null,
                                IsOptional = p.IsOptional
                            };
                        }).ToList()
                    };
                }).ToList();
        }

        private static List<FieldDesc> EnumerateFields(Type type)
        {
            var publicFields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            var exposedFields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.GetCustomAttribute<PuppetExposeAttribute>() != null
                         && f.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            return publicFields.Concat(exposedFields).Select(f =>
                {
                    var summary = XmlDocExtractor.GetSummary(f, out var source);
                    return new FieldDesc
                    {
                        Name = f.Name,
                        Type = f.FieldType.FullName,
                        IsReadOnly = f.IsInitOnly,
                        Summary = summary,
                        SummarySource = summary != null ? source : null,
                        Description = f.GetCustomAttribute<PuppetDescriptionAttribute>()?.Description
                    };
                }).ToList();
        }

        private static string ToMarkdown(CapabilityDoc doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {doc.InstanceName}");
            sb.AppendLine($"- Type: `{doc.TypeName}`");
            sb.AppendLine($"- Assembly: `{doc.AssemblyName}`");
            if (!string.IsNullOrEmpty(doc.Summary))
            {
                sb.AppendLine($"- Summary: {doc.Summary}");
                if (doc.SummarySource == InfoSource.HumanSummary)
                    sb.AppendLine("  > 注：此 summary 由人类维护，可能陈旧失真，仅作次级参考");
            }
            sb.AppendLine();

            if (doc.Properties?.Count > 0)
            {
                sb.AppendLine("## Properties");
                foreach (var p in doc.Properties)
                {
                    sb.AppendLine($"- `{p.Name}` ({p.Type}) [R:{p.CanRead} W:{p.CanWrite}]");
                    if (!string.IsNullOrEmpty(p.Summary)) sb.AppendLine($"  - Summary: {p.Summary}");
                }
                sb.AppendLine();
            }

            if (doc.Methods?.Count > 0)
            {
                sb.AppendLine("## Methods");
                foreach (var m in doc.Methods)
                {
                    var parms = string.Join(", ", m.Parameters?.Select(p => $"{p.Type} {p.Name}") ?? Enumerable.Empty<string>());
                    sb.AppendLine($"- `{m.Name}({parms})` → {m.ReturnType}");
                    if (!string.IsNullOrEmpty(m.Summary)) sb.AppendLine($"  - Summary: {m.Summary}");
                }
                sb.AppendLine();
            }

            if (doc.Fields?.Count > 0)
            {
                sb.AppendLine("## Fields");
                foreach (var f in doc.Fields)
                {
                    sb.AppendLine($"- `{f.Name}` ({f.Type}) [ReadOnly:{f.IsReadOnly}]");
                    if (!string.IsNullOrEmpty(f.Summary)) sb.AppendLine($"  - Summary: {f.Summary}");
                }
            }
            return sb.ToString();
        }
    }
}
