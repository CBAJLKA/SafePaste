using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SafePaste.Imaging
{
    /// <summary>Вид заглушки: в цветах картинки, как обычный текст, или тёмной плашкой.</summary>
    internal enum StubStyle
    {
        Text,
        Box
    }

    /// <summary>Что закрыть и что написать поверх. Label = null: только закрыть.</summary>
    internal sealed class RedactBox
    {
        internal RectangleF Bounds;
        internal string Label;

        internal RedactBox(RectangleF bounds, string label)
        {
            Bounds = bounds;
            Label = label;
        }
    }

    /// <summary>
    /// Затирание. Место закрывается сплошной заливкой: размытие и крупные пиксели восстанавливают, заливку нет.
    /// Поверх пишется метка. Картинка собирается заново из пикселей, поэтому метаданные исходного файла
    /// и другие форматы буфера (ссылка на исходник в HTML) с ней не уходят.
    /// </summary>
    internal static class ImageRedactor
    {
        private static readonly Color BoxFill = Color.FromArgb(34, 37, 44);
        private static readonly Color BoxInk = Color.FromArgb(236, 238, 242);

        internal static StubStyle ParseStyle(string value)
        {
            return string.Equals(value, "Box", StringComparison.OrdinalIgnoreCase) ? StubStyle.Box : StubStyle.Text;
        }

        /// <summary>
        /// Новая картинка с закрытыми местами. visible: прочитанные слова, которые остаются: метка, которой
        /// не хватает места, растягивает заливку по пустому фону до них, но не на них.
        /// </summary>
        internal static Bitmap Render(Bitmap source, IList<RedactBox> boxes, IList<RectangleF> visible, StubStyle style)
        {
            int width = source.Width;
            int height = source.Height;
            int[] pixels = ReadPixels(source);
            Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    // Прозрачное ложится на белый: без альфы программа вставила бы его чёрным.
                    graphics.Clear(Color.White);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, width, height, GraphicsUnit.Pixel);
                    List<Stub> stubs = new List<Stub>();
                    foreach (RedactBox box in boxes)
                    {
                        Rectangle area = Snap(box.Bounds, width, height);
                        if (area.Width <= 0 || area.Height <= 0)
                        {
                            continue;
                        }
                        Stub stub = new Stub();
                        stub.Area = area;
                        stub.Label = box.Label;
                        stub.Fill = style == StubStyle.Box ? BoxFill : RingColor(pixels, width, height, area);
                        stub.Ink = style == StubStyle.Box ? BoxInk : InkColor(pixels, width, area, stub.Fill);
                        FitLabel(graphics, stub, visible, width);
                        stubs.Add(stub);
                    }
                    // Сначала все заливки, потом метки: соседняя заливка не должна срезать метку.
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    foreach (Stub stub in stubs)
                    {
                        using (SolidBrush brush = new SolidBrush(stub.Fill))
                        {
                            graphics.FillRectangle(brush, stub.Area);
                        }
                    }
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    foreach (Stub stub in stubs)
                    {
                        if (style == StubStyle.Text && stub.Area.Width >= 4 && stub.Area.Height >= 4)
                        {
                            using (Pen pen = new Pen(Color.FromArgb(70, stub.Ink), 1f))
                            {
                                graphics.DrawRectangle(pen, stub.Area.X + 0.5f, stub.Area.Y + 0.5f, stub.Area.Width - 1f, stub.Area.Height - 1f);
                            }
                        }
                        if (stub.Font != null)
                        {
                            using (SolidBrush brush = new SolidBrush(stub.Ink))
                            using (StringFormat format = LabelFormat())
                            {
                                graphics.SetClip(stub.Area);
                                graphics.DrawString(stub.Label, stub.Font, brush, new RectangleF(stub.Area.X, stub.Area.Y,
                                    stub.Area.Width, stub.Area.Height), format);
                                graphics.ResetClip();
                            }
                            stub.Font.Dispose();
                        }
                    }
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
            finally
            {
                Array.Clear(pixels, 0, pixels.Length);
            }
        }

        private sealed class Stub
        {
            internal Rectangle Area;
            internal string Label;
            internal Color Fill;
            internal Color Ink;
            internal Font Font;
        }

        private static Rectangle Snap(RectangleF bounds, int width, int height)
        {
            Rectangle area = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
            area.Intersect(new Rectangle(0, 0, width, height));
            return area;
        }

        private static StringFormat LabelFormat()
        {
            StringFormat format = new StringFormat(StringFormat.GenericTypographic);
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags |= StringFormatFlags.NoWrap;
            format.Trimming = StringTrimming.None;
            return format;
        }

        /// <summary>
        /// Шрифт метки по высоте строки. Если метка не влезает, заливка растягивается по пустому фону
        /// вправо, потом влево, но не на соседние слова; дальше уменьшается шрифт.
        /// </summary>
        private static void FitLabel(Graphics graphics, Stub stub, IList<RectangleF> visible, int imageWidth)
        {
            if (string.IsNullOrEmpty(stub.Label))
            {
                return;
            }
            float size = Math.Max(7f, stub.Area.Height * 0.62f);
            float minimum = Math.Max(6f, stub.Area.Height * 0.4f);
            float needed;
            using (StringFormat format = LabelFormat())
            {
                while (true)
                {
                    Font font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
                    needed = graphics.MeasureString(stub.Label, font, new PointF(0, 0), format).Width + 4f;
                    if (needed <= stub.Area.Width || size <= minimum)
                    {
                        stub.Font = font;
                        break;
                    }
                    int room = Room(stub.Area, visible, imageWidth, true) + Room(stub.Area, visible, imageWidth, false);
                    if (needed <= stub.Area.Width + room)
                    {
                        stub.Font = font;
                        break;
                    }
                    font.Dispose();
                    size = Math.Max(minimum, size - 1f);
                }
            }
            int missing = (int)Math.Ceiling(needed - stub.Area.Width);
            if (missing <= 0)
            {
                return;
            }
            int right = Math.Min(missing, Room(stub.Area, visible, imageWidth, true));
            stub.Area = new Rectangle(stub.Area.X, stub.Area.Y, stub.Area.Width + right, stub.Area.Height);
            missing -= right;
            if (missing > 0)
            {
                int left = Math.Min(missing, Room(stub.Area, visible, imageWidth, false));
                stub.Area = new Rectangle(stub.Area.X - left, stub.Area.Y, stub.Area.Width + left, stub.Area.Height);
            }
        }

        /// <summary>Сколько пустого места справа (или слева) до ближайшего видимого слова на той же высоте.</summary>
        private static int Room(Rectangle area, IList<RectangleF> visible, int imageWidth, bool toRight)
        {
            float limit = toRight ? imageWidth : 0f;
            if (visible != null)
            {
                foreach (RectangleF word in visible)
                {
                    float overlap = Math.Min(word.Bottom, area.Bottom) - Math.Max(word.Top, area.Top);
                    if (overlap <= 0)
                    {
                        continue;
                    }
                    if (toRight && word.Left >= area.Right - 1)
                    {
                        limit = Math.Min(limit, word.Left - 2f);
                    }
                    else if (!toRight && word.Right <= area.Left + 1)
                    {
                        limit = Math.Max(limit, word.Right + 2f);
                    }
                }
            }
            return Math.Max(0, toRight ? (int)Math.Floor(limit) - area.Right : area.Left - (int)Math.Ceiling(limit));
        }

        /// <summary>Цвет фона: самый частый цвет по кольцу в пару пикселей вокруг места.</summary>
        private static Color RingColor(int[] pixels, int width, int height, Rectangle area)
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();
            Dictionary<int, long[]> sums = new Dictionary<int, long[]>();
            Rectangle ring = Rectangle.Inflate(area, 2, 2);
            ring.Intersect(new Rectangle(0, 0, width, height));
            for (int y = ring.Top; y < ring.Bottom; y++)
            {
                for (int x = ring.Left; x < ring.Right; x++)
                {
                    if (area.Contains(x, y))
                    {
                        continue;
                    }
                    Collect(pixels[y * width + x], counts, sums);
                }
            }
            if (counts.Count == 0)
            {
                for (int y = area.Top; y < area.Bottom; y++)
                {
                    for (int x = area.Left; x < area.Right; x++)
                    {
                        Collect(pixels[y * width + x], counts, sums);
                    }
                }
            }
            int best = -1;
            int bestCount = 0;
            foreach (KeyValuePair<int, int> pair in counts)
            {
                if (pair.Value > bestCount)
                {
                    best = pair.Key;
                    bestCount = pair.Value;
                }
            }
            if (best < 0)
            {
                return Color.White;
            }
            long[] sum = sums[best];
            return Color.FromArgb(255, (int)(sum[0] / bestCount), (int)(sum[1] / bestCount), (int)(sum[2] / bestCount));
        }

        private static void Collect(int argb, Dictionary<int, int> counts, Dictionary<int, long[]> sums)
        {
            Color color = Flatten(argb);
            int key = ((color.R >> 4) << 8) | ((color.G >> 4) << 4) | (color.B >> 4);
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
            long[] sum;
            if (!sums.TryGetValue(key, out sum))
            {
                sum = new long[3];
                sums.Add(key, sum);
            }
            sum[0] += color.R;
            sum[1] += color.G;
            sum[2] += color.B;
        }

        /// <summary>Цвет текста: самые далёкие от фона пиксели внутри места; если текста не видно, контраст к фону.</summary>
        private static Color InkColor(int[] pixels, int width, Rectangle area, Color fill)
        {
            int fillLuma = Luma(fill);
            List<Color> ink = new List<Color>();
            for (int y = area.Top; y < area.Bottom; y++)
            {
                for (int x = area.Left; x < area.Right; x++)
                {
                    Color color = Flatten(pixels[y * width + x]);
                    if (Math.Abs(Luma(color) - fillLuma) > 60)
                    {
                        ink.Add(color);
                    }
                }
            }
            Color contrast = fillLuma > 140 ? Color.FromArgb(24, 24, 24) : Color.FromArgb(236, 236, 236);
            if (ink.Count < 3)
            {
                return contrast;
            }
            ink.Sort(delegate(Color a, Color b)
            {
                return Math.Abs(Luma(b) - fillLuma).CompareTo(Math.Abs(Luma(a) - fillLuma));
            });
            int take = Math.Max(1, ink.Count / 4);
            int r = 0;
            int g = 0;
            int bl = 0;
            for (int i = 0; i < take; i++)
            {
                r += ink[i].R;
                g += ink[i].G;
                bl += ink[i].B;
            }
            Color average = Color.FromArgb(r / take, g / take, bl / take);
            return Math.Abs(Luma(average) - fillLuma) < 90 ? contrast : average;
        }

        private static int Luma(Color color)
        {
            return (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
        }

        /// <summary>Прозрачное ложится на белый: так картинку покажет и вставит большинство программ.</summary>
        private static Color Flatten(int argb)
        {
            int a = (argb >> 24) & 0xFF;
            int r = (argb >> 16) & 0xFF;
            int g = (argb >> 8) & 0xFF;
            int b = argb & 0xFF;
            if (a == 255)
            {
                return Color.FromArgb(r, g, b);
            }
            return Color.FromArgb((r * a + 255 * (255 - a)) / 255, (g * a + 255 * (255 - a)) / 255, (b * a + 255 * (255 - a)) / 255);
        }

        private static int[] ReadPixels(Bitmap image)
        {
            int width = image.Width;
            int height = image.Height;
            BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] pixels = new int[width * height];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), pixels, y * width, width);
                }
                return pixels;
            }
            finally
            {
                image.UnlockBits(data);
            }
        }
    }
}
