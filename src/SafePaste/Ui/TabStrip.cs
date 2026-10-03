using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SafePaste.Ui
{
    /// <summary>Вкладка окна проверки: подпись, подсказка и признак расшифровки или картинки.</summary>
    internal sealed class TabInfo
    {
        internal string Caption;
        internal string Tip;
        internal bool Decrypt;
        internal bool Picture;
    }

    /// <summary>
    /// Вкладки окна проверки, по одной на скопированный текст или картинку. Щелчок открывает вкладку, крестик
    /// или средняя кнопка мыши закрывают её. Вкладка расшифровки помечена открытым замком, картинка значком.
    /// </summary>
    internal sealed class TabStrip : GlassControl
    {
        private readonly List<TabInfo> tabs = new List<TabInfo>();
        private int selected;
        private int hover = -1;
        private bool hoverClose;

        internal event EventHandler SelectedChanged;
        internal event Action<int> CloseRequested;

        internal TabStrip()
        {
            Font = Theme.SmallFont;
            SetStyle(ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PageTabList;
            AccessibleName = "Вкладки";
        }

        internal int Count
        {
            get { return tabs.Count; }
        }

        internal int SelectedIndex
        {
            get { return selected; }
        }

        internal TabInfo this[int index]
        {
            get { return tabs[index]; }
        }

        /// <summary>Новый набор вкладок без события выбора: его задаёт само окно.</summary>
        internal void SetTabs(List<TabInfo> items, int selectedIndex)
        {
            tabs.Clear();
            tabs.AddRange(items);
            selected = Math.Max(0, Math.Min(tabs.Count - 1, selectedIndex));
            hover = -1;
            AccessibleDescription = tabs.Count == 0 ? string.Empty : tabs[selected].Caption;
            Invalidate();
        }

        private RectangleF[] TabBounds()
        {
            RectangleF[] result = new RectangleF[tabs.Count];
            if (tabs.Count == 0)
            {
                return result;
            }
            float gap = Dpi.F(4);
            float width = (Width - gap * (tabs.Count - 1)) / tabs.Count;
            width = Math.Max(Dpi.F(56), Math.Min(Dpi.F(210), width));
            int visible = Math.Max(1, (int)Math.Floor((Width + gap) / (width + gap)));
            int first = Math.Max(0, Math.Min(selected - visible + 1, tabs.Count - visible));
            float x = -first * (width + gap);
            for (int i = 0; i < tabs.Count; i++)
            {
                result[i] = new RectangleF(x, 0.5f, width, Height - 1f);
                x += width + gap;
            }
            return result;
        }

        private static RectangleF CloseBox(RectangleF tab)
        {
            float size = Dpi.F(20);
            return new RectangleF(tab.Right - size - Dpi.F(5), tab.Y + (tab.Height - size) / 2f, size, size);
        }

        private int IndexAt(Point point, out bool onClose)
        {
            onClose = false;
            RectangleF[] bounds = TabBounds();
            for (int i = 0; i < bounds.Length; i++)
            {
                if (bounds[i].Contains(point))
                {
                    onClose = CloseBox(bounds[i]).Contains(point);
                    return i;
                }
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            RectangleF[] bounds = TabBounds();
            for (int i = 0; i < tabs.Count; i++)
            {
                RectangleF tab = bounds[i];
                if (tab.Left >= Width)
                {
                    break;
                }
                float radius = tab.Height / 2f;
                if (i == selected)
                {
                    using (GraphicsPath path = Theme.Round(tab, radius))
                    {
                        using (SolidBrush brush = new SolidBrush(Theme.FillHover))
                        {
                            graphics.FillPath(brush, path);
                        }
                        Theme.StrokeEdge(graphics, path, tab, Theme.EdgeTop, Theme.EdgeBottom);
                    }
                }
                else
                {
                    Theme.FillRound(graphics, tab, radius, i == hover ? Theme.Fill : Theme.FillPressed);
                }
                float left = tab.X + Dpi.F(12);
                if (tabs[i].Decrypt || tabs[i].Picture)
                {
                    Theme.DrawGlyph(graphics, tabs[i].Decrypt ? Glyphs.Unlock : Glyphs.Picture, Theme.SmallIconFont,
                        tabs[i].Decrypt ? Theme.Accent : Theme.Secondary, new RectangleF(left, tab.Y, Dpi.F(14), tab.Height));
                    left += Dpi.F(18);
                }
                bool showClose = i == selected || i == hover;
                float right = showClose ? CloseBox(tab).Left - Dpi.F(2) : tab.Right - Dpi.F(10);
                Theme.DrawText(graphics, tabs[i].Caption, i == selected ? Theme.StrongFont : Font,
                    i == selected ? Theme.Text : Theme.Secondary, new RectangleF(left, tab.Y, Math.Max(0, right - left), tab.Height));
                if (showClose)
                {
                    RectangleF box = CloseBox(tab);
                    if (i == hover && hoverClose)
                    {
                        Theme.FillRound(graphics, box, box.Height / 2f, Theme.FillHover);
                    }
                    Theme.DrawGlyph(graphics, Glyphs.Close, Theme.SmallIconFont, Theme.Secondary, box);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool onClose;
            int index = IndexAt(e.Location, out onClose);
            if (index == hover && onClose == hoverClose)
            {
                return;
            }
            bool moved = index != hover;
            hover = index;
            hoverClose = onClose;
            Invalidate();
            if (!moved)
            {
                return;
            }
            if (index >= 0 && !string.IsNullOrEmpty(tabs[index].Tip))
            {
                GlassTip.ShowText(this, RectangleToScreen(Rectangle.Round(TabBounds()[index])), tabs[index].Tip, 450);
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
            hoverClose = false;
            Invalidate();
            GlassTip.HideFor(this);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            GlassTip.HideFor(this);
            bool onClose;
            int index = IndexAt(e.Location, out onClose);
            if (index < 0)
            {
                return;
            }
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && onClose))
            {
                if (CloseRequested != null)
                {
                    CloseRequested(index);
                }
                return;
            }
            if (e.Button == MouseButtons.Left && index != selected)
            {
                Select(index);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (tabs.Count > 1)
            {
                Select((selected + (e.Delta < 0 ? 1 : tabs.Count - 1)) % tabs.Count);
            }
        }

        /// <summary>Открыть вкладку, как щелчком мыши.</summary>
        internal void Select(int index)
        {
            if (index < 0 || index >= tabs.Count || index == selected)
            {
                return;
            }
            selected = index;
            AccessibleDescription = tabs[selected].Caption;
            Invalidate();
            if (SelectedChanged != null)
            {
                SelectedChanged(this, EventArgs.Empty);
            }
        }
    }
}
