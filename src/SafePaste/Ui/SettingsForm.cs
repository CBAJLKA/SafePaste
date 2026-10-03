using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SafePaste.Storage;

namespace SafePaste.Ui
{
    /// <summary>
    /// Что умеет окно настроек. Окно одно на меню значка и шестерёнку окна проверки, поэтому
    /// действия передаются сюда, а не создаются в каждом окне.
    /// </summary>
    internal sealed class SettingsActions
    {
        internal Action ShowRules;
        internal Action ShowBridge;
        internal Func<string> DescribeBridge;
        /// <summary>Запуск при входе в Windows.</summary>
        internal Func<bool> StartsWithWindows;
        internal Action<bool> SetStartsWithWindows;
        /// <summary>Предупреждение при запуске, если Windows хранит копии буфера обмена.</summary>
        internal Func<bool> ClipboardWarning;
        internal Action<bool> SetClipboardWarning;
        /// <summary>Записывать метки вставок в labels.dat.</summary>
        internal Func<bool> Saving;
        internal Action<bool> SetSaving;
        /// <summary>Скрывать запомненное без подсказок.</summary>
        internal Func<bool> Hiding;
        internal Action<bool> SetHiding;
        /// <summary>Контекстный поиск паролей и токенов в обычных фразах.</summary>
        internal Func<bool> SmartSecrets;
        internal Action<bool> SetSmartSecrets;

        /// <summary>Действия без приложения в трее: для проверок интерфейса и демонстрации.</summary>
        internal static SettingsActions Offline()
        {
            bool start = false;
            bool warn = true;
            bool save = true;
            bool hide = true;
            bool smart = true;
            SettingsActions actions = new SettingsActions();
            actions.ShowRules = delegate { };
            actions.ShowBridge = delegate { };
            actions.DescribeBridge = delegate { return "Выключен"; };
            actions.StartsWithWindows = delegate { return start; };
            actions.SetStartsWithWindows = delegate(bool on) { start = on; };
            actions.ClipboardWarning = delegate { return warn; };
            actions.SetClipboardWarning = delegate(bool on) { warn = on; };
            actions.Saving = delegate { return save; };
            actions.SetSaving = delegate(bool on) { save = on; };
            actions.Hiding = delegate { return hide; };
            actions.SetHiding = delegate(bool on) { hide = on; };
            actions.SmartSecrets = delegate { return smart; };
            actions.SetSmartSecrets = delegate(bool on) { smart = on; };
            return actions;
        }

        /// <summary>Выключатели, которые берут и меняют настройки в settings.json.</summary>
        internal void UseSettingsFile()
        {
            ClipboardWarning = delegate { return SafePasteSettings.Load().ShowClipboardWarning; };
            SetClipboardWarning = delegate(bool on) { SafePasteSettings.Update(delegate(SafePasteSettings latest) { latest.ShowClipboardWarning = on; }); };
            Saving = delegate { return SafePasteSettings.Load().SaveLabels; };
            SetSaving = delegate(bool on) { SafePasteSettings.Update(delegate(SafePasteSettings latest) { latest.SaveLabels = on; }); };
            Hiding = delegate { return SafePasteSettings.Load().HideRemembered; };
            SetHiding = delegate(bool on) { SafePasteSettings.Update(delegate(SafePasteSettings latest) { latest.HideRemembered = on; }); };
            SmartSecrets = delegate { return SafePasteSettings.Load().SmartSecrets; };
            SetSmartSecrets = delegate(bool on) { SafePasteSettings.Update(delegate(SafePasteSettings latest) { latest.SmartSecrets = on; }); };
        }
    }

    /// <summary>
    /// Окно настроек: запуск, проверка и метки, правила и мост. Всё на одной странице группами,
    /// изменения сохраняются сразу, кнопки «Сохранить» нет. Стрелки вверх и вниз ходят по строкам,
    /// Пробел переключает, Esc закрывает.
    /// </summary>
    internal sealed class SettingsForm : GlassForm
    {
        private const int FormWidth = 540;

