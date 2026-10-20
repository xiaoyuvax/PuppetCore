using System.Reflection;
using System.Text;
using Puppet.Core.AppAgent;
using Wima.Core;

namespace Puppet.Core.Describe
{
    /// <summary>
    /// 能力描述生成器。通过反射枚举可被 Agent 访问的成员，提取 <summary>。
    /// 规则：仅列出 public 成员与带 [PuppetExpose] 的非 public 成员（广告/焦点，非访问授权）；带 [PuppetIgnore] 者不列出。
    /// <summary> 由人类维护，可能陈旧失真，仅作次级参考（通过 SummarySource 字段标识）。
    /// </summary>
    public static class CapabilityDescriber
    {
        /// <summary>生成完整能力描述（JSON 或 Markdown）</summary>
        /// <param name="target">目标实例</param>
        /// <param name="format">输出格式</param>
        /// <param name="own">仅列本类型声明的成员（加 DeclaredOnly），滤掉 WinForms 基类继承的
        /// Control/Form 属性/字段（AcceptButton、AccessibleRole…）——大窗口实例 describe 体积可再省一个量级。
        /// 方法本就用 DeclaredOnly，不受影响；状态读写（state/get/set）仍可达继承成员，仅广告面收窄。</param>
        /// <param name="values">为属性附加当前值（仅简单类型，读失败留空）。让 Agent 一次拿到"有哪些状态 + 现值"，
        /// 省掉"猜属性名 → get → 404"的往返；复杂对象不填充（跨线程/序列化风险）。</param>
        public static string Describe(this IPuppet target, DescribeFormat format = DescribeFormat.Json, string section = null, bool own = false, bool values = false)
        {
            var type = target.GetType();
            var doc = new CapabilityDoc
            {
                InstanceName = target.AgentInstanceName,
                TypeName = type.FullName,
                AssemblyName = type.Assembly.GetName().Name,
                Summary = XmlDocExtractor.GetSummary(type, out var typeSource),
                SummarySource = typeSource,
                Properties = EnumerateProperties(type, own),
                Methods = EnumerateMethods(type),
                Fields = EnumerateFields(type, own),
                Hints = EnumerateHints(type)
            };

            // 框架合成方法（非实例成员）：由 PuppetUiActions 按注入能力追加，无能力的实例不出现
            var synthetic = PuppetUiActions.DescribeSynthetic(target);
            if (synthetic.Count > 0) doc.Methods.AddRange(synthetic);

            // section 精简（methods|properties|fields|hints|state）：大实例 describe 体积可省一个数量级；未知 section 原样返回
            if (!string.IsNullOrEmpty(section)) ApplySection(doc, section);
            if (values) FillPropertyValues(target, type, doc.Properties);

            return format == DescribeFormat.Json
                ? Utils.ToJson(doc, compact: true)
                : ToMarkdown(doc);
        }

        /// <summary>为属性描述填充当前值：只读 + 简单类型 + 限 80 个；单个属性异常不影响整体。</summary>
        private static void FillPropertyValues(IPuppet target, Type type, List<PropertyDesc> props)
        {
            foreach (var p in props.Where(x => x.CanRead).Take(80))
            {
                try
                {
                    var pi = type.GetProperty(p.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi == null || pi.GetIndexParameters().Length > 0 || !IsSimple(pi.PropertyType)) continue;
                    p.Value = PuppetUiContext.ShouldSwitch()
                        ? PuppetUiContext.Send(() => pi.GetValue(target))
                        : pi.GetValue(target);
                }
                catch
                {
                    // 属性 getter 可能依赖 UI 状态/已释放资源：读失败留空即可，describe 不应因此失败
                }
            }
        }

        private static bool IsSimple(Type type)
        {
            if (type == null) return false;
            if (type.IsPrimitive || type.IsEnum) return true;
            if (type == typeof(string) || type == typeof(decimal) || type == typeof(char)) return true;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return true;
            if (type == typeof(Guid) || type == typeof(Uri)) return true;
            var underlying = Nullable.GetUnderlyingType(type);
            return underlying != null && IsSimple(underlying);
        }

