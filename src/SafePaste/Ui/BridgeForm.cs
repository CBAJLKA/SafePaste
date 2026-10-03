using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using SafePaste.Mcp;
using SafePaste.Storage;

namespace SafePaste.Ui
{
    /// <summary>
    /// Окно моста: включение, подключённые агенты, журнал вызовов и настройки. Состояние берётся
    /// у моста дважды в секунду, списки перерисовываются, только когда что-то изменилось.
    /// </summary>
    internal sealed class BridgeForm : GlassForm
    {
        private readonly IBridgeControl bridge;
        private readonly Action showLabels;
        private readonly Func<string, string> reveal;
        private readonly Timer refresh;
        private readonly List<Control> optionControls = new List<Control>();
        private WindowHeader header;
        private GlassButton closeButton;
        private Segmented power;
        private Segmented tabs;
        private BlockHeader sessionsHeader;
        private GlassList sessionsList;
        private GlassCard sessionsCard;
        private GlassButton stopButton;
        private GlassButton disconnectButton;
        private GlassButton labelsButton;
        private BlockHeader logHeader;
        private GlassList logList;
        private GlassCard logCard;
        private GlassButton detailsButton;
        private GlassButton clearButton;
        private BlockHeader rootsHeader;
        private GlassList rootsList;
        private GlassCard rootsCard;
        private GlassButton addRootButton;
        private GlassButton removeRootButton;
        private GlassLabel fullCaption;
        private GlassLabel editsCaption;
        private GlassLabel localCaption;
        private GlassLabel endpointCaption;
        private GlassLabel pageCaption;
        private Segmented fullSwitch;
        private Segmented editsSwitch;
        private Segmented localSwitch;
        private GlassButton modelButton;
        private GlassInput pageInput;
        private GlassLabel settingsNote;
        private GlassLabel statusLabel;
        private BridgeStatus shown;
        private bool rendering;
        private string message;

        internal BridgeForm(IBridgeControl bridge, Action showLabels, Func<string, string> reveal)
        {
            this.bridge = bridge;
            this.showLabels = showLabels;
            this.reveal = reveal;
            BuildLayout();
            refresh = new Timer();
            refresh.Interval = 500;
            refresh.Tick += delegate { RefreshStatus(); };
            ShowTab(0);
            RefreshStatus();
        }

        private void BuildLayout()
        {
            Text = "SafePaste: мост для агентов";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Dpi.S(940), Dpi.S(600));
            MinimumSize = new Size(Dpi.S(760), Dpi.S(520));

            header = new WindowHeader("Мост для агентов", "Выключен");
            closeButton = new GlassButton(Glyphs.Close, null, ButtonKind.Close, "Закрыть\nEsc");
            closeButton.TabStop = false;
            closeButton.Click += delegate { Close(); };
            power = new Segmented(new string[] { "Включён", "Выключен" });
            power.AccessibleName = "Мост включён или выключен";
            power.Tips = new string[] { "Агенты могут подключаться", "Агенты не подключатся, подключённые отключатся" };
            power.SelectedChanged += delegate { SetPower(power.SelectedIndex == 0); };
            tabs = new Segmented(new string[] { "Агенты", "Журнал", "Настройки" });
            tabs.AccessibleName = "Разделы окна моста";
            tabs.Tips = new string[] { "Кто подключён и что делает\nCtrl+1", "Последние вызовы инструментов\nCtrl+2", "Папки и разрешения\nCtrl+3" };
            tabs.SelectedChanged += delegate { message = null; ShowTab(tabs.SelectedIndex); };

