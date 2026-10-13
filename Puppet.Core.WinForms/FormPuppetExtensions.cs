using System.Reflection;
using Wima.Core;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// WinForm 窗体控件树遍历与控件操作扩展。
    /// 提供 DescribeControls（控件能力描述）和 InvokeControl（控件操作）。
    /// 除 Control 树外，还枚举 ToolStrip/MenuStrip/StatusStrip 里的 ToolStripItem（工具栏按钮、菜单项
    /// 都不是 Control，纯控件树遍历不到——实测盲区：tsbUpload 这类工具栏按钮对 /agent/control 不可见）。
    /// </summary>
    public static class FormAgentExtensions
    {
        /// <summary>枚举窗体所有可操作控件及其状态（Control 树 + ToolStripItem）</summary>
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

            var items = WalkToolStripItems(root, root).Select(i => new
            {
                name = i.Name,
                text = i.Text,
                type = i.GetType().Name,
                enabled = i.Enabled,
                visible = i.Visible,
                bounds = new { x = i.Bounds.X, y = i.Bounds.Y, w = i.Bounds.Width, h = i.Bounds.Height },
                capabilities = GetItemCapabilities(i)
            });

            // 两段匿名类型结构相同 → 同一类型，可合并进同一个 controls 数组
            return Utils.ToJson(new { ok = true, controls = controls.Concat(items) });
        }

        /// <summary>执行控件操作（Click/SetText/SelectIndex/Toggle 等）。
        /// 控件树找不到时回落到 ToolStripItem（工具栏按钮/菜单项都不是 Control，需单独寻址）。</summary>
        /// <param name="form">目标窗体</param>
        /// <param name="controlName">控件名或 ToolStripItem 名</param>
        /// <param name="action">操作类型：click/settext/select/toggle/getvalue/setvalue/gettext</param>
        /// <param name="value">操作值</param>
        public static string InvokeControl(this IPuppet form, string controlName, string action, object value = null)
        {
            if (form is not Control root) return Utils.ToJson(new { ok = false, err = "not a form" });
            var ctrl = FindControl(root, controlName);
            if (ctrl == null)
            {
                var item = FindToolStripItem(root, root, controlName);
                if (item == null) return Utils.ToJson(new { ok = false, err = "control not found" });
                if (root.InvokeRequired)
                    return (string)root.Invoke(new Func<string>(() => InvokeToolStripItemCore(item, action)));
                return InvokeToolStripItemCore(item, action);
            }

            if (ctrl.InvokeRequired)
                return (string)ctrl.Invoke(new Func<string>(() => InvokeControlCore(ctrl, action, value)));
            return InvokeControlCore(ctrl, action, value);
        }

        /// <summary>获取指定控件或 ToolStripItem 的运行时状态</summary>
        public static string GetControlState(this IPuppet form, string controlName)
        {
            if (form is not Control root) return Utils.ToJson(new { ok = false, err = "not a form" });
            var ctrl = FindControl(root, controlName);
            if (ctrl == null)
            {
                var item = FindToolStripItem(root, root, controlName);
                if (item == null) return Utils.ToJson(new { ok = false, err = "control not found" });
                return Utils.ToJson(new
                {
                    ok = true,
                    name = item.Name,
                    text = item.Text,
                    type = item.GetType().Name,
                    enabled = item.Enabled,
                    visible = item.Visible,
                    checkedState = CheckedStateOf(item)
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

        /// <summary>遍历所有 ToolStrip（MenuStrip/ToolStrip/StatusStrip 同根）里的条目，含 DropDown 子项。
        /// [PuppetIgnore] 按「声明该字段的类型」与 root 两级判定（Designer 把条目字段声明在 Form 上，
        /// UserControl 自带工具栏时声明在 UserControl 上）。</summary>
        private static IEnumerable<ToolStripItem> WalkToolStripItems(Control root, Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is ToolStrip ts)
                    foreach (ToolStripItem i in ts.Items)
                    {
                        if (IsPuppetIgnored(root, ts, i)) continue;
                        yield return i;
                        foreach (var d in WalkToolStripItemChildren(i)) yield return d;
                    }
                foreach (var d in WalkToolStripItems(root, c)) yield return d;
            }
        }

        private static IEnumerable<ToolStripItem> WalkToolStripItemChildren(ToolStripItem item)
        {
            if (item is not ToolStripDropDownItem dd) yield break;
            foreach (ToolStripItem child in dd.DropDownItems)
            {
                yield return child;
                foreach (var d in WalkToolStripItemChildren(child)) yield return d;
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

        private static string[] GetItemCapabilities(ToolStripItem i)
        {
            if (i is ToolStripMenuItem) return new[] { "click", "gettext", "checked" };
            if (i is ToolStripButton) return new[] { "click", "gettext" };
            return new[] { "gettext" };
        }

        private static object CheckedStateOf(ToolStripItem item) =>
            item is ToolStripMenuItem mi ? mi.Checked :
            item is ToolStripButton btn ? btn.Checked : (object)null;

        private static bool IsPuppetIgnored(Control root, ToolStrip owner, ToolStripItem item)
        {
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            return owner?.GetType().GetField(item.Name, F)?.GetCustomAttribute<PuppetIgnoreAttribute>() != null
                || root.GetType().GetField(item.Name, F)?.GetCustomAttribute<PuppetIgnoreAttribute>() != null;
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

        /// <summary>在所有 ToolStrip 容器里递归查找条目（工具栏按钮/菜单项/下拉子项），找不到返回 null</summary>
        private static ToolStripItem FindToolStripItem(Control root, Control parent, string name)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is ToolStrip ts)
                    foreach (ToolStripItem i in ts.Items)
                        if (FindToolStripItemRecursive(i, name) is ToolStripItem hit && !IsPuppetIgnored(root, ts, hit))
                            return hit;
                var found = FindToolStripItem(root, c, name);
                if (found != null) return found;
            }
            return null;
        }

        private static ToolStripItem FindToolStripItemRecursive(ToolStripItem item, string name)
        {
            if (item == null) return null;
            if (item.Name == name) return item;
            if (item is not ToolStripDropDownItem dd) return null;
            foreach (ToolStripItem child in dd.DropDownItems)
                if (FindToolStripItemRecursive(child, name) is ToolStripItem hit) return hit;
            return null;
        }

        private static string InvokeToolStripItemCore(ToolStripItem item, string action)
        {
            try
            {
                switch (action)
                {
                    case "click":
                        if (!item.Enabled) return Utils.ToJson(new { ok = false, err = "item disabled" });
                        item.PerformClick();
                        return Utils.ToJson(new { ok = true, clicked = item.Name });
                    case "gettext":
                        return Utils.ToJson(new { ok = true, name = item.Name, text = item.Text, enabled = item.Enabled, checkedState = CheckedStateOf(item) });
                    default:
                        return Utils.ToJson(new { ok = false, err = $"unsupported action '{action}' on {item.GetType().Name} {item.Name}" });
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
