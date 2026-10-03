using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SafePaste.Ui
{
    internal sealed class GlassColumn
    {
        internal readonly string Header;
        internal readonly int Width;
        internal readonly bool Right;

        /// <summary>width задаётся для 96 DPI; при 0 столбец забирает оставшееся место.</summary>
        internal GlassColumn(string header, int width, bool right)
        {
            Header = header;
            Width = width;
            Right = right;
        }
    }

    internal delegate void CellPainter(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered);

    internal delegate string ItemTipProvider(object item);

    /// <summary>Список на стекле: строки-таблетки, выделение как в Проводнике, тонкая полоса прокрутки.</summary>
    internal sealed class GlassList : GlassControl
    {
        private readonly List<GlassColumn> columns = new List<GlassColumn>();
        private readonly List<object> items = new List<object>();
        private readonly HashSet<int> selected = new HashSet<int>();
        private int focus = -1;
        private int anchor = -1;
        private int hover = -1;
        private int scroll;
        private bool thumbDragging;
        private int thumbGrab;
        private bool barHovered;
        private string emptyText = string.Empty;

        internal CellPainter Painter;
        internal ItemTipProvider TipProvider;

        internal event EventHandler SelectionChanged;
        internal event EventHandler ItemActivated;
        internal event EventHandler ToggleRequested;
        internal event EventHandler DeleteRequested;
        internal event MouseEventHandler ContextRequested;

        internal GlassList()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
        }

        internal void AddColumn(string header, int width, bool right)
        {
            columns.Add(new GlassColumn(header, width, right));
            Invalidate();
        }

        internal int RowHeight
        {
            get { return Dpi.S(34); }
        }

        private int HeaderHeight
        {
            get { return Dpi.S(28); }
        }

        internal string EmptyText
        {
            get { return emptyText; }
            set { emptyText = value ?? string.Empty; Invalidate(); }
        }

        internal int Count
        {
            get { return items.Count; }
        }

        internal object ItemAt(int index)
        {
            return index >= 0 && index < items.Count ? items[index] : null;
        }

        internal void SetItems(IEnumerable<object> source, Predicate<object> keepSelected)
        {
            items.Clear();
            selected.Clear();
            focus = -1;
            anchor = -1;
            hover = -1;
            foreach (object item in source)
            {
                items.Add(item);
            }
            if (keepSelected != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (keepSelected(items[i]))
                    {
                        selected.Add(i);
                        if (focus < 0)
                        {
                            focus = i;
                            anchor = i;
                        }
                    }
                }
            }
            ClampScroll();
            GlassTip.HideFor(this);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        }

        internal List<object> SelectedItems
        {
            get
            {
                List<int> indexes = new List<int>(selected);
                indexes.Sort();
                List<object> result = new List<object>(indexes.Count);
                foreach (int index in indexes)
                {
                    result.Add(items[index]);
                }
                return result;
            }
        }

        internal object FocusedItem
        {
            get { return focus >= 0 && focus < items.Count && selected.Contains(focus) ? items[focus] : null; }
        }

        internal void SelectIndex(int index)
        {
            if (index < 0 || index >= items.Count)
            {
                return;
            }
            selected.Clear();
            selected.Add(index);
            focus = index;
            anchor = index;
            EnsureVisible(index);
            Invalidate();
            RaiseSelectionChanged();
        }

        private void RaiseSelectionChanged()
        {
            if (SelectionChanged != null)
            {
                SelectionChanged(this, EventArgs.Empty);
            }
        }

        // ---------------------------------------------------------------- геометрия

        private int[] ColumnWidths()
        {
            int[] widths = new int[columns.Count];
            int available = ClientSize.Width - Dpi.S(20);
            int fixedWidth = 0;
            int fill = -1;
            for (int i = 0; i < columns.Count; i++)
            {
                if (columns[i].Width <= 0)
                {
                    fill = i;
                    continue;
                }
                widths[i] = Dpi.S(columns[i].Width);
                fixedWidth += widths[i];
            }
            if (fill >= 0)
            {
                widths[fill] = Math.Max(Dpi.S(80), available - fixedWidth);
            }
            return widths;
        }

        private int ContentHeight
        {
            get { return items.Count * RowHeight + Dpi.S(4); }
        }

        private int BodyHeight
        {
            get { return Math.Max(0, ClientSize.Height - HeaderHeight); }
        }

        private int MaxScroll
        {
            get { return Math.Max(0, ContentHeight - BodyHeight); }
        }

        private void ClampScroll()
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll));
        }

        internal int RowAt(int y)
        {
            if (y < HeaderHeight)
            {
                return -1;
            }
            int index = (y - HeaderHeight + scroll) / RowHeight;
            return index >= 0 && index < items.Count ? index : -1;
        }

        internal Rectangle RowBounds(int index)
        {
            return new Rectangle(0, HeaderHeight + index * RowHeight - scroll, ClientSize.Width, RowHeight);
        }

        private int ColumnAt(int x)
        {
            int[] widths = ColumnWidths();
            int left = Dpi.S(8);
            for (int i = 0; i < widths.Length; i++)
            {
                if (x < left + widths[i])
                {
                    return i;
                }
                left += widths[i];
            }
            return widths.Length - 1;
        }

        private void EnsureVisible(int index)
        {
            int top = index * RowHeight;
            if (top < scroll)
            {
                scroll = top;
            }
            else if (top + RowHeight > scroll + BodyHeight)
            {
                scroll = top + RowHeight - BodyHeight;
            }
            ClampScroll();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
            Invalidate();
        }

        // ---------------------------------------------------------------- отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            int[] widths = ColumnWidths();
            float left = Dpi.F(8);
            for (int i = 0; i < columns.Count; i++)
            {
                RectangleF cell = new RectangleF(left + Dpi.F(6), 0, Math.Max(0, widths[i] - Dpi.F(12)), HeaderHeight);
                Theme.DrawText(graphics, columns[i].Header, Theme.SmallFont, Theme.Tertiary, cell,
                    columns[i].Right ? StringAlignment.Far : StringAlignment.Near, StringAlignment.Center, false);
                left += widths[i];
            }
            using (Pen pen = new Pen(Theme.Line))
            {
                graphics.DrawLine(pen, Dpi.S(8), HeaderHeight - 1, ClientSize.Width - Dpi.S(8), HeaderHeight - 1);
            }
            if (items.Count == 0)
            {
                Theme.DrawText(graphics, emptyText, Theme.UiFont, Theme.Tertiary,
                    new RectangleF(Dpi.F(16), HeaderHeight, Math.Max(0, Width - Dpi.F(32)), BodyHeight),
                    StringAlignment.Center, StringAlignment.Center, true);
                return;
            }
            GraphicsState state = graphics.Save();
            graphics.SetClip(new Rectangle(0, HeaderHeight, ClientSize.Width, BodyHeight));
            int first = scroll / RowHeight;
            int last = Math.Min(items.Count - 1, (scroll + BodyHeight) / RowHeight);
            for (int index = first; index <= last; index++)
            {
                Rectangle row = RowBounds(index);
                RectangleF pill = new RectangleF(Dpi.F(4), row.Top + Dpi.F(1), ClientSize.Width - Dpi.F(8) - Dpi.F(12), RowHeight - Dpi.F(2));
                bool isSelected = selected.Contains(index);
                bool isHovered = index == hover;
                if (isSelected)
                {
                    Theme.FillRound(graphics, pill, Dpi.F(8), Theme.RowSelected);
                }
                else if (isHovered)
                {
                    Theme.FillRound(graphics, pill, Dpi.F(8), Theme.RowHover);
                }
                if (index == focus && Focused && ShowFocusCues)
                {
                    using (GraphicsPath path = Theme.Round(pill, Dpi.F(8)))
                    using (Pen pen = new Pen(Color.FromArgb(160, Theme.Accent), Dpi.F(1.2f)))
                    {
                        graphics.DrawPath(pen, path);
                    }
                }
                if (Painter != null)
                {
                    float x = Dpi.F(8);
                    for (int column = 0; column < columns.Count; column++)
                    {
                        RectangleF cell = new RectangleF(x + Dpi.F(6), row.Top, Math.Max(0, widths[column] - Dpi.F(12)), RowHeight);
                        Painter(graphics, cell, items[index], column, isSelected, isHovered);
                        x += widths[column];
                    }
                }
            }
            graphics.Restore(state);
            PaintScrollBar(graphics);
        }

        private Rectangle Track
        {
            get
            {
                int width = Dpi.S(12);
                return new Rectangle(ClientSize.Width - width - Dpi.S(1), HeaderHeight + Dpi.S(4), width, Math.Max(0, BodyHeight - Dpi.S(8)));
            }
        }

        private Rectangle Thumb
        {
            get
            {
                int max = MaxScroll;
                if (max <= 0)
                {
                    return Rectangle.Empty;
                }
                Rectangle track = Track;
                int height = (int)Math.Max(Dpi.S(28), (long)track.Height * BodyHeight / Math.Max(1, ContentHeight));
                height = Math.Min(height, track.Height);
                int y = track.Top + (int)((long)(track.Height - height) * scroll / max);
                return new Rectangle(track.Left, y, track.Width, height);
            }
        }

        private void PaintScrollBar(Graphics graphics)
        {
            Rectangle thumb = Thumb;
            if (thumb.IsEmpty)
            {
                return;
            }
            float width = barHovered || thumbDragging ? Dpi.F(8) : Dpi.F(5);
            RectangleF bar = new RectangleF(thumb.Right - width - Dpi.F(2), thumb.Top, width, thumb.Height);
            Theme.FillRound(graphics, bar, width / 2f, Color.FromArgb(thumbDragging ? 150 : barHovered ? 115 : 72, 255, 255, 255));
        }

        // ---------------------------------------------------------------- мышь

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            GlassTip.HideFor(this);
            if (!Focused)
            {
                Focus();
            }
            if (e.Button == MouseButtons.Left && MaxScroll > 0 && Track.Contains(e.Location))
            {
                Rectangle thumb = Thumb;
                if (thumb.Contains(e.Location))
                {
                    thumbDragging = true;
                    thumbGrab = e.Y - thumb.Top;
                    Capture = true;
                }
                else
                {
                    scroll += e.Y < thumb.Top ? -BodyHeight : BodyHeight;
                    ClampScroll();
                }
                Invalidate();
                return;
            }
            int index = RowAt(e.Y);
            if (index < 0)
            {
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                if (!selected.Contains(index))
                {
                    SelectIndex(index);
                }
                focus = index;
                Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (!selected.Remove(index))
                {
                    selected.Add(index);
                }
                focus = index;
                anchor = index;
                Invalidate();
                RaiseSelectionChanged();
                return;
            }
            if ((ModifierKeys & Keys.Shift) != 0 && anchor >= 0)
            {
                SelectRange(anchor, index);
                return;
            }
            SelectIndex(index);
            if (e.Clicks >= 2 && ColumnAt(e.X) > 0)
            {
                if (ItemActivated != null)
                {
                    ItemActivated(this, EventArgs.Empty);
                }
            }
            else if (e.Clicks == 1 && ColumnAt(e.X) == 0 && ToggleRequested != null)
            {
                ToggleRequested(this, EventArgs.Empty);
            }
        }

        private void SelectRange(int from, int to)
        {
            selected.Clear();
            int low = Math.Min(from, to);
            int high = Math.Max(from, to);
            for (int i = low; i <= high; i++)
            {
                selected.Add(i);
            }
            focus = to;
            EnsureVisible(to);
            Invalidate();
            RaiseSelectionChanged();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDragging)
            {
                Rectangle track = Track;
                int room = Math.Max(1, track.Height - Thumb.Height);
                scroll = (int)((long)(e.Y - thumbGrab - track.Top) * MaxScroll / room);
                ClampScroll();
                Invalidate();
                return;
            }
            bool overBar = MaxScroll > 0 && Track.Contains(e.Location);
            if (overBar != barHovered)
            {
                barHovered = overBar;
                Invalidate();
            }
            SetHover(overBar ? -1 : RowAt(e.Y));
        }

        private void SetHover(int index)
        {
            if (index == hover)
            {
                return;
            }
            hover = index;
            Invalidate();
            if (index < 0 || TipProvider == null)
            {
                GlassTip.HideFor(this);
                return;
            }
            string tip = TipProvider(items[index]);
            if (string.IsNullOrEmpty(tip))
            {
                GlassTip.HideFor(this);
                return;
            }
            Rectangle row = RowBounds(index);
            GlassTip.ShowText(this, RectangleToScreen(new Rectangle(Dpi.S(40), row.Top, Dpi.S(260), row.Height)), tip, 650);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (thumbDragging)
            {
                thumbDragging = false;
                Capture = false;
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Right && ContextRequested != null && RowAt(e.Y) >= 0)
            {
                ContextRequested(this, e);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            barHovered = false;
            SetHover(-1);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            GlassTip.HideFor(this);
            scroll -= e.Delta * RowHeight * 3 / 120;
            ClampScroll();
            hover = -1;
            Invalidate();
        }

        // ---------------------------------------------------------------- клавиатура

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                case Keys.Space:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int page = Math.Max(1, BodyHeight / RowHeight - 1);
            int target = -1;
            switch (e.KeyCode)
            {
                case Keys.Up:
                    target = Math.Max(0, focus - 1);
                    break;
                case Keys.Down:
                    target = Math.Min(items.Count - 1, focus + 1);
                    break;
                case Keys.PageUp:
                    target = Math.Max(0, focus - page);
                    break;
                case Keys.PageDown:
                    target = Math.Min(items.Count - 1, focus + page);
                    break;
                case Keys.Home:
                    target = 0;
                    break;
                case Keys.End:
                    target = items.Count - 1;
                    break;
                case Keys.Space:
                    Raise(ToggleRequested, e);
                    return;
                case Keys.Enter:
                    Raise(ItemActivated, e);
                    return;
                case Keys.Delete:
                    Raise(DeleteRequested, e);
                    return;
                case Keys.A:
                    if (e.Control)
                    {
                        if (items.Count > 0)
                        {
                            SelectRange(0, items.Count - 1);
                        }
                        e.Handled = true;
                    }
                    break;
                case Keys.Apps:
                case Keys.F10:
                    if (e.KeyCode == Keys.Apps || e.Shift)
                    {
                        OpenContextFromKeyboard();
                        e.Handled = true;
                    }
                    break;
            }
            if (target >= 0 && items.Count > 0)
            {
                if (e.Shift && anchor >= 0)
                {
                    SelectRange(anchor, target);
                }
                else
                {
                    SelectIndex(target);
                }
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private void Raise(EventHandler handler, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (handler != null && selected.Count > 0)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OpenContextFromKeyboard()
        {
            if (focus < 0 || ContextRequested == null)
            {
                return;
            }
            if (!selected.Contains(focus))
            {
                SelectIndex(focus);
            }
            Rectangle row = RowBounds(focus);
            ContextRequested(this, new MouseEventArgs(MouseButtons.Right, 1, Dpi.S(60), row.Bottom - Dpi.S(4), 0));
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (focus < 0 && items.Count > 0)
            {
                focus = 0;
            }
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }
    }
}