        private readonly SettingsActions actions;
        private readonly WindowHeader header;
        private readonly GlassButton closeButton;
        private readonly List<SettingsGroup> groups = new List<SettingsGroup>();
        private readonly List<SettingRow> rows = new List<SettingRow>();
        private readonly SettingRow bridgeRow;
        private readonly Timer refresh;

        /// <summary>Выключатель переключили: открытое окно проверки берёт новые настройки.</summary>
        internal event EventHandler Changed;

        internal SettingsForm(SettingsActions actions)
        {
            this.actions = actions;
            Text = "SafePaste: настройки";
            Resizable = false;
            StartPosition = FormStartPosition.Manual;

            header = new WindowHeader("Настройки", "Изменения сохраняются сразу");
            closeButton = new GlassButton(Glyphs.Close, null, ButtonKind.Close, "Закрыть\nEsc");
            closeButton.TabStop = false;
            closeButton.Click += delegate { Close(); };

            SettingRow start = Switch("Запускать при входе в Windows", "SafePaste сам стартует в трее, без сообщения",
                "Запускайте SafePaste сами, когда он нужен", actions.StartsWithWindows, actions.SetStartsWithWindows);
            SettingRow warning = Switch("Предупреждать о журнале буфера", "При запуске, если Windows хранит копии буфера",
                "Предупреждение выключено", actions.ClipboardWarning, actions.SetClipboardWarning);
            SettingRow smart = Switch("Интеллектуальный анализ", "Пароли в обычных фразах: RU EN UK DE FR ES PT IT PL TR",
                "Пароли ищутся только по явным признакам", actions.SmartSecrets, actions.SetSmartSecrets);
            SettingRow save = Switch("Запоминать метки", "Один номер для значения во всех вставках",
                "Новые метки не сохраняются", actions.Saving, actions.SetSaving);
            SettingRow hide = Switch("Скрывать запомненное", "Даже без подсказок вокруг",
                "Только то, что найдено в тексте", actions.Hiding, actions.SetHiding);
            SettingRow rules = SettingRow.Link("Правила и исключения", "Что SafePaste помнит между вставками", actions.ShowRules);
            bridgeRow = SettingRow.Link("Мост для агентов", DescribeBridge(), actions.ShowBridge);
            rows.Add(rules);
            rows.Add(bridgeRow);

            groups.Add(new SettingsGroup("Запуск", start, warning));
            groups.Add(new SettingsGroup("Проверка и метки", smart, save, hide));
            groups.Add(new SettingsGroup("Правила и агенты", rules, bridgeRow));

            Controls.Add(header);
            Controls.Add(closeButton);
            int order = 0;
            foreach (SettingsGroup group in groups)
            {
                group.TabIndex = order++;
                Controls.Add(group);
            }

            refresh = new Timer();
            refresh.Interval = 1000;
            refresh.Tick += delegate { bridgeRow.Description = DescribeBridge(); };
            ClientSize = new Size(Dpi.S(FormWidth), PreferredHeight());
        }

        private SettingRow Switch(string title, string onText, string offText, Func<bool> current, Action<bool> change)
        {
            SettingRow row = SettingRow.Switch(title, onText, offText, current, change);
            row.Changed += delegate
            {
                if (Changed != null)
                {
                    Changed(this, EventArgs.Empty);
                }
            };
            row.Failed += delegate(string message)
            {
                Alerts.Show(this, AlertKind.Error, "Настройка не сохранилась", message);
            };
            rows.Add(row);
            return row;
        }

        /// <summary>Строки сверху вниз: по ним ходят стрелки.</summary>
        internal List<SettingRow> Rows
        {
            get { return new List<SettingRow>(rows); }
        }

        private static int Pad
        {
            get { return Dpi.S(16); }
        }

        private static int HeaderHeight
        {
            get { return Dpi.S(40); }
        }

