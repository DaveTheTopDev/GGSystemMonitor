using System;
using System.Collections.Generic;
using System.Drawing;

namespace GGSystemMonitor
{
    internal partial class Program
    {
        internal const string NativeWidgetFontId = "@native";
        private const int NativeBitmapTextX = 2;
        private const int KeyboardNativeLinePitch = 19; // Measured on the 128x40 native OLED.
        private const int BitmapIndicatorPitch = 16;
        private const int BitmapIndicatorMaxInkWidth = 13;

        private readonly struct NativePixelGlyph
        {
            internal readonly string Text;
            internal readonly int X, Advance;
            internal readonly byte[] Rows;
            internal NativePixelGlyph(string text, int x, int advance, byte[] rows)
            { Text = text; X = x; Advance = advance; Rows = rows; }
        }

        private static int NativeBitmapGlyphCols(string element, byte[] rows) =>
            rows != null ? NativeOledPixelFont.GetWidth(rows) / NativeOledPixelFont.CellWidth
                          : Math.Max(GetNativeOledTextElementCols(element), element.Length);

        private static int NativeBitmapTextCols(string suffix) =>
            Math.Max(0, BitmapTextWidth(suffix) - NativeBitmapTextX) / NativeOledPixelFont.CellWidth;

        private static List<NativePixelGlyph> LayoutNativeBitmapLine(string text, string suffix)
        {
            int right = BitmapTextWidth(suffix);
            int x = NativeBitmapTextX;
            var glyphs = new List<NativePixelGlyph>(DEFAULT_SCREEN_COLS);
            foreach (var element in GetTextElements(text))
            {
                var rows = NativeOledPixelFont.FindGlyph(element);
                int advance = NativeOledPixelFont.CellWidth * NativeBitmapGlyphCols(element, rows);
                if (x + advance > right) break;
                glyphs.Add(new NativePixelGlyph(element, x, advance, rows));
                x += advance;
            }
            return glyphs;
        }

        private static int NativeBitmapLineTop(int y, int rowHeight)
        {
            if (rowHeight != 20) return y + (rowHeight - NativeOledPixelFont.Height) / 2;
            int inset = (rowHeight * 2 - KeyboardNativeLinePitch - NativeOledPixelFont.Height) / 2;
            return inset + (y == 0 ? 0 : KeyboardNativeLinePitch);
        }

        private static void BlitNativeBitmapLine(byte[] packed, List<NativePixelGlyph> glyphs, int y, int rowHeight, string suffix)
        {
            if (glyphs == null) return;
            int top = NativeBitmapLineTop(y, rowHeight);
            int right = BitmapTextWidth(suffix);
            foreach (var glyph in glyphs)
                if (glyph.Rows != null)
                    NativeOledPixelFont.Blit(packed, OLED_IMAGE_WIDTH / 8, glyph.X, top, glyph.Rows, right);
        }

        private static int BitmapTextWidth(string suffix)
        {
            int cols = GetIndicatorLogicalCols(suffix);
            if (cols == 0) return OLED_IMAGE_WIDTH;
            int reserved = BitmapIndicatorMaxInkWidth + (cols == 3 ? BitmapIndicatorPitch : 0);
            return OLED_IMAGE_WIDTH - reserved - 1; // One dark pixel between text and indicators.
        }

        private static void BlitBitmapIndicators(byte[] packed, int y, int rowHeight, string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return;
            int right = OLED_IMAGE_WIDTH;
            int top = NativeBitmapLineTop(y, rowHeight);
            void Blit(string symbol)
            {
                // Drop only the blank right margin (2 pixels for the warning
                // triangle, 3 for the arrow and fire), not any visible dots.
                int trim = symbol == TEMPERATURE_WARNING_INDICATOR ? 2 : 3;
                NativeOledPixelFont.Blit(packed, OLED_IMAGE_WIDTH / 8, right - BitmapIndicatorPitch + trim,
                    top, NativeOledPixelFont.FindGlyph(symbol), right);
            }

            if (suffix.EndsWith(DEFAULT_CAPS_LOCK_INDICATOR, StringComparison.Ordinal))
            {
                Blit(DEFAULT_CAPS_LOCK_INDICATOR);
                right -= BitmapIndicatorPitch;
            }
            if (suffix.StartsWith(TEMPERATURE_WARNING_INDICATOR, StringComparison.Ordinal))
                Blit(TEMPERATURE_WARNING_INDICATOR);
            else if (suffix.StartsWith(TEMPERATURE_CRITICAL_INDICATOR, StringComparison.Ordinal))
                Blit(TEMPERATURE_CRITICAL_INDICATOR);
        }

