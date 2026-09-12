using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Puppet.Core.Serialization
{
    /// <summary>
    /// 控制 Agent 状态序列化的字段暴露：
    /// - 跳过 [PuppetIgnore] 标记的成员
    /// - 支持 include 白名单（仅序列化指定字段）
    /// - 支持 exclude 黑名单（排除指定字段）
    /// - 跳过 WinForms 基类声明的属性（见构造函数说明）
    /// </summary>
    public class AgentContractResolver : DefaultContractResolver
    {
        private readonly HashSet<string> _include;
        private readonly HashSet<string> _exclude;
        private readonly Type _rootType;

        /// <param name="include">白名单字段名（仅序列化这些字段），null=不限</param>
        /// <param name="exclude">黑名单字段名（排除这些字段），null=不限</param>
        /// <param name="rootType">序列化根类型。声明于 WinForms/ComponentModel 基类（Control/Form 等）的属性
        /// 会被跳过全量序列化，原因：
        /// 1. 部分基类属性 getter 有副作用或依赖 COM/UIA（如 AccessibilityObject 在无障碍服务环境下
        ///    抛 NotSupportedException"参考的对象类型不支持尝试的操作"）；
        /// 2. 部分属性持有原生句柄/触发深层对象图遍历，实测曾导致宿主进程直接崩溃（无法被 catch）。
        /// 业务类型自身声明的属性（含 frmRealtimeEditorBase 等项目基类）不受影响；
        /// 基类控件属性仍可经 GetState 的 path 参数（走 GetProperty）、/agent/get、/agent/control 查询。</param>
        public AgentContractResolver(string[] include, string[] exclude, Type rootType = null)
        {
            _include = include?.Length > 0 ? new HashSet<string>(include, StringComparer.OrdinalIgnoreCase) : null;
            _exclude = exclude?.Length > 0 ? new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase) : null;
            _rootType = rootType;
        }

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);

            // 跳过 [PuppetIgnore] 标记
            if (member.GetCustomAttribute<PuppetIgnoreAttribute>() != null)
            {
                property.ShouldSerialize = _ => false;
                return property;
            }

            // 跳过 WinForms/ComponentModel 基类声明的属性（仅全量序列化根类型自身及项目基类的成员）
            if (_rootType != null
                && member.DeclaringType != _rootType
                && member.DeclaringType != null
                && (member.DeclaringType.Namespace == "System.Windows.Forms"
                    || member.DeclaringType.Namespace == "System.ComponentModel"))
            {
                property.ShouldSerialize = _ => false;
                return property;
            }

            var name = member.Name;

            // 全量模式仅序列化简单类型属性：复杂导航属性（如 FileMan→整个 L3D 工程、
            // Func 委托、其他 Form）会带出数亿级对象图。显式 include 豁免此过滤；
            // 复杂属性可经 GetState path / /agent/get / include 查询。
            if (_include == null && property.PropertyType != null && !IsSimpleSerializableType(property.PropertyType))
            {
                property.ShouldSerialize = _ => false;
                return property;
            }

            if (_include != null && !_include.Contains(name))
            {
                property.ShouldSerialize = _ => false;
                return property;
            }

            if (_exclude != null && _exclude.Contains(name))
            {
                property.ShouldSerialize = _ => false;
            }

            return property;
        }

        /// <summary>
        /// 判断类型是否适合全量状态序列化：基本类型、字符串、枚举、decimal、日期时间、Guid、Uri
        /// 及其 Nullable 包装。集合/复杂对象不在其列。
        /// </summary>
        private static bool IsSimpleSerializableType(Type type)
        {
            if (type == null) return false;
            if (type.IsPrimitive) return true;
            if (type.IsEnum) return true;
            if (type == typeof(string) || type == typeof(decimal) || type == typeof(char)) return true;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return true;
            if (type == typeof(Guid) || type == typeof(Uri)) return true;
            var underlying = Nullable.GetUnderlyingType(type);
            return underlying != null && IsSimpleSerializableType(underlying);
        }
    }
}
