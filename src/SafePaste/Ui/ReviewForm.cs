using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using SafePaste.Detecting;
using SafePaste.Storage;
using SafePaste.Bridge;

namespace SafePaste.Ui
{
    internal enum ReviewAction
    {
        Cancel,
        Paste
    }

    /// <summary>
    /// Окно проверки. По умолчанию это один блок с исходным текстом: найденное подсвечено прямо
    /// в тексте, замена видна при наведении. Результат раскрывается справа, список находок снизу.
    /// Исходный текст можно править: после паузы в наборе он проверяется заново.
    /// </summary>
    internal sealed partial class ReviewForm : GlassForm
    {
        private const int CompactWidth = 760;
        private const int CompactHeight = 540;
        private const int ListHeight = 250;

        private readonly SafePasteSettings settings;
        private SafePasteDatabase database;
        private readonly bool canPaste;
        private readonly string targetTitle;

        private string sourceText;
        private List<Detection> detections;
        private TextMap map;
        private TextMap resultMap;
        private readonly List<DetectionGroup> groups = new List<DetectionGroup>();
        private readonly Dictionary<Detection, DetectionGroup> groupOf = new Dictionary<Detection, DetectionGroup>();
        private List<Detection> enabledDetections = new List<Detection>();
        private int[] segmentSource = new int[0];
        private int[] segmentSourceEnd = new int[0];
        private int[] segmentResult = new int[0];
        private int[] segmentResultEnd = new int[0];

        private WindowHeader header;
        private Segmented modeSwitch;
        private GlassButton settingsButton;
        private GlassButton closeButton;
        private BlockHeader sourceHeader;
        private TextView preview;
        private Legend legend;
        private GlassButton pasteInButton;
        private GlassCard sourceCard;
        private Spine resultSpine;
        private BlockHeader resultHeader;
        private TextView resultPreview;
        private GlassCard resultCard;
        private Spine listSpine;
        private BlockHeader listHeader;
        private Segmented filter;
        private GlassList list;
        private GlassCard listCard;
        private GlassLabel statusLabel;
        private GlassButton copyButton;
        private GlassButton pasteButton;
        private readonly Timer statusTimer = new Timer();
        private readonly Timer scanTimer = new Timer();

        private bool showResult;
        private bool showList;
        private bool syncing;
        private string defaultStatus;
        private ControlMode mode;

        // Правка текста. После паузы в наборе текст проверяется заново; до этого находки только сдвигаются.
        // Эти поля принадлежат открытой вкладке: при переключении вкладок они меняются целиком.
        private bool scanPending;
        private EditHistory history = new EditHistory();
        // Решения пользователя переживают повторную проверку: значение, которое оставили, снова не скроется.
        private Dictionary<string, bool> choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        // Отмеченное вручную в этот раз, в том числе секреты, которые в базу не пишутся.
        private List<Detection> manualMarks = new List<Detection>();
        // Когда правила, запомненные метки и настройки читались с диска: их могут поменять в другом окне.
        private DateTime rulesStamp;
        private DateTime labelsStamp;
        private DateTime settingsStamp;

        internal ReviewAction Action = ReviewAction.Cancel;
        /// <summary>Шестерёнка в шапке: открывает окно настроек, то же, что пункт «Настройки» в трее.</summary>
        internal Action<Form> ShowSettings = delegate { };

        internal ReviewForm(string text, List<Detection> found, SafePasteDatabase database,
            SafePasteSettings settings, string targetTitle, bool canPaste)
        {
            this.settings = settings;
            this.database = database;
            this.canPaste = canPaste;
            this.targetTitle = targetTitle;
            sourceText = text ?? string.Empty;
            detections = found ?? new List<Detection>();
            map = new TextMap(sourceText);
            showResult = settings.ReviewShowResult;
            showList = settings.ReviewShowFindings;
            mode = settings.Mode;
            // Галочки всегда по текущему режиму, кто бы ни подготовил находки.
            foreach (Detection detection in detections)
            {
                detection.Enabled = ControlModes.HiddenInReview(detection, mode);
            }
            statusTimer.Interval = 7000;
            statusTimer.Tick += delegate
            {
                statusTimer.Stop();
                statusLabel.Text = defaultStatus;
            };
            scanTimer.Tick += delegate { FlushScan(); };

            RememberStamps();
            BuildLayout();
            BuildTabs();
            BuildImageLayout();
            RefreshAll(null);
        }

        /// <summary>
        /// Результат, как он уйдёт из окна. Если текст правили и ещё не проверили, он проверяется сейчас:
        /// по старым находкам новое значение попало бы в результат открытым. На диск ничего не пишется.
        /// </summary>
        internal ReplacementResult BuildResult()
        {
            CheckEdited();
            return LabelMemory.Preview(sourceText, detections, database);
        }

        /// <summary>
        /// Результат, который уходит из окна: вставка или копирование. Если в настройках включено
        /// запоминание, скрытые значения запоминаются вместе с номерами.
        /// </summary>
        internal ReplacementResult BuildPasteResult()
        {
            CheckEdited();
            // Настройку могли поменять в трее, пока окно открыто, поэтому она читается заново.
            ReplacementResult result = LabelMemory.Apply(sourceText, detections, database, SafePasteSettings.Load().SaveLabels);
            RememberStamps();
            return result;
        }

        private void CheckEdited()
        {
            if (!FlushScan())
            {
                throw new InvalidOperationException("Изменённый текст не удалось проверить. Вставка отменена.");
            }
        }

        /// <summary>Запомненное для текущего режима: срок, в который оно скрывается без подсказок, зависит от режима.</summary>
        private void LoadRemembered(SafePasteDatabase target)
        {
            LabelMemory.Load(target, SafePasteSettings.Load(), mode);
        }

        /// <summary>Сообщение в строке состояния сразу после открытия окна.</summary>
        internal void ShowNotice(string message)
        {
            ShowStatus(message);
        }

        // ---------------------------------------------------------------- разметка

        private static int Pad
        {
            get { return Dpi.S(12); }
        }

        private static int Gap
        {
            get { return Dpi.S(10); }
        }

        private static int SpineSize
        {
            get { return Dpi.S(18); }
        }

        private static int HeaderHeight
        {
            get { return Dpi.S(36); }
        }

        private static int FooterHeight
        {
            get { return Dpi.S(52); }
        }

        private void BuildLayout()
        {
            Text = "SafePaste";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(Dpi.S(540), Dpi.S(380));

            header = new WindowHeader("Проверка перед вставкой", null);
            modeSwitch = new Segmented(new string[] { "Строгий", "Обычный", "Лёгкий" });
            modeSwitch.AccessibleName = "Режим проверки";
            modeSwitch.Tips = new string[]
            {
                "Строгий: скрывать всё найденное, даже догадки и пути\nCtrl+1",
                "Обычный: скрывать то, в чём SafePaste уверен\nCtrl+2",
                "Лёгкий: только пароли, ключи и ваши правила\nCtrl+3"
            };
            modeSwitch.SelectedIndex = (int)mode;
            modeSwitch.SelectedChanged += delegate { ApplyMode((ControlMode)modeSwitch.SelectedIndex); };
            closeButton = new GlassButton(Glyphs.Close, null, ButtonKind.Close, "Закрыть\nEsc");
            closeButton.TabStop = false;
            closeButton.Click += delegate { Close(); };
            settingsButton = new GlassButton(Glyphs.Settings, null, ButtonKind.Plain, "Настройки");
            settingsButton.TabStop = false;
            settingsButton.GlyphFont = Theme.MediumIconFont;
            settingsButton.Click += delegate
            {
                GlassTip.HideAll();
                ShowSettings(this);
            };

            preview = new TextView();
            preview.ReadOnly = false;
            preview.AccessibleName = "Исходный текст";
            preview.Placeholder = "Вставьте текст (Ctrl+V) или начните печатать";
            preview.AllowDrop = true;
            preview.HoverChanged += OnPreviewHover;
            preview.ContextRequested += OnPreviewContext;
            preview.Scrolled += delegate { SyncScroll(preview); };
            preview.TextDropped += delegate(string value) { LoadText(value, "Текст перенесён в окно."); };
            preview.EditRequested += OnPreviewEdit;
            preview.HistoryRequested += delegate(bool redo) { StepHistory(redo); };
            sourceHeader = new BlockHeader("Исходный текст", Theme.Text, "Поиск по тексту\nCtrl+F");
            sourceHeader.SearchChanged += delegate
            {
                preview.Search(sourceHeader.Query);
                UpdateCounter(sourceHeader, preview);
            };
            sourceHeader.SearchStep += delegate(bool backwards)
            {
                preview.StepMatch(backwards ? -1 : 1);
                UpdateCounter(sourceHeader, preview);
            };
            legend = new Legend();
            pasteInButton = new GlassButton(Glyphs.Sync, null, ButtonKind.Plain, "Взять текст из буфера\nЗаменит весь текст");
            pasteInButton.Click += delegate { LoadFromClipboard(); };
            sourceHeader.AddTool(legend, Dpi.S(10), true);
            sourceHeader.AddTool(pasteInButton, 0);
            sourceCard = new GlassCard(sourceHeader, preview);

            resultSpine = new Spine(true, "Показать результат справа", "Скрыть результат");
            resultSpine.Click += delegate { ToggleResult(); };
            resultPreview = new TextView();
            resultPreview.AccessibleName = "Результат";
            resultPreview.HoverChanged += OnResultHover;
            resultPreview.ContextRequested += OnResultContext;
            resultPreview.Scrolled += delegate { SyncScroll(resultPreview); };
            resultHeader = new BlockHeader("Результат", Theme.Accent, "Поиск по результату\nCtrl+F");
            resultHeader.SearchChanged += delegate
            {
                resultPreview.Search(resultHeader.Query);
                UpdateCounter(resultHeader, resultPreview);
            };
            resultHeader.SearchStep += delegate(bool backwards)
            {
                resultPreview.StepMatch(backwards ? -1 : 1);
                UpdateCounter(resultHeader, resultPreview);
            };
            resultCard = new GlassCard(resultHeader, resultPreview);

            listSpine = new Spine(false, "Показать все находки списком", "Свернуть список");
            listSpine.Click += delegate { ToggleList(); };
            list = new GlassList();
            list.AccessibleName = "Находки";
            list.AddColumn(string.Empty, 36, false);
            list.AddColumn("Тип", 118, false);
            list.AddColumn("Значение", 0, false);
            list.AddColumn("Раз", 44, true);
            list.AddColumn("Замена", 170, false);
            list.Painter = PaintCell;
            list.TipProvider = ListTip;
            list.ToggleRequested += delegate { ToggleSelected(); };
            list.ItemActivated += delegate { ToggleSelected(); };
            list.ContextRequested += OnListContext;
            list.SelectionChanged += delegate { RevealSelected(); };
            filter = new Segmented(new string[] { "Все", "Скроются", "Останутся" });
            filter.AccessibleName = "Какие находки показывать";
            filter.SelectedChanged += delegate { RebuildList(); };
            listHeader = new BlockHeader("Находки", Theme.Text, "Поиск по находкам\nCtrl+F");
            listHeader.SearchChanged += delegate { RebuildList(); };
            listHeader.SearchStep += delegate { list.Focus(); };
            listHeader.AddTool(filter, filter.PreferredWidth);
            listCard = new GlassCard(listHeader, list);

            statusLabel = new GlassLabel(string.Empty, Theme.SmallFont, Theme.Secondary);
            copyButton = new GlassButton(Glyphs.Paste, null, canPaste ? ButtonKind.Glass : ButtonKind.Primary,
                "Копировать результат\nCtrl+S");
            copyButton.Click += delegate { CopyToClipboard(); };
            pasteButton = new GlassButton(Glyphs.Return, null, ButtonKind.Primary,
                "Вставить в «" + TargetName() + "»\nCtrl+Enter");
            pasteButton.Visible = canPaste;
            pasteButton.Enabled = canPaste;
            pasteButton.Click += delegate { PasteCurrent(); };
            defaultStatus = canPaste
                ? "Вставка в «" + TargetName() + "»"
                : "Окно для вставки не найдено. Скопируйте результат и вставьте его сами.";
            statusLabel.Text = defaultStatus;

            Controls.Add(header);
            Controls.Add(modeSwitch);
            Controls.Add(sourceCard);
            Controls.Add(resultSpine);
            Controls.Add(resultCard);
            Controls.Add(listSpine);
            Controls.Add(listCard);
            Controls.Add(statusLabel);
            Controls.Add(copyButton);
            Controls.Add(pasteButton);
            Controls.Add(settingsButton);
            Controls.Add(closeButton);
            sourceCard.TabIndex = 0;
            resultSpine.TabIndex = 1;
            resultCard.TabIndex = 2;
            listSpine.TabIndex = 3;
            listCard.TabIndex = 4;
            pasteButton.TabIndex = 5;
            copyButton.TabIndex = 6;
            modeSwitch.TabIndex = 7;

            resultCard.Visible = showResult;
            resultSpine.Expanded = showResult;
            listCard.Visible = showList;
            listSpine.Expanded = showList;
            ClientSize = InitialSize();
        }

