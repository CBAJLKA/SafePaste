using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace SafePaste.Ui
{
    internal static class AppIcon
    {
        internal static readonly Icon Value = Load();

        private static Icon Load()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SafePaste.ico"))
            using (Icon icon = new Icon(stream, new Size(32, 32))) return (Icon)icon.Clone();
        }
    }

    /// <summary>Original vector artwork, shared by the app and the reproducible ICO builder.</summary>
    internal static class IconArtwork
    {
        internal static Bitmap Render(int size)
        {
            Bitmap bitmap = new Bitmap(size, size);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(size / 64f, size / 64f);
                using (GraphicsPath shape = new GraphicsPath())
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(124, 224, 194)))
                {
                    shape.AddArc(5, 4, 18, 18, 180, 90);
                    shape.AddArc(41, 4, 18, 18, 270, 90);
                    shape.AddArc(41, 42, 18, 18, 0, 90);
                    shape.AddArc(5, 42, 18, 18, 90, 90);
                    shape.CloseFigure();
                    graphics.FillPath(fill, shape);
                }
                using (Pen ink = new Pen(Color.FromArgb(17, 28, 28), 4.5f))
                {
                    ink.StartCap = LineCap.Round;
                    ink.EndCap = LineCap.Round;
                    ink.LineJoin = LineJoin.Round;
                    graphics.DrawLines(ink, new PointF[] {
                        new PointF(22, 18), new PointF(17, 18), new PointF(17, 48),
                        new PointF(47, 48), new PointF(47, 18), new PointF(42, 18) });
                    graphics.DrawRectangle(ink, 25, 14, 14, 8);
                    graphics.DrawLines(ink, new PointF[] {
                        new PointF(24, 34), new PointF(30, 40), new PointF(41, 29) });
                }
            }
            return bitmap;
        }
    }
}
