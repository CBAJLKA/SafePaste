using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SafePaste.Interop;

namespace SafePaste.Ui
{
    /// <summary>
    /// Всплывающая подсказка на стекле. Одна на всё приложение: не забирает фокус
    /// и пропускает мышь сквозь себя.
    /// </summary>
    internal sealed class GlassTip : Form
    {
        private static GlassTip instance;
        private static Control owner;
        private static Timer delayTimer;
        private static Content pending;
        private static Control pendingOwner;
        private static Rectangle pendingAnchor;

        private sealed class Content
        {
            internal string Title;
            internal Font TitleFont;
            internal Color TitleColor;
            internal string Body;
            internal string Hint;
            internal bool Centered;
        }

        private Content content;
        private bool glass;
        private RectangleF titleBounds;
        private RectangleF bodyBounds;
        private RectangleF hintBounds;

        private GlassTip()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.PopupSolid;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glass = Native.EnableGlass(Handle, !Theme.ForceSolid);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.WM_NCHITTEST)
            {
                message.Result = new IntPtr(Native.HTTRANSPARENT);
                return;
            }
            if (message.Msg == Native.WM_MOUSEACTIVATE)
            {
                message.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref message);
        }

        /// <summary>Текст показанной подсказки, нужен проверкам.</summary>
        internal static string VisibleText
        {
            get
            {
                if (instance == null || !instance.Visible || instance.content == null)
                {
                    return null;
                }
                Content shown = instance.content;
                return shown.Title + "\n" + shown.Body + "\n" + shown.Hint;
            }
        }

        /// <summary>Подсказка к кнопке: в первой строке действие, во второй сочетание клавиш.</summary>
        internal static void ShowText(Control target, Rectangle anchor, string text, int delay)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            Content tip = new Content();
            int newline = text.IndexOf('\n');
            tip.Title = newline < 0 ? text : text.Substring(0, newline);
            tip.Hint = newline < 0 ? null : text.Substring(newline + 1);
            tip.TitleFont = Theme.UiFont;
            tip.TitleColor = Theme.Text;
            tip.Centered = true;
            Schedule(target, anchor, tip, delay);
        }

        internal static void ShowRich(Control target, Rectangle anchor, string title, Font titleFont, Color titleColor,
            string body, string hint, int delay)
        {
            Content tip = new Content();
            tip.Title = title;
            tip.TitleFont = titleFont;
            tip.TitleColor = titleColor;
            tip.Body = body;
            tip.Hint = hint;
            Schedule(target, anchor, tip, delay);
        }

        internal static void HideFor(Control target)
        {
            if (pendingOwner == target)
            {
                pending = null;
                pendingOwner = null;
                if (delayTimer != null)
                {
                    delayTimer.Stop();
                }
            }
            if (owner == target)
            {
                HideAll();
            }
        }

        internal static void HideAll()
        {
            pending = null;
            pendingOwner = null;
            owner = null;
            if (delayTimer != null)
            {
                delayTimer.Stop();
            }
            if (instance != null && !instance.IsDisposed && instance.Visible)
            {
                instance.Hide();
            }
        }

        private static void Schedule(Control target, Rectangle anchor, Content tip, int delay)
        {
            if (target == null || target.IsDisposed)
            {
                return;
            }
            pending = tip;
            pendingOwner = target;
            pendingAnchor = anchor;
            // Если подсказка уже на экране, следующая появляется сразу: так удобно водить мышью по тексту.
            if (delay <= 0 || (instance != null && !instance.IsDisposed && instance.Visible))
            {
                ShowPending();
                return;
            }
            if (delayTimer == null)
            {
                delayTimer = new Timer();
                delayTimer.Tick += delegate { ShowPending(); };
            }
            delayTimer.Stop();
            delayTimer.Interval = delay;
            delayTimer.Start();
        }

        private static void ShowPending()
        {
            if (delayTimer != null)
            {
                delayTimer.Stop();
            }
            Content tip = pending;
            Control target = pendingOwner;
            pending = null;
            pendingOwner = null;
            if (tip == null || target == null || target.IsDisposed || !target.Visible)
            {
                return;
            }
            Form form = target.FindForm();
            if (form == null || !form.Visible)
            {
                return;
            }
            if (instance == null || instance.IsDisposed)
            {
                instance = new GlassTip();
            }
            owner = target;
            instance.content = tip;
            instance.Place(pendingAnchor.IsEmpty ? target.RectangleToScreen(target.ClientRectangle) : pendingAnchor);
            instance.Invalidate();
            if (!instance.Visible)
            {
                instance.Show();
            }
        }

        private void Place(Rectangle anchor)
        {
            Size size = Arrange();
            Rectangle area = Screen.FromRectangle(anchor).WorkingArea;
            int gap = Dpi.S(6);
            int x = content.Centered ? anchor.Left + (anchor.Width - size.Width) / 2 : anchor.Left - Dpi.S(4);
            int y = anchor.Bottom + gap;
            if (y + size.Height > area.Bottom)
            {
                y = anchor.Top - gap - size.Height;
            }
            if (x + size.Width > area.Right - gap)
            {
                x = area.Right - gap - size.Width;
            }
            if (x < area.Left + gap)
            {
                x = area.Left + gap;
            }
            if (y < area.Top)
            {
                y = area.Top;
            }
            Bounds = new Rectangle(x, y, size.Width, size.Height);
        }

        private Size Arrange()
        {
            float padX = Dpi.F(12);
            float padY = Dpi.F(8);
            float limit = Dpi.F(360);
            SizeF title = Theme.Measure(content.Title, content.TitleFont, 0);
            SizeF body = string.IsNullOrEmpty(content.Body) ? SizeF.Empty : Theme.Measure(content.Body, Theme.SmallFont, limit);
            SizeF hint = string.IsNullOrEmpty(content.Hint) ? SizeF.Empty : Theme.Measure(content.Hint, Theme.SmallFont, limit);
            float width = Math.Min(limit, Math.Max(title.Width, Math.Max(body.Width, hint.Width))) + Dpi.F(2);
            float y = padY;
            titleBounds = new RectangleF(padX, y, width, title.Height);
            y += title.Height;
            if (!body.IsEmpty)
            {
                y += Dpi.F(2);
                bodyBounds = new RectangleF(padX, y, width, body.Height + Dpi.F(1));
                y += body.Height;
            }
            if (!hint.IsEmpty)
            {
                y += Dpi.F(3);
                hintBounds = new RectangleF(padX, y, width, hint.Height + Dpi.F(1));
                y += hint.Height;
            }
            y += padY;
            return new Size((int)Math.Ceiling(width + 2 * padX), (int)Math.Ceiling(y));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme.PaintPopup(e.Graphics, ClientRectangle, glass);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (content == null)
            {
                return;
            }
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            StringAlignment alignment = content.Centered ? StringAlignment.Center : StringAlignment.Near;
            Theme.DrawText(graphics, content.Title, content.TitleFont, content.TitleColor, titleBounds,
                alignment, StringAlignment.Near, false);
            if (!string.IsNullOrEmpty(content.Body))
            {
                Theme.DrawText(graphics, content.Body, Theme.SmallFont, Theme.Secondary, bodyBounds,
                    alignment, StringAlignment.Near, true);
            }
            if (!string.IsNullOrEmpty(content.Hint))
            {
                Theme.DrawText(graphics, content.Hint, Theme.SmallFont, Theme.Tertiary, hintBounds,
                    alignment, StringAlignment.Near, true);
            }
        }
    }

    /// <summary>Контекстное меню на стекле со скруглёнными углами.</summary>
    internal sealed class GlassMenu : ContextMenuStrip
    {
        private bool glass;

        internal GlassMenu()
        {
            Renderer = new GlassMenuRenderer();
            ShowImageMargin = false;
            ShowCheckMargin = false;
            Font = Theme.UiFont;
            ForeColor = Theme.Text;
            BackColor = Theme.PopupSolid;
            Padding = new Padding(Dpi.S(5));
        }

        internal bool IsGlass
        {
            get { return glass; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glass = Native.EnableGlass(Handle, !Theme.ForceSolid);
        }

        protected override void OnOpening(CancelEventArgs e)
        {
            GlassTip.HideAll();
            base.OnOpening(e);
        }

        internal ToolStripMenuItem AddItem(string text, string shortcut, bool enabled, EventHandler click)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Padding = new Padding(Dpi.S(4), Dpi.S(5), Dpi.S(4), Dpi.S(5));
            if (!string.IsNullOrEmpty(shortcut))
            {
                item.ShortcutKeyDisplayString = shortcut;
                item.ShowShortcutKeys = true;
            }
            item.Enabled = enabled;
            if (click != null)
            {
                item.Click += click;
            }
            Items.Add(item);
            return item;
        }

        internal void AddHeader(string text)
        {
            MenuHeader header = new MenuHeader(text);
            header.Padding = new Padding(Dpi.S(4), Dpi.S(4), Dpi.S(4), Dpi.S(2));
            Items.Add(header);
        }

        internal void AddSeparator()
        {
            if (Items.Count > 0 && !(Items[Items.Count - 1] is ToolStripSeparator))
            {
                Items.Add(new ToolStripSeparator());
            }
        }

        /// <summary>Пункт с вложенным меню в том же оформлении. Пункты добавляются во вложенное меню.</summary>
        internal GlassMenu AddSubmenu(string text)
        {
            ToolStripMenuItem item = AddItem(text, null, true, null);
            GlassMenu submenu = new GlassMenu();
            item.DropDown = submenu;
            return submenu;
        }

        /// <summary>Убирает разделитель в конце. false: показывать нечего.</summary>
        internal bool Tidy()
        {
            while (Items.Count > 0 && Items[Items.Count - 1] is ToolStripSeparator)
            {
                Items.RemoveAt(Items.Count - 1);
            }
            return Items.Count > 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Вложенное меню, заданное явно, пункт сам не освобождает.
                foreach (ToolStripItem item in Items)
                {
                    ToolStripMenuItem owner = item as ToolStripMenuItem;
                    if (owner != null && owner.DropDown is GlassMenu)
                    {
                        owner.DropDown.Dispose();
                    }
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>Последнее показанное меню, нужно проверкам интерфейса.</summary>
        internal static GlassMenu LastShown;

        /// <summary>Меню собирается под каждый щелчок и после закрытия удаляется.</summary>
        internal void ShowOnce(Control control, Point location)
        {
            LastShown = this;
            Closed += delegate
            {
                if (!control.IsDisposed && control.IsHandleCreated)
                {
                    control.BeginInvoke(new MethodInvoker(Dispose));
                }
            };
            Show(control, location);
        }
    }

    /// <summary>Неактивная строка меню: подзаголовок группы действий.</summary>
    internal sealed class MenuHeader : ToolStripMenuItem
    {
        internal MenuHeader(string text)
            : base(text)
        {
            Enabled = false;
            Font = Theme.SmallFont;
        }
    }

    internal sealed class GlassMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            GlassMenu menu = e.ToolStrip as GlassMenu;
            Theme.PaintPopup(e.Graphics, new Rectangle(Point.Empty, e.ToolStrip.Size), menu != null && menu.IsGlass);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(Dpi.F(1), 0.5f, e.Item.Width - Dpi.F(2), e.Item.Height - 1f);
            Theme.FillRound(e.Graphics, bounds, Dpi.F(6), Theme.FillHover);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            ToolStripMenuItem item = e.Item as ToolStripMenuItem;
            bool shortcut = item != null && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString)
                && e.Text == item.ShortcutKeyDisplayString;
            bool header = e.Item is MenuHeader;
            Color color = header || shortcut || !e.Item.Enabled ? Theme.Tertiary : Theme.Text;
            Font font = header || shortcut ? Theme.SmallFont : e.TextFont;
            Theme.DrawText(e.Graphics, e.Text, font, color, e.TextRectangle,
                shortcut ? StringAlignment.Far : StringAlignment.Near, StringAlignment.Center, false);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int middle = e.Item.Height / 2;
            using (Pen pen = new Pen(Theme.Line))
            {
                e.Graphics.DrawLine(pen, Dpi.S(8), middle, e.Item.Width - Dpi.S(8), middle);
            }
        }

        /// <summary>Стрелка вложенного меню и кнопок прокрутки длинного меню.</summary>
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            string glyph;
            switch (e.Direction)
            {
                case ArrowDirection.Left:
                    glyph = Glyphs.ChevronLeft;
                    break;
                case ArrowDirection.Up:
                    glyph = Glyphs.ChevronUp;
                    break;
                case ArrowDirection.Down:
                    glyph = Glyphs.ChevronDown;
                    break;
                default:
                    glyph = Glyphs.ChevronRight;
                    break;
            }
            bool enabled = e.Item == null || e.Item.Enabled;
            Theme.DrawGlyph(e.Graphics, glyph, Theme.SmallIconFont, enabled ? Theme.Secondary : Theme.Tertiary, e.ArrowRectangle);
        }
    }
}
