using System;
using System.Drawing;
using System.IO;
using Puppet.Core.WinUI;
using Xunit;

namespace Puppet.Core.Tests
{
    public class WinUiPngTests
    {
        [Fact]
        public void Encode_RoundTripsBgraPixels()
        {
            // 2x2：红 / 绿 / 蓝 / 白。Win32 位图的 alpha 字节无意义（PrintWindow 不写），编码器统一补 255。
            var bgra = new byte[]
            {
                0, 0, 255, 0, 0, 255, 0, 0,
                255, 0, 0, 0, 255, 255, 255, 0
            };

            byte[] png = WinUiPng.Encode(bgra, 2, 2);

            using var image = new Bitmap(new MemoryStream(png));
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(Color.Red.ToArgb(), image.GetPixel(0, 0).ToArgb());
            Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(1, 0).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), image.GetPixel(0, 1).ToArgb());
            Assert.Equal(Color.White.ToArgb(), image.GetPixel(1, 1).ToArgb());
        }

        [Fact]
        public void Encode_RejectsShortPixelBuffer()
            => Assert.Throws<ArgumentException>(() => WinUiPng.Encode(new byte[4], 2, 2));
    }
}
