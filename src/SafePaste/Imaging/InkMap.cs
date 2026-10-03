using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SafePaste.Imaging
{
    /// <summary>
    /// «Чернила» картинки: пиксели, заметно отличные от ровного фона вокруг. По ним видно, где на картинке
    /// есть текст, который распознавание не прочитало, и где кончается строка справа от слова «пароль».
    /// Пёстрый фон (фотографии) в расчёт не берётся: там нельзя отличить текст от рисунка.
    /// </summary>
    internal sealed class InkMap
    {
        private const int Block = 16;
        private const int InkContrast = 48;
        private const int SmearGap = 6;

        internal readonly int Width;
        internal readonly int Height;
        private readonly byte[] ink;

        private InkMap(int width, int height, byte[] ink)
        {
            Width = width;
            Height = height;
            this.ink = ink;
        }

        internal bool IsInk(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height && ink[y * Width + x] != 0;
        }

        internal static InkMap Create(Bitmap image)
        {
            int width = image.Width;
            int height = image.Height;
            byte[] luma = ReadLuma(image);
            int blocksX = (width + Block - 1) / Block;
            int blocksY = (height + Block - 1) / Block;
            int[] background = new int[blocksX * blocksY];
            bool[] flat = new bool[blocksX * blocksY];
            int[] histogram = new int[32];
            int[] sums = new int[32];
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    Array.Clear(histogram, 0, histogram.Length);
                    Array.Clear(sums, 0, sums.Length);
                    int count = 0;
                    int yEnd = Math.Min(height, (by + 1) * Block);
                    int xEnd = Math.Min(width, (bx + 1) * Block);
                    for (int y = by * Block; y < yEnd; y++)
                    {
                        int row = y * width;
                        for (int x = bx * Block; x < xEnd; x++)
                        {
                            int value = luma[row + x];
                            histogram[value >> 3]++;
                            sums[value >> 3] += value;
                            count++;
                        }
                    }
                    int mode = 0;
                    for (int bin = 1; bin < 32; bin++)
                    {
                        if (histogram[bin] > histogram[mode])
                        {
                            mode = bin;
                        }
                    }
                    int index = by * blocksX + bx;
                    // Соседние корзины тоже фон: сглаживание и градиенты размазывают его по двум корзинам.
                    int near = histogram[mode] + (mode > 0 ? histogram[mode - 1] : 0) + (mode < 31 ? histogram[mode + 1] : 0);
                    flat[index] = count > 0 && near * 100 >= count * 55;
                    background[index] = histogram[mode] == 0 ? 255 : sums[mode] / histogram[mode];
                }
            }
            // Блок, плотно забитый крупным текстом, берёт фон у ровного соседа.
            bool[] usable = (bool[])flat.Clone();
            int[] usableBackground = (int[])background.Clone();
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int index = by * blocksX + bx;
                    if (flat[index])
                    {
                        continue;
                    }
                    int[] dx = { -1, 1, 0, 0 };
                    int[] dy = { 0, 0, -1, 1 };
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = bx + dx[k];
                        int ny = by + dy[k];
                        if (nx < 0 || ny < 0 || nx >= blocksX || ny >= blocksY || !flat[ny * blocksX + nx])
                        {
                            continue;
                        }
                        usable[index] = true;
                        usableBackground[index] = background[ny * blocksX + nx];
                        break;
                    }
                }
            }
            byte[] mask = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int blockRow = (y / Block) * blocksX;
                for (int x = 0; x < width; x++)
                {
                    int block = blockRow + x / Block;
                    if (usable[block] && Math.Abs(luma[row + x] - usableBackground[block]) >= InkContrast)
                    {
                        mask[row + x] = 1;
                    }
                }
            }
            Array.Clear(luma, 0, luma.Length);
            return new InkMap(width, height, mask);
        }

        /// <summary>
        /// Места, похожие на строки текста, в которые не попало ни одно прочитанное слово.
        /// Слова вычитаются из чернил с запасом, остаток склеивается по строкам и отбирается по виду:
        /// высота строки текста, вытянутость, плотность.
        /// </summary>
        internal List<Rectangle> FindUnread(IList<RectangleF> words)
        {
            byte[] work = (byte[])ink.Clone();
            foreach (RectangleF word in words)
            {
                float pad = Math.Max(2f, word.Height * 0.25f);
                Rectangle clear = Rectangle.FromLTRB((int)Math.Floor(word.Left - pad), (int)Math.Floor(word.Top - pad),
                    (int)Math.Ceiling(word.Right + pad), (int)Math.Ceiling(word.Bottom + pad));
                clear.Intersect(new Rectangle(0, 0, Width, Height));
                for (int y = clear.Top; y < clear.Bottom; y++)
                {
                    Array.Clear(work, y * Width + clear.Left, clear.Width);
                }
            }
            List<Rectangle> found = new List<Rectangle>();
            foreach (Component component in Components(work))
            {
                int width = component.Right - component.Left;
                int height = component.Bottom - component.Top;
                if (height < 5 || height > 64 || width < Math.Max(10, height * 6 / 5) || component.Ink < 20)
                {
                    continue;
                }
                double density = component.Ink / (double)(width * height);
                if (density < 0.05 || density > 0.7 || (double)width * height > 0.12 * Width * Height)
                {
                    continue;
                }
                found.Add(Rectangle.FromLTRB(Math.Max(0, component.Left - 2), Math.Max(0, component.Top - 2),
                    Math.Min(Width, component.Right + 2), Math.Min(Height, component.Bottom + 2)));
            }
            return Merge(found);
        }

        /// <summary>
        /// Где кончаются чернила строки справа: от fromX вправо, пока пробел не шире maxGap.
        /// Так закрывается и непрочитанный хвост после слова «пароль».
        /// </summary>
        internal float RightmostInk(RectangleF band, float fromX, float maxGap)
        {
            int top = Math.Max(0, (int)Math.Floor(band.Top));
            int bottom = Math.Min(Height, (int)Math.Ceiling(band.Bottom));
            int x = Math.Max(0, (int)Math.Floor(fromX));
            int last = -1;
            int gap = Math.Max(4, (int)Math.Ceiling(maxGap));
            for (; x < Width; x++)
            {
                bool column = false;
                for (int y = top; y < bottom && !column; y++)
                {
                    column = ink[y * Width + x] != 0;
                }
                if (column)
                {
                    last = x;
                }
                else if (last >= 0 ? x - last > gap : x - fromX > gap)
                {
                    break;
                }
            }
            return last < 0 ? fromX : last + 1;
        }

        private sealed class Component
        {
            internal int Left = int.MaxValue;
            internal int Top = int.MaxValue;
            internal int Right;
            internal int Bottom;
            internal int Ink;
        }

        /// <summary>Связные области чернил после склейки по горизонтали с промежутком до SmearGap.</summary>
        private List<Component> Components(byte[] mask)
        {
            List<int> runStart = new List<int>();
            List<int> runEnd = new List<int>();
            List<int> runRow = new List<int>();
            List<int> runInk = new List<int>();
            List<int> rowFirst = new List<int>(Height + 1);
            for (int y = 0; y < Height; y++)
            {
                rowFirst.Add(runStart.Count);
                int row = y * Width;
                int x = 0;
                while (x < Width)
                {
                    while (x < Width && mask[row + x] == 0)
                    {
                        x++;
                    }
                    if (x >= Width)
                    {
                        break;
                    }
                    int start = x;
                    int last = x;
                    int count = 0;
                    while (x < Width && x - last <= SmearGap)
                    {
                        if (mask[row + x] != 0)
                        {
                            last = x;
                            count++;
                        }
                        x++;
                    }
                    runStart.Add(start);
                    runEnd.Add(last + 1);
                    runRow.Add(y);
                    runInk.Add(count);
                    x = last + 1;
                }
            }
            rowFirst.Add(runStart.Count);
            int[] parent = new int[runStart.Count];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }
            for (int y = 1; y < Height; y++)
            {
                int a = rowFirst[y - 1];
                int aEnd = rowFirst[y];
                int b = rowFirst[y];
                int bEnd = rowFirst[y + 1];
                while (a < aEnd && b < bEnd)
                {
                    if (runStart[a] < runEnd[b] && runStart[b] < runEnd[a])
                    {
                        Union(parent, a, b);
                    }
                    if (runEnd[a] < runEnd[b])
                    {
                        a++;
                    }
                    else
                    {
                        b++;
                    }
                }
            }
            Dictionary<int, Component> components = new Dictionary<int, Component>();
            for (int i = 0; i < parent.Length; i++)
            {
                int root = Find(parent, i);
                Component component;
                if (!components.TryGetValue(root, out component))
                {
                    component = new Component();
                    components.Add(root, component);
                }
                component.Left = Math.Min(component.Left, runStart[i]);
                component.Right = Math.Max(component.Right, runEnd[i]);
                component.Top = Math.Min(component.Top, runRow[i]);
                component.Bottom = Math.Max(component.Bottom, runRow[i] + 1);
                component.Ink += runInk[i];
            }
            return new List<Component>(components.Values);
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
            {
                parent[Math.Max(rootA, rootB)] = Math.Min(rootA, rootB);
            }
        }

        /// <summary>Соседние куски одной строки становятся одним местом.</summary>
        private static List<Rectangle> Merge(List<Rectangle> found)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < found.Count && !merged; i++)
                {
                    for (int j = i + 1; j < found.Count; j++)
                    {
                        Rectangle a = found[i];
                        Rectangle b = found[j];
                        int overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                        int gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
                        if (a.IntersectsWith(b) || (overlap * 2 >= Math.Min(a.Height, b.Height) && gap <= Math.Max(a.Height, b.Height)))
                        {
                            found[i] = Rectangle.Union(a, b);
                            found.RemoveAt(j);
                            merged = true;
                            break;
                        }
                    }
                }
            }
            found.Sort(delegate(Rectangle a, Rectangle b)
            {
                return a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left);
            });
            return found;
        }

        /// <summary>Яркость пикселей; прозрачное считается белым фоном.</summary>
        private static byte[] ReadLuma(Bitmap image)
        {
            int width = image.Width;
            int height = image.Height;
            BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[width * 4];
                byte[] luma = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < width; x++)
                    {
                        int b = row[x * 4];
                        int g = row[x * 4 + 1];
                        int r = row[x * 4 + 2];
                        int a = row[x * 4 + 3];
                        int value = (r * 299 + g * 587 + b * 114) / 1000;
                        luma[y * width + x] = (byte)((value * a + 255 * (255 - a)) / 255);
                    }
                }
                Array.Clear(row, 0, row.Length);
                return luma;
            }
            finally
            {
                image.UnlockBits(data);
            }
        }
    }
}
