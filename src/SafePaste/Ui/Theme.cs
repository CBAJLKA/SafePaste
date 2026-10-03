using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SafePaste.Ui
{
    /// <summary>Размеры в коде заданы для 96 DPI и пересчитываются под экран.</summary>
    internal static class Dpi
    {
        internal static readonly float Factor = ReadFactor();

        private static float ReadFactor()
        {
            try
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return graphics.DpiX / 96f;
                }
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        internal static int S(int value)
        {
            return (int)Math.Round(value * Factor);
        }

        internal static float F(float value)
        {
            return value * Factor;
        }
    }

    /// <summary>Значки из шрифта Segoe Fluent Icons (в Windows 10 те же коды у Segoe MDL2 Assets).</summary>
    internal static class Glyphs
    {
        internal const string Search = "\uE721";
        internal const string Close = "\uE8BB";
        internal const string Clear = "\uE711";
        internal const string Paste = "\uE77F";
        internal const string Sync = "\uE895";
        internal const string Return = "\uE751";
        internal const string ChevronRight = "\uE76C";
        internal const string ChevronLeft = "\uE76B";
        internal const string ChevronDown = "\uE70D";
        internal const string ChevronUp = "\uE70E";
        internal const string Delete = "\uE74D";
        internal const string Folder = "\uE8B7";
        internal const string Add = "\uE710";
        internal const string Reset = "\uE777";
        internal const string Warning = "\uE7BA";
        internal const string Error = "\uE783";
        internal const string Info = "\uE946";
        internal const string Question = "\uE9CE";
        internal const string Check = "\uE73E";
        internal const string Pin = "\uE840";
        internal const string Lock = "\uE72E";
        internal const string Settings = "\uE713";
        internal const string Erase = "\uE75C";
        internal const string Unlock = "\uE785";
        internal const string SelectAll = "\uE8B3";
        internal const string Picture = "\uE91B";
        internal const string Text = "\uE8D2";
        internal const string OpenWindow = "\uE8A7";
    }

    /// <summary>
    /// Оформление «под стекло». В Windows 11 за окном лежит размытый фон, поверх него рисуются
    /// полупрозрачные слои. На старых системах те же слои ложатся на сплошной тёмный фон.
    /// </summary>
    internal static class Theme
    {
        /// <summary>Проверки интерфейса включают сплошной фон, чтобы снимки не зависели от рабочего стола.</summary>
        internal static bool ForceSolid = false;

        internal static readonly Color Text = Color.FromArgb(238, 241, 245);
        internal static readonly Color Secondary = Color.FromArgb(172, 180, 192);
        internal static readonly Color Tertiary = Color.FromArgb(128, 136, 149);
        internal static readonly Color Accent = Color.FromArgb(124, 224, 194);
        internal static readonly Color AccentHover = Color.FromArgb(152, 236, 211);
        internal static readonly Color AccentPressed = Color.FromArgb(100, 196, 168);
        internal static readonly Color AccentInk = Color.FromArgb(8, 38, 31);
        internal static readonly Color Warm = Color.FromArgb(255, 184, 122);
        internal static readonly Color Danger = Color.FromArgb(255, 122, 112);
        internal static readonly Color DangerFill = Color.FromArgb(235, 72, 64);
        internal static readonly Color CloseHover = Color.FromArgb(255, 95, 87);

        // Слои. Альфа-канал важен: на стекле они просвечивают.
        internal static readonly Color WindowGlass = Color.FromArgb(192, 16, 18, 24);
        internal static readonly Color WindowSolid = Color.FromArgb(22, 24, 30);
        internal static readonly Color PopupGlass = Color.FromArgb(226, 28, 30, 37);
        internal static readonly Color PopupSolid = Color.FromArgb(34, 36, 43);
        internal static readonly Color Well = Color.FromArgb(84, 0, 0, 0);
        internal static readonly Color EdgeTop = Color.FromArgb(46, 255, 255, 255);
        internal static readonly Color EdgeBottom = Color.FromArgb(12, 255, 255, 255);
        internal static readonly Color Line = Color.FromArgb(24, 255, 255, 255);
        internal static readonly Color Fill = Color.FromArgb(22, 255, 255, 255);
        internal static readonly Color FillHover = Color.FromArgb(38, 255, 255, 255);
        internal static readonly Color FillPressed = Color.FromArgb(12, 255, 255, 255);
        internal static readonly Color RowHover = Color.FromArgb(14, 255, 255, 255);
        internal static readonly Color RowSelected = Color.FromArgb(46, 124, 224, 194);
        internal static readonly Color TextSelection = Color.FromArgb(112, 10, 132, 255);

        // Подсветка значений в тексте.
        internal static readonly Color HiddenFill = Color.FromArgb(46, 255, 159, 10);
        internal static readonly Color KeptFill = Color.FromArgb(28, 255, 255, 255);
        internal static readonly Color SecretFill = Color.FromArgb(56, 255, 69, 58);
        internal static readonly Color PlaceholderFill = Color.FromArgb(40, 124, 224, 194);
        internal static readonly Color RestoredFill = Color.FromArgb(24, 124, 224, 194);
        internal static readonly Color MissingFill = Color.FromArgb(34, 255, 184, 122);
        internal static readonly Color MatchFill = Color.FromArgb(72, 255, 214, 10);
        internal static readonly Color MatchCurrent = Color.FromArgb(236, 255, 214, 10);
        internal static readonly Color MatchInk = Color.FromArgb(34, 26, 0);

        internal static readonly Font UiFont = CreateFont(10f, "Segoe UI Variable Text", "Segoe UI");
        internal static readonly Font SmallFont = CreateFont(9f, "Segoe UI Variable Small", "Segoe UI");
        internal static readonly Font StrongFont = CreateFont(10f, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
        internal static readonly Font TitleFont = CreateFont(12.5f, "Segoe UI Variable Display Semib", "Segoe UI Semibold");
        internal static readonly Font HeadingFont = CreateFont(19f, "Segoe UI Variable Display Semib", "Segoe UI Semibold");
        internal static readonly Font MonoFont = CreateFont(10.5f, "Cascadia Mono", "Consolas");
        internal static readonly Font SmallMonoFont = CreateFont(9.5f, "Cascadia Mono", "Consolas");
        internal static readonly Font ShortcutFont = CreateFont(11.5f, "Cascadia Mono", "Consolas");
        internal static readonly Font IconFont = CreateFont(11f, "Segoe Fluent Icons", "Segoe MDL2 Assets");
        internal static readonly Font SmallIconFont = CreateFont(8.5f, "Segoe Fluent Icons", "Segoe MDL2 Assets");
        internal static readonly Font MediumIconFont = CreateFont(14f, "Segoe Fluent Icons", "Segoe MDL2 Assets");
        internal static readonly Font LargeIconFont = CreateFont(17f, "Segoe Fluent Icons", "Segoe MDL2 Assets");

        private static readonly Dictionary<string, GraphicsPath> glyphPaths = new Dictionary<string, GraphicsPath>(StringComparer.Ordinal);
        private static readonly Bitmap measureSurface = CreateMeasureSurface();
        private static readonly Graphics measureGraphics = CreateMeasureGraphics();

        private static Font CreateFont(float size, params string[] families)
        {
            foreach (string family in families)
            {
                if (IsInstalled(family))
                {
                    return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
                }
            }
            return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static bool IsInstalled(string family)
        {
            try
            {
                using (FontFamily found = new FontFamily(family))
                {
                    return found.IsStyleAvailable(FontStyle.Regular);
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static Bitmap CreateMeasureSurface()
        {
            Bitmap bitmap = new Bitmap(1, 1);
            bitmap.SetResolution(96f * Dpi.Factor, 96f * Dpi.Factor);
            return bitmap;
        }

        private static Graphics CreateMeasureGraphics()
        {
            Graphics graphics = Graphics.FromImage(measureSurface);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            return graphics;
        }

        // ---------------------------------------------------------------- текст

        internal static StringFormat CreateFormat(StringAlignment horizontal, StringAlignment vertical, bool wrap)
        {
            StringFormat format = new StringFormat(StringFormat.GenericTypographic);
            format.Alignment = horizontal;
            format.LineAlignment = vertical;
            format.HotkeyPrefix = HotkeyPrefix.None;
            format.Trimming = wrap ? StringTrimming.Word : StringTrimming.EllipsisCharacter;
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            if (!wrap)
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
            }
            return format;
        }

        internal static void PrepareText(Graphics graphics)
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
        }

        internal static void DrawText(Graphics graphics, string text, Font font, Color color, RectangleF bounds,
            StringAlignment horizontal, StringAlignment vertical, bool wrap)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (StringFormat format = CreateFormat(horizontal, vertical, wrap))
            using (SolidBrush brush = new SolidBrush(color))
            {
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }

        internal static void DrawText(Graphics graphics, string text, Font font, Color color, RectangleF bounds)
        {
            DrawText(graphics, text, font, color, bounds, StringAlignment.Near, StringAlignment.Center, false);
        }

        internal static void DrawGlyph(Graphics graphics, string glyph, Font font, Color color, RectangleF bounds)
        {
            DrawText(graphics, glyph, font, color, bounds, StringAlignment.Center, StringAlignment.Center, false);
        }

        /// <summary>
        /// Значок контуром с центром рисунка в точке center, с поворотом и масштабом. Контур, а не текст:
        /// при повороте значок не дрожит и не меняет толщину линий в начале и в конце движения.
        /// </summary>
        internal static void FillGlyph(Graphics graphics, string glyph, Font font, Color color, PointF center, float angle, float scale)
        {
            if (string.IsNullOrEmpty(glyph))
            {
                return;
            }
            float em = font.SizeInPoints * graphics.DpiY / 72f;
            string key = glyph + "\u0001" + font.Name + "\u0001" + em.ToString("0.00", CultureInfo.InvariantCulture);
            GraphicsPath path;
            if (!glyphPaths.TryGetValue(key, out path))
            {
                path = new GraphicsPath();
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    path.AddString(glyph, font.FontFamily, (int)font.Style, em, PointF.Empty, format);
                }
                RectangleF ink = path.GetBounds();
                using (Matrix shift = new Matrix())
                {
                    shift.Translate(-(ink.X + ink.Width / 2f), -(ink.Y + ink.Height / 2f));
                    path.Transform(shift);
                }
                glyphPaths[key] = path;
            }
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(center.X, center.Y);
            if (angle != 0f)
            {
                graphics.RotateTransform(angle);
            }
            if (scale != 1f)
            {
                graphics.ScaleTransform(scale, scale);
            }
            using (SolidBrush brush = new SolidBrush(color))
            {
                graphics.FillPath(brush, path);
            }
            graphics.Restore(state);
        }

        /// <summary>Размер текста в пикселях экрана; при width = 0 без переноса строк.</summary>
        internal static SizeF Measure(string text, Font font, float width)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new SizeF(0f, font.GetHeight(measureGraphics));
            }
            bool wrap = width > 0;
            using (StringFormat format = CreateFormat(StringAlignment.Near, StringAlignment.Near, wrap))
            {
                return wrap
                    ? measureGraphics.MeasureString(text, font, new SizeF(width, 100000f), format)
                    : measureGraphics.MeasureString(text, font, new PointF(0, 0), format);
            }
        }

        internal static float LineHeight(Font font)
        {
            return font.GetHeight(measureGraphics);
        }

        // ---------------------------------------------------------------- поверхности

        internal static GraphicsPath Round(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
            if (diameter < 1f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Фон окна: на стекле тонировка поверх размытия, иначе сплошной цвет.</summary>
        internal static void PaintWindow(Graphics graphics, Rectangle bounds, bool glass)
        {
            PaintSurface(graphics, bounds, glass, WindowGlass, WindowSolid);
        }

        internal static void PaintPopup(Graphics graphics, Rectangle bounds, bool glass)
        {
            PaintSurface(graphics, bounds, glass, PopupGlass, PopupSolid);
            if (!glass)
            {
                // Без стекла Windows не рисует рамку всплывающего окна.
                using (Pen pen = new Pen(Color.FromArgb(58, 61, 70)))
                {
                    graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
                }
            }
        }

        private static void PaintSurface(Graphics graphics, Rectangle bounds, bool glass, Color tint, Color solid)
        {
            if (glass)
            {
                graphics.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(tint))
                {
                    graphics.FillRectangle(brush, bounds);
                }
                return;
            }
            graphics.Clear(solid);
        }

        /// <summary>Карточка: притемнённое стекло со светлой кромкой сверху.</summary>
        internal static void PaintCard(Graphics graphics, RectangleF bounds, float radius)
        {
            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Round(bounds, radius))
            {
                using (SolidBrush fill = new SolidBrush(Well))
                {
                    graphics.FillPath(fill, path);
                }
                StrokeEdge(graphics, path, bounds, EdgeTop, EdgeBottom);
            }
            graphics.SmoothingMode = previous;
        }

        internal static void StrokeEdge(Graphics graphics, GraphicsPath path, RectangleF bounds, Color top, Color bottom)
        {
            RectangleF area = RectangleF.Inflate(bounds, 1f, 1f);
            using (LinearGradientBrush edge = new LinearGradientBrush(area, top, bottom, LinearGradientMode.Vertical))
            using (Pen pen = new Pen(edge, 1f))
            {
                graphics.DrawPath(pen, path);
            }
        }

        internal static void FillRound(Graphics graphics, RectangleF bounds, float radius, Color color)
        {
            if (color.A == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            using (GraphicsPath path = Round(bounds, radius))
            using (SolidBrush brush = new SolidBrush(color))
            {
                graphics.FillPath(brush, path);
            }
        }

        internal static Color Fade(Color color, float opacity)
        {
            int alpha = (int)Math.Round(color.A * Math.Max(0f, Math.Min(1f, opacity)));
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        /// <summary>Цвет между from и to: amount 0 даёт from, 1 даёт to.</summary>
        internal static Color Blend(Color from, Color to, float amount)
        {
            if (amount <= 0f)
            {
                return from;
            }
            if (amount >= 1f)
            {
                return to;
            }
            // У прозрачного цвета нет своего оттенка: берём оттенок второго, иначе середина потемнеет.
            if (from.A == 0)
            {
                from = Color.FromArgb(0, to);
            }
            if (to.A == 0)
            {
                to = Color.FromArgb(0, from);
            }
            return Color.FromArgb(Mix(from.A, to.A, amount), Mix(from.R, to.R, amount),
                Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));
        }

        private static int Mix(int from, int to, float amount)
        {
            return (int)Math.Round(from + (to - from) * amount);
        }
    }
}