        private string TargetName()
        {
            if (string.IsNullOrEmpty(targetTitle))
            {
                return "предыдущее окно";
            }
            return targetTitle.Length <= 48 ? targetTitle : targetTitle.Substring(0, 47) + "…";
        }

        private Size InitialSize()
        {
            int width = Dpi.S(CompactWidth);
            int height = Dpi.S(CompactHeight);
            if (showResult)
            {
                width += width - 2 * Pad - SpineSize - Gap;
            }
            if (showList)
            {
                height += Dpi.S(ListHeight) + Gap;
            }
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            return new Size(Math.Min(width, area.Width), Math.Min(height, area.Height));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (preview == null)
            {
                return;
            }
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int pad = Pad;
            int gap = Gap;
            int spine = SpineSize;
            int close = Dpi.S(30);
            int gear = Dpi.S(38);
            closeButton.SetBounds(width - pad - close, pad + (HeaderHeight - close) / 2, close, close);
            settingsButton.SetBounds(closeButton.Left - Dpi.S(4) - gear, pad + (HeaderHeight - gear) / 2, gear, gear);
            int switchWidth = modeSwitch.PreferredWidth;
            int switchHeight = Dpi.S(30);
            modeSwitch.SetBounds(settingsButton.Left - Dpi.S(10) - switchWidth, pad + (HeaderHeight - switchHeight) / 2,
                switchWidth, switchHeight);
            int headerLeft = pad + Dpi.S(6);
            int top = pad + HeaderHeight + Dpi.S(8);
            if (tabStrip != null && documents.Count > 1)
            {
                int titleWidth = Dpi.S(22) + Dpi.S(10)
                    + TextRenderer.MeasureText(header.Title, Theme.StrongFont).Width + Dpi.S(8);
                header.SetBounds(headerLeft, pad, titleWidth, HeaderHeight);
                int tabLeft = header.Right + Dpi.S(8);
                int tabRight = modeSwitch.Left - Dpi.S(12);
                if (tabRight - tabLeft < Dpi.S(120))
                {
                    // В узком окне вкладки остаются в шапке, а режим переходит под неё.
                    tabRight = settingsButton.Left - Dpi.S(10);
                    modeSwitch.SetBounds(width - pad - switchWidth, top, switchWidth, switchHeight);
                    top += switchHeight + Dpi.S(8);
                }
                int tabHeight = Dpi.S(30);
                tabStrip.SetBounds(tabLeft, pad + (HeaderHeight - tabHeight) / 2,
                    Math.Max(0, tabRight - tabLeft), tabHeight);
            }
            else
            {
                int headerWidth = Math.Max(0, modeSwitch.Left - Dpi.S(12) - headerLeft);
                header.SetBounds(headerLeft, pad, headerWidth, HeaderHeight);
            }

            int footerTop = height - pad - FooterHeight;
            int right = width - pad;
            // Постоянные кнопки стоят на своих местах: «Вставить» справа, «Копировать» рядом с ней. Кнопки,
            // которые есть не всегда («Вставить все», «Вставить текстом» у картинки), добавляются левее и
            // не сдвигают постоянные. В расшифровке и без окна для вставки главная кнопка копирует.
            bool pasting = canPaste && !decrypt;
            if (pasting)
            {
                int size = Dpi.S(44);
                pasteButton.SetBounds(right - size, footerTop + (FooterHeight - size) / 2, size, size);
                right = pasteButton.Left - Dpi.S(10);
            }
            int copySize = pasting ? Dpi.S(38) : Dpi.S(44);
            copyButton.SetBounds(right - copySize, footerTop + (FooterHeight - copySize) / 2, copySize, copySize);
            right = copyButton.Left - Dpi.S(10);
            if (pasteAllShown)
            {
                int size = Dpi.S(38);
                pasteAllButton.SetBounds(right - size, footerTop + (FooterHeight - size) / 2, size, size);
                right = pasteAllButton.Left - Dpi.S(10);
            }
            right = LayoutTextPaste(right, footerTop) - Dpi.S(2);
            statusLabel.SetBounds(pad + Dpi.S(8), footerTop, Math.Max(0, right - pad - Dpi.S(8)), FooterHeight);

            int bottom = footerTop - Dpi.S(4);
            int inner = width - 2 * pad;
            // Результат и список нужны только проверке; в расшифровке исходный блок занимает всё.
            if (decrypt)
            {
                sourceCard.SetBounds(pad, top, inner, Math.Max(0, bottom - top));
                return;
            }
            int listBlock = 0;
            if (showList)
            {
                int room = bottom - top - spine - gap * 2 - Dpi.S(150);
                int listSize = Math.Max(Dpi.S(130), Math.Min(Dpi.S(ListHeight), room));
                listCard.SetBounds(pad, bottom - listSize, inner, listSize);
                listBlock = listSize + gap;
            }
            int spineTop = bottom - listBlock - spine;
            listSpine.SetBounds(pad, spineTop, inner, spine);
            int rowHeight = Math.Max(0, spineTop - gap - top);
            if (showResult)
            {
                int each = Math.Max(0, (inner - spine - 2 * gap) / 2);
                sourceCard.SetBounds(pad, top, each, rowHeight);
                resultSpine.SetBounds(sourceCard.Right + gap, top, spine, rowHeight);
                int resultLeft = resultSpine.Right + gap;
                resultCard.SetBounds(resultLeft, top, Math.Max(0, width - pad - resultLeft), rowHeight);
            }
            else
            {
                sourceCard.SetBounds(pad, top, Math.Max(0, inner - spine - gap), rowHeight);
                resultSpine.SetBounds(sourceCard.Right + gap, top, spine, rowHeight);
            }
            LayoutImageCards();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            TopMost = true;
            TopMost = false;
            Activate();
            if (image != null && !image.ShowText)
            {
                imageView.Focus();
            }
            else
            {
                preview.Focus();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            statusTimer.Dispose();
            scanTimer.Dispose();
            ReleaseImages();
            base.OnFormClosed(e);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            ReloadIfChanged();
        }

        // ---------------------------------------------------------------- настройки

        /// <summary>
        /// В окне настроек переключили выключатель. Окно проверки берёт свежие настройки сразу, не дожидаясь,
        /// пока его снова активируют: иначе текст за окном настроек проверялся бы по старым.
        /// </summary>
        internal void SettingsChanged()
        {
            ReloadIfChanged();
        }

        private static DateTime Stamp(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private void RememberStamps()
        {
            rulesStamp = Stamp(Paths.DatabaseFile);
            labelsStamp = Stamp(Paths.LabelsFile);
            settingsStamp = Stamp(Paths.SettingsFile);
        }

        /// <summary>
        /// Правила, запомненные метки или выключатели поменяли в другом окне: в «Правилах и исключениях»,
        /// в меню трея, а метки пишет и мост. Окно берёт свежие: иначе следующее сохранение правила вернуло бы
        /// удалённое, а забытая метка продолжала бы скрываться. Текст проверяется заново, только если набор
        /// запомненного и правда изменился: мост часто пишет в файл одни даты.
        /// </summary>
        private void ReloadIfChanged()
        {
            bool rules = Stamp(Paths.DatabaseFile) != rulesStamp;
            bool memory = Stamp(Paths.LabelsFile) != labelsStamp || Stamp(Paths.SettingsFile) != settingsStamp;
            if (!rules && !memory)
            {
                return;
            }
            SafePasteDatabase fresh = database;
            string previous = decrypt ? decoded.Text : null;
            bool smartChanged = false;
            try
            {
                string before = RememberedKey(database);
                SafePasteSettings latest = SafePasteSettings.Load();
                smartChanged = latest.SmartSecrets != settings.SmartSecrets;
                settings.SmartSecrets = latest.SmartSecrets;
                if (rules)
                {
                    fresh = SafePasteDatabase.Load();
                }
                LoadRemembered(fresh);
                RememberStamps();
                if (!rules && !smartChanged && RememberedKey(fresh) == before && !decrypt)
                {
                    return;
                }
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return;
            }
            database = fresh;
            MarkOthersStale(false);
            if (decrypt)
            {
                // Расшифровка берёт метки с диска: сообщение нужно, только если текст правда изменился.
                Redecode(null);
                if (decoded.Text != previous)
                {
                    ShowStatus("Метки изменились, текст расшифрован заново.");
                }
                return;
            }
            Rescan(rules ? "Правила изменились, текст проверен заново."
                : smartChanged ? "Интеллектуальный поиск изменён, текст проверен заново."
                : "Запомненные метки изменились, текст проверен заново.");
        }

        private static string RememberedKey(SafePasteDatabase source)
        {
            List<string> keys = new List<string>(source.Remembered.Count);
            foreach (LearnedValue value in source.Remembered)
            {
                keys.Add(GroupKey(value.Type, value.Value));
            }
            keys.Sort(StringComparer.Ordinal);
            return string.Join("\u0002", keys.ToArray());
        }

        /// <summary>Режим или настройка запомненного поменялись: список запомненного берётся заново.</summary>
        private void RefreshRemembered(string message)
        {
            try
            {
                LoadRemembered(database);
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return;
            }
            Rescan(message);
        }

        /// <summary>Сохраняет правила и запоминает, что файл изменило само окно.</summary>
        private void SaveDatabase()
        {
            database.Save();
            rulesStamp = Stamp(Paths.DatabaseFile);
        }

        // ---------------------------------------------------------------- панели справа и снизу

        private void ToggleResult()
        {
            GlassTip.HideAll();
            showResult = !showResult;
            if (!Zoomed)
            {
                Rectangle bounds = Bounds;
                bounds.Width += showResult ? sourceCard.Width + Gap : -(resultCard.Width + Gap);
                MoveWithinScreen(bounds);
            }
            resultCard.Visible = showResult;
            resultSpine.Expanded = showResult;
            if (image != null)
            {
                ApplyImageMode();
                RefreshImagePreview();
            }
            PerformLayout();
            SyncScroll(preview);
            SaveSettings();
        }

        private void ToggleList()
        {
            GlassTip.HideAll();
            showList = !showList;
            if (!Zoomed)
            {
                Rectangle bounds = Bounds;
                bounds.Height += showList ? Dpi.S(ListHeight) + Gap : -(listCard.Height + Gap);
                MoveWithinScreen(bounds);
            }
            listCard.Visible = showList;
            listSpine.Expanded = showList;
            UpdateListSpine();
            PerformLayout();
            SaveSettings();
        }

        /// <summary>
        /// Раскладка окна и режим запоминаются до следующего раза. Остальные настройки берутся свежими
        /// с диска: пока окно открыто, их могли поменять в трее.
        /// </summary>
        private void SaveSettings()
        {
            settings.ReviewShowResult = showResult;
            settings.ReviewShowFindings = showList;
            settings.Mode = mode;
            try
            {
                SafePasteSettings.Update(delegate(SafePasteSettings latest)
                {
                    latest.ReviewShowResult = showResult;
                    latest.ReviewShowFindings = showList;
                    latest.Mode = mode;
                });
                settingsStamp = Stamp(Paths.SettingsFile);
            }
            catch (Exception)
            {
                // Раскладка окна не стоит сообщения об ошибке.
            }
        }

        // ---------------------------------------------------------------- режим

        /// <summary>
        /// Переключает режим: текст проверяется заново, галочки у всех находок ставятся по его правилам.
        /// Заново, а не только галочки, потому что от режима зависит и сама находка: в строгом режиме
        /// путь скрывается целиком, в обычном только узел и папка внутри него.
        /// Режим общий, поэтому действует и на быструю вставку.
        /// </summary>
        private void ApplyMode(ControlMode next)
        {
            GlassTip.HideAll();
            mode = next;
            if (modeSwitch.SelectedIndex != (int)next)
            {
                modeSwitch.SelectedIndex = (int)next;
                return; // обработчик переключателя вызовет этот метод ещё раз
            }
            choices.Clear();
            MarkOthersStale(true);
            SaveSettings();
            // Срок, в который запомненное скрывается без подсказок, у каждого режима свой.
            RefreshRemembered(ModeMessage(next));
        }

        private static string ModeMessage(ControlMode value)
        {
            switch (value)
            {
                case ControlMode.Strict:
                    return "Строгий режим: скрыто всё найденное, даже догадки и пути.";
                case ControlMode.Light:
                    return "Лёгкий режим: скрыты только пароли, ключи и ваши правила. Запомненное без подсказок не ищется.";
                default:
                    return "Обычный режим: догадки и пути остаются в тексте.";
            }
        }

        private void UpdateListSpine()
        {
            listSpine.Caption = showList
                ? "Свернуть список"
                : groups.Count == 0 ? "Список находок" : "Список находок: " + groups.Count.ToString(CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- обновление

        private void RefreshAll(string message)
        {
            if (decrypt)
            {
                RefreshDecrypt(message);
                return;
            }
            if (image != null)
            {
                RefreshImage(message);
                return;
            }
            detections = OverlapResolver.Resolve(detections);
            ReplacementResult result = LabelMemory.Preview(sourceText, detections, database);
            resultMap = new TextMap(result.Text);
            BuildSegments();
            BuildGroups();
            string display = MaskedDisplay();
            if (display != preview.Content)
            {
                preview.SetContent(display, BuildSourceMarks(), true);
            }
            else
            {
                preview.SetMarks(BuildSourceMarks());
            }
            resultPreview.SetContent(resultMap.Display, BuildResultMarks(), true);
            UpdateCounter(sourceHeader, preview);
            UpdateCounter(resultHeader, resultPreview);
            RebuildList();
            UpdateSummary();
            UpdateListSpine();
            SyncScroll(preview);
            UpdateTabs();
            if (message != null)
            {
                ShowStatus(message);
            }
        }

        /// <summary>Секреты не показываются даже в окне проверки: вместо них точки той же длины.</summary>
        private string MaskedDisplay()
        {
            string display = map.Display;
            char[] masked = null;
            foreach (Detection detection in detections)
            {
                if (!detection.Locked)
                {
                    continue;
                }
                if (masked == null)
                {
                    masked = display.ToCharArray();
                }
                int end = map.ToDisplay(detection.End);
                for (int index = map.ToDisplay(detection.Start); index < end && index < masked.Length; index++)
                {
                    if (masked[index] != '\n')
                    {
                        masked[index] = '•';
                    }
                }
            }
            return masked == null ? display : new string(masked);
        }

        private List<TextMark> BuildSourceMarks()
        {
            List<TextMark> marks = new List<TextMark>(detections.Count);
            foreach (Detection detection in detections)
            {
                int start = map.ToDisplay(detection.Start);
                int end = map.ToDisplay(detection.End);
                if (end <= start)
                {
                    continue;
                }
                MarkKind kind = detection.Locked ? MarkKind.Secret : detection.Enabled ? MarkKind.Hidden : MarkKind.Kept;
                marks.Add(new TextMark(start, end - start, kind, detection));
            }
            return marks;
        }

        private List<TextMark> BuildResultMarks()
        {
            List<TextMark> marks = new List<TextMark>();
            for (int i = 0; i < segmentResult.Length; i++)
            {
                int start = resultMap.ToDisplay(segmentResult[i]);
                int end = resultMap.ToDisplay(segmentResultEnd[i]);
                if (end > start)
                {
                    marks.Add(new TextMark(start, end - start, MarkKind.Placeholder, enabledDetections[i]));
                }
            }
            return marks;
        }

        /// <summary>Соответствие позиций исходника и результата для синхронной прокрутки.</summary>
        private void BuildSegments()
        {
            enabledDetections = new List<Detection>();
            List<int> sourceStarts = new List<int>();
            List<int> sourceEnds = new List<int>();
            List<int> resultStarts = new List<int>();
            List<int> resultEnds = new List<int>();
            int offset = 0;
            foreach (Detection detection in detections)
            {
                if (!detection.Enabled)
                {
                    continue;
                }
                enabledDetections.Add(detection);
                sourceStarts.Add(detection.Start);
                sourceEnds.Add(detection.End);
                resultStarts.Add(detection.Start + offset);
                resultEnds.Add(detection.Start + offset + detection.Placeholder.Length);
                offset += detection.Placeholder.Length - detection.Length;
            }
            segmentSource = sourceStarts.ToArray();
            segmentSourceEnd = sourceEnds.ToArray();
            segmentResult = resultStarts.ToArray();
            segmentResultEnd = resultEnds.ToArray();
        }

        private int SourceToResult(int position)
        {
            int index = LastAtOrBefore(segmentSource, position);
            if (index < 0)
            {
                return position;
            }
            if (position < segmentSourceEnd[index])
            {
                return segmentResult[index];
            }
            return segmentResultEnd[index] + position - segmentSourceEnd[index];
        }

        private int ResultToSource(int position)
        {
            int index = LastAtOrBefore(segmentResult, position);
            if (index < 0)
            {
                return position;
            }
            if (position < segmentResultEnd[index])
            {
                return segmentSource[index];
            }
            return segmentSourceEnd[index] + position - segmentResultEnd[index];
        }

        private static int LastAtOrBefore(int[] values, int position)
        {
            int found = Array.BinarySearch(values, position);
            return found >= 0 ? found : ~found - 1;
        }

        /// <summary>Прокрутка одного блока ведёт за собой другой: видны одни и те же строки.</summary>
        private void SyncScroll(TextView from)
        {
            if (syncing || !showResult || resultMap == null || decrypt)
            {
                return;
            }
            syncing = true;
            try
            {
                if (from == preview)
                {
                    int target = resultMap.ToDisplay(SourceToResult(map.ToOriginal(preview.TopIndex)));
                    resultPreview.ScrollToIndex(target, preview.TopFraction);
                }
                else
                {
                    int target = map.ToDisplay(ResultToSource(resultMap.ToOriginal(resultPreview.TopIndex)));
                    preview.ScrollToIndex(target, resultPreview.TopFraction);
                }
            }
            finally
            {
                syncing = false;
            }
        }

        private void BuildGroups()
        {
            groups.Clear();
            groupOf.Clear();
            Dictionary<string, DetectionGroup> index = new Dictionary<string, DetectionGroup>(StringComparer.Ordinal);
            foreach (Detection detection in detections)
            {
                string key = GroupKey(detection.Type, detection.Value);
                DetectionGroup group;
                if (!index.TryGetValue(key, out group))
                {
                    group = new DetectionGroup(detection.Type, detection.Value);
                    index.Add(key, group);
                    groups.Add(group);
                }
                group.Items.Add(detection);
                groupOf[detection] = group;
            }
            foreach (DetectionGroup group in groups)
            {
                group.Reserved = database.GetReservedIndex(group.Type, group.Value) > 0;
            }
        }

        private static string GroupKey(string type, string value)
        {
            return type + "\u0001" + value.ToLowerInvariant();
        }

        private DetectionGroup GroupOf(Detection detection)
        {
            DetectionGroup group;
            return detection != null && groupOf.TryGetValue(detection, out group) ? group : null;
        }

        private void UpdateSummary()
        {
            int hidden = 0;
            int kept = 0;
            int secrets = 0;
            foreach (Detection detection in detections)
            {
                if (detection.Locked)
                {
                    secrets++;
                }
                else if (detection.Enabled)
                {
                    hidden++;
                }
                else
                {
                    kept++;
                }
            }
            List<KeyValuePair<Color, string>> items = new List<KeyValuePair<Color, string>>();
            if (detections.Count == 0)
            {
                items.Add(new KeyValuePair<Color, string>(Color.Empty, "Ничего не найдено"));
            }
            if (hidden > 0)
            {
                items.Add(new KeyValuePair<Color, string>(Theme.Warm, "Скрыто: " + hidden.ToString(CultureInfo.InvariantCulture)));
            }
            if (secrets > 0)
            {
                items.Add(new KeyValuePair<Color, string>(Theme.Danger, "Секреты: " + secrets.ToString(CultureInfo.InvariantCulture)));
            }
            if (kept > 0)
            {
                items.Add(new KeyValuePair<Color, string>(Theme.Secondary, "Оставлено: " + kept.ToString(CultureInfo.InvariantCulture)));
            }
            legend.SetItems(items);
            sourceHeader.SetToolWidth(legend, legend.PreferredWidth);
        }

        private void RebuildList()
        {
            HashSet<string> keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in list.SelectedItems)
            {
                DetectionGroup chosen = (DetectionGroup)item;
                keep.Add(GroupKey(chosen.Type, chosen.Value));
            }
            string query = listHeader.Query.Trim();
            List<object> visible = new List<object>();
            foreach (DetectionGroup group in groups)
            {
                if (filter.SelectedIndex == 1 && !group.Enabled)
                {
                    continue;
                }
                if (filter.SelectedIndex == 2 && group.AllEnabled)
                {
                    continue;
                }
                if (query.Length > 0 && !Matches(group, query))
                {
                    continue;
                }
                visible.Add(group);
            }
            list.SetItems(visible, delegate(object item)
            {
                DetectionGroup group = (DetectionGroup)item;
                return keep.Contains(GroupKey(group.Type, group.Value));
            });
            if (groups.Count == 0)
            {
                list.EmptyText = "Ничего не найдено. Если что-то пропущено, выделите это в тексте и нажмите правую кнопку мыши.";
            }
            else if (query.Length > 0)
            {
                list.EmptyText = "Нет совпадений с «" + query + "»";
            }
            else
            {
                list.EmptyText = filter.SelectedIndex == 1 ? "Сейчас ничего не скрывается" : "Всё найденное будет скрыто";
            }
            listHeader.SetCounter(query.Length == 0 ? string.Empty : visible.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static bool Matches(DetectionGroup group, string query)
        {
            return Contains(group.DisplayValue, query) || Contains(group.Type, query)
                || Contains(TypeNames.Describe(group.Type), query) || Contains(group.Placeholder, query);
        }

        private static bool Contains(string value, string query)
        {
            return value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void UpdateCounter(BlockHeader block, TextView view)
        {
            if (!block.Searching || view.Query.Length == 0)
            {
                block.SetCounter(string.Empty);
                return;
            }
            block.SetCounter(view.MatchCount == 0
                ? "нет"
                : (view.CurrentMatch + 1).ToString(CultureInfo.InvariantCulture) + " из "
                    + view.MatchCount.ToString(CultureInfo.InvariantCulture));
        }

        private void ShowStatus(string message)
        {
            statusLabel.Text = message;
            statusTimer.Stop();
            statusTimer.Start();
        }

        // ---------------------------------------------------------------- список

        private void PaintCell(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered)
        {
            DetectionGroup group = (DetectionGroup)item;
            switch (column)
            {
                case 0:
                    float size = Dpi.F(18);
                    RectangleF box = new RectangleF(bounds.X, bounds.Y + (bounds.Height - size) / 2f, size, size);
                    CheckState state = group.AllEnabled ? CheckState.Checked : group.Enabled ? CheckState.Indeterminate : CheckState.Unchecked;
                    CheckMark.Draw(graphics, box, state, group.Locked, hovered);
                    break;
                case 1:
                    Theme.DrawText(graphics, group.Type, Theme.SmallMonoFont, group.Locked ? Theme.Danger : group.Enabled ? Theme.Warm : Theme.Secondary, bounds);
                    break;
                case 2:
                    Theme.DrawText(graphics, group.DisplayValue, Theme.UiFont, Theme.Text, bounds);
                    break;
                case 3:
                    Theme.DrawText(graphics, group.Items.Count.ToString(CultureInfo.InvariantCulture), Theme.SmallFont,
                        Theme.Tertiary, bounds, StringAlignment.Far, StringAlignment.Center, false);
                    break;
                default:
                    if (!group.Enabled)
                    {
                        Theme.DrawText(graphics, "останется", Theme.SmallFont, Theme.Tertiary, bounds);
                        break;
                    }
                    float left = bounds.X;
                    if (group.Reserved)
                    {
                        Theme.DrawGlyph(graphics, Glyphs.Pin, Theme.SmallIconFont, Theme.Accent, new RectangleF(left, bounds.Y, Dpi.F(14), bounds.Height));
                        left += Dpi.F(18);
                    }
                    if (!group.AllEnabled)
                    {
                        float width = Theme.Measure("частично", Theme.SmallFont, 0).Width;
                        Theme.DrawText(graphics, "частично", Theme.SmallFont, Theme.Tertiary, new RectangleF(left, bounds.Y, width + Dpi.F(2), bounds.Height));
                        left += width + Dpi.F(6);
                    }
                    Theme.DrawText(graphics, group.Placeholder, Theme.SmallMonoFont, Theme.Accent,
                        new RectangleF(left, bounds.Y, Math.Max(0, bounds.Right - left), bounds.Height));
                    break;
            }
        }

        private string ListTip(object item)
        {
            DetectionGroup group = (DetectionGroup)item;
            string first = TypeNames.Describe(group.Type) + ", " + TypeNames.DescribeFinding(group.Items[0]);
            if (group.Sensitive)
            {
                // В списке пароль закрыт, значение видно только при наведении.
                first += ": " + Shorten(group.Value, 60);
            }
            return first + "\n" + (group.Locked
                ? "Правый клик: оставить или отметить ошибку"
                : "Пробел или флажок: скрыть или оставить. Правый клик: другие действия");
        }

        private void RevealSelected()
        {
            DetectionGroup group = list.FocusedItem as DetectionGroup;
            if (group == null || group.Items.Count == 0)
            {
                return;
            }
            Detection first = group.Items[0];
            if (image != null && !image.ShowText)
            {
                RevealInImage(first);
                return;
            }
            int start = map.ToDisplay(first.Start);
            int end = map.ToDisplay(first.End);
            preview.Select(start, end - start);
            preview.RevealRange(start, end - start);
        }

        private void ToggleSelected()
        {
            FlushScan();
            List<DetectionGroup> chosen = new List<DetectionGroup>();
            foreach (object item in list.SelectedItems)
            {
                DetectionGroup group = (DetectionGroup)item;
                if (!group.Locked)
                {
                    chosen.Add(group);
                }
            }
            if (chosen.Count == 0)
            {
                ShowStatus(SecretHint);
                return;
            }
            if (chosen.Count == 1)
            {
                ToggleGroup(chosen[0]);
                return;
            }
            SetEnabled(chosen, !chosen[0].AllEnabled);
        }

        private void SetEnabled(List<DetectionGroup> chosen, bool enable)
        {
            foreach (DetectionGroup group in chosen)
            {
                Choose(group, enable);
            }
            RefreshAll(enable ? "Выбранные значения будут скрыты." : "Выбранные значения останутся в тексте.");
        }

        /// <summary>Галочка для значения; решение запоминается до конца проверки, в том числе после правки текста.</summary>
        private void Choose(DetectionGroup group, bool enable)
        {
            group.SetEnabled(enable);
            choices[GroupKey(group.Type, group.Value)] = enable;
        }

        // ---------------------------------------------------------------- подсказки и меню

        private void OnPreviewHover(object sender, EventArgs e)
        {
            TextMark mark = preview.HoveredMark;
            if (mark == null)
            {
                GlassTip.HideFor(preview);
                return;
            }
            if (mark.Tag is DecodedSpan)
            {
                ShowDecodedTip((DecodedSpan)mark.Tag);
                return;
            }
            Detection detection = (Detection)mark.Tag;
            string[] tip = DescribeSourceMark(detection);
            GlassTip.ShowRich(preview, preview.RectangleToScreen(preview.HoverBounds), tip[0],
                TipFont(detection), TipColor(detection), tip[1], tip[2], 250);
        }

        /// <summary>Заголовок подсказки: метка или значение моноширинным, оставленное обычным.</summary>
        private static Font TipFont(Detection detection)
        {
            return detection.Enabled || detection.Locked ? Theme.MonoFont : Theme.StrongFont;
        }

        private static Color TipColor(Detection detection)
        {
            return detection.Locked ? Theme.Danger : detection.Enabled ? Theme.Accent : Theme.Text;
        }

        /// <summary>
        /// Подсказка к подсвеченному значению: чем заменится, что это и что можно сделать. Пароль в тексте
        /// закрыт точками, поэтому в заголовке его подсказки само значение.
        /// </summary>
        private string[] DescribeSourceMark(Detection detection)
        {
            DetectionGroup group = GroupOf(detection);
            string kind = TypeNames.Describe(detection.Type);
            string times = group != null && group.Items.Count > 1 ? ", в тексте " + TypeNames.Times(group.Items.Count) : string.Empty;
            if (detection.Locked)
            {
                return new string[] { Shorten(detection.Value, 80),
                    kind + times + ". Заменится на " + detection.Placeholder + DescribeReason(detection),
                    "Правый клик: оставить или отметить ошибку" };
            }
            if (detection.Enabled)
            {
                string body = kind + times;
                if (group != null && group.Reserved)
                {
                    body += ". Номер закреплён";
                }
                else if (detection.Transient)
                {
                    body += ". Только в этот раз, без сохранения";
                }
                else if (detection.Source == Detection.RememberedSource)
                {
                    body += ". Метка запомнена раньше";
                }
                body += DescribeReason(detection);
                return new string[] { detection.Placeholder, body, "Правый клик: оставить, закрепить номер, исключение" };
            }
            return new string[] { "Останется как есть", kind + ", " + TypeNames.DescribeFinding(detection) + DescribeReason(detection),
                "Правый клик: скрыть" };
        }

        /// <summary>Почему детектор нашёл значение: «. После слова «кластер», латиница в русском тексте».</summary>
        private static string DescribeReason(Detection detection)
        {
            if (string.IsNullOrEmpty(detection.Reason))
            {
                return string.Empty;
            }
            return ". " + char.ToUpperInvariant(detection.Reason[0]) + detection.Reason.Substring(1);
        }

        private void OnResultHover(object sender, EventArgs e)
        {
            TextMark mark = resultPreview.HoveredMark;
            if (mark == null)
            {
                GlassTip.HideFor(resultPreview);
                return;
            }
            Detection detection = (Detection)mark.Tag;
            Rectangle anchor = resultPreview.RectangleToScreen(resultPreview.HoverBounds);
            GlassTip.ShowRich(resultPreview, anchor, Shorten(detection.Value, 60), Theme.MonoFont,
                detection.IsSensitiveValue ? Theme.Danger : Theme.Warm,
                "Так было в исходном тексте. " + TypeNames.Describe(detection.Type), "Правый клик: действия", 250);
        }

        private void OnPreviewContext(object sender, MouseEventArgs e)
        {
            GlassTip.HideAll();
            if (decrypt)
            {
                ShowMenu(BuildDecryptMenu(), preview, e.Location);
                return;
            }
            FlushScan(); // меню строится по находкам в текущем тексте
            ShowMenu(BuildPreviewMenu(e.Location), preview, e.Location);
        }

        /// <summary>Меню показывается, только если в нём есть что выбрать.</summary>
        private static void ShowMenu(GlassMenu menu, Control owner, Point location)
        {
            if (menu == null)
            {
                return;
            }
            if (menu.Tidy())
            {
                menu.ShowOnce(owner, location);
            }
            else
            {
                menu.Dispose();
            }
        }

        /// <summary>
        /// Меню исходного текста. Правка, поиск и замена всего текста здесь не повторяются: для них есть
        /// кнопки и сочетания клавиш. Пункт, который к этому месту текста не применить, не показывается.
        /// </summary>
        private GlassMenu BuildPreviewMenu(Point location)
        {
            GlassMenu menu = new GlassMenu();
            int index = preview.CharIndexAt(location);
            TextMark mark = index >= 0 ? preview.MarkAt(index) : null;
            bool hasSelection = preview.SelectedText.Trim().Length > 0;
            TextMark selected = hasSelection ? preview.MarkAt(preview.SelectionStart) : null;
            if (selected != null && selected.Start == preview.SelectionStart && selected.Length == preview.SelectionLength)
            {
                // Выделено ровно найденное значение (например, щелчком в списке): показываем действия со значением.
                hasSelection = false;
                mark = selected;
            }
            if (hasSelection)
            {
                AddSelectionActions(menu);
            }
            else if (mark != null)
            {
                AddGroupActions(menu, GroupOf((Detection)mark.Tag));
            }
            menu.AddSeparator();
            if (preview.SelectionLength > 0)
            {
                menu.AddItem("Копировать", "Ctrl+C", true, delegate { preview.CopySelection(); });
            }
            if (!preview.ReadOnly && ClipboardService.HasText())
            {
                menu.AddItem("Вставить", "Ctrl+V", true, delegate { preview.PasteFromClipboard(); });
            }
            return menu;
        }

        /// <summary>
        /// Действия с выделенным. «Отметить» скрывает его меткой HIDE_TEXT и запоминает. В «Пометить как»
        /// типы, которые подходят к выделенному, частые выше, и отметка без сохранения.
        /// </summary>
        private void AddSelectionActions(GlassMenu menu)
        {
            SelectionInfo selection = ReadSelection(false);
            if (selection == null)
            {
                return;
            }
            int end = selection.Start + selection.Length;
            Detection secret = null;
            bool found = false;
            bool hidden = false;
            foreach (Detection detection in detections)
            {
                if (!detection.Overlaps(selection.Start, end))
                {
                    continue;
                }
                if (detection.Locked)
                {
                    secret = detection;
                    continue;
                }
                found = true;
                hidden |= detection.Enabled;
            }
            if (secret != null)
            {
                // Отметка не ляжет поверх пароля: он и так скрыт, а часть его не должна стать отдельным значением.
                // Сам пароль можно оставить или отметить как ошибку, как по правому клику на нём.
                AddGroupActions(menu, GroupOf(secret));
            }
            else
            {
                menu.AddItem("Отметить", MarkTypes.Generic, true, delegate { MarkSelection(MarkTypes.Generic, true); });
                GlassMenu types = menu.AddSubmenu("Пометить как");
                foreach (string type in MarkTypes.For(selection.Value, settings.MarkTypeUsage))
                {
                    string chosen = type;
                    string name = TypeNames.Describe(type);
                    // Справа тип метки, если название не совпадает с ним (GUID, SID).
                    types.AddItem(name, name == type ? null : type, true, delegate { MarkSelectionAs(chosen); });
                }
                types.AddSeparator();
                types.AddItem("Отметить без сохранения", MarkTypes.Generic, true,
                    delegate { MarkSelection(MarkTypes.Generic, false); });
            }
            if (hidden)
            {
                menu.AddItem("Не скрывать", null, true, delegate { UnmarkSelection(); });
            }
            if (found)
            {
                menu.AddItem("Никогда не скрывать", null, true, delegate { NeverHideSelection(); });
            }
        }

        private void OnResultContext(object sender, MouseEventArgs e)
        {
            GlassTip.HideAll();
            FlushScan();
            ShowMenu(BuildResultMenu(e.Location), resultPreview, e.Location);
        }

        /// <summary>Меню результата: действия с меткой под курсором и копирование выделенного.</summary>
        private GlassMenu BuildResultMenu(Point location)
        {
            GlassMenu menu = new GlassMenu();
            int index = resultPreview.CharIndexAt(location);
            TextMark mark = index >= 0 ? resultPreview.MarkAt(index) : null;
            if (mark != null)
            {
                AddGroupActions(menu, GroupOf((Detection)mark.Tag));
                menu.AddSeparator();
            }
            if (resultPreview.SelectionLength > 0)
            {
                menu.AddItem("Копировать выделенное", "Ctrl+C", true, delegate { resultPreview.CopySelection(); });
            }
            return menu;
        }

        private void OnListContext(object sender, MouseEventArgs e)
        {
            FlushScan();
            ShowMenu(BuildListMenu(), list, e.Location);
        }

        private GlassMenu BuildListMenu()
        {
            List<object> chosen = list.SelectedItems;
            if (chosen.Count == 0)
            {
                return null;
            }
            GlassMenu menu = new GlassMenu();
            if (chosen.Count == 1)
            {
                AddGroupActions(menu, (DetectionGroup)chosen[0]);
                return menu;
            }
            List<DetectionGroup> groupsChosen = new List<DetectionGroup>();
            bool anyKept = false;
            bool anyHidden = false;
            foreach (object item in chosen)
            {
                DetectionGroup group = (DetectionGroup)item;
                if (!group.Locked)
                {
                    groupsChosen.Add(group);
                    anyKept |= !group.AllEnabled;
                    anyHidden |= group.Enabled;
                }
            }
            menu.AddHeader("Выбрано: " + chosen.Count.ToString(CultureInfo.InvariantCulture));
            if (groupsChosen.Count == 0)
            {
                menu.AddHeader("Пароли снимаются по одному");
            }
            if (anyKept)
            {
                menu.AddItem("Скрыть все", null, true, delegate { SetEnabled(groupsChosen, true); });
            }
            if (anyHidden)
            {
                menu.AddItem("Не скрывать ни одно", null, true, delegate { SetEnabled(groupsChosen, false); });
            }
            return menu;
        }

        /// <summary>
        /// Действия со значением: одинаковые в тексте, в результате, в списке и на картинке. Что к значению
        /// не применить (закрепить номер у оставленного, запомнить уже запомненное или секрет), не показывается.
        /// У пароля в заголовке само значение: в тексте и в списке оно закрыто.
        /// </summary>
        private void AddGroupActions(GlassMenu menu, DetectionGroup group)
        {
            if (group == null)
            {
                return;
            }
            menu.AddHeader(Shorten(group.Sensitive ? group.Value : group.DisplayValue, 42));
            if (group.Locked)
            {
                menu.AddItem("Не скрывать в этот раз", null, true, delegate { ReleaseSecret(group, false); });
                menu.AddItem("Распознано ошибочно", null, true, delegate { ReleaseSecret(group, true); });
                return;
            }
            menu.AddItem(group.AllEnabled ? "Не скрывать" : "Скрыть", null, true, delegate { ToggleGroup(group); });
            if (group.Reserved)
            {
                menu.AddItem("Снять закрепление " + ReservedPlaceholder(group), null, true, delegate { Reserve(group, false); });
            }
            else if (group.Enabled && !group.Sensitive)
            {
                menu.AddItem("Закрепить номер " + group.Placeholder, null, true, delegate { Reserve(group, true); });
            }
            if (!group.Sensitive && !database.IsLearned(group.Value, group.Type))
            {
                menu.AddItem("Скрывать всегда", null, true, delegate { Remember(group); });
            }
            menu.AddItem("Никогда не скрывать", null, true, delegate { Allow(group); });
        }

        private string ReservedPlaceholder(DetectionGroup group)
        {
            return "[" + group.Type + "_" + database.GetReservedIndex(group.Type, group.Value).ToString(CultureInfo.InvariantCulture) + "]";
        }

        private static string Shorten(string value, int limit)
        {
            string single = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return single.Length <= limit ? single : single.Substring(0, limit - 1) + "…";
        }

        // ---------------------------------------------------------------- действия со значениями

        private void ToggleGroup(DetectionGroup group)
        {
            if (group == null)
            {
                return;
            }
            if (group.Locked)
            {
                ShowStatus(SecretHint);
                return;
            }
            bool enable = !group.AllEnabled;
            Choose(group, enable);
            string message = enable
                ? Shorten(group.DisplayValue, 40) + " будет заменено."
                : Shorten(group.DisplayValue, 40) + " останется в тексте.";
            if (enable && group.Sensitive)
            {
                // Пароль, оставленный в этот раз, после проверки снова закрыт точками и не снимается галочкой.
                Rescan(message);
                return;
            }
            RefreshAll(message);
        }

        private void Reserve(DetectionGroup group, bool reserve)
        {
            if (group == null || group.Locked || (reserve && group.Sensitive))
            {
                ShowStatus("Номера секретов не закрепляются: они не должны повторяться между вставками.");
                return;
            }
            try
            {
                if (reserve)
                {
                    int index = database.Reserve(group.Type, group.Value, ParseIndex(group.Placeholder));
                    if (index <= 0)
                    {
                        ShowStatus("Не получилось закрепить номер.");
                        return;
                    }
                    // Закреплённое значение должно находиться и дальше, иначе номер не пригодится.
                    database.AddLearned(group.Value, group.Type);
                    SaveDatabase();
                    RefreshAll("За " + Shorten(group.DisplayValue, 40) + " закреплён [" + group.Type + "_"
                        + index.ToString(CultureInfo.InvariantCulture) + "], он сохранится и в следующих вставках.");
                    return;
                }
                if (!database.Unreserve(group.Type, group.Value))
                {
                    ShowStatus("У этого значения нет закреплённого номера.");
                    return;
                }
                SaveDatabase();
                RefreshAll("Закрепление снято, номер снова выдаётся по порядку.");
            }
            catch (Exception failure)
            {
                ShowStatus("Не удалось сохранить правило: " + failure.Message);
            }
        }

        private void Remember(DetectionGroup group)
        {
            if (group == null || group.Locked || group.Sensitive)
            {
                return;
            }
            try
            {
                bool added = database.AddLearned(group.Value, group.Type);
                SaveDatabase();
                Choose(group, true);
                RefreshAll(added
                    ? Shorten(group.DisplayValue, 40) + " теперь скрывается всегда."
                    : Shorten(group.DisplayValue, 40) + " уже есть в правилах.");
            }
            catch (Exception failure)
            {
                ShowStatus("Не удалось сохранить правило: " + failure.Message);
            }
        }

        private void Allow(DetectionGroup group)
        {
            if (group == null || group.Locked)
            {
                return;
            }
            string value = group.Value;
            try
            {
                database.AddAllowed(value);
                // Пароль, который оставили в этот раз: исключения на секреты не действуют, нужна своя отметка.
                if (group.Sensitive)
                {
                    database.AddNotSecret(value);
                }
                SaveDatabase();
            }
            catch (Exception failure)
            {
                ShowStatus("Не удалось сохранить правило: " + failure.Message);
                return;
            }
            ForgetValue(value);
            RefreshAll(Shorten(group.DisplayValue, 40) + " больше не будет скрываться.");
        }

        private const string SecretHint = "Пароль можно оставить правым кликом по нему.";

        /// <summary>
        /// Пароль или ключ найден по ошибке. remember: «Распознано ошибочно», значение запоминается в правилах,
        /// и детектор больше не считает его секретом; снять отметку можно в «Правилах и исключениях».
        /// Без него значение остаётся открытым только в этой проверке, до смены режима, как «Не скрывать».
        /// </summary>
        private void ReleaseSecret(DetectionGroup group, bool remember)
        {
            if (group == null || !group.Locked)
            {
                return;
            }
            string value = group.Value;
            if (remember)
            {
                try
                {
                    database.AddNotSecret(value);
                    SaveDatabase();
                }
                catch (Exception failure)
                {
                    ShowStatus("Не удалось сохранить правило: " + failure.Message);
                    return;
                }
                MarkOthersStale(false);
            }
            // Решение по значению, а не по месту: повторы и тот же текст другим типом (TOKEN и SECRET) тоже открываются.
            foreach (Detection detection in detections)
            {
                if (detection.Locked && string.Equals(detection.Value, value, StringComparison.Ordinal))
                {
                    choices[GroupKey(detection.Type, detection.Value)] = false;
                }
            }
            Rescan(remember
                ? "Отмечено: это не пароль. Отметку можно снять в «Правилах и исключениях»."
                : "Значение останется в тексте, только в этот раз. Скрыть его снова можно правым кликом.");
        }

        /// <summary>
        /// Значение попало в исключения: убрать его находки и отметки этой проверки, а запомненную метку
        /// забыть, чтобы она не держала номер и не всплывала в списке запомненного.
        /// </summary>
        private void ForgetValue(string value)
        {
            Predicate<Detection> same = delegate(Detection item)
            {
                return !item.Locked && string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase);
            };
            detections.RemoveAll(same);
            manualMarks.RemoveAll(same);
            database.Remembered.RemoveAll(delegate(LearnedValue item)
            {
                return string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase);
            });
            try
            {
                LabelMemory.Forget(value);
                labelsStamp = Stamp(Paths.LabelsFile);
            }
            catch (Exception)
            {
                // Исключение уже сохранено и действует; метка забудется сама через 30 дней.
            }
        }

        private static int ParseIndex(string placeholder)
        {
            if (string.IsNullOrEmpty(placeholder))
            {
                return 0;
            }
            int underscore = placeholder.LastIndexOf('_');
            int close = placeholder.LastIndexOf(']');
            if (underscore < 0 || close <= underscore)
            {
                return 0;
            }
            int index;
            return int.TryParse(placeholder.Substring(underscore + 1, close - underscore - 1), out index) ? index : 0;
        }

        // ---------------------------------------------------------------- действия с выделением

        private sealed class SelectionInfo
        {
            internal int Start;
            internal int Length;
            internal string Value;
        }

        private SelectionInfo ReadSelection()
        {
            return ReadSelection(true);
        }

        /// <summary>report: объяснить в строке состояния, почему выделение не подходит.</summary>
        private SelectionInfo ReadSelection(bool report)
        {
            string selected = preview.SelectedText;
            string trimmed = selected.Trim();
            if (trimmed.Length == 0)
            {
                if (report)
                {
                    ShowStatus("Сначала выделите значение в тексте.");
                }
                return null;
            }
            int displayStart = preview.SelectionStart + (selected.Length - selected.TrimStart().Length);
            int start = map.ToOriginal(displayStart);
            int end = map.EndToOriginal(displayStart, displayStart + trimmed.Length);
            if (start < 0 || end > sourceText.Length || end <= start)
            {
                if (report)
                {
                    ShowStatus("Не получилось сопоставить выделение с исходным текстом.");
                }
                return null;
            }
            SelectionInfo info = new SelectionInfo();
            info.Start = start;
            info.Length = end - start;
            info.Value = sourceText.Substring(start, info.Length);
            return info;
        }

        /// <summary>Отметка с выбранным типом. Выбор запоминается: частые типы поднимаются в меню выше.</summary>
        private void MarkSelectionAs(string type)
        {
            int count;
            settings.MarkTypeUsage.TryGetValue(type, out count);
            settings.MarkTypeUsage[type] = count + 1;
            try
            {
                SafePasteSettings.Update(delegate(SafePasteSettings latest)
                {
                    int stored;
                    latest.MarkTypeUsage.TryGetValue(type, out stored);
                    latest.MarkTypeUsage[type] = stored + 1;
                });
            }
            catch (Exception)
            {
                // Порядок в меню не стоит сообщения об ошибке.
            }
            MarkSelection(type, true);
        }

        private void MarkSelection(string type)
        {
            MarkSelection(type, true);
        }

        /// <summary>
        /// Отмечает выделенное и все его вхождения. save: значение попадает в правила «Скрывать всегда»
        /// (кроме паролей, токенов и ключей); без него скрывается только в этот раз и меткой не запоминается.
        /// </summary>
        private void MarkSelection(string type, bool save)
        {
            SelectionInfo selection = ReadSelection();
            if (selection == null)
            {
                return;
            }
            List<int> starts = Detector.FindOccurrences(sourceText, selection.Value);
            if (!starts.Contains(selection.Start))
            {
                starts.Insert(0, selection.Start);
            }
            List<Detection> next = new List<Detection>(detections);
            List<Detection> placed = new List<Detection>();
            int skipped = 0;
            foreach (int start in starts)
            {
                Detection manual = PlaceManual(next, start, selection.Length, type, !save);
                if (manual == null)
                {
                    skipped++;
                    continue;
                }
                placed.Add(manual);
            }
            int added = placed.Count;
            if (added == 0)
            {
                ShowStatus("Выделение попадает в пароль или ключ, он и так скрыт.");
                return;
            }
            detections = next;
            // Отметка переживает правку текста и смену режима, а решение «оставить» с ней снимается.
            foreach (Detection manual in placed)
            {
                manualMarks.RemoveAll(delegate(Detection item) { return item.Overlaps(manual.Start, manual.End); });
                manualMarks.Add(manual.Clone());
            }
            choices[GroupKey(type, selection.Value)] = true;
            string message = "Отмечено как " + type + ", мест в тексте: " + added.ToString(CultureInfo.InvariantCulture) + ".";
            if (skipped > 0)
            {
                message += " Внутри секретов пропущено: " + skipped.ToString(CultureInfo.InvariantCulture) + ".";
            }
            if (!save)
            {
                message += " Только в этот раз, без сохранения.";
            }
            else if (!IsSecretType(type))
            {
                message += RememberValue(selection.Value, type);
            }
            else
            {
                message += ForgetNotSecret(selection.Value);
            }
            preview.Select(preview.SelectionStart, 0);
            RefreshAll(message);
        }

        /// <summary>Пароли, токены и ключи в правила не пишутся никогда.</summary>
        private static bool IsSecretType(string type)
        {
            return type == "SECRET" || type == "TOKEN" || type == "PRIVATE_KEY";
        }

        private string RememberValue(string value, string type)
        {
            try
            {
                if (database.AddLearned(value, type))
                {
                    SaveDatabase();
                    return " Значение запомнено.";
                }
                return string.Empty;
            }
            catch (Exception failure)
            {
                return " Запомнить не удалось: " + failure.Message;
            }
        }

        /// <summary>Значение снова отметили паролем: прежняя отметка «Распознано ошибочно» снимается.</summary>
        private string ForgetNotSecret(string value)
        {
            try
            {
                if (database.RemoveNotSecret(value))
                {
                    SaveDatabase();
                    return " Отметка «Распознано ошибочно» снята.";
                }
                return string.Empty;
            }
            catch (Exception failure)
            {
                return " Снять отметку не удалось: " + failure.Message;
            }
        }

        private void NeverHideSelection()
        {
            SelectionInfo selection = ReadSelection();
            if (selection == null)
            {
                return;
            }
            string value = selection.Value;
            Detection single = null;
            int overlaps = 0;
            foreach (Detection detection in detections)
            {
                if (detection.Overlaps(selection.Start, selection.Start + selection.Length))
                {
                    overlaps++;
                    single = detection;
                }
            }
            // Выделение внутри одной находки: в исключения идёт её значение целиком.
            if (overlaps == 1 && single != null)
            {
                if (single.Locked)
                {
                    ShowStatus("Пароль не попадает в исключения. Если это не пароль, выберите «Распознано ошибочно».");
                    return;
                }
                value = single.Value;
            }
            try
            {
                database.AddAllowed(value);
                SaveDatabase();
            }
            catch (Exception failure)
            {
                ShowStatus("Не удалось сохранить правило: " + failure.Message);
                return;
            }
            ForgetValue(value);
            RefreshAll(Shorten(value, 40) + " больше не будет скрываться.");
        }

        private void UnmarkSelection()
        {
            SelectionInfo selection = ReadSelection();
            if (selection == null)
            {
                return;
            }
            int end = selection.Start + selection.Length;
            // Значение остаётся везде в этом тексте: так решение переживёт и правку текста.
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Detection detection in detections)
            {
                if (detection.Overlaps(selection.Start, end) && !detection.Locked)
                {
                    keys.Add(GroupKey(detection.Type, detection.Value));
                }
            }
            foreach (Detection detection in detections)
            {
                if (!detection.Locked && keys.Contains(GroupKey(detection.Type, detection.Value)))
                {
                    detection.Enabled = false;
                }
            }
            foreach (string key in keys)
            {
                choices[key] = false;
            }
            if (keys.Count == 0)
            {
                ShowStatus("В выделении нет найденного значения.");
                return;
            }
            RefreshAll("Выделенное останется в тексте, только в этот раз.");
        }

        // ---------------------------------------------------------------- правка текста

        /// <summary>Правка в блоке исходного текста: позиции показанного текста переводятся в исходные.</summary>
        private void OnPreviewEdit(object sender, TextEditEventArgs e)
        {
            e.Handled = true;
            int start;
            int end;
            if (e.Length > 0)
            {
                start = map.RangeStartToOriginal(e.Start);
                end = map.CaretToOriginal(e.Start + e.Length);
            }
            else
            {
                start = map.CaretToOriginal(e.Start);
                end = start;
            }
            // Весь текст заменён другим (Ctrl+A и Ctrl+V): прошлые решения к нему не относятся.
            bool wasEmpty = preview.Content.Length == 0;
            bool replaced = e.Start == 0 && e.Length == preview.Content.Length && e.Text.Length > 1;
            if (!ApplyEdit(start, end - start, WithNewlines(e.Text), true))
            {
                return;
            }
            if (replaced)
            {
                choices.Clear();
                manualMarks.Clear();
                Rescan(wasEmpty ? "Текст вставлен и проверен." : "Текст заменён и проверен.");
            }
        }

        /// <summary>
        /// Применяет правку к исходному тексту. Находки сразу сдвигаются вместе с текстом,
        /// а проверка заново идёт после паузы в наборе.
        /// </summary>
        private bool ApplyEdit(int start, int length, string inserted, bool remember)
        {
            if (sourceText.Length - length + inserted.Length > settings.MaxClipboardChars)
            {
                ShowStatus("Слишком большой текст: предел " + settings.MaxClipboardChars.ToString(CultureInfo.InvariantCulture)
                    + " символов.");
                return false;
            }
            string removed = sourceText.Substring(start, length);
            if (remember)
            {
                history.Add(start, removed, inserted);
            }
            sourceText = sourceText.Substring(0, start) + inserted + sourceText.Substring(start + length);
            map = new TextMap(sourceText);
            ShiftDetections(detections, start, length, inserted.Length, true);
            ShiftDetections(manualMarks, start, length, inserted.Length, false);
            GlassTip.HideAll();
            preview.SetContent(MaskedDisplay(), BuildSourceMarks(), true);
            preview.SetCaret(map.ToDisplay(start + inserted.Length));
            ScheduleScan();
            return true;
        }

        /// <summary>
        /// Сдвигает находки за местом правки. Находка, которую правка задела, пропадает до новой проверки.
        /// Секрет не пропадает, а растягивается на изменённое место: иначе он мелькнул бы открытым.
        /// </summary>
        private static void ShiftDetections(List<Detection> list, int start, int removed, int inserted, bool keepSecrets)
        {
            int end = start + removed;
            int delta = inserted - removed;
            for (int index = list.Count - 1; index >= 0; index--)
            {
                Detection item = list[index];
                if (item.End <= start)
                {
                    continue;
                }
                if (item.Start >= end)
                {
                    item.Start += delta;
                    continue;
                }
                if (keepSecrets && item.Locked)
                {
                    int newStart = Math.Min(item.Start, start);
                    int newEnd = Math.Max(item.End <= end ? start + inserted : item.End + delta, start + inserted);
                    item.Start = newStart;
                    item.Length = newEnd - newStart;
                    if (item.Length > 0)
                    {
                        continue;
                    }
                }
                list.RemoveAt(index);
            }
        }

        /// <summary>Переводы строк в новом тексте такие же, как в исходном: в тексте из Windows это \r\n.</summary>
        private string WithNewlines(string value)
        {
            if (value.IndexOf('\n') < 0)
            {
                return value;
            }
            string newline = sourceText.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n"
                : sourceText.IndexOf('\n') >= 0 ? "\n"
                : sourceText.IndexOf('\r') >= 0 ? "\r"
                : "\r\n";
            return newline == "\n" ? value : value.Replace("\n", newline);
        }

        private void ScheduleScan()
        {
            scanPending = true;
            scanTimer.Stop();
            // Длинный текст проверяется дольше, поэтому и пауза перед проверкой длиннее.
            scanTimer.Interval = sourceText.Length > 100000 ? 1200 : 400;
            scanTimer.Start();
        }

        /// <summary>Проверяет текст заново, если его правили. false: проверить не удалось.</summary>
        private bool FlushScan()
        {
            scanTimer.Stop();
            return !scanPending || Rescan(null);
        }

        /// <summary>
        /// Новая проверка текущего текста. Отмеченное вручную и решения пользователя возвращаются,
        /// поэтому правка опечатки не сбрасывает галочки.
        /// </summary>
        private bool Rescan(string message)
        {
            scanTimer.Stop();
            if (decrypt)
            {
                // Вкладка расшифровки не проверяется, а расшифровывается заново.
                scanPending = false;
                Redecode(message);
                return true;
            }
            if (image != null)
            {
                // У картинки проверяется её распознанный текст, само распознавание не повторяется.
                return RescanImage(message);
            }
            List<Detection> next;
            try
            {
                next = Detector.Scan(sourceText, database, mode, false, settings.SmartSecrets);
            }
            catch (Exception failure)
            {
                scanPending = true;
                ShowStatus(failure.Message);
                return false;
            }
            scanPending = false;
            detections = next;
            ReapplyManualMarks();
            ApplyChoices();
            RefreshAll(message);
            return true;
        }

        /// <summary>Отмеченное вручную возвращается на свои места и на новые вхождения того же значения.</summary>
        private void ReapplyManualMarks()
        {
            if (manualMarks.Count == 0)
            {
                return;
            }
            List<Detection> previous = new List<Detection>(manualMarks);
            manualMarks.Clear();
            HashSet<string> placed = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, Detection> kinds = new Dictionary<string, Detection>(StringComparer.Ordinal);
            foreach (Detection mark in previous)
            {
                if (mark.End <= sourceText.Length
                    && string.Compare(sourceText, mark.Start, mark.Value, 0, mark.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    PlaceRemembered(mark.Start, mark.Length, mark.Type, mark.Transient, placed);
                }
                string kind = GroupKey(mark.Type, mark.Value);
                if (!kinds.ContainsKey(kind))
                {
                    kinds.Add(kind, mark);
                }
            }
            foreach (Detection mark in kinds.Values)
            {
                foreach (int start in Detector.FindOccurrences(sourceText, mark.Value))
                {
                    PlaceRemembered(start, mark.Length, mark.Type, mark.Transient, placed);
                }
            }
        }

        private void PlaceRemembered(int start, int length, string type, bool transient, HashSet<string> placed)
        {
            if (!placed.Add(start.ToString(CultureInfo.InvariantCulture) + ":" + type))
            {
                return;
            }
            Detection manual = PlaceManual(detections, start, length, type, transient);
            if (manual != null)
            {
                manualMarks.Add(manual.Clone());
            }
        }

        /// <summary>
        /// Ставит отметку вручную вместо всего, что с ней пересекается. Секреты не трогает:
        /// их нельзя оставить открытыми, а отметку можно снять. transient: отмечено без сохранения.
        /// </summary>
        private Detection PlaceManual(List<Detection> list, int start, int length, string type, bool transient)
        {
            int end = start + length;
            if (end > sourceText.Length || list.Exists(delegate(Detection item) { return item.Locked && item.Overlaps(start, end); }))
            {
                return null;
            }
            list.RemoveAll(delegate(Detection item) { return item.Overlaps(start, end); });
            Detection manual = new Detection(start, length, sourceText.Substring(start, length), type, Confidence.Manual, 95);
            manual.Manual = true;
            manual.Transient = transient;
            manual.Source = "Вручную";
            list.Add(manual);
            return manual;
        }

        private void ApplyChoices()
        {
            if (choices.Count == 0)
            {
                return;
            }
            foreach (Detection detection in detections)
            {
                bool enabled;
                if (!choices.TryGetValue(GroupKey(detection.Type, detection.Value), out enabled))
                {
                    continue;
                }
                if (detection.Locked)
                {
                    // «Не скрывать в этот раз» у пароля: он становится обычной находкой, её можно снова скрыть.
                    if (!enabled)
                    {
                        detection.Locked = false;
                        detection.Enabled = false;
                    }
                    continue;
                }
                detection.Enabled = enabled;
            }
        }

        /// <summary>Ctrl+Z и Ctrl+Y.</summary>
        private void StepHistory(bool redo)
        {
            TextEdit edit = redo ? history.Redo() : history.Undo();
            if (edit == null)
            {
                ShowStatus(redo ? "Повторять нечего." : "Отменять нечего.");
                return;
            }
            if (redo)
            {
                ApplyEdit(edit.Start, edit.Removed.Length, edit.Inserted, false);
            }
            else
            {
                ApplyEdit(edit.Start, edit.Inserted.Length, edit.Removed, false);
            }
        }

        // ---------------------------------------------------------------- буфер обмена

        private void CopyToClipboard()
        {
            if (decrypt)
            {
                CopyDecrypted();
                return;
            }
            if (image != null)
            {
                CopyImage();
                return;
            }
            try
            {
                string error;
                if (!ClipboardService.TrySetText(BuildPasteResult().Text, out error))
                {
                    ShowStatus(error);
                    return;
                }
                ShowStatus("Результат скопирован. Буфер сам не очистится.");
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
            }
        }

        private void LoadFromClipboard()
        {
            string text;
            string error;
            bool empty;
            if (!ClipboardService.TryGetText(out text, out error, out empty))
            {
                // Текста нет, но может быть картинка: в проверке она заменяет картинку этой вкладки.
                string imageError = null;
                System.Drawing.Bitmap picture = empty && !decrypt ? ClipboardService.TryGetImage(out imageError) : null;
                if (picture != null)
                {
                    OpenImage(picture, "Картинка взята из буфера.", true, null);
                    return;
                }
                ShowStatus(imageError ?? error);
                return;
            }
            LoadText(text, "Текст взят из буфера.");
        }

        /// <summary>Новый текст: вставлен через Ctrl+V или перетащен в окно.</summary>
        private void LoadText(string text, string message)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (text.Length > settings.MaxClipboardChars)
            {
                ShowStatus("Слишком большой текст: " + text.Length.ToString(CultureInfo.InvariantCulture)
                    + " символов, предел " + settings.MaxClipboardChars.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }
            if (decrypt)
            {
                LoadDecryptText(text, message);
                return;
            }
            if (image != null)
            {
                // Текст не заменяет картинку, а открывается своей вкладкой.
                OpenText(text, false, "Текст открыт во вкладке.");
                return;
            }
            List<Detection> next;
            try
            {
                next = Detector.Scan(text, database, mode, false, settings.SmartSecrets);
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return;
            }
            GlassTip.HideAll();
            // Новый текст проверяется с чистого листа: прошлые правки, отметки и решения к нему не относятся.
            scanTimer.Stop();
            scanPending = false;
            history.Clear();
            choices.Clear();
            manualMarks.Clear();
            sourceText = text;
            map = new TextMap(text);
            detections = OverlapResolver.Resolve(next);
            preview.SetContent(MaskedDisplay(), BuildSourceMarks(), false);
            resultPreview.SetContent(string.Empty, null, false);
            RefreshAll(message);
        }

        // ---------------------------------------------------------------- клавиатура

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Enter:
                    PasteCurrent();
                    return true;
                case Keys.Control | Keys.Shift | Keys.Enter:
                    PasteAll();
                    return true;
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.PageDown:
                    StepDocument(1);
                    return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.PageUp:
                    StepDocument(-1);
                    return true;
                case Keys.Control | Keys.W:
                    CloseDocument(current);
                    return true;
                case Keys.Control | Keys.T:
                    if (image != null)
                    {
                        PasteRecognizedText();
                        return true;
                    }
                    break;
                case Keys.Control | Keys.D0:
                    if (image != null)
                    {
                        imageView.ResetZoom();
                        return true;
                    }
                    break;
                case Keys.Control | Keys.S:
                    CopyToClipboard();
                    return true;
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    LoadFromClipboard();
                    return true;
                case Keys.Control | Keys.F:
                    ActiveHeader().OpenSearch();
                    return true;
                case Keys.F3:
                case Keys.Shift | Keys.F3:
                    StepSearch((keyData & Keys.Shift) != 0 ? -1 : 1);
                    return true;
                case Keys.Control | Keys.D1:
                    ApplyMode(ControlMode.Strict);
                    return true;
                case Keys.Control | Keys.D2:
                    ApplyMode(ControlMode.Balanced);
                    return true;
                case Keys.Control | Keys.D3:
                    ApplyMode(ControlMode.Light);
                    return true;
                case Keys.Escape:
                    Close();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private BlockHeader ActiveHeader()
        {
            if (showResult && resultCard.ContainsFocus)
            {
                return resultHeader;
            }
            if (showList && listCard.ContainsFocus)
            {
                return listHeader;
            }
            if (image != null && !image.ShowText)
            {
                return imageHeader;
            }
            return sourceHeader;
        }

        private void StepSearch(int direction)
        {
            BlockHeader block = ActiveHeader();
            if (block == imageHeader)
            {
                if (block.Searching)
                {
                    StepImageMatch(direction);
                }
                return;
            }
            TextView view = block == resultHeader ? resultPreview : block == sourceHeader ? preview : null;
            if (view == null || !block.Searching)
            {
                return;
            }
            view.StepMatch(direction);
            UpdateCounter(block, view);
        }

        /// <summary>Строка списка: одно значение и все его вхождения.</summary>
        private sealed class DetectionGroup
        {
            internal readonly string Type;
            internal readonly string Value;
            internal readonly List<Detection> Items = new List<Detection>();
            internal bool Reserved;

            internal DetectionGroup(string type, string value)
            {
                Type = type;
                Value = value;
            }

            internal bool Locked
            {
                get { return Items.Exists(delegate(Detection item) { return item.Locked; }); }
            }

            internal bool Enabled
            {
                get { return Items.Exists(delegate(Detection item) { return item.Enabled; }); }
            }

            internal bool AllEnabled
            {
                get { return Items.Count > 0 && Items.TrueForAll(delegate(Detection item) { return item.Enabled; }); }
            }

            /// <summary>Пароль, токен или ключ, в том числе отмеченный вручную: в правила не пишется.</summary>
            internal bool Sensitive
            {
                get { return Items.Exists(delegate(Detection item) { return item.IsSensitiveValue; }); }
            }

            internal Confidence Confidence
            {
                get { return Items.Count > 0 ? Items[0].Confidence : Confidence.Medium; }
            }

            internal string Placeholder
            {
                get
                {
                    foreach (Detection item in Items)
                    {
                        if (!string.IsNullOrEmpty(item.Placeholder))
                        {
                            return item.Placeholder;
                        }
                    }
                    return string.Empty;
                }
            }

            internal string DisplayValue
            {
                get
                {
                    Detection first = Items.Count > 0 ? Items[0] : null;
                    if (first != null && first.IsSensitiveValue)
                    {
                        return "скрыто, " + Value.Length.ToString(CultureInfo.InvariantCulture) + " симв.";
                    }
                    string value = Value.Replace("\r", " ").Replace("\n", " ");
                    return value.Length <= 160 ? value : value.Substring(0, 159) + "…";
                }
            }

            internal void SetEnabled(bool enabled)
            {
                foreach (Detection item in Items)
                {
                    if (!item.Locked)
                    {
                        item.Enabled = enabled;
                    }
                }
            }
        }
    }
}
