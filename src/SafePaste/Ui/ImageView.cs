using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SafePaste.Ui
{
    internal enum ImageMarkKind
    {
        Hidden,
        Kept,
        Secret,
        /// <summary>Текст не прочитан и остаётся открытым.</summary>
        Unread,
        /// <summary>Текст не прочитан, место закрывается.</summary>
        UnreadHidden,
        /// <summary>Закрыто рамкой вручную.</summary>
        Manual,
        /// <summary>Метка на готовой картинке.</summary>
        Placeholder,
        /// <summary>Совпадение с поиском.</summary>
        Match
    }

    /// <summary>Рамка на картинке; прямоугольник в пикселях картинки.</summary>
    internal sealed class ImageMark
    {
        internal readonly RectangleF Bounds;
        internal readonly ImageMarkKind Kind;
        internal readonly object Tag;

        internal ImageMark(RectangleF bounds, ImageMarkKind kind, object tag)
        {
            Bounds = bounds;
            Kind = kind;
            Tag = tag;
        }
    }

    internal delegate void AreaDrawnHandler(RectangleF area);

    /// <summary>
    /// Картинка с рамками находок. По умолчанию вписана в блок целиком; Ctrl и колесо мыши меняют масштаб,
    /// колесо двигает картинку по вертикали, Shift и колесо по горизонтали, средняя кнопка тащит,
    /// двойной щелчок возвращает вписанный вид. Рамка, нарисованная левой кнопкой, закрывает место (AreaDrawn).
    /// Картинку элемент не владеет и не освобождает.
    /// </summary>
    internal sealed class ImageView : GlassControl
    {
        private static readonly Color UnreadColor = Color.FromArgb(196, 168, 255);

        private Bitmap image;
        private Bitmap cache;
        private float cacheScale;
        private List<ImageMark> marks = new List<ImageMark>();
        // 0: вписать; иначе масштаб, выбранный колесом мыши.
        private float zoom;
        // Верхний левый угол картинки в координатах элемента при заданном масштабе.
        private PointF origin;
        private ImageMark hovered;
        private bool busy;
        private string busyText = string.Empty;
        private string placeholder = string.Empty;
        private bool pressing;
        private bool drawing;
        private Point pressPoint;
        private Rectangle dragRect;
        private bool panning;
        private Point panPoint;

        internal event EventHandler HoverChanged;
        internal event MouseEventHandler ContextRequested;
        internal event AreaDrawnHandler AreaDrawn;

        /// <summary>Можно рисовать рамку мышью.</summary>
        internal bool AllowDrawing;

        internal ImageView()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
            TabStop = true;
            Font = Theme.UiFont;
            AccessibleRole = AccessibleRole.Graphic;
        }

        internal Bitmap Image
        {
            get { return image; }
        }

        internal List<ImageMark> Marks
        {
            get { return marks; }
        }

        internal string Placeholder
        {
            get { return placeholder; }
            set
            {
                placeholder = value ?? string.Empty;
                Invalidate();
            }
        }

        /// <summary>Картинка ещё читается: поверх неё полупрозрачная пелена и подпись.</summary>
        internal bool Busy
        {
            get { return busy; }
            set
            {
                busy = value;
                Invalidate();
            }
        }

        internal string BusyText
        {
            get { return busyText; }
            set
            {
                busyText = value ?? string.Empty;
                Invalidate();
            }
        }

        /// <summary>Масштаб показа: сколько точек экрана на пиксель картинки.</summary>
        internal float DisplayScale
        {
            get { return CurrentScale(); }
        }

        internal bool Fitted
        {
            get { return zoom <= 0; }
        }

        internal ImageMark HoveredMark
        {
            get { return hovered; }
        }

        /// <summary>Рамка под мышью в координатах элемента.</summary>
        internal Rectangle HoverBounds
        {
            get { return hovered == null ? Rectangle.Empty : Rectangle.Round(ToClient(hovered.Bounds)); }
        }

        internal void SetImage(Bitmap value, List<ImageMark> newMarks, bool keepView)
        {
            if (!ReferenceEquals(value, image))
            {
                DropCache();
                if (!keepView || image == null || value == null || value.Size != image.Size)
                {
                    zoom = 0;
                }
            }
            image = value;
            SetMarks(newMarks);
        }

        internal void SetMarks(List<ImageMark> newMarks)
        {
            marks = newMarks ?? new List<ImageMark>();
            ImageMark before = hovered;
            hovered = null;
            Point mouse = PointToClient(Cursor.Position);
            if (ClientRectangle.Contains(mouse) && before != null)
            {
                hovered = MarkAt(mouse);
            }
            Invalidate();
            if ((before != null || hovered != null) && HoverChanged != null)
            {
                HoverChanged(this, EventArgs.Empty);
            }
        }

        // ---------------------------------------------------------------- координаты

        private float FitScale()
        {
            if (image == null || image.Width == 0 || image.Height == 0)
            {
                return 1f;
            }
            float margin = Dpi.F(10);
            float width = Math.Max(1f, ClientSize.Width - 2 * margin);
            float height = Math.Max(1f, ClientSize.Height - 2 * margin);
            // Маленькую картинку не растягиваем сильнее, чем в 2 раза: иначе она расплывается.
            return Math.Min(Math.Min(width / image.Width, height / image.Height), 2f * Dpi.Factor);
        }

        private float CurrentScale()
        {
            return zoom > 0 ? zoom : FitScale();
        }

        private PointF Origin()
        {
            if (image == null)
            {
                return PointF.Empty;
            }
            float scale = CurrentScale();
            float width = image.Width * scale;
            float height = image.Height * scale;
            if (zoom <= 0)
            {
                return new PointF((ClientSize.Width - width) / 2f, (ClientSize.Height - height) / 2f);
            }
            return new PointF(ClampAxis(origin.X, width, ClientSize.Width), ClampAxis(origin.Y, height, ClientSize.Height));
        }

        /// <summary>Картинка меньше блока стоит посередине, больше блока не уходит краем внутрь.</summary>
        private static float ClampAxis(float value, float content, float view)
        {
            if (content <= view)
            {
                return (view - content) / 2f;
            }
            return Math.Max(view - content, Math.Min(0f, value));
        }

        internal RectangleF ToClient(RectangleF area)
        {
            float scale = CurrentScale();
            PointF start = Origin();
            return new RectangleF(start.X + area.X * scale, start.Y + area.Y * scale, area.Width * scale, area.Height * scale);
        }

        internal PointF ToImage(Point point)
        {
            float scale = CurrentScale();
            PointF start = Origin();
            return new PointF((point.X - start.X) / scale, (point.Y - start.Y) / scale);
        }

        private RectangleF ImageBounds()
        {
            return image == null ? RectangleF.Empty : ToClient(new RectangleF(0, 0, image.Width, image.Height));
        }

        /// <summary>Самая маленькая рамка под точкой: вложенная рамка важнее той, что её накрывает.</summary>
        internal ImageMark MarkAt(Point point)
        {
            ImageMark best = null;
            float bestArea = float.MaxValue;
            foreach (ImageMark mark in marks)
            {
                RectangleF area = RectangleF.Inflate(ToClient(mark.Bounds), Dpi.F(2), Dpi.F(2));
                if (!area.Contains(point))
                {
                    continue;
                }
                float size = mark.Bounds.Width * mark.Bounds.Height;
                if (size < bestArea)
                {
                    best = mark;
                    bestArea = size;
                }
            }
            return best;
        }

        /// <summary>Показывает место картинки: при увеличении прокручивает к нему.</summary>
        internal void Reveal(RectangleF area)
        {
            if (image == null || zoom <= 0)
            {
                return;
            }
            float scale = CurrentScale();
            origin = new PointF(ClientSize.Width / 2f - (area.X + area.Width / 2f) * scale,
                ClientSize.Height / 2f - (area.Y + area.Height / 2f) * scale);
            Invalidate();
        }

        // ---------------------------------------------------------------- масштаб

        internal void ZoomAt(Point anchor, float factor)
        {
            if (image == null)
            {
                return;
            }
            float before = CurrentScale();
            float after = Math.Max(Math.Min(FitScale(), 0.1f), Math.Min(8f * Dpi.Factor, before * factor));
            PointF start = Origin();
            float imageX = (anchor.X - start.X) / before;
            float imageY = (anchor.Y - start.Y) / before;
            zoom = after;
            origin = new PointF(anchor.X - imageX * after, anchor.Y - imageY * after);
            DropCache();
            Invalidate();
            RefreshHover(anchor);
        }

        internal void ResetZoom()
        {
            zoom = 0;
            DropCache();
            Invalidate();
        }

        private void DropCache()
        {
            if (cache != null)
            {
                cache.Dispose();
                cache = null;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (zoom <= 0)
            {
                DropCache();
            }
        }

        // ---------------------------------------------------------------- отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            if (image == null)
            {
                Theme.DrawText(graphics, placeholder, Theme.UiFont, Theme.Tertiary,
                    new RectangleF(Dpi.F(14), 0, Math.Max(0, Width - Dpi.F(28)), Height), StringAlignment.Center, StringAlignment.Center, true);
                return;
            }
            RectangleF bounds = ImageBounds();
            PaintImage(graphics, bounds);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (ImageMark mark in marks)
            {
                if (mark != hovered)
                {
                    PaintMark(graphics, mark, false);
                }
            }
            if (hovered != null)
            {
                PaintMark(graphics, hovered, true);
            }
            if (drawing)
            {
                using (Pen pen = new Pen(Theme.Accent, Dpi.F(1.5f)))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, Theme.Accent)))
                {
                    pen.DashStyle = DashStyle.Dash;
                    graphics.FillRectangle(fill, dragRect);
                    graphics.DrawRectangle(pen, dragRect);
                }
            }
            PaintScrollHints(graphics, bounds);
            if (busy)
            {
                using (SolidBrush veil = new SolidBrush(Color.FromArgb(150, 12, 14, 18)))
                {
                    graphics.FillRectangle(veil, RectangleF.Intersect(bounds, ClientRectangle));
                }
                SizeF size = Theme.Measure(busyText, Theme.StrongFont, 0);
                RectangleF pill = new RectangleF((Width - size.Width) / 2f - Dpi.F(16), (Height - size.Height) / 2f - Dpi.F(9),
                    size.Width + Dpi.F(32), size.Height + Dpi.F(18));
                Theme.FillRound(graphics, pill, pill.Height / 2f, Color.FromArgb(220, 34, 36, 43));
                Theme.DrawText(graphics, busyText, Theme.StrongFont, Theme.Text, pill, StringAlignment.Center, StringAlignment.Center, false);
            }
            if (Focused && ShowFocusCues)
            {
                using (Pen pen = new Pen(Color.FromArgb(120, Theme.Accent), Dpi.F(1)))
                {
                    graphics.DrawRectangle(pen, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                }
            }
        }

        /// <summary>
        /// Уменьшенная картинка рисуется один раз и хранится: иначе наведение на рамки перерисовывало бы
        /// большой снимок с дорогой интерполяцией на каждое движение мыши.
        /// </summary>
        private void PaintImage(Graphics graphics, RectangleF bounds)
        {
            float scale = CurrentScale();
            if (scale < 0.999f)
            {
                int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                if (cache == null || Math.Abs(cacheScale - scale) > 0.0001f || cache.Width != width || cache.Height != height)
                {
                    DropCache();
                    cache = new Bitmap(width, height);
                    using (Graphics target = Graphics.FromImage(cache))
                    {
                        target.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        target.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        target.DrawImage(image, new Rectangle(0, 0, width, height));
                    }
                    cacheScale = scale;
                }
                graphics.DrawImage(cache, (float)Math.Round(bounds.X), (float)Math.Round(bounds.Y), width, height);
                return;
            }
            InterpolationMode previous = graphics.InterpolationMode;
            PixelOffsetMode offset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = scale >= 1.999f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(image, bounds);
            graphics.InterpolationMode = previous;
            graphics.PixelOffsetMode = offset;
        }

        private void PaintMark(Graphics graphics, ImageMark mark, bool hover)
        {
            RectangleF area = RectangleF.Inflate(ToClient(mark.Bounds), Dpi.F(1), Dpi.F(1));
            if (area.Width < 1 || area.Height < 1)
            {
                return;
            }
            Color line;
            Color fill;
            bool dashed = false;
            switch (mark.Kind)
            {
                case ImageMarkKind.Secret:
                    line = Theme.Danger;
                    fill = Color.FromArgb(hover ? 110 : 70, Theme.DangerFill);
                    break;
                case ImageMarkKind.Kept:
                    line = Theme.Secondary;
                    fill = Color.FromArgb(hover ? 40 : 0, 255, 255, 255);
                    dashed = true;
                    break;
                case ImageMarkKind.Unread:
                    line = UnreadColor;
                    fill = Color.FromArgb(hover ? 50 : 0, UnreadColor);
                    dashed = true;
                    break;
                case ImageMarkKind.UnreadHidden:
                    line = UnreadColor;
                    fill = Color.FromArgb(hover ? 110 : 70, UnreadColor);
                    break;
                case ImageMarkKind.Manual:
                    line = Theme.Warm;
                    fill = Color.FromArgb(hover ? 110 : 70, Theme.Warm);
                    dashed = true;
                    break;
                case ImageMarkKind.Placeholder:
                    if (!hover)
                    {
                        return;
                    }
                    line = Theme.Accent;
                    fill = Color.FromArgb(40, Theme.Accent);
                    break;
                case ImageMarkKind.Match:
                    line = Theme.MatchCurrent;
                    fill = Theme.MatchFill;
                    break;
                default:
                    line = Theme.Warm;
                    fill = Color.FromArgb(hover ? 100 : 56, Theme.Warm);
                    break;
            }
            float radius = Math.Min(Dpi.F(4), Math.Min(area.Width, area.Height) / 2f);
            Theme.FillRound(graphics, area, radius, fill);
            using (GraphicsPath path = Theme.Round(area, radius))
            using (Pen pen = new Pen(Color.FromArgb(hover ? 255 : 220, line), hover ? Dpi.F(2) : Dpi.F(1.4f)))
            {
                if (dashed)
                {
                    pen.DashStyle = DashStyle.Dash;
                }
                graphics.DrawPath(pen, path);
            }
        }

        /// <summary>При увеличении тонкие полоски у края показывают, какая часть картинки видна.</summary>
        private void PaintScrollHints(Graphics graphics, RectangleF bounds)
        {
            float thickness = Dpi.F(4);
            if (bounds.Height > Height + 1)
            {
                float length = Math.Max(Dpi.F(24), Height * Height / bounds.Height);
                float top = (Height - length) * (-bounds.Y) / (bounds.Height - Height);
                Theme.FillRound(graphics, new RectangleF(Width - thickness - Dpi.F(3), top, thickness, length), thickness / 2f,
                    Color.FromArgb(90, 255, 255, 255));
            }
            if (bounds.Width > Width + 1)
            {
                float length = Math.Max(Dpi.F(24), Width * Width / bounds.Width);
                float left = (Width - length) * (-bounds.X) / (bounds.Width - Width);
                Theme.FillRound(graphics, new RectangleF(left, Height - thickness - Dpi.F(3), length, thickness), thickness / 2f,
                    Color.FromArgb(90, 255, 255, 255));
            }
        }

        // ---------------------------------------------------------------- мышь

        private void RefreshHover(Point point)
        {
            ImageMark next = drawing || panning ? null : MarkAt(point);
            if (next == hovered)
            {
                return;
            }
            hovered = next;
            Invalidate();
            if (HoverChanged != null)
            {
                HoverChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (panning)
            {
                PointF start = Origin();
                origin = new PointF(start.X + e.X - panPoint.X, start.Y + e.Y - panPoint.Y);
                panPoint = e.Location;
                Invalidate();
                return;
            }
            if (pressing && AllowDrawing && image != null)
            {
                if (!drawing && (Math.Abs(e.X - pressPoint.X) > Dpi.S(4) || Math.Abs(e.Y - pressPoint.Y) > Dpi.S(4)))
                {
                    drawing = true;
                    GlassTip.HideFor(this);
                    RefreshHover(e.Location);
                }
                if (drawing)
                {
                    RectangleF limit = RectangleF.Intersect(ImageBounds(), ClientRectangle);
                    int x = (int)Math.Max(limit.Left, Math.Min(limit.Right, e.X));
                    int y = (int)Math.Max(limit.Top, Math.Min(limit.Bottom, e.Y));
                    dragRect = Rectangle.FromLTRB(Math.Min(pressPoint.X, x), Math.Min(pressPoint.Y, y),
                        Math.Max(pressPoint.X, x), Math.Max(pressPoint.Y, y));
                    Invalidate();
                    return;
                }
            }
            Cursor = AllowDrawing && image != null && !busy && ImageBounds().Contains(e.Location) ? Cursors.Cross : Cursors.Default;
            RefreshHover(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered != null)
            {
                hovered = null;
                Invalidate();
                if (HoverChanged != null)
                {
                    HoverChanged(this, EventArgs.Empty);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            GlassTip.HideFor(this);
            if (!Focused)
            {
                Focus();
            }
            if (e.Button == MouseButtons.Middle && image != null)
            {
                if (zoom <= 0)
                {
                    zoom = CurrentScale();
                    origin = Origin();
                }
                panning = true;
                panPoint = e.Location;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button == MouseButtons.Left && image != null && !busy && ImageBounds().Contains(e.Location))
            {
                pressing = true;
                pressPoint = e.Location;
                dragRect = new Rectangle(e.Location, Size.Empty);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (panning && e.Button == MouseButtons.Middle)
            {
                panning = false;
                Cursor = Cursors.Default;
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                if (ContextRequested != null)
                {
                    ContextRequested(this, e);
                }
                return;
            }
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            bool finished = drawing;
            Rectangle drawn = dragRect;
            pressing = false;
            drawing = false;
            Invalidate();
            if (!finished)
            {
                return;
            }
            float scale = CurrentScale();
            PointF start = Origin();
            RectangleF area = new RectangleF((drawn.X - start.X) / scale, (drawn.Y - start.Y) / scale, drawn.Width / scale, drawn.Height / scale);
            area.Intersect(new RectangleF(0, 0, image.Width, image.Height));
            if (area.Width >= 3 && area.Height >= 3 && AreaDrawn != null)
            {
                AreaDrawn(area);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                ResetZoom();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (image == null)
            {
                return;
            }
            GlassTip.HideFor(this);
            if ((ModifierKeys & Keys.Control) != 0)
            {
                ZoomAt(e.Location, e.Delta > 0 ? 1.25f : 0.8f);
                return;
            }
            if (zoom <= 0)
            {
                return;
            }
            PointF start = Origin();
            float step = e.Delta / 120f * Dpi.F(60);
            origin = (ModifierKeys & Keys.Shift) != 0 ? new PointF(start.X + step, start.Y) : new PointF(start.X, start.Y + step);
            Invalidate();
            RefreshHover(e.Location);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DropCache();
            }
            base.Dispose(disposing);
        }
    }
}
