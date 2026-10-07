using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Puppet.Core.AppAgent;
using Puppet.Core.Web;

namespace Puppet.Core.Wpf
{
    /// <summary>
    /// WPF 截屏适配：把 WPF 窗口/元素渲染为 PNG 产物，供 /agent/describe 与 /agent/invoke 的
    /// 合成方法 <c>CaptureAction</c> 使用。宿主用法：
    /// <code>
    /// new PuppetWebServer().UseWpfCapture().UseAppAgent(...);
    /// </code>
    /// 用 <see cref="RenderTargetBitmap"/> 渲染**本窗口自身的可视化树**——不读取桌面、不读取其他窗口，
    /// 因此不产生整屏大图，也不遮挡任何界面（窗口被遮挡甚至最小化仍可截取）。
    /// </summary>
    public static class WpfPuppetCapture
    {
        private static bool _registered;

        /// <summary>注入 WPF 截屏适配（幂等）。未启用 WPF 应用（无 Application）时不注入。</summary>
        public static void UseCapture()
        {
            if (_registered) return;
            _registered = true;
            PuppetUiActions.CaptureResolver = o =>
            {
                if (o == null) return null;
                if (o is FrameworkElement) return new WpfCapture(o);
                return Application.Current != null ? new WpfCapture(o) : null;
            };
        }
    }

    /// <summary>
    /// 截屏实现：实例是可视化元素则截该元素；否则（如注册的是视图模型）截主窗口。
    /// 窗口解析延迟到 <see cref="Capture"/> 内——该调用固定发生在 UI 线程，可安全访问 DispatcherObject。
    /// </summary>
    internal sealed class WpfCapture : IPuppetCapture
    {
        private readonly object _instance;
        public WpfCapture(object instance) => _instance = instance;

        private Visual Resolve()
            => _instance as FrameworkElement ?? Application.Current?.MainWindow;

        public PuppetArtifact Capture(int x, int y, int width, int height)
        {
            var visual = Resolve();
            if (visual == null) throw new InvalidOperationException("未找到可截取的窗口");

            int w = visual is FrameworkElement fe ? (int)Math.Ceiling(fe.ActualWidth) : 0;
            int h = visual is FrameworkElement fe2 ? (int)Math.Ceiling(fe2.ActualHeight) : 0;
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException("元素尚未完成布局（宽高为 0），无法截取");

            // 96 DPI 渲染：1 逻辑单位 = 1 图像像素，图像尺寸即布局尺寸，天然小于高 DPI 下的物理像素
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            BitmapSource source = bitmap;
            if (width > 0 && height > 0)
            {
                int cx = Math.Max(0, x), cy = Math.Max(0, y);
                int cw = Math.Min(width, w - cx), ch = Math.Min(height, h - cy);
                if (cw <= 0 || ch <= 0) throw new ArgumentException("截取区域完全落在元素之外");
                source = new CroppedBitmap(bitmap, new Int32Rect(cx, cy, cw, ch));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return new PuppetArtifact("capture.png", "image/png", ms.ToArray());
        }
    }

    /// <summary>
    /// PuppetWebServer 的 WPF 扩展：注入 WPF 截屏适配器。
    /// <code>
    /// new PuppetWebServer().UseWpfCapture().UseAppAgent(o => o.ProductName = "...");
    /// </code>
    /// </summary>
    public static class PuppetWebServerWpfExtensions
    {
        /// <param name="server">PuppetWebServer 实例</param>
        public static PuppetWebServer UseWpfCapture(this PuppetWebServer server)
        {
            WpfPuppetCapture.UseCapture();
            return server;
        }
    }
}