            sessionsList = new GlassList();
            sessionsList.AccessibleName = "Подключённые агенты";
            sessionsList.AddColumn("Агент", 160, false);
            sessionsList.AddColumn("Папка", 0, false);
            sessionsList.AddColumn("Подключён", 92, false);
            sessionsList.AddColumn("Вызовов", 76, true);
            sessionsList.AddColumn("Сейчас", 200, false);
            sessionsList.Painter = PaintSession;
            sessionsList.TipProvider = delegate(object item) { return ((BridgeSessionInfo)item).Folder; };
            sessionsList.SelectionChanged += delegate { UpdateButtons(); };
            sessionsHeader = new BlockHeader("Подключения", Theme.Text, "Поиск по агенту или папке\nCtrl+F");
            sessionsHeader.SearchChanged += delegate { RenderLists(); };
            sessionsCard = new GlassCard(sessionsHeader, sessionsList);
            stopButton = new GlassButton(null, "Остановить команду", ButtonKind.Glass, null);
            stopButton.Click += delegate { StopSelected(); };
            disconnectButton = new GlassButton(null, "Отключить агента", ButtonKind.Glass, null);
            disconnectButton.Click += delegate { DisconnectSelected(); };
            labelsButton = new GlassButton(null, "Метки моста", ButtonKind.Glass, null);
            labelsButton.Click += delegate { if (this.showLabels != null) this.showLabels(); };

            logList = new GlassList();
            logList.AccessibleName = "Журнал вызовов";
            logList.AddColumn("Время", 76, false);
            logList.AddColumn("Агент", 130, false);
            logList.AddColumn("Инструмент", 110, false);
            logList.AddColumn("Запрос", 0, false);
            logList.AddColumn("Итог", 100, false);
            logList.Painter = PaintCall;
            logList.TipProvider = delegate(object item)
            {
                BridgeCallInfo call = (BridgeCallInfo)item;
                string answer = FirstLine(call.Response);
                return answer.Length == 0 ? call.Request : call.Request + "\n" + answer;
            };
            logList.SelectionChanged += delegate
            {
                message = null;
                UpdateButtons();
                UpdateStatus();
            };
            logList.ItemActivated += delegate { ShowDetails(); };
            logHeader = new BlockHeader("Последние вызовы", Theme.Text, "Поиск по инструменту, запросу или ответу\nCtrl+F");
            logHeader.SearchChanged += delegate { RenderLists(); };
            logCard = new GlassCard(logHeader, logList);
            detailsButton = new GlassButton(null, "Подробнее", ButtonKind.Glass, null);
            detailsButton.Click += delegate { ShowDetails(); };
            clearButton = new GlassButton(null, "Очистить журнал", ButtonKind.Glass, null);
            clearButton.Click += delegate { ClearLog(); };

            rootsList = new GlassList();
            rootsList.AccessibleName = "Папки для агентов";
            rootsList.AddColumn("Папка", 0, false);
            rootsList.Painter = PaintRoot;
            rootsList.EmptyText = "Папок нет: агентам доступны все файлы пользователя.";
            rootsList.SelectionChanged += delegate { UpdateButtons(); };
            rootsList.DeleteRequested += delegate { RemoveRoots(); };
            rootsHeader = new BlockHeader("Папки для агентов", Theme.Text, "Поиск по папкам\nCtrl+F");
            rootsHeader.SearchChanged += delegate { RenderLists(); };
            addRootButton = new GlassButton(Glyphs.Add, null, ButtonKind.Plain, "Добавить папку");
            addRootButton.Click += delegate { ChooseRoot(); };
            removeRootButton = new GlassButton(Glyphs.Delete, null, ButtonKind.Plain, "Убрать выбранные папки\nDelete");
            removeRootButton.Click += delegate { RemoveRoots(); };
            rootsHeader.AddTool(addRootButton, 0);
            rootsHeader.AddTool(removeRootButton, 0);
            rootsCard = new GlassCard(rootsHeader, rootsList);