        private static List<PropertyDesc> EnumerateProperties(Type type, bool own)
        {
            var ownFlag = own ? BindingFlags.DeclaredOnly : BindingFlags.Default;
            var publicProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | ownFlag)
                .Where(p => p.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            var exposedProps = type.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | ownFlag)
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

        private static List<FieldDesc> EnumerateFields(Type type, bool own)
        {
            var ownFlag = own ? BindingFlags.DeclaredOnly : BindingFlags.Default;
            var publicFields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | ownFlag)
                .Where(f => f.GetCustomAttribute<PuppetIgnoreAttribute>() == null);
            var exposedFields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | ownFlag)
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

        private static List<HintDesc> EnumerateHints(Type type)
        {
            var hints = new List<HintDesc>();

            // Type level
            var typeHints = type.GetCustomAttributes<PuppetHintAttribute>(false);
            foreach (var n in typeHints)
                hints.Add(CreateHintDesc(type.FullName, "", "Type", n));

            // Methods
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName);
            foreach (var m in methods)
            {
                var mHints = m.GetCustomAttributes<PuppetHintAttribute>(false);
                foreach (var n in mHints)
                    hints.Add(CreateHintDesc(type.FullName, m.Name, "Method", n));
            }

            // Properties
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var p in props)
            {
                var pHints = p.GetCustomAttributes<PuppetHintAttribute>(false);
                foreach (var n in pHints)
                    hints.Add(CreateHintDesc(type.FullName, p.Name, "Property", n));
            }

            // Fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in fields)
            {
                var fHints = f.GetCustomAttributes<PuppetHintAttribute>(false);
                foreach (var n in fHints)
                    hints.Add(CreateHintDesc(type.FullName, f.Name, "Field", n));
            }

            // Events
            var events = type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var e in events)
            {
                var eHints = e.GetCustomAttributes<PuppetHintAttribute>(false);
                foreach (var n in eHints)
                    hints.Add(CreateHintDesc(type.FullName, e.Name, "Event", n));
            }

            // Constructors
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var c in ctors)
            {
                var cHints = c.GetCustomAttributes<PuppetHintAttribute>(false);
                foreach (var n in cHints)
                    hints.Add(CreateHintDesc(type.FullName, ".ctor", "Constructor", n));
            }

            return hints;
        }

        private static HintDesc CreateHintDesc(string targetType, string targetMember, string memberKind, PuppetHintAttribute attr)
            => new HintDesc
            {
                TargetType = targetType,
                TargetMember = targetMember,
                MemberKind = memberKind,
                Category = attr.Category,
                Text = attr.Text,
                AgentId = attr.AgentId,
                CreatedAt = attr.CreatedAt
            };

        /// <summary>按 section 保留对应列表，其余置 null（ToMarkdown 用 ?.Count 已容忍 null）。
        /// state = properties + fields。未知 section 不裁剪。</summary>
        private static void ApplySection(CapabilityDoc doc, string section)
        {
            var s = section.ToLowerInvariant();
            if (s is not ("methods" or "properties" or "fields" or "hints" or "state")) return;
            if (s != "methods") doc.Methods = null;
            if (s is not ("properties" or "state")) doc.Properties = null;
            if (s is not ("fields" or "state")) doc.Fields = null;
            if (s != "hints") doc.Hints = null;
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

            if (doc.Hints?.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Hints");
                foreach (var n in doc.Hints)
                {
                    var target = string.IsNullOrEmpty(n.TargetMember) ? n.TargetType : $"{n.TargetType}.{n.TargetMember}";
                    sb.AppendLine($"- **[{n.Category}]** `{target}` ({n.MemberKind})");
                    sb.AppendLine($"  {n.Text}");
                    if (!string.IsNullOrEmpty(n.AgentId)) sb.AppendLine($"  > by {n.AgentId} @ {n.CreatedAt}");
                }
            }
            return sb.ToString();
        }
    }
}
