using System.Reflection;
using Wima.Core;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// WinForm 窗体控件树遍历与控件操作扩展。
    /// 提供 DescribeControls（控件能力描述）和 InvokeControl（控件操作）。
    /// </summary>
    public static class FormAgentExtensions
    {
        /// <summary>枚举窗体所有可操作控件及其状态</summary>
        public static string DescribeControls(this IPuppet form)
        {
            if (form is not Control root) return Utils.ToJson(new { ok = false, err = "not a form" });

            var controls = WalkControlTree(root).Select(c => new
            {
                name = c.Name,
                text = c.Text,
                type = c.GetType().Name,
                enabled = c.Enabled,
                visible = c.Visible,
                bounds = new { x = c.Bounds.X, y = c.Bounds.Y, w = c.Bounds.Width, h = c.Bounds.Height },
                capabilities = GetControlCapabilities(c)
            });

            return Utils.ToJson(new { ok = true, controls });
        }

        /// <summary>执行控件操作（Click/SetText/SelectIndex/Toggle 等）</summary>
        /// <param name="form">目标窗体</param>
        /// <param name="controlName">控件名</param>
        /// <param name="action">操作类型：click/settext/select/toggle/getvalue/setvalue</param>
        /// <param name="value">操作值</param>
        public static string InvokeControl(this IPuppet form, string controlName, string action, object value = null)
        {
            if (form is not Control root) return Utils.ToJson(new { ok = false, err = "not a form" });
            var ctrl = FindControl(root, controlName);
            if (ctrl == null) return Utils.ToJson(new { ok = false, err = "control not found" });

            if (ctrl.InvokeRequired)
                return (string)ctrl.Invoke(new Func<string>(() => InvokeControlCore(ctrl, action, value)));
            return InvokeControlCore(ctrl, action, value);
        }

        /// <summary>获取指定控件的运行时状态</summary>
        public static string GetControlState(this IPuppet form, string controlName)
        {
            if (form is not Control root) return Utils.ToJson(new { ok = false, err = "not a form" });
            var ctrl = FindControl(root, controlName);
            if (ctrl == null) return Utils.ToJson(new { ok = false, err = "control not found" });
            return Utils.ToJson(new
            {
                ok = true,
                name = ctrl.Name,
                text = ctrl.Text,
                type = ctrl.GetType().Name,
                enabled = ctrl.Enabled,
                visible = ctrl.Visible,
                bounds = new { x = ctrl.Bounds.X, y = ctrl.Bounds.Y, w = ctrl.Bounds.Width, h = ctrl.Bounds.Height }
            });
        }

        private static IEnumerable<Control> WalkControlTree(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                var field = parent.GetType().GetField(c.Name,
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field?.GetCustomAttribute<PuppetIgnoreAttribute>() != null) continue;

                yield return c;
                foreach (var child in WalkControlTree(c)) yield return child;
            }
        }

        private static string[] GetControlCapabilities(Control c)
        {
            if (c is CheckBox) return new[] { "check", "toggle", "checked" };
            if (c is ComboBox) return new[] { "gettext", "select", "items" };
            if (c is NumericUpDown) return new[] { "getvalue", "setvalue" };
            if (c is MenuStrip) return new[] { "items", "click" };
            if (c is TreeView) return new[] { "nodes", "select", "expand", "collapse" };
            if (c is DataGridView) return new[] { "rows", "cells", "select" };
            if (c is Button) return new[] { "click", "enabled" };
            if (c is TextBoxBase) return new[] { "gettext", "settext", "readonly" };
            return new[] { "gettext" };
        }

        private static Control FindControl(Control parent, string name)
        {
            foreach (Control c in parent.Controls)
            {
                if (c.Name == name) return c;
                var child = FindControl(c, name);
                if (child != null) return child;
            }
            return null;
        }

        private static string InvokeControlCore(Control ctrl, string action, object value)
        {
            try
            {
                switch (ctrl)
                {
                    case Button btn when action == "click":
                        btn.PerformClick();
                        return Utils.ToJson(new { ok = true });
                    case TextBoxBase tb when action == "settext":
                        tb.Text = value?.ToString();
                        return Utils.ToJson(new { ok = true });
                    case ComboBox cb when action == "select":
                        cb.SelectedIndex = Convert.ToInt32(value);
                        return Utils.ToJson(new { ok = true });
                    case CheckBox cbx when action == "toggle":
                        cbx.Checked = !cbx.Checked;
                        return Utils.ToJson(new { ok = true, @checked = cbx.Checked });
                    case CheckBox cbx2 when action == "check":
                        cbx2.Checked = Convert.ToBoolean(value);
                        return Utils.ToJson(new { ok = true, @checked = cbx2.Checked });
                    case NumericUpDown nud when action == "setvalue":
                        nud.Value = Convert.ToDecimal(value);
                        return Utils.ToJson(new { ok = true });
                    default:
                        return Utils.ToJson(new { ok = false, err = $"unsupported action '{action}' on {ctrl.GetType().Name}" });
                }
            }
            catch (Exception ex)
            {
                return Utils.ToJson(new { ok = false, err = ex.Message });
            }
        }
    }
}