            fullCaption = Caption("Полный PowerShell (profile: full)");
            fullSwitch = new Segmented(new string[] { "С подтверждением", "Запрещён" });
            fullSwitch.AccessibleName = "Полный PowerShell";
            fullSwitch.SelectedChanged += delegate
            {
                bool allow = fullSwitch.SelectedIndex == 0;
                ChangeSettings(delegate(BridgeSettings next) { next.AllowFull = allow; },
                    allow ? "Полный PowerShell снова доступен после подтверждения." : "Полный PowerShell запрещён.");
            };
            editsCaption = Caption("Правка и запись файлов");
            editsSwitch = new Segmented(new string[] { "С подтверждением", "Запрещена" });
            editsSwitch.AccessibleName = "Правка и запись файлов";
            editsSwitch.SelectedChanged += delegate
            {
                bool allow = editsSwitch.SelectedIndex == 0;
                ChangeSettings(delegate(BridgeSettings next) { next.AllowEdits = allow; },
                    allow ? "Правка файлов снова доступна после подтверждения." : "Правка файлов запрещена.");
            };
            localCaption = Caption("Локальная модель (sp_ask_local)");
            localSwitch = new Segmented(new string[] { "Разрешена", "Запрещена" });
            localSwitch.AccessibleName = "Локальная модель";
            localSwitch.SelectedChanged += delegate
            {
                bool allow = localSwitch.SelectedIndex == 0;
                ChangeSettings(delegate(BridgeSettings next) { next.AllowLocalModel = allow; },
                    allow ? "Локальная модель разрешена." : "Локальная модель запрещена.");
            };
            endpointCaption = Caption("Модель: не выбрана");
            modelButton = new GlassButton(null, "Найти и подключить ИИ", ButtonKind.Glass, null);
            modelButton.Click += delegate
            {
                using (LocalModelForm form = new LocalModelForm(bridge))
                    if (form.ShowDialog(this) == DialogResult.OK) message = "Локальная модель подключена.";
                RefreshNow();
            };
            pageCaption = Caption("Размер страницы ответа, символов");
            pageInput = new GlassInput();
            pageInput.ShowGlyph = false;
            pageInput.Placeholder = "12000";
            pageInput.AccessibleName = "Размер страницы ответа";
            pageInput.Submitted += delegate { ApplyPage(); };
            pageInput.LostFocus += delegate { ApplyPage(); };
            settingsNote = new GlassLabel(string.Empty, Theme.SmallFont, Theme.Secondary);
            settingsNote.Wrap = true;
            optionControls.AddRange(new Control[] { fullCaption, fullSwitch, editsCaption, editsSwitch, localCaption, localSwitch,
                endpointCaption, modelButton, pageCaption, pageInput, settingsNote });

            statusLabel = new GlassLabel(string.Empty, Theme.SmallFont, Theme.Secondary);

            Controls.Add(header);
            Controls.Add(power);
            Controls.Add(closeButton);
            Controls.Add(tabs);
            Controls.Add(sessionsCard);
            Controls.Add(logCard);
            Controls.Add(rootsCard);
            foreach (Control control in optionControls) Controls.Add(control);
            Controls.Add(statusLabel);
            Controls.Add(stopButton);
            Controls.Add(disconnectButton);
            Controls.Add(labelsButton);
            Controls.Add(detailsButton);
            Controls.Add(clearButton);
        }

        private static GlassLabel Caption(string text)
        {
            return new GlassLabel(text, Theme.SmallFont, Theme.Secondary);
        }

        // ---------------------------------------------------------------- раскладка

