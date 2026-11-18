using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Puppet.Core.WinUI
{
    /// <summary>
    /// 最小 PNG 编码：32 位 BGRA 像素 → RGBA PNG。只用 BCL（<see cref="ZLibStream"/>），
    /// 不为一张截图引入图像库；Win32 位图本身不带 alpha，输出统一按不透明处理。
    /// </summary>
    internal static class WinUiPng
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <param name="bgra">自上而下的 BGRA 像素，长度至少 width*height*4</param>
        public static byte[] Encode(byte[] bgra, int width, int height)
        {
            if (bgra == null) throw new ArgumentNullException(nameof(bgra));
            if (width <= 0 || height <= 0) throw new ArgumentException("图像尺寸必须为正");
            if (bgra.Length < width * height * 4) throw new ArgumentException("像素数据不足");

            // 每行前置 1 字节过滤器（0 = None）
            var scanlines = new byte[height * (width * 4 + 1)];
            int s = 0;
            for (int y = 0; y < height; y++)
            {
                scanlines[s++] = 0;
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x * 4;
                    scanlines[s++] = bgra[p + 2];
                    scanlines[s++] = bgra[p + 1];
                    scanlines[s++] = bgra[p];
                    scanlines[s++] = 255;
                }
            }

            byte[] idat;
            using (var raw = new MemoryStream())
            {
                using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
                    zlib.Write(scanlines, 0, scanlines.Length);
                idat = raw.ToArray();
            }

            var header = new byte[13];
            WriteInt(header, 0, width);
            WriteInt(header, 4, height);
            header[8] = 8; // 位深
            header[9] = 6; // 颜色类型：RGBA

            using var png = new MemoryStream(idat.Length + 64);
            png.Write(Signature, 0, Signature.Length);
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", idat);
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var head = new byte[8];
            WriteInt(head, 0, data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            stream.Write(head, 0, head.Length);
            stream.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFF;
            for (int i = 4; i < head.Length; i++) crc = CrcTable[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < data.Length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

            var tail = new byte[4];
            WriteInt(tail, 0, unchecked((int)(crc ^ 0xFFFFFFFF)));
            stream.Write(tail, 0, tail.Length);
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}
