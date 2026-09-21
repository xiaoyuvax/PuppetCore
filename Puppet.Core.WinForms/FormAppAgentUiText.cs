using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// 面向 B 的 WinForms L1 文案桥（提案 §3.3）：
    /// 启动时扫描控件树提取 UI 文案（AccessibleName 优先 > Text，剥离 & 助记符），
    /// 供 manifest 三层文案解析使用（UI 同源，消除双份维护）。
    /// 核心库不引用 System.Windows.Forms——WinForms 包经 AppAgentUiTextBridge 注入提取委托。
    /// </summary>
    public static class FormAppAgentUiText
    {
        /// <summary>挂接到核心库文案桥（UseFormControls/UseAppAgent 时自动调用）</summary>
        public static void Register() =>
            AppAgent.AppAgentUiTextBridge.Extract = instance =>
            {
                var map = new Dictionary<string, string>();
                if (instance is not Control root) return map;
                Walk(root, map);
                return map;
            };

        private static void Walk(Control parent, Dictionary<string, string> map)
        {
            foreach (Control c in parent.Controls)
            {
                if (!string.IsNullOrEmpty(c.Name))
                {
                    var text = !string.IsNullOrEmpty(c.AccessibleName) ? c.AccessibleName : c.Text;
                    text = StripMnemonic(text);
                    if (!string.IsNullOrWhiteSpace(text)) map[c.Name] = text;
                }
                Walk(c, map);
            }
        }

        /// <summary>剥离 & 助记符（"添加 (&A)" → "添加 (A)"，"&Add" → "Add"）</summary>
        private static string StripMnemonic(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("(&", "(").Replace("&", "");
        }
    }
}
