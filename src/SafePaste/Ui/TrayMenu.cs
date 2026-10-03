using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SafePaste.Detecting;

namespace SafePaste.Ui
{
    internal static class TrayMenu
    {
        /// <summary>
        /// Меню значка: шапка (щелчок открывает окно проверки), проверка, быстрая вставка, расшифровка,
        /// режим, настройки, справка и выход. Настройки открываются отдельным окном, как и по шестерёнке
        /// окна проверки.
        /// </summary>
        internal static ContextMenuStrip Create(Action review, Action quick, Action reveal, Action settings, Action about,
            Action exit, Func<ControlMode> currentMode, Action<ControlMode> chooseMode, out ToolStripMenuItem status)
        {
            GlassMenu menu = new GlassMenu();
            menu.Padding = new Padding(Dpi.S(6));
            status = new TrayHeaderItem(review);
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new TrayActionItem("Проверить текст", "Показать замены перед вставкой", "Ctrl+Alt+Shift+V", true, review));
            menu.Items.Add(new TrayActionItem("Быстрая вставка", "Скрыть найденное и сразу вставить", "Ctrl+Shift+V", false, quick));
            menu.Items.Add(new TrayActionItem("Показать с реальными значениями", "Раскрыть метки в тексте из буфера",
                "Ctrl+Shift+C", false, reveal));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new TrayModeItem(currentMode, chooseMode));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new TrayActionItem("Настройки", "Запуск, метки, правила, мост", null, false, settings));
            menu.Items.Add(new TrayActionItem("Как пользоваться", null, null, false, about));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new TrayActionItem("Выйти", null, null, false, exit));
            Fit(menu);
            return menu;
        }

        /// <summary>Обычное меню меряет только Text и не знает о второй строке пунктов: размер задаётся здесь.</summary>
        internal static void Fit(ToolStripDropDownMenu menu)
        {
            menu.AutoSize = false;
            int width = 0;
            int height = menu.Padding.Vertical;
            foreach (ToolStripItem item in menu.Items)
            {
                Size preferred = item.GetPreferredSize(Size.Empty);
                width = Math.Max(width, preferred.Width);
                item.AutoSize = false;
                item.Height = preferred.Height;
                height += item.Height + item.Margin.Vertical;
            }
            menu.Size = new Size(width + menu.Padding.Horizontal + 2, height + 2);
        }
    }

    /// <summary>
    /// Шапка меню: значок, название и состояние. Щелчок по ней открывает окно проверки, как двойной
    /// щелчок по значку в трее.
    /// </summary>
    internal sealed class TrayHeaderItem : ToolStripMenuItem
    {
        private readonly Bitmap logo = IconArtwork.Render(64);

        internal TrayHeaderItem(Action open)
        {
            Text = "Работает локально";
            AccessibleName = "Открыть SafePaste";
            AccessibleDescription = Text;
            if (open != null)
            {
                Click += delegate { open(); };
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleDescription = Text;
        }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            return new Size(Dpi.S(360), Dpi.S(68));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            bool lit = Selected || Pressed;
            if (lit)
            {
                Theme.FillRound(graphics, new RectangleF(Dpi.F(2), 1f, Width - Dpi.F(4), Height - 2f), Dpi.F(10), Theme.FillHover);
            }
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int size = Dpi.S(36);
            int left = Dpi.S(14);
            graphics.DrawImage(logo, new Rectangle(left, (Height - size) / 2, size, size));
            float textLeft = left + size + Dpi.F(12);
            float middle = Height / 2f;
            float arrow = Dpi.F(34);
            Theme.DrawText(graphics, "SafePaste", Theme.TitleFont, Theme.Text,
                new RectangleF(textLeft, 0, Width - textLeft - arrow, middle + Dpi.F(2)), StringAlignment.Near, StringAlignment.Far, false);
            float dot = Dpi.F(7);
            using (SolidBrush brush = new SolidBrush(Theme.Accent))
            {
                graphics.FillEllipse(brush, textLeft, middle + Dpi.F(7), dot, dot);
            }
            Theme.DrawText(graphics, Text, Theme.SmallFont, Theme.Secondary,
                new RectangleF(textLeft + dot + Dpi.F(6), middle + Dpi.F(2), Width - textLeft - dot - Dpi.F(6) - arrow, middle - Dpi.F(4)),
                StringAlignment.Near, StringAlignment.Near, false);
            // Значок «открыть окно» справа: шапка кликается.
            Theme.DrawGlyph(graphics, Glyphs.OpenWindow, Theme.SmallIconFont, lit ? Theme.Text : Theme.Tertiary,
                new RectangleF(Width - arrow, 0, arrow - Dpi.F(10), Height));
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
    /// <summary>
    /// Выбор режима прямо в меню: три сегмента, как в окне проверки.
    /// Стрелки влево и вправо переключают режим с клавиатуры.
    /// </summary>
    internal sealed class TrayModeItem : ToolStripMenuItem
    {
        internal static readonly string[] Names = { "Строгий", "Обычный", "Лёгкий" };
        private static readonly string[] Descriptions =
        {
            "Скрывать всё, даже догадки и пути",
            "Скрывать то, в чём SafePaste уверен",
            "Только пароли, ключи и ваши правила"
        };
        private readonly Func<ControlMode> current;
        private readonly Action<ControlMode> choose;
        private int hover = -1;
        private int pressed = -1;

        internal TrayModeItem(Func<ControlMode> current, Action<ControlMode> choose)
            : base("Режим")
        {
            this.current = current;
            this.choose = choose;
            UpdateAccessibleName();
        }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            return new Size(Dpi.S(360), Dpi.S(74));
        }

        private int CurrentIndex
        {
            get { return (int)current(); }
        }

        private RectangleF Track
        {
            get
            {
                float inset = Dpi.F(14);
                return new RectangleF(inset, Height - Dpi.F(40), Width - 2 * inset, Dpi.F(30));
            }
        }

        private RectangleF SegmentAt(int index)
        {
            RectangleF track = Track;
            float width = (track.Width - Dpi.F(6)) / Names.Length;
            return new RectangleF(track.X + Dpi.F(3) + index * width, track.Y + Dpi.F(3), width, track.Height - Dpi.F(6));
        }

        private int IndexAt(Point point)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                if (SegmentAt(i).Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        internal void Choose(int index)
        {
            if (index < 0 || index >= Names.Length)
            {
                return;
            }
            choose((ControlMode)index);
            UpdateAccessibleName();
            Invalidate();
        }

        private void UpdateAccessibleName()
        {
            AccessibleName = "Режим: " + Names[CurrentIndex].ToLowerInvariant();
            AccessibleDescription = Descriptions[CurrentIndex];
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            float inset = Dpi.F(14);
            int selected = CurrentIndex;
            RectangleF caption = new RectangleF(inset, Dpi.F(4), Width - 2 * inset, Dpi.F(26));
            Theme.DrawText(graphics, "Режим", Theme.UiFont, Theme.Text, caption);
            Theme.DrawText(graphics, Descriptions[selected], Theme.SmallFont, Theme.Tertiary, caption,
                StringAlignment.Far, StringAlignment.Center, false);
            RectangleF track = Track;
            Theme.FillRound(graphics, track, track.Height / 2f, Theme.FillPressed);
            for (int i = 0; i < Names.Length; i++)
            {
                RectangleF segment = SegmentAt(i);
                if (i == selected)
                {
                    using (GraphicsPath path = Theme.Round(segment, segment.Height / 2f))
                    {
                        using (SolidBrush brush = new SolidBrush(Theme.FillHover))
                        {
                            graphics.FillPath(brush, path);
                        }
                        Theme.StrokeEdge(graphics, path, segment, Theme.EdgeTop, Theme.EdgeBottom);
                    }
                }
                else if (i == hover)
                {
                    Theme.FillRound(graphics, segment, segment.Height / 2f, Theme.RowHover);
                }
                Theme.DrawText(graphics, Names[i], i == selected ? Theme.StrongFont : Theme.SmallFont,
                    i == selected ? Theme.Text : Theme.Secondary, segment, StringAlignment.Center, StringAlignment.Center, false);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = IndexAt(e.Location);
            if (index != hover)
            {
                hover = index;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = IndexAt(e.Location);
            base.OnMouseDown(e);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            int index = pressed;
            pressed = -1;
            Choose(index);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right)
            {
                Choose(CurrentIndex + (keyData == Keys.Left ? -1 : 1));
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }
    }

    internal sealed class TrayActionItem : ToolStripMenuItem
    {
        private string description;
        private readonly string shortcut;
        private readonly bool primary;

        internal TrayActionItem(string title, string description, string shortcut, bool primary, Action action)
            : base(title)
        {
            this.description = description;
            this.shortcut = shortcut;
            this.primary = primary;
            AccessibleName = title;
            UpdateAccessibleDescription();
            // У пункта с вложенным меню действия нет: щелчок только раскрывает меню.
            if (action != null)
            {
                Click += delegate { action(); };
            }
        }

        /// <summary>Вторая строка пункта. Высота пункта задана при создании меню, поэтому строку нельзя убрать.</summary>
        internal string Description
        {
            get { return description; }
            set
            {
                description = string.IsNullOrEmpty(value) ? " " : value;
                UpdateAccessibleDescription();
                Invalidate();
            }
        }

        private void UpdateAccessibleDescription()
        {
            AccessibleDescription = description + (shortcut == null ? string.Empty : ", " + shortcut);
        }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            return new Size(Dpi.S(360), Dpi.S(description == null ? 38 : 58));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            if (Selected || Pressed)
            {
                Theme.FillRound(graphics, new RectangleF(Dpi.F(2), 1f, Width - Dpi.F(4), Height - 2f), Dpi.F(8), Theme.FillHover);
            }
            float inset = Dpi.F(14);
            float width = Width - 2 * inset;
            Font font = primary ? Theme.StrongFont : Theme.UiFont;
            Color color = primary ? Theme.Accent : Theme.Text;
            if (HasDropDownItems)
            {
                Theme.DrawGlyph(graphics, Glyphs.ChevronRight, Theme.SmallIconFont, Theme.Secondary,
                    new RectangleF(Width - inset - Dpi.F(12), 0, Dpi.F(12), Height));
                width -= Dpi.F(20);
            }
            if (description == null)
            {
                Theme.DrawText(graphics, Text, font, color, new RectangleF(inset, 0, width, Height));
                return;
            }
            float middle = Height / 2f;
            Theme.DrawText(graphics, Text, font, color, new RectangleF(inset, 0, width, middle + Dpi.F(1)),
                StringAlignment.Near, StringAlignment.Far, false);
            Theme.DrawText(graphics, shortcut, Theme.SmallMonoFont, Theme.Tertiary, new RectangleF(inset, 0, width, middle + Dpi.F(1)),
                StringAlignment.Far, StringAlignment.Far, false);
            Theme.DrawText(graphics, description, Theme.SmallFont, Theme.Secondary,
                new RectangleF(inset, middle + Dpi.F(3), width, middle - Dpi.F(3)), StringAlignment.Near, StringAlignment.Near, false);
        }
    }
}
