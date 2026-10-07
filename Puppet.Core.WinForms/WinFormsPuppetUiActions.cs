using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using Puppet.Core.AppAgent;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// WinForms 适配：把 <see cref="Form"/> / <see cref="Control"/> 适配为
    /// <see cref="IPuppetUiElement"/> / <see cref="IPuppetWindow"/> 并注入 <see cref="PuppetUiActions"/> 解析器，
    /// 使框架能对所有 WinForms IPuppet 窗体/控件**自动 Actionize UI 基础 action**（宿主无需逐窗体声明）。
    /// 由 <c>UseFormControls()</c> 自动调用（幂等）。
    /// 坐标语义：Form 的 Bounds/Location 为屏幕坐标，Control 的为父容器坐标（与 WinForms 一致）。
    /// </summary>
    public static class WinFormsPuppetUiActions
    {
        private static bool _registered;

        /// <summary>注入 WinForms 适配器（幂等）。</summary>
        public static void UsePuppetUiActions()
        {
            if (_registered) return;
            _registered = true;

            PuppetUiActions.UiElementResolver = o => o switch
            {
                Form f => new WinFormsWindow(f),
                Control c => new WinFormsUiElement(c),
                _ => null
            };
            PuppetUiActions.WindowResolver = o => o is Form f ? new WinFormsWindow(f) : null;
            PuppetUiActions.CaptureResolver = o => o is Control c ? new WinFormsCapture(c) : null;
            PuppetUiActions.Confirm ??= (owner, text, caption) =>
                PuppetDialog.Ask(owner as IWin32Window, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                    == DialogResult.Yes;
            // 屏幕环境：供 Agent 了解单屏/多屏及各屏几何（/appagent/state/ScreenCount|Screens）
            PuppetUiActions.ScreensProvider = () => Screen.AllScreens.Select(s => new PuppetScreen
            {
                DeviceName = s.DeviceName,
                Primary = s.Primary,
                X = s.Bounds.X,
                Y = s.Bounds.Y,
                Width = s.Bounds.Width,
                Height = s.Bounds.Height,
                WorkX = s.WorkingArea.X,
                WorkY = s.WorkingArea.Y,
                WorkWidth = s.WorkingArea.Width,
                WorkHeight = s.WorkingArea.Height
            }).ToList();
        }

        internal class WinFormsUiElement : IPuppetUiElement
        {
            protected readonly Control C;
            public WinFormsUiElement(Control c) => C = c;

            public int X => C.Left;
            public int Y => C.Top;
            public int Width => C.Width;
            public int Height => C.Height;
            public bool Visible { get => C.Visible; set => C.Visible = value; }
            public bool Enabled { get => C.Enabled; set => C.Enabled = value; }

            /// <summary>子类可覆写：改几何前先归一化（如窗口从最大化还原）。</summary>
            protected virtual void Normalize() { }

            public void Move(int x, int y) { Normalize(); C.Location = new Point(x, y); }
            public void Resize(int width, int height) { Normalize(); C.Size = new Size(width, height); }
            public void SetBounds(int x, int y, int width, int height) { Normalize(); C.Bounds = new Rectangle(x, y, width, height); }
            public void Focus() { C.Focus(); }
        }

        internal sealed class WinFormsWindow : WinFormsUiElement, IPuppetWindow
        {
            public WinFormsWindow(Form f) : base(f) { }
            private Form F => (Form)C;

            // 最大化/最小化状态下改 Bounds/Location 无效，先还原为 Normal
            protected override void Normalize()
            {
                if (F.WindowState != FormWindowState.Normal) F.WindowState = FormWindowState.Normal;
            }

            public PuppetWindowState WindowState
            {
                get => F.WindowState switch
                {
                    FormWindowState.Maximized => PuppetWindowState.Maximized,
                    FormWindowState.Minimized => PuppetWindowState.Minimized,
                    _ => PuppetWindowState.Normal
                };
                set => F.WindowState = value switch
                {
                    PuppetWindowState.Maximized => FormWindowState.Maximized,
                    PuppetWindowState.Minimized => FormWindowState.Minimized,
                    _ => FormWindowState.Normal
                };
            }

            public bool TopMost { get => F.TopMost; set => F.TopMost = value; }
            public string Title { get => F.Text; set => F.Text = value; }
            public double Opacity
            {
                get => F.Opacity;
                set
                {
                    F.Opacity = value;
                    // 恢复到不透明时一并复位 AllowTransparency：设过 Opacity<1 会让窗体成为层叠窗口，
                    // 仅把 Opacity 设回 1.0 不会复位该状态，窗体仍显示半透明（实测踩坑）。
                    if (value >= 1.0) F.AllowTransparency = false;
                }
            }
            public void Activate() => F.Activate();
            public void Close() => F.Close();
        }

        /// <summary>
        /// 截屏适配：用 <see cref="Control.DrawToBitmap"/> 读取该控件**自身已绘制的像素**——
        /// 不读取桌面、不读取其他窗口，因此不产生整屏大图，也不遮挡任何界面。
        /// 限制：独立句柄子窗（下拉、工具提示）与 GPU 加速内容不在绘制结果内。
        /// </summary>
        internal sealed class WinFormsCapture : IPuppetCapture
        {
            private readonly Control C;
            public WinFormsCapture(Control c) => C = c;

            public PuppetArtifact Capture(int x, int y, int width, int height)
            {
                if (C.Width <= 0 || C.Height <= 0)
                    throw new InvalidOperationException("控件尺寸为空，无法截取");

                using var full = new Bitmap(C.Width, C.Height);
                C.DrawToBitmap(full, new Rectangle(0, 0, C.Width, C.Height));

                Bitmap shot = full, owned = null;
                if (width > 0 && height > 0)
                {
                    var rect = Rectangle.Intersect(new Rectangle(x, y, width, height),
                                                   new Rectangle(0, 0, full.Width, full.Height));
                    if (rect.Width <= 0 || rect.Height <= 0)
                        throw new ArgumentException("截取区域完全落在元素之外");
                    shot = full.Clone(rect, PixelFormat.Format32bppPArgb);
                    owned = shot;
                }

                try
                {
                    using var ms = new System.IO.MemoryStream();
                    shot.Save(ms, ImageFormat.Png);
                    return new PuppetArtifact("capture.png", "image/png", ms.ToArray());
                }
                finally { owned?.Dispose(); }
            }
        }
    }
}
