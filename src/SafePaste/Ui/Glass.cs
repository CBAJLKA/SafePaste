using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SafePaste.Interop;

namespace SafePaste.Ui
{
    /// <summary>
    /// Окно без системной рамки: весь фон стеклянный, окно перетаскивается за любой пустой участок,
    /// размер меняется за края.
    /// </summary>
    internal class GlassForm : Form
    {
        private bool glass;
        private bool resizable = true;
        private bool zoomed;
        private Rectangle normalBounds;

        internal GlassForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.WindowSolid;
            ForeColor = Theme.Text;
            Font = Theme.UiFont;
            Icon = AppIcon.Value;
        }

        internal bool IsGlass
        {
            get { return glass; }
        }

        internal bool Resizable
        {
            get { return resizable; }
            set { resizable = value; }
        }

        internal bool Zoomed
        {
            get { return zoomed; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                // Без этих стилей окно без рамки не сворачивается с панели задач и не закрывается по Alt+F4.
                parameters.Style |= Native.WS_MINIMIZEBOX | Native.WS_SYSMENU;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glass = Native.EnableGlass(Handle, !Theme.ForceSolid);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme.PaintWindow(e.Graphics, ClientRectangle, glass);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.WM_NCHITTEST)
            {
                base.WndProc(ref message);
                if (message.Result.ToInt64() == Native.HTCLIENT)
                {
                    message.Result = new IntPtr(HitTest(PointToClient(Native.PointFromLParam(message.LParam))));
                }
                return;
            }
            if (message.Msg == Native.WM_NCLBUTTONDBLCLK && message.WParam.ToInt64() == Native.HTCAPTION)
            {
                ToggleZoom();
                return;
            }
            base.WndProc(ref message);
        }

        private int HitTest(Point point)
        {
            if (resizable && !zoomed)
            {
                int edge = Dpi.S(7);
                bool left = point.X < edge;
                bool right = point.X >= ClientSize.Width - edge;
                bool top = point.Y < edge;
                bool bottom = point.Y >= ClientSize.Height - edge;
                if (top && left) return Native.HTTOPLEFT;
                if (top && right) return Native.HTTOPRIGHT;
                if (bottom && left) return Native.HTBOTTOMLEFT;
                if (bottom && right) return Native.HTBOTTOMRIGHT;
                if (left) return Native.HTLEFT;
                if (right) return Native.HTRIGHT;
                if (top) return Native.HTTOP;
                if (bottom) return Native.HTBOTTOM;
            }
            // Пустой фон работает как заголовок: за него окно можно перетащить.
            return Native.HTCAPTION;
        }

        /// <summary>Развернуть на всю рабочую область и обратно (двойной щелчок по фону).</summary>
        internal void ToggleZoom()
        {
            if (!resizable)
            {
                return;
            }
            if (zoomed)
            {
                zoomed = false;
                Bounds = normalBounds;
                return;
            }
            normalBounds = Bounds;
            zoomed = true;
            Bounds = Screen.FromControl(this).WorkingArea;
        }

