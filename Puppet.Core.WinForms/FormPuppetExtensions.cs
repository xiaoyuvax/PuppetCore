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

        /// <summary>执行控件操作（Click/SetText/SelectIndex/Toggle 等）。
        /// 控件树找不到时回落到菜单项（ToolStripMenuItem 不是 Control，需单独寻址）。</summary>
        /// <param name="form">目标窗体</param>
        /// <param name="controlName">控件名或菜单项名</param>
        /// <param name="action">操作类型：click/settext/select/toggle/getvalue/setvalue</param>
        /// <param name="value">操作值</param>
        public static string InvokeControl(this IPuppet form, string controlName, string action, object value = null)
        {
            if (form is not Control root) return Utils.ToJson(new { ok = false, err = "not a form" });
            var ctrl = FindControl(root, controlName);
            if (ctrl == null)
            {
                var mi = FindMenuItem(root, controlName);
                if (mi == null) return Utils.ToJson(new { ok = false, err = "control not found" });
                if (root.InvokeRequired)
                    return (string)root.Invoke(new Func<string>(() => InvokeMenuItemCore(mi, action)));
                return InvokeMenuItemCore(mi, action);
            }

            if (ctrl.InvokeRequired)
                return (string)ctrl.Invoke(new Func<string>(() => InvokeControlCore(ctrl, action, value)));
            return InvokeControlCore(ctrl, action, value);
        }

        /// <summary>获取指定控件或菜单项的运行时状态</summary>
        public static string GetControlState(this IPuppet form, string controlName)
        {
            if (form is not Control root) return Utils.ToJson(new { ok = false, err = "not a form" });
            var ctrl = FindControl(root, controlName);
            if (ctrl == null)
            {
                var mi = FindMenuItem(root, controlName);
                if (mi == null) return Utils.ToJson(new { ok = false, err = "control not found" });
                return Utils.ToJson(new
                {
                    ok = true,
                    name = mi.Name,
                    text = mi.Text,
                    type = mi.GetType().Name,
                    enabled = mi.Enabled,
                    visible = mi.Visible,
                    checkedState = mi.Checked
                });
            }
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

        /// <summary>在控件树中递归查找菜单项（含子菜单），找不到返回 null</summary>
        private static ToolStripMenuItem FindMenuItem(Control root, string name)
        {
            foreach (Control c in root.Controls)
            {
                if (c is MenuStrip ms)
                {
                    foreach (ToolStripItem item in ms.Items)
                        if (FindMenuItemRecursive(item, name) is ToolStripMenuItem hit) return hit;
                }
                var found = FindMenuItem(c, name);
                if (found != null) return found;
            }
            return null;
        }

        private static ToolStripMenuItem FindMenuItemRecursive(ToolStripItem item, string name)
        {
            if (item == null) return null;
            if (item is ToolStripMenuItem mi)
            {
                if (mi.Name == name) return mi;
                foreach (ToolStripItem child in mi.DropDownItems)
                    if (FindMenuItemRecursive(child, name) is ToolStripMenuItem hit) return hit;
            }
            return null;
        }

        private static string InvokeMenuItemCore(ToolStripMenuItem mi, string action)
        {
            try
            {
                switch (action)
                {
                    case "click":
                        if (!mi.Enabled) return Utils.ToJson(new { ok = false, err = "menu item disabled" });
                        mi.PerformClick();
                        return Utils.ToJson(new { ok = true, clicked = mi.Name });
                    case "gettext":
                        return Utils.ToJson(new { ok = true, name = mi.Name, text = mi.Text, enabled = mi.Enabled, checkedState = mi.Checked });
                    default:
                        return Utils.ToJson(new { ok = false, err = $"unsupported action '{action}' on menu item {mi.Name}" });
                }
            }
            catch (Exception ex)
            {
                return Utils.ToJson(new { ok = false, err = ex.Message });
            }
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
