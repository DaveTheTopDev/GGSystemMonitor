using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GGSystemMonitor
{
    internal static class NativeOledPixelFont
    {
        internal const int Height = 16;
        internal const int CellWidth = 8;

        // Load the 14 KB subset once, when a native line first needs software rendering.
        // The embedded glyph data is licensed separately: see THIRD-PARTY-NOTICES.md.
        private static class Atlas
        {
            internal static readonly Dictionary<int, byte[]> Glyphs = Load();

            private static Dictionary<int, byte[]> Load()
            {
                var glyphs = new Dictionary<int, byte[]>();
                using (var reader = new StreamReader(typeof(NativeOledPixelFont).Assembly
                    .GetManifestResourceStream("GGSystemMonitor.OledNativeGlyphs.hex")))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int split = line.IndexOf(':');
                        int codePoint = int.Parse(line.Substring(0, split), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        var rows = new byte[(line.Length - split - 1) / 2];
                        for (int i = 0; i < rows.Length; i++)
                            rows[i] = Convert.ToByte(line.Substring(split + 1 + i * 2, 2), 16);
                        glyphs.Add(codePoint, rows);
                    }
                }
                return glyphs;
            }
        }

        internal static byte[] FindGlyph(string element)
        {
            if (element.Length > 1 && (element[element.Length - 1] == '\uFE0E' || element[element.Length - 1] == '\uFE0F'))
                element = element.Substring(0, element.Length - 1);
            if (element.Length == 2 && !char.IsSurrogate(element[0]) && !char.IsSurrogate(element[1]))
                element = element.Normalize(NormalizationForm.FormC);
            int codePoint;
            if (element.Length == 1)
                codePoint = element[0];
            else if (element.Length == 2 && char.IsSurrogatePair(element, 0))
                codePoint = char.ConvertToUtf32(element, 0);
            else
                return null;
            return FindGlyph(codePoint);
        }

        internal static byte[] FindGlyph(int codePoint) => Atlas.Glyphs.TryGetValue(codePoint, out var rows) ? rows : null;

        internal static int GetWidth(byte[] rows) => rows.Length / Height * CellWidth;

        internal static void Blit(byte[] frame, int stride, int x, int y, byte[] rows, int right)
        {
            // Both the 8- and 16-pixel glyphs and the OLED frame are MSB-first.
            // A glyph can start at any bit, so a 16-pixel row can span three bytes.
            int bytesPerRow = rows.Length / Height;
            int width = Math.Min(bytesPerRow * CellWidth, right - x);
            if (width <= 0) return;
            int bitShift = x & 7;
            int mask = 0xFFFF << (16 - width);
            int pos = y * stride + (x >> 3);
            for (int row = 0, i = 0; row < Height; row++, i += bytesPerRow, pos += stride)
            {
                int bits = (rows[i] << 8) | (bytesPerRow == 2 ? rows[i + 1] : 0);
                int chunk = (bits & mask) << (8 - bitShift);
                frame[pos] |= (byte)(chunk >> 16);
                if (bitShift + width > 8) frame[pos + 1] |= (byte)(chunk >> 8);
                if (bitShift + width > 16) frame[pos + 2] |= (byte)chunk;
            }
        }
    }
}