        private List<GlassButton> FooterButtons()
        {
            List<GlassButton> buttons = new List<GlassButton>();
            if (tabs.SelectedIndex == 0)
            {
                buttons.Add(stopButton);
                buttons.Add(disconnectButton);
                buttons.Add(labelsButton);
            }
            else if (tabs.SelectedIndex == 1)
            {
                buttons.Add(detailsButton);
                buttons.Add(clearButton);
            }
            return buttons;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (statusLabel == null)
            {
                return;
            }
            int pad = Dpi.S(14);
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int headerHeight = Dpi.S(44);
            int close = Dpi.S(30);
            closeButton.SetBounds(width - pad - close, pad + (headerHeight - close) / 2, close, close);
            int powerWidth = power.PreferredWidth;
            power.SetBounds(closeButton.Left - Dpi.S(12) - powerWidth, pad + (headerHeight - Dpi.S(30)) / 2, powerWidth, Dpi.S(30));
            header.SetBounds(pad + Dpi.S(4), pad, Math.Max(0, power.Left - pad - Dpi.S(20)), headerHeight);
            tabs.SetBounds(pad, header.Bottom + Dpi.S(10), tabs.PreferredWidth, Dpi.S(32));

            int footer = Dpi.S(50);
            int footerTop = height - pad - footer;
            int buttonHeight = Dpi.S(38);
            int buttonTop = footerTop + (footer - buttonHeight) / 2;
            int left = width - pad;
            foreach (GlassButton button in FooterButtons())
            {
                int buttonWidth = button.PreferredWidth(buttonHeight);
                left -= buttonWidth;
                button.SetBounds(left, buttonTop, buttonWidth, buttonHeight);
                left -= Dpi.S(10);
            }
            statusLabel.SetBounds(pad + Dpi.S(6), footerTop, Math.Max(0, left - pad - Dpi.S(6)), footer);

            int top = tabs.Bottom + Dpi.S(12);
            Rectangle body = new Rectangle(pad, top, width - 2 * pad, Math.Max(0, footerTop - Dpi.S(6) - top));
            sessionsCard.Bounds = body;
            logCard.Bounds = body;
            int optionsWidth = Dpi.S(340);
            rootsCard.SetBounds(body.X, body.Y, Math.Max(Dpi.S(220), body.Width - optionsWidth - Dpi.S(18)), body.Height);
            int x = rootsCard.Right + Dpi.S(18);
            int rowWidth = Math.Max(0, body.Right - x);
            int y = body.Y + Dpi.S(2);
            Row(fullCaption, fullSwitch, x, ref y, rowWidth);
            Row(editsCaption, editsSwitch, x, ref y, rowWidth);
            Row(localCaption, localSwitch, x, ref y, rowWidth);
            Row(endpointCaption, modelButton, x, ref y, rowWidth);
            Row(pageCaption, pageInput, x, ref y, rowWidth);
            settingsNote.SetBounds(x, y, rowWidth, Math.Max(0, body.Bottom - y));
        }

        private static void Row(Control caption, Control control, int x, ref int y, int width)
        {
            caption.SetBounds(x, y, width, Dpi.S(22));
            y += Dpi.S(24);
            Segmented segmented = control as Segmented;
            int controlWidth = segmented != null ? Math.Min(width, segmented.PreferredWidth) : width;
            control.SetBounds(x, y, controlWidth, Dpi.S(30));
            y += Dpi.S(30) + Dpi.S(14);
        }

