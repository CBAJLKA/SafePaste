using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SafePaste.Ui
{
    internal sealed class AboutForm : GlassForm
    {
        private readonly WindowHeader header;
        private readonly GlassButton closeButton;
        private readonly ShortcutCard quick;
        private readonly ShortcutCard review;
        private readonly ShortcutCard decrypt;
        private readonly GlassCard guideCard;
        private readonly GuideView guide;
        private readonly GlassButton okButton;

        internal AboutForm()
        {
            Text = "SafePaste: как пользоваться";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Dpi.S(760), Dpi.S(660));
            MinimumSize = new Size(Dpi.S(640), Dpi.S(480));

            header = new WindowHeader("SafePaste", "Версия " + AppInfo.Version + ", работает без сети");
            closeButton = new GlassButton(Glyphs.Close, null, ButtonKind.Close, "Закрыть\nEsc");
            closeButton.TabStop = false;
            closeButton.Click += delegate { Close(); };
            quick = new ShortcutCard("Ctrl + Shift + V", "Быстрая вставка", "Скрывает найденное и вставляет");
            review = new ShortcutCard("Ctrl + Alt + Shift + V", "Проверка", "Показывает, что и чем заменится");
            decrypt = new ShortcutCard("Ctrl + Shift + C", "Расшифровка", "Реальные значения вместо меток");
            guide = new GuideView();
            guide.AddSection("Как это работает",
                "Скопируйте текст как обычно. В окне, куда его нужно вставить, нажмите одно из сочетаний. "
                + "SafePaste заменит адреса, ФИО, логины и пароли метками вроде [IP_1] и [PERSON_1] и вставит результат.");
            guide.AddSection("Окно проверки",
                "Найденное подсвечено прямо в тексте: оранжевым то, что заменится, серым то, что останется, "
                + "красным пароли и ключи. Наведите курсор на подсветку, чтобы увидеть замену. Правый клик по ней "
                + "позволяет оставить значение, закрепить номер или добавить его в исключения. Если что-то пропущено, "
                + "выделите это и нажмите правую кнопку мыши: «Отметить» скроет выделенное меткой вроде [HIDE_TEXT_1], "
                + "в «Пометить как» есть подходящие типы и отметка без сохранения.\n"
                + "Текст можно править прямо в окне, как в блокноте: после паузы в наборе он проверяется заново, "
                + "Ctrl+Z отменяет правку. Если буфер пуст, окно всё равно откроется: текст можно вставить или набрать. "
                + "Кнопка у правого края открывает результат, кнопка снизу открывает список всех находок, шестерёнка "
                + "в шапке открывает настройки. Ctrl+Enter вставляет результат, Ctrl+S копирует его, Esc закрывает окно.");
            guide.AddSection("Вкладки",
                "Пока окно проверки открыто, каждое Ctrl+C в любой программе открывает скопированный текст новой "
                + "вкладкой, у каждой свои находки и решения. Ctrl+Tab переключает вкладки, Ctrl+W закрывает. "
                + "Ctrl+Enter вставляет открытую вкладку, а окно с остальными остаётся в фоне: то же сочетание вернёт его. "
                + "Ctrl+Shift+Enter вставляет все вкладки одним текстом через пустую строку. В фоне, без открытого окна, "
                + "SafePaste копии не собирает, а скопированное из менеджеров паролей не открывает.");
            guide.AddSection("Расшифровка",
                "Выделите ответ с метками, например от ИИ, и нажмите Ctrl+Shift+C: SafePaste сам скопирует выделенное "
                + "и покажет его с реальными значениями вместо меток. Если ничего не выделено, берётся текст из буфера. "
                + "Метки, которые расшифровать нельзя, подсвечены оранжевым, при наведении видно почему. Расшифрованный "
                + "текст можно скопировать (Ctrl+S), в чужое окно SafePaste его не вставляет.");
            guide.AddSection("Режимы",
                "Строгий скрывает всё найденное, даже догадки и пути к файлам целиком. Обычный скрывает то, "
                + "в чём SafePaste уверен, а догадки и пути оставляет. Лёгкий скрывает только пароли, ключи "
                + "и значения из ваших правил. Режим переключается в шапке окна проверки (Ctrl+1, Ctrl+2, Ctrl+3) "
                + "или в меню значка и действует и на быструю вставку.");
            guide.AddSection("Запоминание меток",
                "После вставки SafePaste запоминает, какое значение стоит за какой меткой. В следующих вставках то же "
                + "значение получает тот же номер и скрывается даже без подсказок вокруг: в строгом режиме 30 дней, "
                + "в обычном 14, в лёгком запомненное без подсказок не ищется. Пароли, ключи и отмеченное без сохранения "
                + "не запоминаются. Запомненное видно в «Правилах и исключениях» на вкладке «Запомнено», там же его можно "
                + "забыть одной кнопкой, правила при этом останутся. Выключатели есть в настройках.");
            guide.AddSection("Что остаётся на компьютере",
                "Исходный текст не сохраняется и никуда не отправляется. Правила и запомненные метки лежат "
                + "в зашифрованных файлах, открыть их может только ваша учётная запись Windows. Пароли и ключи туда "
                + "не записываются. Метка, которая не встречалась 30 дней, забывается сама.");
            guide.AddSection("После вставки",
                "Примерно через полторы секунды SafePaste очищает буфер, если там всё ещё результат. "
                + "В окно, запущенное от имени администратора, вставить автоматически нельзя: результат останется "
                + "в буфере, вставьте его сами.");
            guide.AddSection("История буфера Windows",
                "Если включена история буфера (Win+V) или синхронизация между устройствами, исходный текст мог "
                + "попасть туда ещё до проверки. SafePaste эти настройки не меняет.");
            guideCard = new GlassCard(null, guide);
            okButton = new GlassButton(null, "Понятно", ButtonKind.Primary, null);
            okButton.Click += delegate { Close(); };

            Controls.Add(header);
            Controls.Add(quick);
            Controls.Add(review);
            Controls.Add(decrypt);
            Controls.Add(guideCard);
            Controls.Add(okButton);
            Controls.Add(closeButton);
            guideCard.TabIndex = 0;
            okButton.TabIndex = 1;
            CancelButton = okButton;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (okButton == null)
            {
                return;
            }
            int pad = Dpi.S(16);
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int close = Dpi.S(30);
            header.SetBounds(pad, pad, width - 2 * pad - close - Dpi.S(8), Dpi.S(44));
            closeButton.SetBounds(width - pad - close, pad + (Dpi.S(44) - close) / 2, close, close);
            int top = header.Bottom + Dpi.S(14);
            int gap = Dpi.S(12);
            int third = (width - 2 * pad - 2 * gap) / 3;
            quick.SetBounds(pad, top, third, Dpi.S(96));
            review.SetBounds(quick.Right + gap, top, third, Dpi.S(96));
            decrypt.SetBounds(review.Right + gap, top, width - pad - review.Right - gap, Dpi.S(96));
            int buttonHeight = Dpi.S(38);
            int buttonWidth = Math.Max(Dpi.S(120), okButton.PreferredWidth(buttonHeight));
            okButton.SetBounds(width - pad - buttonWidth, height - pad - buttonHeight, buttonWidth, buttonHeight);
            int guideTop = quick.Bottom + gap;
            guideCard.SetBounds(pad, guideTop, width - 2 * pad, Math.Max(0, okButton.Top - gap - guideTop));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            guide.Focus();
        }
    }

    /// <summary>Карточка с сочетанием клавиш.</summary>
    internal sealed class ShortcutCard : GlassControl
    {
        private readonly string keys;
        private readonly string title;
        private readonly string description;

        internal ShortcutCard(string keys, string title, string description)
        {
            this.keys = keys;
            this.title = title;
            this.description = description;
            MouseTransparent = true;
            AccessibleName = title + ", " + keys;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            Theme.PaintCard(graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Dpi.F(14));
            float left = Dpi.F(16);
            float width = Width - 2 * left;
            Theme.DrawText(graphics, keys, Theme.ShortcutFont, Theme.Accent, new RectangleF(left, Dpi.F(12), width, Dpi.F(26)));
            Theme.DrawText(graphics, title, Theme.StrongFont, Theme.Text, new RectangleF(left, Dpi.F(40), width, Dpi.F(22)));
            Theme.DrawText(graphics, description, Theme.SmallFont, Theme.Secondary, new RectangleF(left, Dpi.F(62), width, Dpi.F(22)));
        }
    }

    /// <summary>Текст справки: заголовки и абзацы с переносом, прокрутка колесом и клавишами.</summary>
    internal sealed class GuideView : GlassControl
    {
        private readonly List<KeyValuePair<string, string>> sections = new List<KeyValuePair<string, string>>();
        private int scroll;
        private int contentHeight;

        internal GuideView()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.Document;
            AccessibleName = "Как пользоваться SafePaste";
        }

        internal void AddSection(string title, string body)
        {
            sections.Add(new KeyValuePair<string, string>(title, body));
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, string> section in sections)
            {
                parts.Add(section.Key + ". " + section.Value);
            }
            AccessibleDescription = string.Join(" ", parts.ToArray());
            Invalidate();
        }

        private float Measure(Graphics graphics, bool paint)
        {
            float left = Dpi.F(18);
            float width = Math.Max(Dpi.F(80), Width - 2 * left - Dpi.F(8));
            float y = Dpi.F(16) - scroll;
            foreach (KeyValuePair<string, string> section in sections)
            {
                SizeF titleSize = Theme.Measure(section.Key, Theme.StrongFont, width);
                if (paint)
                {
                    Theme.DrawText(graphics, section.Key, Theme.StrongFont, Theme.Accent,
                        new RectangleF(left, y, width, titleSize.Height + Dpi.F(2)), StringAlignment.Near, StringAlignment.Near, true);
                }
                y += titleSize.Height + Dpi.F(4);
                foreach (string paragraph in section.Value.Split('\n'))
                {
                    SizeF size = Theme.Measure(paragraph, Theme.UiFont, width);
                    if (paint)
                    {
                        Theme.DrawText(graphics, paragraph, Theme.UiFont, Theme.Secondary,
                            new RectangleF(left, y, width + Dpi.F(2), size.Height + Dpi.F(2)), StringAlignment.Near, StringAlignment.Near, true);
                    }
                    y += size.Height + Dpi.F(6);
                }
                y += Dpi.F(12);
            }
            return y + scroll;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            contentHeight = (int)Math.Ceiling(Measure(graphics, true));
            int max = Math.Max(0, contentHeight - Height);
            if (max <= 0)
            {
                return;
            }
            float track = Height - Dpi.F(16);
            float thumb = Math.Max(Dpi.F(28), track * Height / contentHeight);
            float top = Dpi.F(8) + (track - thumb) * scroll / max;
            Theme.FillRound(graphics, new RectangleF(Width - Dpi.F(9), top, Dpi.F(5), thumb), Dpi.F(2.5f), Color.FromArgb(72, 255, 255, 255));
        }

        private void ScrollBy(int delta)
        {
            int max = Math.Max(0, contentHeight - Height);
            int next = Math.Max(0, Math.Min(max, scroll + delta));
            if (next != scroll)
            {
                scroll = next;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-e.Delta * Dpi.S(60) / 120);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: ScrollBy(-Dpi.S(30)); break;
                case Keys.Down: ScrollBy(Dpi.S(30)); break;
                case Keys.PageUp: ScrollBy(-Height + Dpi.S(40)); break;
                case Keys.PageDown: ScrollBy(Height - Dpi.S(40)); break;
                case Keys.Home: ScrollBy(-scroll); break;
                case Keys.End: ScrollBy(contentHeight); break;
                default:
                    base.OnKeyDown(e);
                    return;
            }
            e.Handled = true;
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }
    }
}