        /// <summary>Держит окно в пределах рабочей области экрана.</summary>
        internal void MoveWithinScreen(Rectangle target)
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (target.Width > area.Width) target.Width = area.Width;
            if (target.Height > area.Height) target.Height = area.Height;
            if (target.Right > area.Right) target.X = area.Right - target.Width;
            if (target.Bottom > area.Bottom) target.Y = area.Bottom - target.Height;
            if (target.X < area.Left) target.X = area.Left;
            if (target.Y < area.Top) target.Y = area.Top;
            Bounds = target;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            GlassTip.HideAll();
            base.OnDeactivate(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            GlassTip.HideAll();
            base.OnFormClosing(e);
        }
    }

    /// <summary>
    /// Элемент со своей отрисовкой. Фон он берёт у родителя вместе с прозрачностью,
    /// поэтому на стекле не остаётся непрозрачных прямоугольников.
    /// </summary>
    internal class GlassControl : Control
    {
        private bool mouseTransparent;

        internal GlassControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            Font = Theme.UiFont;
            TabStop = false;
        }

        /// <summary>Мышь проходит насквозь к родителю: так окно тянется и за подписи.</summary>
        internal bool MouseTransparent
        {
            get { return mouseTransparent; }
            set { mouseTransparent = value; }
        }

        protected override void WndProc(ref Message message)
        {
            if (mouseTransparent && message.Msg == Native.WM_NCHITTEST)
            {
                message.Result = new IntPtr(Native.HTTRANSPARENT);
                return;
            }
            base.WndProc(ref message);
        }
    }

    /// <summary>Карточка-блок: заголовок сверху и содержимое под ним.</summary>
    internal sealed class GlassCard : GlassControl
    {
        private readonly Control header;
        private readonly Control body;

        internal GlassCard(Control header, Control body)
        {
            this.header = header;
            this.body = body;
            MouseTransparent = false;
            if (header != null)
            {
                Controls.Add(header);
                header.TabIndex = 1;
            }
            Controls.Add(body);
            body.TabIndex = 0;
        }

        internal static int HeaderHeight
        {
            get { return Dpi.S(46); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.PaintCard(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Dpi.F(14));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (body == null)
            {
                return; // вызов из конструктора базового класса
            }
            int top = 0;
            if (header != null)
            {
                top = HeaderHeight;
                header.SetBounds(Dpi.S(14), Dpi.S(3), Math.Max(0, Width - Dpi.S(22)), top - Dpi.S(3));
            }
            int inset = Dpi.S(3);
            body.SetBounds(inset, top, Math.Max(0, Width - 2 * inset), Math.Max(0, Height - top - inset));
        }
    }

    /// <summary>Шапка окна: значок и название. Мышь уходит окну, чтобы его можно было тянуть.</summary>
    internal sealed class WindowHeader : GlassControl
    {
        private readonly Bitmap logo = IconArtwork.Render(64);
        private string title;
        private string subtitle;

        internal WindowHeader(string title, string subtitle)
        {
            this.title = title;
            this.subtitle = subtitle;
            MouseTransparent = true;
        }

        internal string Title
        {
            get { return title; }
            set { title = value; Invalidate(); }
        }

        internal string Subtitle
        {
            get { return subtitle; }
            set { subtitle = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int size = Dpi.S(22);
            graphics.DrawImage(logo, new Rectangle(0, (Height - size) / 2, size, size));
            float left = size + Dpi.F(10);
            RectangleF area = new RectangleF(left, 0, Width - left, Height);
            if (string.IsNullOrEmpty(subtitle))
            {
                Theme.DrawText(graphics, title, Theme.StrongFont, Theme.Text, area);
                return;
            }
            float middle = Height / 2f;
            Theme.DrawText(graphics, title, Theme.StrongFont, Theme.Text,
                new RectangleF(left, 0, area.Width, middle + Dpi.F(1)), StringAlignment.Near, StringAlignment.Far, false);
            Theme.DrawText(graphics, subtitle, Theme.SmallFont, Theme.Tertiary,
                new RectangleF(left, middle + Dpi.F(1), area.Width, middle), StringAlignment.Near, StringAlignment.Near, false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                logo.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class GlassLabel : GlassControl
    {
        private StringAlignment alignment = StringAlignment.Near;
        private bool wrap;

        internal GlassLabel(string text, Font font, Color color)
        {
            Text = text;
            Font = font;
            ForeColor = color;
            MouseTransparent = true;
        }

        internal StringAlignment Alignment
        {
            get { return alignment; }
            set { alignment = value; Invalidate(); }
        }

        internal bool Wrap
        {
            get { return wrap; }
            set { wrap = value; Invalidate(); }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.DrawText(e.Graphics, Text, Font, ForeColor, new RectangleF(0, 0, Width, Height), alignment,
                wrap ? StringAlignment.Near : StringAlignment.Center, wrap);
        }
    }

    internal enum ButtonKind
    {
        Glass,
        Primary,
        Plain,
        Danger,
        Close
    }

    /// <summary>
    /// Кнопка-капсула или круглая кнопка со значком. Подпись для значка показывается при наведении.
    /// Подсветка появляется и гаснет плавно, при нажатии кнопка немного утапливается, значок может
    /// поворачиваться при наведении (шестерёнка, крестик).
    /// </summary>
    internal sealed class GlassButton : GlassControl, IButtonControl
    {
        private const int HoverInMs = 140;
        private const int HoverOutMs = 240;
        private const int TurnMs = 480;

        private string glyph;
        private string caption;
        private ButtonKind kind;
        private string tip;
        private bool pressed;
        private DialogResult dialogResult;
        private Font glyphFont = Theme.IconFont;
        private float hoverTurn;
        private readonly Tween hover;
        private readonly Tween press;
        private readonly Tween turn;

        internal GlassButton(string glyph, string caption, ButtonKind kind, string tip)
        {
            this.glyph = glyph;
            this.caption = caption;
            this.kind = kind;
            this.tip = tip;
            hover = new Tween(this, 0f);
            press = new Tween(this, 0f);
            turn = new Tween(this, 0f);
            hoverTurn = DefaultTurn(glyph);
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            UpdateAccessibleName();
        }

        /// <summary>Шрифт значка круглой кнопки: у крупных кнопок значок крупнее.</summary>
        internal Font GlyphFont
        {
            get { return glyphFont; }
            set { glyphFont = value ?? Theme.IconFont; Invalidate(); }
        }

        /// <summary>
        /// На сколько градусов значок поворачивается при наведении. Годится для симметричных значков:
        /// в конце поворота они выглядят как в начале.
        /// </summary>
        internal float HoverTurn
        {
            get { return hoverTurn; }
            set { hoverTurn = value; Invalidate(); }
        }

        /// <summary>
        /// Поворот по значку, а не по окну: одинаковые кнопки везде двигаются одинаково. Крестик и шестерёнка
        /// поворачиваются на четверть оборота, стрелки «взять из буфера» на пол-оборота.
        /// </summary>
        internal static float DefaultTurn(string glyph)
        {
            switch (glyph)
            {
                case Glyphs.Close:
                case Glyphs.Clear:
                case Glyphs.Settings:
                    return 90f;
                case Glyphs.Sync:
                    return 180f;
                default:
                    return 0f;
            }
        }

        private void SetHovered(bool value)
        {
            hover.To(value ? 1f : 0f, value ? HoverInMs : HoverOutMs);
            if (hoverTurn != 0f)
            {
                turn.To(value ? hoverTurn : 0f, TurnMs);
            }
        }

        private void SetPressed(bool value)
        {
            pressed = value;
            press.To(value ? 1f : 0f, value ? 70 : 180);
        }

        internal string Glyph
        {
            get { return glyph; }
            set { glyph = value; Invalidate(); }
        }

        internal string Caption
        {
            get { return caption; }
            set { caption = value; UpdateAccessibleName(); Invalidate(); }
        }

        internal ButtonKind Kind
        {
            get { return kind; }
            set { kind = value; Invalidate(); }
        }

        /// <summary>Подпись при наведении: в первой строке действие, во второй сочетание клавиш.</summary>
        internal string Tip
        {
            get { return tip; }
            set { tip = value; UpdateAccessibleName(); }
        }

        public DialogResult DialogResult
        {
            get { return dialogResult; }
            set { dialogResult = value; }
        }

        public void NotifyDefault(bool value)
        {
        }

        public void PerformClick()
        {
            if (Enabled && Visible)
            {
                OnClick(EventArgs.Empty);
            }
        }

        /// <summary>Ширина капсулы с подписью; круглая кнопка квадратна.</summary>
        internal int PreferredWidth(int height)
        {
            if (string.IsNullOrEmpty(caption))
            {
                return height;
            }
            float width = Theme.Measure(caption, Theme.StrongFont, 0).Width + Dpi.F(36);
            if (!string.IsNullOrEmpty(glyph))
            {
                width += Dpi.F(24);
            }
            return (int)Math.Ceiling(width);
        }

        private void UpdateAccessibleName()
        {
            string name = caption;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(tip))
            {
                int newline = tip.IndexOf('\n');
                name = newline < 0 ? tip : tip.Substring(0, newline);
            }
            AccessibleName = name;
            Text = name ?? string.Empty;
        }

        protected override void OnClick(EventArgs e)
        {
            GlassTip.HideFor(this);
            base.OnClick(e);
            if (dialogResult != DialogResult.None)
            {
                Form form = FindForm();
                if (form != null)
                {
                    form.DialogResult = dialogResult;
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            SetHovered(true);
            if (!string.IsNullOrEmpty(tip))
            {
                GlassTip.ShowText(this, RectangleToScreen(ClientRectangle), tip, 450);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHovered(false);
            SetPressed(false);
            GlassTip.HideFor(this);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            GlassTip.HideFor(this);
            if (e.Button == MouseButtons.Left)
            {
                SetPressed(true);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            SetPressed(false);
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                SetPressed(true);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                PerformClick();
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && pressed)
            {
                SetPressed(false);
                e.Handled = true;
                PerformClick();
            }
            base.OnKeyUp(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            SetPressed(false);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible)
            {
                // Спрятанная под курсором кнопка не получит MouseLeave: иначе она вернётся подсвеченной.
                pressed = false;
                hover.Snap(0f);
                press.Snap(0f);
                turn.Snap(0f);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Math.Min(bounds.Width, bounds.Height) / 2f;
            Color rest;
            Color over;
            Color down;
            Color ink;
            Color inkOver;
            bool edge = false;
            switch (kind)
            {
                case ButtonKind.Primary:
                    rest = Theme.Accent;
                    over = Theme.AccentHover;
                    down = Theme.AccentPressed;
                    ink = inkOver = Theme.AccentInk;
                    break;
                case ButtonKind.Danger:
                    rest = Theme.DangerFill;
                    over = Color.FromArgb(250, 90, 80);
                    down = Color.FromArgb(205, 58, 52);
                    ink = inkOver = Color.White;
                    break;
                case ButtonKind.Plain:
                    rest = Color.Transparent;
                    over = Theme.Fill;
                    down = Theme.FillPressed;
                    ink = Theme.Secondary;
                    inkOver = Theme.Text;
                    break;
                case ButtonKind.Close:
                    rest = Color.Transparent;
                    over = Theme.CloseHover;
                    down = Color.FromArgb(215, 70, 64);
                    ink = Theme.Secondary;
                    inkOver = Color.FromArgb(70, 8, 6);
                    break;
                default:
                    rest = Theme.Fill;
                    over = Theme.FillHover;
                    down = Theme.FillPressed;
                    ink = inkOver = Theme.Text;
                    edge = true;
                    break;
            }
            float lift = hover.Value;
            float push = press.Value;
            Color fill = Theme.Blend(Theme.Blend(rest, over, lift), down, push);
            ink = Theme.Blend(ink, inkOver, lift);
            if (!Enabled)
            {
                fill = Theme.Fade(rest, 0.45f);
                ink = Theme.Tertiary;
            }
            // Нажатая кнопка чуть утапливается: вся капсула сжимается к центру.
            GraphicsState state = null;
            if (push > 0f)
            {
                float scale = 1f - 0.06f * push;
                state = graphics.Save();
                graphics.TranslateTransform(Width / 2f, Height / 2f);
                graphics.ScaleTransform(scale, scale);
                graphics.TranslateTransform(-Width / 2f, -Height / 2f);
            }
            using (GraphicsPath path = Theme.Round(bounds, radius))
            {
                if (fill.A > 0)
                {
                    using (SolidBrush brush = new SolidBrush(fill))
                    {
                        graphics.FillPath(brush, path);
                    }
                }
                if (kind == ButtonKind.Primary && Enabled)
                {
                    // Блик по верхнему краю, как у стеклянной капсулы.
                    RectangleF upper = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2f + 1f);
                    using (LinearGradientBrush shine = new LinearGradientBrush(upper,
                        Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                    {
                        graphics.FillPath(shine, path);
                    }
                }
                if (edge)
                {
                    Theme.StrokeEdge(graphics, path, bounds, Theme.Blend(Theme.EdgeTop, Color.FromArgb(70, 255, 255, 255), lift),
                        Theme.EdgeBottom);
                }
            }
            if (Focused && ShowFocusCues)
            {
                using (GraphicsPath ring = Theme.Round(RectangleF.Inflate(bounds, -1.5f, -1.5f), radius - 1.5f))
                using (Pen pen = new Pen(kind == ButtonKind.Primary ? Theme.AccentInk : Theme.Accent, Dpi.F(1.5f)))
                {
                    graphics.DrawPath(pen, ring);
                }
            }
            DrawContent(graphics, bounds, ink, lift);
            if (state != null)
            {
                graphics.Restore(state);
            }
        }

        private void DrawContent(Graphics graphics, RectangleF bounds, Color ink, float lift)
        {
            bool hasGlyph = !string.IsNullOrEmpty(glyph);
            if (string.IsNullOrEmpty(caption))
            {
                if (hasGlyph)
                {
                    // Значок чуть подрастает при наведении и поворачивается, если так задано.
                    PointF center = new PointF(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
                    Theme.FillGlyph(graphics, glyph, glyphFont, ink, center, turn.Value, 1f + 0.08f * lift);
                }
                return;
            }
            float textWidth = Theme.Measure(caption, Theme.StrongFont, 0).Width;
            float glyphWidth = hasGlyph ? Dpi.F(24) : 0f;
            float left = bounds.X + (bounds.Width - textWidth - glyphWidth) / 2f;
            if (hasGlyph)
            {
                Theme.DrawGlyph(graphics, glyph, Theme.IconFont, ink, new RectangleF(left, bounds.Y, Dpi.F(18), bounds.Height));
            }
            Theme.DrawText(graphics, caption, Theme.StrongFont, ink,
                new RectangleF(left + glyphWidth, bounds.Y, textWidth + Dpi.F(2), bounds.Height));
        }
    }

    /// <summary>
    /// Переключатель вариантов в одну капсулу, как в macOS. Выбранный вариант переезжает плавно,
    /// подсветка под курсором проявляется и гаснет.
    /// </summary>
    internal sealed class Segmented : GlassControl
    {
        private readonly string[] items;
        private int selected;
        private int hover = -1;
        // Вариант, подсветка которого сейчас проявляется или гаснет.
        private int glowIndex = -1;
        private readonly Tween slide;
        private readonly Tween glow;

        /// <summary>Подсказки к вариантам: в первой строке пояснение, во второй сочетание клавиш.</summary>
        internal string[] Tips;

        internal event EventHandler SelectedChanged;

        internal Segmented(string[] items)
        {
            this.items = items;
            slide = new Tween(this, 0f);
            glow = new Tween(this, 0f);
            Font = Theme.SmallFont;
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PageTabList;
        }

        internal int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < 0 || value >= items.Length || value == selected)
                {
                    return;
                }
                selected = value;
                slide.To(value, 220);
                Invalidate();
                AccessibleDescription = items[selected];
                if (SelectedChanged != null)
                {
                    SelectedChanged(this, EventArgs.Empty);
                }
            }
        }

        internal int PreferredWidth
        {
            get
            {
                float total = Dpi.F(6);
                foreach (string item in items)
                {
                    total += Theme.Measure(item, Font, 0).Width + Dpi.F(22);
                }
                return (int)Math.Ceiling(total);
            }
        }

        private RectangleF[] Segments()
        {
            RectangleF[] result = new RectangleF[items.Length];
            float inset = Dpi.F(3);
            float[] widths = new float[items.Length];
            float sum = 0;
            for (int i = 0; i < items.Length; i++)
            {
                widths[i] = Theme.Measure(items[i], Font, 0).Width + Dpi.F(22);
                sum += widths[i];
            }
            float scale = sum <= 0 ? 1f : (Width - 2 * inset) / sum;
            float x = inset;
            for (int i = 0; i < items.Length; i++)
            {
                float width = widths[i] * scale;
                result[i] = new RectangleF(x, inset, width, Height - 2 * inset);
                x += width;
            }
            return result;
        }

        /// <summary>Капсула выбранного варианта: между двумя соседними, пока она переезжает.</summary>
        private RectangleF Thumb(RectangleF[] segments)
        {
            if (segments.Length == 0)
            {
                return RectangleF.Empty;
            }
            float position = Math.Max(0f, Math.Min(segments.Length - 1, slide.Value));
            int left = (int)Math.Floor(position);
            int right = Math.Min(segments.Length - 1, left + 1);
            float amount = position - left;
            RectangleF a = segments[left];
            RectangleF b = segments[right];
            return new RectangleF(a.X + (b.X - a.X) * amount, a.Y, a.Width + (b.Width - a.Width) * amount, a.Height);
        }

        private int IndexAt(Point point)
        {
            RectangleF[] segments = Segments();
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Theme.FillRound(graphics, bounds, bounds.Height / 2f, Theme.FillPressed);
            RectangleF[] segments = Segments();
            if (glowIndex >= 0 && glowIndex < segments.Length && glowIndex != selected && glow.Value > 0f)
            {
                RectangleF lit = segments[glowIndex];
                Theme.FillRound(graphics, lit, lit.Height / 2f, Theme.Fade(Theme.RowHover, glow.Value));
            }
            RectangleF thumb = Thumb(segments);
            using (GraphicsPath path = Theme.Round(thumb, thumb.Height / 2f))
            {
                using (SolidBrush brush = new SolidBrush(Theme.FillHover))
                {
                    graphics.FillPath(brush, path);
                }
                Theme.StrokeEdge(graphics, path, thumb, Theme.EdgeTop, Theme.EdgeBottom);
            }
            for (int i = 0; i < items.Length; i++)
            {
                // Подпись светлеет по мере того, как к ней подъезжает выбранный вариант.
                float near = Math.Max(0f, 1f - Math.Abs(slide.Value - i));
                Theme.DrawText(graphics, items[i], i == selected ? Theme.StrongFont : Font,
                    Theme.Blend(Theme.Secondary, Theme.Text, near), segments[i], StringAlignment.Center, StringAlignment.Center, false);
            }
            if (Focused && ShowFocusCues)
            {
                using (GraphicsPath ring = Theme.Round(RectangleF.Inflate(bounds, -1f, -1f), bounds.Height / 2f - 1f))
                using (Pen pen = new Pen(Theme.Accent, Dpi.F(1.5f)))
                {
                    graphics.DrawPath(pen, ring);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            GlassTip.HideFor(this);
            int index = IndexAt(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0)
            {
                SelectedIndex = index;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = IndexAt(e.Location);
            if (index == hover)
            {
                return;
            }
            hover = index;
            Glow(index);
            if (Tips != null && index >= 0 && index < Tips.Length && !string.IsNullOrEmpty(Tips[index]))
            {
                GlassTip.ShowText(this, RectangleToScreen(Rectangle.Round(Segments()[index])), Tips[index], 450);
            }
            else
            {
                GlassTip.HideFor(this);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Glow(-1);
            GlassTip.HideFor(this);
        }

        /// <summary>Подсветка под курсором: у нового варианта проявляется, у прежнего гаснет.</summary>
        private void Glow(int index)
        {
            if (index >= 0)
            {
                glowIndex = index;
                glow.Snap(0f);
                glow.To(1f, 140);
            }
            else
            {
                glow.To(0f, 200);
            }
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left && selected > 0)
            {
                SelectedIndex = selected - 1;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right && selected < items.Length - 1)
            {
                SelectedIndex = selected + 1;
                e.Handled = true;
            }
            base.OnKeyDown(e);
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
    }

    /// <summary>
    /// Тонкая кнопка во всю высоту или ширину блока: раскрывает соседнюю панель.
    /// </summary>
    internal sealed class Spine : GlassControl
    {
        private readonly bool vertical;
        private readonly string tipOpen;
        private readonly string tipClose;
        private bool expanded;
        private bool pressed;
        private string caption;
        private readonly Tween glow;

        internal Spine(bool vertical, string tipOpen, string tipClose)
        {
            this.vertical = vertical;
            this.tipOpen = tipOpen;
            this.tipClose = tipClose;
            glow = new Tween(this, 0f);
            // Второй щелчок двойного щелчка не должен тут же закрывать только что открытую панель.
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = Theme.SmallFont;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = tipOpen;
        }

        internal bool Expanded
        {
            get { return expanded; }
            set
            {
                expanded = value;
                AccessibleName = expanded ? tipClose : tipOpen;
                Invalidate();
            }
        }

        internal string Caption
        {
            get { return caption; }
            set { caption = value; Invalidate(); }
        }

        internal string CurrentTip
        {
            get { return expanded ? tipClose : tipOpen; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Math.Min(bounds.Width, bounds.Height) / 2f;
            float lit = glow.Value;
            Color fill = pressed ? Theme.FillPressed : Theme.Blend(Theme.Fill, Theme.FillHover, lit);
            using (GraphicsPath path = Theme.Round(bounds, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    graphics.FillPath(brush, path);
                }
                Theme.StrokeEdge(graphics, path, bounds, Theme.Blend(Theme.Line, Theme.EdgeTop, lit), Theme.EdgeBottom);
            }
            Color ink = Theme.Blend(Theme.Secondary, Theme.Text, lit);
            if (Focused && ShowFocusCues)
            {
                ink = Theme.Accent;
            }
            string glyph = vertical
                ? (expanded ? Glyphs.ChevronLeft : Glyphs.ChevronRight)
                : (expanded ? Glyphs.ChevronUp : Glyphs.ChevronDown);
            // При наведении стрелка чуть сдвигается туда, куда откроется или свернётся панель.
            float nudge = Dpi.F(2.5f) * lit * (expanded ? -1f : 1f);
            if (vertical)
            {
                bounds.Offset(nudge, 0f);
            }
            else
            {
                bounds.Offset(0f, nudge);
            }
            if (vertical || string.IsNullOrEmpty(caption))
            {
                Theme.DrawGlyph(graphics, glyph, Theme.SmallIconFont, ink, bounds);
                return;
            }
            float textWidth = Theme.Measure(caption, Font, 0).Width;
            float glyphWidth = Dpi.F(18);
            float left = bounds.X + (bounds.Width - textWidth - glyphWidth) / 2f;
            Theme.DrawGlyph(graphics, glyph, Theme.SmallIconFont, ink, new RectangleF(left, bounds.Y, Dpi.F(12), bounds.Height));
            Theme.DrawText(graphics, caption, Font, ink, new RectangleF(left + glyphWidth, bounds.Y, textWidth + Dpi.F(2), bounds.Height));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            glow.To(1f, 140);
            // У длинной кнопки подсказка появляется у стрелки, а не под нижним краем.
            Rectangle arrow = vertical
                ? new Rectangle(0, Height / 2 - Dpi.S(12), Width, Dpi.S(24))
                : ClientRectangle;
            GlassTip.ShowText(this, RectangleToScreen(arrow), CurrentTip, 450);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pressed = false;
            glow.To(0f, 240);
            GlassTip.HideFor(this);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            GlassTip.HideFor(this);
            if (e.Button == MouseButtons.Left)
            {
                pressed = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
            }
            base.OnKeyDown(e);
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
    }

    /// <summary>Флажок с подписью.</summary>
    internal sealed class GlassCheck : GlassControl
    {
        private bool isChecked;
        private bool hovered;

        internal GlassCheck(string text)
        {
            Text = text;
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
            AccessibleName = text;
        }

        internal bool Checked
        {
            get { return isChecked; }
            set { isChecked = value; Invalidate(); }
        }

        protected override void OnClick(EventArgs e)
        {
            isChecked = !isChecked;
            Invalidate();
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            float size = Dpi.F(18);
            RectangleF box = new RectangleF(0.5f, (Height - size) / 2f, size, size);
            CheckMark.Draw(graphics, box, isChecked ? CheckState.Checked : CheckState.Unchecked, false, hovered);
            if (Focused && ShowFocusCues)
            {
                using (GraphicsPath ring = Theme.Round(RectangleF.Inflate(box, 2f, 2f), Dpi.F(6)))
                using (Pen pen = new Pen(Theme.Accent, Dpi.F(1.5f)))
                {
                    graphics.DrawPath(pen, ring);
                }
            }
            float left = size + Dpi.F(10);
            Theme.DrawText(graphics, Text, Font, Theme.Secondary, new RectangleF(left, 0, Width - left, Height));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
            }
            base.OnKeyDown(e);
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
    }

    /// <summary>Квадратик-галочка: общий для флажков и строк списка находок.</summary>
    internal static class CheckMark
    {
        internal static void Draw(Graphics graphics, RectangleF box, CheckState state, bool locked, bool hovered)
        {
            float radius = Dpi.F(5);
            if (locked)
            {
                Theme.FillRound(graphics, box, radius, Color.FromArgb(40, 255, 255, 255));
                Theme.DrawGlyph(graphics, Glyphs.Lock, Theme.SmallIconFont, Theme.Secondary, box);
                return;
            }
            if (state == CheckState.Unchecked)
            {
                Theme.FillRound(graphics, box, radius, hovered ? Theme.Fill : Theme.FillPressed);
                using (GraphicsPath path = Theme.Round(box, radius))
                using (Pen pen = new Pen(Color.FromArgb(hovered ? 150 : 110, 255, 255, 255), Dpi.F(1.2f)))
                {
                    graphics.DrawPath(pen, path);
                }
                return;
            }
            Theme.FillRound(graphics, box, radius, hovered ? Theme.AccentHover : Theme.Accent);
            if (state == CheckState.Indeterminate)
            {
                float middle = box.Top + box.Height / 2f;
                using (Pen pen = new Pen(Theme.AccentInk, Dpi.F(2f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    graphics.DrawLine(pen, box.Left + box.Width * 0.28f, middle, box.Right - box.Width * 0.28f, middle);
                }
                return;
            }
            Theme.DrawGlyph(graphics, Glyphs.Check, Theme.SmallIconFont, Theme.AccentInk, box);
        }
    }

    /// <summary>Цветные точки с подписями: и сводка, и легенда подсветки.</summary>
    internal sealed class Legend : GlassControl
    {
        private readonly List<KeyValuePair<Color, string>> items = new List<KeyValuePair<Color, string>>();

        internal Legend()
        {
            Font = Theme.SmallFont;
            MouseTransparent = true;
        }

        internal void SetItems(List<KeyValuePair<Color, string>> values)
        {
            items.Clear();
            items.AddRange(values);
            List<string> parts = new List<string>();
            foreach (KeyValuePair<Color, string> item in items)
            {
                parts.Add(item.Value);
            }
            AccessibleName = string.Join(", ", parts.ToArray());
            Invalidate();
        }

        internal string Summary
        {
            get { return AccessibleName ?? string.Empty; }
        }

        internal int PreferredWidth
        {
            get
            {
                float width = 0;
                foreach (KeyValuePair<Color, string> item in items)
                {
                    width += Dpi.F(item.Key.IsEmpty ? 0 : 14) + Theme.Measure(item.Value, Font, 0).Width + Dpi.F(14);
                }
                return (int)Math.Ceiling(width);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            float x = Math.Max(0, Width - PreferredWidth);
            float dot = Dpi.F(8);
            foreach (KeyValuePair<Color, string> item in items)
            {
                if (!item.Key.IsEmpty)
                {
                    using (SolidBrush brush = new SolidBrush(item.Key))
                    {
                        graphics.FillEllipse(brush, x, (Height - dot) / 2f, dot, dot);
                    }
                    x += Dpi.F(14);
                }
                float width = Theme.Measure(item.Value, Font, 0).Width;
                Theme.DrawText(graphics, item.Value, Font, Theme.Secondary, new RectangleF(x, 0, width + Dpi.F(2), Height));
                x += width + Dpi.F(14);
            }
        }
    }
}