        private void ShowTab(int index)
        {
            sessionsCard.Visible = index == 0;
            stopButton.Visible = index == 0;
            disconnectButton.Visible = index == 0;
            labelsButton.Visible = index == 0;
            logCard.Visible = index == 1;
            detailsButton.Visible = index == 1;
            clearButton.Visible = index == 1;
            rootsCard.Visible = index == 2;
            foreach (Control control in optionControls) control.Visible = index == 2;
            UpdateButtons();
            UpdateStatus();
            PerformLayout();
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            refresh.Start();
            sessionsList.Focus();
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
            if (keyData == (Keys.Control | Keys.F))
            {
                BlockHeader search = tabs.SelectedIndex == 0 ? sessionsHeader : tabs.SelectedIndex == 1 ? logHeader : rootsHeader;
                search.OpenSearch();
                return true;
            }
            if (keyData == (Keys.Control | Keys.D1) || keyData == (Keys.Control | Keys.D2) || keyData == (Keys.Control | Keys.D3))
            {
                tabs.SelectedIndex = keyData == (Keys.Control | Keys.D1) ? 0 : keyData == (Keys.Control | Keys.D2) ? 1 : 2;
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

        // ---------------------------------------------------------------- состояние

        /// <summary>Строка состояния для шапки окна и меню трея.</summary>
        internal static string Describe(BridgeStatus status)
        {
            if (!status.Running)
            {
                return status.Problem != null ? "Не запустился. " + status.Problem : "Выключен";
            }
            if (status.Settings.Problem != null)
            {
                return "Работает, но настройки не читаются";
            }
            int count = status.Sessions.Count;
            return count == 0 ? "Работает, агентов нет" : "Работает, агентов: " + count.ToString(CultureInfo.InvariantCulture);
        }

        private void RefreshStatus()
        {
            BridgeStatus status;
            try
            {
                status = bridge.Snapshot();
            }
            catch (Exception failure)
            {
                statusLabel.Text = failure.Message;
                return;
            }
            if (shown != null && status.Signature == shown.Signature)
            {
                return;
            }
            shown = status;
            Render();
        }

        private void RefreshNow()
        {
            shown = null;
            RefreshStatus();
        }

        private void Render()
        {
            rendering = true;
            try
            {
                header.Subtitle = Describe(shown);
                power.SelectedIndex = shown.Running ? 0 : 1;
                BridgeSettings settings = shown.Settings;
                fullSwitch.SelectedIndex = settings.AllowFull ? 0 : 1;
                editsSwitch.SelectedIndex = settings.AllowEdits ? 0 : 1;
                localSwitch.SelectedIndex = settings.AllowLocalModel ? 0 : 1;
                endpointCaption.Text = "Модель: " + settings.LocalModelId;
                endpointCaption.AccessibleDescription = settings.LocalModel;
                if (!pageInput.Focused) pageInput.Value = settings.PageChars.ToString(CultureInfo.InvariantCulture);
                if (settings.Problem != null)
                {
                    settingsNote.ForeColor = Theme.Danger;
                    settingsNote.Text = settings.Problem + " Любое изменение здесь запишет новый файл, старый останется рядом с датой в имени.";
                }
                else if (settings.Roots.Count == 0)
                {
                    settingsNote.ForeColor = Theme.Warm;
                    settingsNote.Text = "Папки не выбраны: агентам доступны все файлы пользователя. Добавьте папку, чтобы ограничить доступ.";
                }
                else
                {
                    settingsNote.ForeColor = Theme.Secondary;
                    settingsNote.Text = "Изменения действуют сразу, в том числе для подключённых агентов. Ответы агенту обезличиваются всегда.";
                }
                RenderLists();
            }
            finally
            {
                rendering = false;
            }
        }

        private void RenderLists()
        {
            if (shown == null)
            {
                return;
            }
            string query = sessionsHeader.Query.Trim();
            List<object> sessions = new List<object>();
            // Сначала подключённые, ниже отключившиеся за последний час.
            List<BridgeSessionInfo> all = new List<BridgeSessionInfo>(shown.Sessions);
            all.AddRange(shown.Recent);
            foreach (BridgeSessionInfo session in all)
            {
                if (Matches(query, session.Client, session.Folder, session.State)) sessions.Add(session);
            }
            HashSet<int> keepSessions = SelectedIds(sessionsList);
            sessionsList.SetItems(sessions, delegate(object item) { return keepSessions.Contains(((BridgeSessionInfo)item).Id); });
            sessionsList.EmptyText = all.Count > 0 ? "Ничего не найдено."
                : !shown.Running ? "Мост выключен. Включите его переключателем вверху окна."
                : "Агентов нет. Они подключаются через SafePaste.exe --mcp в настройках Codex или Claude.";
            sessionsHeader.SetCounter(query.Length == 0 ? string.Empty : sessions.Count.ToString(CultureInfo.InvariantCulture));

            query = logHeader.Query.Trim();
            List<object> calls = new List<object>();
            for (int i = shown.Calls.Count - 1; i >= 0; i--)
            {
                BridgeCallInfo call = shown.Calls[i];
                if (Matches(query, call.Tool, call.Client, call.Request, call.Outcome, call.Response)) calls.Add(call);
            }
            HashSet<int> keepCalls = SelectedIds(logList);
            logList.SetItems(calls, delegate(object item) { return keepCalls.Contains(((BridgeCallInfo)item).Id); });
            logList.EmptyText = shown.Calls.Count == 0 ? "Вызовов пока не было. Журнал хранится только в памяти." : "Ничего не найдено.";
            logHeader.SetCounter(query.Length == 0 ? string.Empty : calls.Count.ToString(CultureInfo.InvariantCulture));

            query = rootsHeader.Query.Trim();
            List<object> roots = new List<object>();
            foreach (string root in shown.Settings.Roots)
            {
                if (Matches(query, root)) roots.Add(root);
            }
            HashSet<string> keepRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in rootsList.SelectedItems) keepRoots.Add((string)item);
            rootsList.SetItems(roots, delegate(object item) { return keepRoots.Contains((string)item); });
            rootsList.EmptyText = shown.Settings.Roots.Count == 0 ? "Папок нет: агентам доступны все файлы пользователя." : "Ничего не найдено.";
            rootsHeader.SetCounter(query.Length == 0 ? string.Empty : roots.Count.ToString(CultureInfo.InvariantCulture));
            UpdateButtons();
            UpdateStatus();
        }

        private static HashSet<int> SelectedIds(GlassList list)
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (object item in list.SelectedItems)
            {
                BridgeSessionInfo session = item as BridgeSessionInfo;
                BridgeCallInfo call = item as BridgeCallInfo;
                ids.Add(session != null ? session.Id : call != null ? call.Id : 0);
            }
            return ids;
        }