        private int PreferredHeight()
        {
            int height = Pad + HeaderHeight + Dpi.S(6) + Pad;
            foreach (SettingsGroup group in groups)
            {
                height += group.PreferredHeight + Dpi.S(8);
            }
            return height;
        }

        private string DescribeBridge()
        {
            try
            {
                string text = actions.DescribeBridge();
                return string.IsNullOrEmpty(text) ? "Окно моста" : text;
            }
            catch (Exception)
            {
                return "Состояние недоступно";
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (header == null)
            {
                return;
            }
            int width = ClientSize.Width;
            int pad = Pad;
            int close = Dpi.S(30);
            closeButton.SetBounds(width - pad - close, pad + (HeaderHeight - close) / 2, close, close);
            header.SetBounds(pad + Dpi.S(6), pad, Math.Max(0, closeButton.Left - pad - Dpi.S(16)), HeaderHeight);
            int y = pad + HeaderHeight + Dpi.S(6);
            foreach (SettingsGroup group in groups)
            {
                group.SetBounds(pad, y, width - 2 * pad, group.PreferredHeight);
                y += group.PreferredHeight + Dpi.S(8);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Поверх окна проверки, если открыто из него, иначе посередине экрана под курсором.
            Rectangle around = Owner != null ? Owner.Bounds : Screen.FromPoint(Cursor.Position).WorkingArea;
            Point center = new Point(around.X + around.Width / 2, around.Y + around.Height / 2);
            Rectangle area = Screen.FromPoint(center).WorkingArea;
            Rectangle target = new Rectangle(center.X - Width / 2, center.Y - Height / 2, Width, Math.Min(Height, area.Height));
            if (target.Right > area.Right) target.X = area.Right - target.Width;
            if (target.Bottom > area.Bottom) target.Y = area.Bottom - target.Height;
            if (target.X < area.Left) target.X = area.Left;
            if (target.Y < area.Top) target.Y = area.Top;
            Bounds = target;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            if (rows.Count > 0)
            {
                rows[0].Focus();
            }
            refresh.Start();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // Настройки могли поменять в другом окне или в диспетчере задач (автозапуск).
            ReloadAll();
        }

        internal void ReloadAll()
        {
            foreach (SettingRow row in rows)
            {
                row.Reload();
            }
            bridgeRow.Description = DescribeBridge();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refresh.Stop();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            if (keyData == Keys.Down || keyData == Keys.Up)
            {
                int index = rows.IndexOf(ActiveControl as SettingRow);
                int next = index < 0 ? 0 : Math.Max(0, Math.Min(rows.Count - 1, index + (keyData == Keys.Down ? 1 : -1)));
                rows[next].Focus();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Группа настроек: подпись сверху и строки на общей карточке с тонкими разделителями.</summary>
    internal sealed class SettingsGroup : GlassControl
    {
        private readonly string caption;
        private readonly List<SettingRow> rows = new List<SettingRow>();

        internal SettingsGroup(string caption, params SettingRow[] items)
        {
            this.caption = caption;
            // Подпись и поля карточки тянут окно, как пустой фон.
            MouseTransparent = true;
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = caption;
            int order = 0;
            foreach (SettingRow row in items)
            {
                row.TabIndex = order++;
                rows.Add(row);
                Controls.Add(row);
            }
        }

        private static int CaptionHeight
        {
            get { return Dpi.S(28); }
        }

        internal static int RowHeight
        {
            get { return Dpi.S(56); }
        }

        private static int Inset
        {
            get { return Dpi.S(4); }
        }

        internal int PreferredHeight
        {
            get { return CaptionHeight + rows.Count * RowHeight + 2 * Inset; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (rows == null)
            {
                return; // вызов из конструктора базового класса
            }
            int y = CaptionHeight + Inset;
            foreach (SettingRow row in rows)
            {
                row.SetBounds(Inset, y, Math.Max(0, Width - 2 * Inset), RowHeight);
                y += RowHeight;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            Theme.DrawText(graphics, caption, Theme.StrongFont, Theme.Secondary,
                new RectangleF(Dpi.F(8), 0, Width - Dpi.F(16), CaptionHeight - Dpi.F(4)), StringAlignment.Near, StringAlignment.Center, false);
            Theme.PaintCard(graphics, new RectangleF(0.5f, CaptionHeight + 0.5f, Width - 1f, Height - CaptionHeight - 1f), Dpi.F(14));
            using (Pen line = new Pen(Theme.Line, 1f))
            {
                for (int i = 1; i < rows.Count; i++)
                {
                    float y = rows[i].Top - 0.5f;
                    graphics.DrawLine(line, Dpi.F(18), y, Width - Dpi.F(18), y);
                }
            }
        }
    }

    /// <summary>
    /// Строка настроек: название, пояснение и справа выключатель или стрелка. Щелчок по любому месту
    /// строки переключает или открывает, с клавиатуры то же делают Пробел и Enter.
    /// </summary>
    internal sealed class SettingRow : GlassControl
    {
        private readonly bool toggle;
        private readonly string onText;
        private readonly string offText;
        private readonly Func<bool> current;
        private readonly Action<bool> change;
        private readonly Action open;
        private readonly Tween glow;
        private readonly Tween knob;
        private string description;
        private bool on;
        private bool pressed;

        /// <summary>Выключатель переключили.</summary>
        internal event EventHandler Changed;
        /// <summary>Настройка не сохранилась: текст ошибки.</summary>
        internal event Action<string> Failed;

        private SettingRow(string title, bool toggle, string onText, string offText, Func<bool> current, Action<bool> change, Action open)
        {
            this.toggle = toggle;
            this.onText = onText;
            this.offText = offText;
            this.current = current;
            this.change = change;
            this.open = open;
            glow = new Tween(this, 0f);
            knob = new Tween(this, 0f);
            Text = title;
            description = onText;
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleName = title;
            AccessibleRole = toggle ? AccessibleRole.CheckButton : AccessibleRole.PushButton;
            if (toggle)
            {
                Reload();
                knob.Snap(on ? 1f : 0f);
            }
            UpdateAccessible();
        }

        internal static SettingRow Switch(string title, string onText, string offText, Func<bool> current, Action<bool> change)
        {
            return new SettingRow(title, true, onText, offText, current, change, null);
        }

        internal static SettingRow Link(string title, string description, Action open)
        {
            return new SettingRow(title, false, description, description, null, null, open);
        }

        internal bool IsSwitch
        {
            get { return toggle; }
        }

        internal bool On
        {
            get { return on; }
        }

        /// <summary>Вторая строка: у выключателя она зависит от состояния.</summary>
        internal string Description
        {
            get { return toggle ? (on ? onText : offText) : description; }
            set
            {
                if (toggle || value == description)
                {
                    return;
                }
                description = value;
                UpdateAccessible();
                Invalidate();
            }
        }

        /// <summary>Перечитывает состояние: его могли поменять в другом окне.</summary>
        internal void Reload()
        {
            if (!toggle)
            {
                return;
            }
            try
            {
                on = current();
            }
            catch (Exception)
            {
                // Настройка не прочиталась: показываем последнее известное состояние.
            }
            knob.To(on ? 1f : 0f, 200);
            UpdateAccessible();
            Invalidate();
        }

        /// <summary>Щелчок, Пробел или Enter: переключает выключатель или открывает окно.</summary>
        internal void Perform()
        {
            if (!toggle)
            {
                if (open != null)
                {
                    open();
                }
                return;
            }
            try
            {
                change(!on);
            }
            catch (Exception failure)
            {
                if (Failed != null)
                {
                    Failed(failure.Message);
                }
            }
            Reload();
            if (Changed != null)
            {
                Changed(this, EventArgs.Empty);
            }
        }

        private void UpdateAccessible()
        {
            AccessibleDescription = toggle ? (on ? "Включено: " : "Выключено: ") + Description : Description;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Perform();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            glow.To(1f, 140);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pressed = false;
            glow.To(0f, 240);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
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
            if (e.KeyCode == Keys.Space)
            {
                pressed = true;
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                Perform();
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && pressed)
            {
                pressed = false;
                e.Handled = true;
                Perform();
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
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            float lit = glow.Value;
            RectangleF area = new RectangleF(Dpi.F(2), Dpi.F(2), Width - Dpi.F(4), Height - Dpi.F(4));
            Color fill = Theme.Fade(Theme.Fill, lit);
            if (pressed)
            {
                fill = Theme.FillPressed;
            }
            Theme.FillRound(graphics, area, Dpi.F(10), fill);
            if (Focused && ShowFocusCues)
            {
                using (GraphicsPath ring = Theme.Round(area, Dpi.F(10)))
                using (Pen pen = new Pen(Theme.Accent, Dpi.F(1.5f)))
                {
                    graphics.DrawPath(pen, ring);
                }
            }
            float inset = Dpi.F(14);
            float right;
            if (toggle)
            {
                RectangleF track = new RectangleF(Width - inset - Dpi.F(40), (Height - Dpi.F(22)) / 2f, Dpi.F(40), Dpi.F(22));
                DrawSwitch(graphics, track, knob.Value, lit);
                right = track.Left - Dpi.F(12);
            }
            else
            {
                // Стрелка чуть подаётся вправо при наведении: строка открывает другое окно.
                RectangleF arrow = new RectangleF(Width - inset - Dpi.F(16) + Dpi.F(3) * lit, 0, Dpi.F(16), Height);
                Theme.DrawGlyph(graphics, Glyphs.ChevronRight, Theme.SmallIconFont, Theme.Blend(Theme.Tertiary, Theme.Text, lit), arrow);
                right = arrow.Left - Dpi.F(12);
            }
            float width = Math.Max(0f, right - inset);
            float middle = Height / 2f;
            Theme.DrawText(graphics, Text, Theme.UiFont, Theme.Text, new RectangleF(inset, 0, width, middle + Dpi.F(1)),
                StringAlignment.Near, StringAlignment.Far, false);
            Theme.DrawText(graphics, Description, Theme.SmallFont, Theme.Secondary,
                new RectangleF(inset, middle + Dpi.F(3), width, middle - Dpi.F(3)), StringAlignment.Near, StringAlignment.Near, false);
        }

        /// <summary>Выключатель: position 0 выключен, 1 включён, между ними ползунок едет.</summary>
        internal static void DrawSwitch(Graphics graphics, RectangleF track, float position, float lit)
        {
            float radius = track.Height / 2f;
            Color rest = Theme.Blend(Theme.FillPressed, Theme.Fill, lit);
            Color onColor = Theme.Blend(Theme.Accent, Theme.AccentHover, lit);
            using (GraphicsPath path = Theme.Round(track, radius))
            {
                using (SolidBrush brush = new SolidBrush(Theme.Blend(rest, onColor, position)))
                {
                    graphics.FillPath(brush, path);
                }
                if (position < 1f)
                {
                    using (Pen pen = new Pen(Color.FromArgb((int)Math.Round((110 + 40 * lit) * (1f - position)), 255, 255, 255), Dpi.F(1.2f)))
                    {
                        graphics.DrawPath(pen, path);
                    }
                }
            }
            float knob = track.Height - Dpi.F(8);
            float left = track.Left + Dpi.F(4);
            float x = left + (track.Right - Dpi.F(4) - knob - left) * position;
            using (SolidBrush brush = new SolidBrush(Theme.Blend(Theme.Secondary, Theme.AccentInk, position)))
            {
                graphics.FillEllipse(brush, x, track.Top + Dpi.F(4), knob, knob);
            }
        }
    }
}