        private static byte[] InlineBitmapIcon(string text, int at, bool weather, out int length)
        {
            int codePoint = text[at];
            length = 1;
            if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]))
            {
                codePoint = char.ConvertToUtf32(text, at);
                length = 2;
            }
            switch (codePoint)
            {
                case '⏸': case '▶': case '♪': case '♫': // Media source
                case '☀': case '☁': case '☂': case '❄': case '⚡': case '≡': // Weather
                case '⏰': case 0x1F4C5: // Time and date
                case '⚠': case 0x1F525: case 0x1F845: // Indicators typed into a label
                    break;
                case '*': case '·':
                    if (!weather) return null; // Weather animation, not ordinary punctuation.
                    break;
                default:
                    return null;
            }
            int next = at + length;
            if (next < text.Length && (text[next] == '\uFE0E' || text[next] == '\uFE0F')) length++;
            return NativeOledPixelFont.FindGlyph(codePoint);
        }

        // Measure and draw with the same advances. Keeping ordinary text in whole runs
        // preserves the selected font's kerning and shaping, and costs no extra GDI work
        // to draw a line that contains no icons.
        private static float LayoutCustomBitmapText(Graphics graphics, Font font, StringFormat format, string text,
            bool weather, byte[] frame = null, Brush brush = null, int y = 0, int rowHeight = 0, int right = 0)
        {
            float x = 0;
            int start = 0;
            float textY = frame == null ? 0 : y + Math.Max(0, (rowHeight - font.GetHeight(graphics)) / 2);
            void LayoutTextRun(int end, bool measure)
            {
                if (end == start) return;
                string run = start == 0 && end == text.Length ? text : text.Substring(start, end - start);
                if (frame != null) graphics.DrawString(run, font, brush, new PointF(x, textY), format);
                if (measure) x += graphics.MeasureString(run, font, PointF.Empty, format).Width;
            }
            for (int i = 0; i < text.Length;)
            {
                var rows = InlineBitmapIcon(text, i, weather, out int length);
                if (rows != null)
                {
                    LayoutTextRun(i, measure: true);
                    if (frame != null)
                        NativeOledPixelFont.Blit(frame, OLED_IMAGE_WIDTH / 8, (int)(x + .5f),
                            NativeBitmapLineTop(y, rowHeight), rows, right);
                    x += NativeOledPixelFont.GetWidth(rows);
                    start = i + length;
                }
                i += length;
            }
            LayoutTextRun(text.Length, measure: frame == null);
            return x;
        }

        private static void DrawNativeFallbackGlyphs(Graphics graphics, List<NativePixelGlyph> glyphs, Brush brush,
            float y, int rowHeight, StringFormat format)
        {
            foreach (var glyph in glyphs)
            {
                if (glyph.Rows != null || string.IsNullOrWhiteSpace(glyph.Text) || char.IsControl(glyph.Text[0])) continue;
                // Use Windows glyph fallback for uncommon scripts and emoji;
                // normal text is drawn by the packed pixel atlas, not GDI+.
                var font = NativeBitmapFont();
                var state = graphics.Save();
                try
                {
                    graphics.SetClip(new RectangleF(glyph.X, y, glyph.Advance, rowHeight));
                    float width = GetTextAdvanceWidth(font, glyph.Text);
                    float textY = NativeBitmapLineTop((int)y, rowHeight) +
                        Math.Max(0, (NativeOledPixelFont.Height - font.GetHeight(graphics)) / 2);
                    graphics.TranslateTransform(glyph.X, textY);
                    if (width > glyph.Advance) graphics.ScaleTransform(glyph.Advance / width, 1);
                    graphics.DrawString(glyph.Text, font, brush, PointF.Empty, format);
                }
                finally { graphics.Restore(state); }
            }
        }
    }
}
