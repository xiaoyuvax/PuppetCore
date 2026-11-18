using System;
using System.Runtime.InteropServices;
using Puppet.Core.AppAgent;
using Puppet.Core.Web;

namespace Puppet.Core.WinUI
{
    /// <summary>
    /// WinUI 3 截屏适配：注入后 A 面为注册实例提供合成方法 <c>CaptureAction</c>。宿主用法：
    /// <code>
    /// new PuppetWebServer().UseWinUiCapture(() => hwnd);
    /// </code>
    /// 取的是窗口自身的 DWM 合成结果（<c>PrintWindow</c>）：不抓桌面、不读取其他窗口、不抢焦点，
    /// 窗口被遮挡时仍可截取；最小化时用 <c>SW_SHOWNOACTIVATE</c> 还原（不激活窗口）。
    /// </summary>
    public static class WinUiPuppetCapture
    {
        /// <summary>
        /// 注入 WinUI 截屏适配。<paramref name="hwndProvider"/> 延迟到截图时才取窗口句柄——
        /// 窗口往往比 Puppet 注册更晚创建；未创建时返回 <see cref="IntPtr.Zero"/> 即可。
        /// 进程内只能有一个截屏适配器（<see cref="PuppetUiActions.CaptureResolver"/> 是单槽委托）。
        /// </summary>
        public static void UseCapture(Func<IntPtr> hwndProvider)
        {
            if (hwndProvider == null) throw new ArgumentNullException(nameof(hwndProvider));
            PuppetUiActions.CaptureResolver = o => o == null ? null : new WinUiCapture(hwndProvider);
        }
    }

    /// <summary>PuppetWebServer 的 WinUI 扩展。</summary>
    public static class PuppetWebServerWinUiExtensions
    {
        /// <param name="server">PuppetWebServer 实例</param>
        /// <param name="hwndProvider">返回窗口句柄；窗口未创建时返回 <see cref="IntPtr.Zero"/></param>
        public static PuppetWebServer UseWinUiCapture(this PuppetWebServer server, Func<IntPtr> hwndProvider)
        {
            WinUiPuppetCapture.UseCapture(hwndProvider);
            return server;
        }
    }

    /// <summary>
    /// 截屏实现。坐标是**物理像素**、相对客户区左上角（高 DPI 下不要传 DIP）；
    /// width/height &lt;= 0 表示整个客户区。可从任意线程调用。
    /// </summary>
    internal sealed class WinUiCapture : IPuppetCapture
    {
        private readonly Func<IntPtr> _hwndProvider;

        public WinUiCapture(Func<IntPtr> hwndProvider) => _hwndProvider = hwndProvider;

        public PuppetArtifact Capture(int x, int y, int width, int height)
        {
            var hwnd = _hwndProvider();
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("窗口句柄尚未创建，无法截取");
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE); // 最小化时截出来是空白，原位还原但不上前台

            if (!GetWindowRect(hwnd, out var window)) throw new InvalidOperationException("读取窗口矩形失败");
            if (!GetClientRect(hwnd, out var client)) throw new InvalidOperationException("读取客户区矩形失败");
            var origin = default(POINT);
            if (!ClientToScreen(hwnd, ref origin)) throw new InvalidOperationException("读取客户区位置失败");

            int wholeWidth = window.Right - window.Left, wholeHeight = window.Bottom - window.Top;
            if (wholeWidth <= 0 || wholeHeight <= 0) throw new InvalidOperationException("窗口尺寸为空，无法截取");

            byte[] pixels = Grab(hwnd, wholeWidth, wholeHeight);

            // 客户区左上角相对整窗左上角的偏移（标题栏 / 边框）
            int left = Math.Clamp(origin.X - window.Left + x, 0, wholeWidth);
            int top = Math.Clamp(origin.Y - window.Top + y, 0, wholeHeight);
            int w = Math.Min(width <= 0 ? client.Right - client.Left : width, wholeWidth - left);
            int h = Math.Min(height <= 0 ? client.Bottom - client.Top : height, wholeHeight - top);
            if (w <= 0 || h <= 0) throw new ArgumentException("截取区域完全落在窗口之外");

            var region = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(pixels, ((top + row) * wholeWidth + left) * 4, region, row * w * 4, w * 4);

            return new PuppetArtifact("capture.png", "image/png", WinUiPng.Encode(region, w, h));
        }

        private static byte[] Grab(IntPtr hwnd, int width, int height)
        {
            var dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC 失败");

            IntPtr dib = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                var info = new BITMAPINFO
                {
                    Header = new BITMAPINFOHEADER
                    {
                        Size = Marshal.SizeOf<BITMAPINFOHEADER>(),
                        Width = width,
                        Height = -height, // 负数 = 自上而下，省一次翻转
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0 // BI_RGB
                    }
                };

                dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection 失败");

                previous = SelectObject(dc, dib);
                // 2 = PW_RENDERFULLCONTENT（DWM 合成结果）；个别系统/驱动不认该标志，退回旧行为再试一次
                if (!PrintWindow(hwnd, dc, 2) && !PrintWindow(hwnd, dc, 0))
                    throw new InvalidOperationException("PrintWindow 失败，无法读取窗口像素");

                var pixels = new byte[width * height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(dc, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(dc);
            }
        }

        private const int SW_SHOWNOACTIVATE = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER Header;
            public int Colors; // 32 位 BI_RGB 不带调色板，仅占位
        }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
    }
}
