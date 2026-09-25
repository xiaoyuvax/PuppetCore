using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Puppet.Core.Extentions;

namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// v1.0 §3.2 Actionize 范式判定器。
    /// 动作 = 正面形状（public 实例方法、DeclaredOnly、非 special name）+ 语义锚点（成败可观察：
    /// bool / OperationResult / Task&lt;bool&gt; / Task&lt;OperationResult&gt;）+ 负面排除（[PuppetIgnore] /
    /// out|ref 参数 / Is|Can|Has|Should 谓词前缀 / override / static / 泛型方法）。
    /// 状态 = public 简单类型属性（复用内核 IsSimpleType 语义：基本类型/字符串/枚举/日期/Guid 等）。
    /// 覆盖标注 [PuppetAction]/[PuppetState] 用于补漏与特例；[PuppetIgnore] 永远赢。
    /// </summary>
    public static class ActionizePolicy
    {
        /// <summary>谓词前缀：语义是"问状态"不是"做动作"（负面排除清单 #3）</summary>
        private static readonly string[] PredicatePrefixes = { "Is", "Can", "Has", "Should" };

        private const BindingFlags ScanFlags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        /// <summary>
        /// 扫描类型，产出面向 B 的 action/state 成员清单（范式内 + 覆盖收录）。
        /// instance 非空时，若该实例具备 UI 能力（平台包注入的解析器判定），追加「UI 基础 action」
        /// （移动/缩放/可见性/窗口态/关闭等，见 PuppetUiActions）；宿主已声明的同名 action 优先，不重复。
        /// </summary>
        public static (List<ActionMember> Actions, List<StateMember> States) Scan(Type type, object instance = null)
        {
            var actions = new List<ActionMember>();
            var states = new List<StateMember>();

            // ---- 动作方法 ----
            var methods = type.GetMethods(ScanFlags).Where(m => !m.IsSpecialName);
            foreach (var m in methods)
            {
                if (m.GetCustomAttribute<PuppetIgnoreAttribute>(false) != null) continue; // 永远赢

                var pa = m.GetCustomAttribute<PuppetActionAttribute>(false);
                if (pa == null)
                {
                    var (ok, _) = IsActionByPattern(m);
                    if (!ok) continue; // 范式外且无覆盖标注：不收录
                }

                actions.Add(new ActionMember
                {
                    Name = pa?.Name ?? m.Name,
                    Method = m,
                    Desc = pa?.Desc,
                    Risk = pa?.Risk,
                    Group = pa?.Group,
                    Params = ExtractParams(m),
                    Source = pa != null ? "attribute" : "pattern"
                });
            }

            // ---- 状态属性 ----
            foreach (var p in type.GetProperties(ScanFlags))
            {
                if (p.GetMethod == null) continue;
                if (p.GetMethod.GetCustomAttribute<PuppetIgnoreAttribute>(false) != null) continue;

                var ps = p.GetCustomAttribute<PuppetStateAttribute>(false);
                if (ps == null && !IsSimpleType(p.PropertyType)) continue; // 复杂类型且无覆盖：不收录（文本聊天视图）

                states.Add(new StateMember
                {
                    Name = ps?.Name ?? p.Name,
                    Property = p,
                    Desc = ps?.Desc,
                    Source = ps != null ? "attribute" : "pattern"
                });
            }

            // ---- UI 基础 action（仅 GUI 类型；由 PuppetUiActions 的能力解析器判定）----
            // 「仅 GUI 才有的基础操作默认 Actionize」：宿主无需逐窗体声明；同名宿主 action 优先。
            if (instance != null)
            {
                foreach (var ba in PuppetUiActions.ForInstance(instance))
                    if (!actions.Any(a => string.Equals(a.Name, ba.Name, StringComparison.Ordinal)))
                        actions.Add(ba);
                foreach (var bs in PuppetUiActions.StatesForInstance(instance))
                    if (!states.Any(s => string.Equals(s.Name, bs.Name, StringComparison.Ordinal)))
                        states.Add(bs);
            }

            return (actions, states);
        }

        /// <summary>范式判定：成败可观察返回 + 无 out/ref + 非谓词前缀 + 非 override/static/泛型</summary>
        public static (bool ok, string reason) IsActionByPattern(MethodInfo m)
        {
            if (m.IsStatic) return (false, "static");
            if (m.IsGenericMethod) return (false, "generic method");
            if (IsOverride(m)) return (false, "override");
            if (m.GetParameters().Any(p => p.IsOut || p.ParameterType.IsByRef)) return (false, "out/ref param");
            if (PredicatePrefixes.Any(pre => m.Name.StartsWith(pre, StringComparison.Ordinal))) return (false, "predicate prefix");

            var rt = m.ReturnType;
            if (rt == typeof(bool) || rt == typeof(OperationResult)) return (true, "sync");
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                if (inner == typeof(bool) || inner == typeof(OperationResult)) return (true, "async");
            }
            return (false, "return type");
        }

        private static bool IsPredicate(string name) =>
            PredicatePrefixes.Any(pre => name.StartsWith(pre, StringComparison.Ordinal));

        private static bool IsOverride(MethodInfo m) => m.GetBaseDefinition() != m;

        /// <summary>参数清单（manifest 参数 schema；sensitive 标记审计脱敏用）</summary>
        private static List<ParamInfo> ExtractParams(MethodInfo m)
        {
            var list = new List<ParamInfo>();
            var methodLevel = m.GetCustomAttributes<PuppetParamAttribute>(false);
            foreach (var p in m.GetParameters())
            {
                var pa = methodLevel.FirstOrDefault(a => a.Name == p.Name)
                     ?? p.GetCustomAttribute<PuppetParamAttribute>(false);
                list.Add(new ParamInfo
                {
                    Name = p.Name,
                    Type = FriendlyType(p.ParameterType),
                    Required = !p.IsOptional,
                    Desc = pa?.Desc,
                    Sensitive = pa?.Sensitive ?? false
                });
            }
            return list;
        }

        public sealed class ActionMember
        {
            public string Name { get; init; }
            public MethodInfo Method { get; init; }
            public string Desc { get; init; }
            public string Risk { get; init; }
            public string Group { get; init; }
            public List<ParamInfo> Params { get; init; }
            public string Source { get; init; }
            /// <summary>UI 基础 action 的标识（非空表示由框架合成、Method 为 null，执行走 PuppetUiActions）。</summary>
            public string BaseAction { get; init; }
        }

        public sealed class StateMember
        {
            public string Name { get; init; }
            public PropertyInfo Property { get; init; }
            public string Desc { get; init; }
            public string Source { get; init; }
            /// <summary>合成 state 的类型名（Property 为 null 时使用）。</summary>
            public string TypeName { get; init; }
            /// <summary>合成 state 的取值器（Property 为 null 时使用）。</summary>
            public Func<object, object> ValueProvider { get; init; }
        }

        public sealed class ParamInfo
        {
            public string Name { get; init; }
            public string Type { get; init; }
            public bool Required { get; init; }
            public string Desc { get; init; }
            public bool Sensitive { get; init; }
        }

        private static string FriendlyType(Type t)
        {
            if (t == typeof(bool)) return "boolean";
            if (t == typeof(int) || t == typeof(long)) return "integer";
            if (t == typeof(double) || t == typeof(decimal) || t == typeof(float)) return "number";
            if (t == typeof(string)) return "string";
            return t.Name;
        }

        /// <summary>简单类型判定（与内核 GetProperty 的 IsSimpleType 同语义：文本聊天视图的安全集）</summary>
        internal static bool IsSimpleType(Type type)
        {
            if (type == null) return false;
            if (type.IsPrimitive) return true;
            if (type.IsEnum) return true;
            if (type == typeof(string) || type == typeof(decimal) || type == typeof(char)) return true;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return true;
            if (type == typeof(Guid) || type == typeof(Uri)) return true;
            var underlying = Nullable.GetUnderlyingType(type);
            return underlying != null && IsSimpleType(underlying);
        }
    }
}