        private static bool Matches(string query, params string[] values)
        {
            if (query.Length == 0)
            {
                return true;
            }
            foreach (string value in values)
            {
                if (value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private void UpdateButtons()
        {
            BridgeSessionInfo session = SelectedSession;
            stopButton.Enabled = session != null && !session.Closed && session.Task > 0;
            disconnectButton.Enabled = session != null && !session.Closed;
            detailsButton.Enabled = logList.SelectedItems.Count > 0;
            clearButton.Enabled = shown != null && shown.Calls.Count > 0;
            removeRootButton.Enabled = rootsList.SelectedItems.Count > 0;
        }

        private void UpdateStatus()
        {
            if (!string.IsNullOrEmpty(message))
            {
                statusLabel.Text = message;
                return;
            }
            if (shown == null)
            {
                statusLabel.Text = string.Empty;
                return;
            }
            if (tabs.SelectedIndex == 0)
            {
                if (shown.Calls.Count == 0)
                {
                    statusLabel.Text = "Вызовов пока не было.";
                }
                else
                {
                    BridgeCallInfo last = shown.Calls[shown.Calls.Count - 1];
                    statusLabel.Text = "Последний вызов: " + last.Tool + " от " + last.Client + " в "
                        + last.Started.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ", " + last.Outcome + ".";
                }
            }
            else if (tabs.SelectedIndex == 1)
            {
                List<object> chosen = logList.SelectedItems;
                string answer = chosen.Count == 1 ? FirstLine(((BridgeCallInfo)chosen[0]).Response) : string.Empty;
                statusLabel.Text = answer.Length > 0 ? "Ответ: " + answer
                    : "Вызовов в журнале: " + shown.Calls.Count.ToString(CultureInfo.InvariantCulture)
                        + ". Двойной щелчок показывает запрос и ответ.";
            }
            else
            {
                statusLabel.Text = "Настройки лежат в bridge.json в папке данных SafePaste.";
            }
        }

        private BridgeSessionInfo SelectedSession
        {
            get
            {
                List<object> items = sessionsList.SelectedItems;
                return items.Count > 0 ? (BridgeSessionInfo)items[0] : null;
            }
        }

        // ---------------------------------------------------------------- действия

        private void SetPower(bool enabled)
        {
            if (rendering)
            {
                return;
            }
            if (!enabled && shown != null && shown.Sessions.Count > 0)
            {
                bool confirmed = Alerts.Confirm(this, AlertKind.Warning, "Выключить мост?",
                    "Подключённых агентов: " + shown.Sessions.Count.ToString(CultureInfo.InvariantCulture)
                    + ". Они потеряют связь с мостом, их команды будут остановлены.", "Выключить", "Отмена", true);
                if (!confirmed)
                {
                    rendering = true;
                    power.SelectedIndex = 0;
                    rendering = false;
                    return;
                }
            }
            ApplyPower(enabled);
        }

        private void ApplyPower(bool enabled)
        {
            try
            {
                bridge.SetEnabled(enabled);
                message = enabled ? "Мост включён." : "Мост выключен, агенты отключены.";
            }
            catch (Exception failure)
            {
                message = failure.Message;
            }
            RefreshNow();
        }

        private void ChangeSettings(Action<BridgeSettings> change, string done)
        {
            if (rendering)
            {
                return;
            }
            try
            {
                bridge.UpdateSettings(change);
                message = done;
            }
            catch (Exception failure)
            {
                message = "Настройки не сохранены: " + failure.Message;
            }
            RefreshNow();
        }

        private void ApplyPage()
        {
            if (rendering || shown == null)
            {
                return;
            }
            int value;
            if (!int.TryParse(pageInput.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                || value < 1000 || value > 100000)
            {
                message = "Размер страницы: число от 1000 до 100000.";
                UpdateStatus();
                return;
            }
            if (value == shown.Settings.PageChars)
            {
                return;
            }
            ChangeSettings(delegate(BridgeSettings next) { next.PageChars = value; }, "Размер страницы сохранён.");
        }

        private void ChooseRoot()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Папка, которую агенты смогут читать и править через мост";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    AddRoot(dialog.SelectedPath);
                }
            }
        }

        private void AddRoot(string path)
        {
            string problem;
            string root = BridgeSettings.NormalizeRoot(path, out problem);
            if (root == null)
            {
                message = "Папка не добавлена: " + problem + ".";
                UpdateStatus();
                return;
            }
            ChangeSettings(delegate(BridgeSettings next) { BridgeSettings.AddUnique(next.Roots, root); }, "Папка добавлена: " + root);
        }

        private void RemoveRoots()
        {
            List<string> chosen = new List<string>();
            foreach (object item in rootsList.SelectedItems) chosen.Add((string)item);
            if (chosen.Count == 0 || shown == null)
            {
                return;
            }
            if (chosen.Count >= shown.Settings.Roots.Count)
            {
                bool confirmed = Alerts.Confirm(this, AlertKind.Warning, "Убрать все папки?",
                    "Без списка папок агентам станут доступны все файлы пользователя. Ответы по-прежнему обезличиваются.",
                    "Убрать", "Отмена", true);
                if (!confirmed)
                {
                    return;
                }
            }
            RemoveRootsNow(chosen);
        }

        private void RemoveRootsNow(List<string> chosen)
        {
            ChangeSettings(delegate(BridgeSettings next)
            {
                next.Roots.RemoveAll(delegate(string root)
                {
                    foreach (string item in chosen)
                    {
                        if (item.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    return false;
                });
            }, "Папок убрано: " + chosen.Count.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private void StopSelected()
        {
            BridgeSessionInfo session = SelectedSession;
            if (session == null || session.Closed)
            {
                return;
            }
            bridge.StopTask(session.Id);
            message = "Команда агента " + session.Client + " остановлена.";
            RefreshNow();
        }

        private void DisconnectSelected()
        {
            BridgeSessionInfo session = SelectedSession;
            if (session == null || session.Closed)
            {
                return;
            }
            bridge.Disconnect(session.Id);
            message = "Агент " + session.Client + " отключён. Клиент покажет, что сервер safepaste остановлен.";
            RefreshNow();
        }

        private void ClearLog()
        {
            bridge.ClearLog();
            message = "Журнал очищен.";
            RefreshNow();
        }

        private void ShowDetails()
        {
            List<object> items = logList.SelectedItems;
            if (items.Count == 0)
            {
                return;
            }
            BridgeCallInfo call = (BridgeCallInfo)items[0];
            using (BridgeTextForm form = new BridgeTextForm("Вызов " + call.Tool, DescribeCall(call), reveal))
            {
                form.ShowDialog(this);
            }
        }

        internal static string DescribeCall(BridgeCallInfo call)
        {
            StringBuilder text = new StringBuilder();
            text.Append("Агент: ").Append(call.Client).Append('\n');
            text.Append("Время: ").Append(call.Started.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (call.Outcome != "идёт")
            {
                text.Append(", ").Append(FormatDuration(call.Duration));
            }
            text.Append('\n');
            text.Append("Итог: ").Append(call.Outcome).Append("\n\n");
            text.Append("Запрос агента:\n").Append(call.Request.Length == 0 ? "без параметров" : call.Request).Append("\n\n");
            text.Append("Ответ агенту:\n").Append(call.Response);
            if (call.Truncated)
            {
                text.Append("\n\nПоказано начало ответа, 4000 символов.");
            }
            return text.ToString();
        }

        /// <summary>Первая непустая строка ответа: по ней видно, чем кончился вызов.</summary>
        private static string FirstLine(string text)
        {
            foreach (string line in (text ?? string.Empty).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed.Length > 160 ? trimmed.Substring(0, 160) + "..." : trimmed;
                }
            }
            return string.Empty;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalSeconds.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " с";
        }

        // ---------------------------------------------------------------- отрисовка строк

        private static void PaintSession(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered)
        {
            BridgeSessionInfo session = (BridgeSessionInfo)item;
            bool closed = session.Closed;
            switch (column)
            {
                case 0:
                    Theme.DrawText(graphics, session.Client, closed ? Theme.UiFont : Theme.StrongFont,
                        closed ? Theme.Tertiary : Theme.Text, bounds);
                    break;
                case 1:
                    Theme.DrawText(graphics, string.IsNullOrEmpty(session.Folder) ? "папка не известна" : session.Folder,
                        Theme.UiFont, closed || string.IsNullOrEmpty(session.Folder) ? Theme.Tertiary : Theme.Secondary, bounds);
                    break;
                case 2:
                    Theme.DrawText(graphics, session.Connected.ToString("HH:mm", CultureInfo.InvariantCulture), Theme.SmallMonoFont, Theme.Tertiary, bounds);
                    break;
                case 3:
                    Theme.DrawText(graphics, session.Calls.ToString(CultureInfo.InvariantCulture), Theme.SmallMonoFont,
                        closed ? Theme.Tertiary : Theme.Secondary, bounds, StringAlignment.Far, StringAlignment.Center, false);
                    break;
                default:
                    Color color = closed ? Theme.Tertiary
                        : session.State.StartsWith("ждёт вашего", StringComparison.Ordinal) ? Theme.Warm
                        : session.State == "ждёт запроса" ? Theme.Tertiary : Theme.Accent;
                    Theme.DrawText(graphics, session.State, Theme.UiFont, color, bounds);
                    break;
            }
        }

        private static void PaintCall(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered)
        {
            BridgeCallInfo call = (BridgeCallInfo)item;
            switch (column)
            {
                case 0:
                    Theme.DrawText(graphics, call.Started.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Theme.SmallMonoFont, Theme.Tertiary, bounds);
                    break;
                case 1:
                    Theme.DrawText(graphics, call.Client, Theme.UiFont, Theme.Secondary, bounds);
                    break;
                case 2:
                    Theme.DrawText(graphics, call.Tool, Theme.SmallMonoFont, Theme.Accent, bounds);
                    break;
                case 3:
                    Theme.DrawText(graphics, call.Request, Theme.UiFont, Theme.Text, bounds);
                    break;
                default:
                    Color color = call.Outcome == "ошибка" ? Theme.Danger : call.Outcome == "отклонено" ? Theme.Warm
                        : call.Outcome == "идёт" ? Theme.Accent : Theme.Secondary;
                    Theme.DrawText(graphics, call.Outcome, Theme.UiFont, color, bounds);
                    break;
            }
        }

        private static void PaintRoot(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered)
        {
            Theme.DrawText(graphics, (string)item, Theme.UiFont, Theme.Text, bounds);
        }
    }
}
