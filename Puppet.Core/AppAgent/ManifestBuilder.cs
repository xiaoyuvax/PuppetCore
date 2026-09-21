using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Wima.Core;

namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// manifest 构建器（提案 §4 / §3.3）：范式扫描 → 覆盖合并 → 三层文案解析。
    /// manifest 只含显式声明面（范式内成员 + 覆盖标注），不含类型全名/程序集信息（与 A 的 describe 刻意相反）。
    /// 文案优先级：[PuppetAction].Desc > L1 UI 同源（WinForms 桥）> L2 静态表（AppAgentDescriptors）> L3 推断（Inferred）。
    /// </summary>
    public static class ManifestBuilder
    {
        public static string Build(AppAgentInstance inst)
        {
            var type = inst.Instance.GetType();
            var (actions, states) = ActionizePolicy.Scan(type);
            var uiTexts = LoadUiTexts(inst.Instance); // L1：WinForms 桥（非 WinForms 宿主返回空）

            var appInfo = type.GetCustomAttribute<PuppetAppInfoAttribute>(false)
                       ?? (PuppetAppInfoAttribute)Attribute.GetCustomAttribute(type.Assembly, typeof(PuppetAppInfoAttribute), false);

            var doc = new
            {
                appagent = "appagent/1.0",
                app = appInfo == null ? null : new
                {
                    name = appInfo.Name,
                    version = appInfo.Version,
                    vendor = appInfo.Vendor
                },
                productName = inst.ProductName,
                instance = inst.Name,
                @concurrency = new
                {
                    actionExecution = "serialized-per-instance",
                    busyWaitMs = inst.Runtime.Options.BusyWaitMs
                },
                actions = actions.Select(a => new
                {
                    name = a.Name,
                    desc = ResolveText(a.Desc, () => Lookup(uiTexts, a.Name), a.Method?.Name),
                    risk = a.Risk,
                    group = a.Group,
                    parameters = a.Params.Select(p => new
                    {
                        name = p.Name, type = p.Type, required = p.Required,
                        desc = p.Desc, sensitive = p.Sensitive
                    }).ToArray()
                }).ToArray(),
                state = states.Select(s => new
                {
                    key = s.Name,
                    type = FriendlyStateType(s.Property.PropertyType),
                    desc = ResolveText(s.Desc, () => Lookup(uiTexts, s.Name), s.Property.Name)
                }).ToArray(),
                // L1 原始控件文案图（控件名 → 文案）：归属归并（哪个控件事件对应哪个 action）由提炼 Agent 在编译期
                // 落为 L2 静态表，运行时不做事件反射归并（多播委托不可靠，提案 §3.3）。
                // 用户 Agent 可语义匹配 action 名与 UI 文案（如 AddTask ↔ 添加）。
                uiTexts = uiTexts.Count > 0 ? uiTexts : null
            };

            return JsonConvert.SerializeObject(doc, Formatting.Indented);
        }

        /// <summary>三层文案解析：显式 > L1 > L3（L2 静态表由宿主侧生成 .g.cs 时直接以 [PuppetAction(Desc=…)] 落地）</summary>
        private static string ResolveText(string explicitDesc, Func<string> uiLookup, string memberName)
        {
            if (!string.IsNullOrEmpty(explicitDesc)) return explicitDesc;      // 显式标注
            var ui = uiLookup?.Invoke();
            if (!string.IsNullOrEmpty(ui)) return ui;                          // L1：UI 同源
            return Symbolize(memberName);                                      // L3：方法名拆词（Inferred）
        }

        private static string Lookup(Dictionary<string, string> map, string key) =>
            map != null && key != null && map.TryGetValue(key, out var v) ? v : null;

        /// <summary>L3：方法名符号化拆词（AddTask → "Add task"），标 Inferred 供用户 Agent 降权展示</summary>
        private static string Symbolize(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var ch in name)
            {
                if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' ');
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string FriendlyStateType(Type t) =>
            ActionizePolicyScanFriendly(t);

        private static string ActionizePolicyScanFriendly(Type t)
        {
            if (t == typeof(bool)) return "boolean";
            if (t == typeof(int) || t == typeof(long)) return "integer";
            if (t == typeof(double) || t == typeof(decimal) || t == typeof(float)) return "number";
            if (t == typeof(string)) return "string";
            return t.Name;
        }

    /// <summary>
    /// L1 桥接点：从 WinForms 控件树提取 UI 文案（AccessibleName 优先于 Text，剥离助记符），
    /// 以控件名 → 文案的形式返回。核心库不引用 System.Windows.Forms——
    /// WinForms 宿主通过 AppAgentUiTextBridge（WinForms 包）注入提取委托。
    /// </summary>
        internal static Dictionary<string, string> LoadUiTexts(object instance) =>
            AppAgentUiTextBridge.Extract?.Invoke(instance) ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// L1 文案桥：核心库定义注入点，WinForms 包在 UseAppAgent 时自动挂接实现。
    /// 提取规则：控件树遍历，取 AccessibleName（缺省 Text），剥离助记符；键 = 控件 Name。
    /// </summary>
    public static class AppAgentUiTextBridge
    {
        /// <summary>WinForms 包注入：Func&lt;object, Dictionary&lt;string,string&gt;&gt;（实例 → 控件名→文案）</summary>
        public static Func<object, Dictionary<string, string>> Extract { get; set; }
    }
}
