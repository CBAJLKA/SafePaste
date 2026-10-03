using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using SafePaste.Bridge;
using SafePaste.Storage;

namespace SafePaste.Ui
{
    /// <summary>
    /// Просмотр и очистка того, что SafePaste запомнил: иначе ошибку не отменить. Здесь и правила
    /// пользователя, и метки, запомненные при вставках: их можно забыть отдельной кнопкой, правила при этом
    /// остаются.
    /// </summary>
    internal sealed class DatabaseForm : GlassForm
    {
        private WindowHeader header;
        private GlassButton closeButton;
        private BlockHeader searchHeader;
        private Segmented filter;
        private GlassList list;
        private GlassCard card;
        private GlassLabel statusLabel;
        private GlassButton forgetButton;
        private GlassButton folderButton;
        private GlassButton resetButton;
        private GlassButton deleteButton;
        private readonly List<Entry> entries = new List<Entry>();
        private string summary = string.Empty;
        private int rememberedCount;
        private DateTime rulesStamp;
        private DateTime labelsStamp;

        private enum EntryKind
        {
            Learned,
            Allowed,
            Reserved,
            Remembered,
            NotSecret
        }

        private sealed class Entry
        {
            internal EntryKind Kind;
            internal string Value;
            internal string Type;
            internal string Rule;
            internal string Placeholder;
        }

        internal DatabaseForm()
        {
            BuildLayout();
            LoadEntries();
        }

        private void BuildLayout()
        {
            Text = "SafePaste: правила и исключения";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Dpi.S(820), Dpi.S(580));
            MinimumSize = new Size(Dpi.S(640), Dpi.S(420));

            header = new WindowHeader("Правила и исключения", "Что SafePaste помнит между вставками");
            closeButton = new GlassButton(Glyphs.Close, null, ButtonKind.Close, "Закрыть\nEsc");
            closeButton.TabStop = false;
            closeButton.Click += delegate { Close(); };

            list = new GlassList();
            list.AccessibleName = "Сохранённые правила";
            list.AddColumn("Правило", 190, false);
            list.AddColumn("Тип", 120, false);
            list.AddColumn("Значение", 0, false);
            list.Painter = PaintCell;
            list.TipProvider = DescribeEntry;
            list.SelectionChanged += delegate { deleteButton.Enabled = list.SelectedItems.Count > 0; };
            list.DeleteRequested += delegate { DeleteSelected(); };
            filter = new Segmented(new string[] { "Все", "Скрывать", "Исключения", "Номера", "Запомнено" });
            filter.AccessibleName = "Какие правила показывать";
            filter.SelectedChanged += delegate { RenderEntries(); };
            searchHeader = new BlockHeader("Сохранённые значения", Theme.Text, "Поиск по значению или типу\nCtrl+F");
            searchHeader.SearchChanged += delegate { RenderEntries(); };
            searchHeader.SearchStep += delegate { list.Focus(); };
            searchHeader.AddTool(filter, filter.PreferredWidth);
            card = new GlassCard(searchHeader, list);

            statusLabel = new GlassLabel(string.Empty, Theme.SmallFont, Theme.Secondary);
            forgetButton = new GlassButton(Glyphs.Erase, null, ButtonKind.Glass,
                "Забыть запомненные метки\nПравила и исключения останутся");
            forgetButton.Click += delegate { ForgetRemembered(); };
            folderButton = new GlassButton(Glyphs.Folder, null, ButtonKind.Glass, "Открыть папку с данными");
            folderButton.Click += delegate { OpenFolder(); };
            resetButton = new GlassButton(Glyphs.Reset, null, ButtonKind.Glass, "Сбросить все правила");
            resetButton.Click += delegate { ResetDatabase(); };
            deleteButton = new GlassButton(Glyphs.Delete, null, ButtonKind.Glass, "Удалить выбранное\nDelete");
            deleteButton.Enabled = false;
            deleteButton.Click += delegate { DeleteSelected(); };

            Controls.Add(header);
            Controls.Add(card);
            Controls.Add(statusLabel);
            Controls.Add(forgetButton);
            Controls.Add(folderButton);
            Controls.Add(resetButton);
            Controls.Add(deleteButton);
            Controls.Add(closeButton);
            card.TabIndex = 0;
            deleteButton.TabIndex = 1;
            resetButton.TabIndex = 2;
            folderButton.TabIndex = 3;
            forgetButton.TabIndex = 4;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (deleteButton == null)
            {
                return;
            }
            int pad = Dpi.S(14);
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int close = Dpi.S(30);
            header.SetBounds(pad + Dpi.S(4), pad, width - 2 * pad - close - Dpi.S(10), Dpi.S(44));
            closeButton.SetBounds(width - pad - close, pad + (Dpi.S(44) - close) / 2, close, close);
            int footer = Dpi.S(50);
            int footerTop = height - pad - footer;
            int size = Dpi.S(38);
            int buttonTop = footerTop + (footer - size) / 2;
            deleteButton.SetBounds(width - pad - size, buttonTop, size, size);
            resetButton.SetBounds(deleteButton.Left - Dpi.S(10) - size, buttonTop, size, size);
            folderButton.SetBounds(resetButton.Left - Dpi.S(10) - size, buttonTop, size, size);
            forgetButton.SetBounds(folderButton.Left - Dpi.S(10) - size, buttonTop, size, size);
            statusLabel.SetBounds(pad + Dpi.S(6), footerTop, Math.Max(0, forgetButton.Left - pad - Dpi.S(16)), footer);
            int top = header.Bottom + Dpi.S(12);
            card.SetBounds(pad, top, width - 2 * pad, Math.Max(0, footerTop - Dpi.S(6) - top));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            list.Focus();
        }

        /// <summary>Пока окно было в фоне, вставка могла запомнить новые метки, а окно проверки добавить правило.</summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (Stamp(Paths.DatabaseFile) != rulesStamp || Stamp(Paths.LabelsFile) != labelsStamp)
            {
                LoadEntries();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                searchHeader.OpenSearch();
                return true;
            }
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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

        private void LoadEntries()
        {
            entries.Clear();
            rulesStamp = Stamp(Paths.DatabaseFile);
            labelsStamp = Stamp(Paths.LabelsFile);
            string labelProblem = null;
            try
            {
                SafePasteDatabase database = SafePasteDatabase.Load();
                foreach (ReservedPlaceholder reservation in database.Reservations)
                {
                    Add(EntryKind.Reserved, reservation.Value, reservation.Type, "Номер " + reservation.Placeholder, null);
                }
                foreach (LearnedValue learned in database.Learned)
                {
                    Add(EntryKind.Learned, learned.Value, learned.Type, "Скрывать всегда", null);
                }
                foreach (string allowed in database.Allowed)
                {
                    Add(EntryKind.Allowed, allowed, string.Empty, "Не скрывать", null);
                }
                foreach (string notSecret in database.NotSecrets)
                {
                    Add(EntryKind.NotSecret, notSecret, "SECRET", "Не пароль", null);
                }
                rememberedCount = 0;
                try
                {
                    foreach (LabelEntry label in LabelMemory.Snapshot())
                    {
                        Add(EntryKind.Remembered, label.Value, label.Type, "Запомнено " + label.Placeholder, label.Placeholder);
                        rememberedCount++;
                    }
                }
                catch (DatabaseException)
                {
                    labelProblem = "Запомненные метки не читаются. Кнопка «Забыть запомненные метки» начнёт их заново.";
                }
                summary = "Номеров: " + database.Reservations.Count.ToString(CultureInfo.InvariantCulture)
                    + ", скрывать: " + database.Learned.Count.ToString(CultureInfo.InvariantCulture)
                    + ", исключений: " + (database.Allowed.Count + database.NotSecrets.Count).ToString(CultureInfo.InvariantCulture)
                    + ", запомнено меток: " + rememberedCount.ToString(CultureInfo.InvariantCulture);
                statusLabel.Text = labelProblem ?? summary;
                forgetButton.Enabled = rememberedCount > 0 || labelProblem != null;
                RenderEntries();
            }
            catch (DatabaseException failure)
            {
                summary = failure.Message;
                statusLabel.Text = failure.Message;
                list.SetItems(new List<object>(), null);
                list.EmptyText = "Не удалось открыть правила. Подробности внизу окна.";
                deleteButton.Enabled = false;
                forgetButton.Enabled = File.Exists(Paths.LabelsFile);
            }
        }

        private void Add(EntryKind kind, string value, string type, string rule, string placeholder)
        {
            Entry entry = new Entry();
            entry.Kind = kind;
            entry.Value = value;
            entry.Type = type;
            entry.Rule = rule;
            entry.Placeholder = placeholder;
            entries.Add(entry);
        }

        private void RenderEntries()
        {
            string query = searchHeader.Query.Trim();
            List<object> visible = new List<object>();
            foreach (Entry entry in entries)
            {
                if (filter.SelectedIndex == 1 && entry.Kind != EntryKind.Learned) continue;
                if (filter.SelectedIndex == 2 && entry.Kind != EntryKind.Allowed && entry.Kind != EntryKind.NotSecret) continue;
                if (filter.SelectedIndex == 3 && entry.Kind != EntryKind.Reserved) continue;
                if (filter.SelectedIndex == 4 && entry.Kind != EntryKind.Remembered) continue;
                if (query.Length > 0 && (entry.Value + " " + entry.Type + " " + TypeNames.Describe(entry.Type) + " " + entry.Rule)
                    .IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                visible.Add(entry);
            }
            list.SetItems(visible, null);
            deleteButton.Enabled = false;
            if (entries.Count == 0)
            {
                list.EmptyText = "Правил пока нет. Отметьте значение в окне проверки, и оно появится здесь.";
            }
            else if (query.Length == 0 && filter.SelectedIndex == 4)
            {
                list.EmptyText = "Запомненных меток нет. Они появятся после вставки, если запоминание включено в настройках.";
            }
            else
            {
                list.EmptyText = "Ничего не найдено. Измените запрос или фильтр.";
            }
            searchHeader.SetCounter(query.Length == 0 ? string.Empty : visible.Count.ToString(CultureInfo.InvariantCulture));
        }

        private string DescribeEntry(object item)
        {
            Entry entry = (Entry)item;
            if (entry.Kind == EntryKind.Remembered)
            {
                return entry.Value + "\nЗапомнено при вставке. Забывается само, если не встречается 30 дней";
            }
            if (entry.Kind == EntryKind.NotSecret)
            {
                return entry.Value + "\nОтмечено «Распознано ошибочно»: детектор не считает это паролем. Удалите строку, чтобы вернуть";
            }
            return entry.Value;
        }

        private void PaintCell(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered)
        {
            Entry entry = (Entry)item;
            if (column == 0)
            {
                Color color = entry.Kind == EntryKind.Allowed || entry.Kind == EntryKind.NotSecret ? Theme.Secondary
                    : entry.Kind == EntryKind.Reserved ? Theme.Accent
                    : entry.Kind == EntryKind.Remembered ? Theme.Fade(Theme.Accent, 0.7f) : Theme.Warm;
                Theme.DrawText(graphics, entry.Rule, Theme.UiFont, color, bounds);
            }
            else if (column == 1)
            {
                Theme.DrawText(graphics, entry.Type, Theme.SmallMonoFont, Theme.Tertiary, bounds);
            }
            else
            {
                Theme.DrawText(graphics, entry.Value, Theme.UiFont, Theme.Text, bounds);
            }
        }

        private void DeleteSelected()
        {
            List<object> chosen = list.SelectedItems;
            if (chosen.Count == 0)
            {
                statusLabel.Text = "Выберите строки, которые нужно удалить.";
                return;
            }
            try
            {
                SafePasteDatabase database = SafePasteDatabase.Load();
                LabelStore labels = null;
                int removed = 0;
                bool rulesChanged = false;
                foreach (object item in chosen)
                {
                    Entry entry = (Entry)item;
                    bool done;
                    if (entry.Kind == EntryKind.Remembered)
                    {
                        if (labels == null)
                        {
                            labels = LabelStore.OpenIfExists();
                        }
                        done = labels != null && labels.Delete(entry.Placeholder);
                    }
                    else
                    {
                        if (entry.Kind == EntryKind.Allowed)
                        {
                            done = database.RemoveAllowed(entry.Value);
                        }
                        else if (entry.Kind == EntryKind.NotSecret)
                        {
                            done = database.RemoveNotSecret(entry.Value);
                        }
                        else if (entry.Kind == EntryKind.Reserved)
                        {
                            done = database.Unreserve(entry.Type, entry.Value);
                        }
                        else
                        {
                            done = database.RemoveLearned(entry.Value, entry.Type);
                        }
                        rulesChanged |= done;
                    }
                    if (done)
                    {
                        removed++;
                    }
                }
                if (rulesChanged)
                {
                    database.Save();
                }
                LoadEntries();
                statusLabel.Text = "Удалено: " + removed.ToString(CultureInfo.InvariantCulture) + ". " + summary;
            }
            catch (Exception failure)
            {
                statusLabel.Text = failure.Message;
            }
        }

        /// <summary>Забывает метки, запомненные при вставках. Правила пользователя не трогаются.</summary>
        private void ForgetRemembered()
        {
            bool confirmed = Alerts.Confirm(this, AlertKind.Warning, "Забыть запомненные метки?",
                "Значения снова будут получать номера по порядку, а старые метки больше не раскроются, "
                + "в том числе у агентов моста. Закреплённые номера, правила и исключения останутся.",
                "Забыть", "Отмена", true);
            if (!confirmed)
            {
                return;
            }
            try
            {
                int removed = LabelMemory.Clear();
                LoadEntries();
                statusLabel.Text = "Забыто меток: " + removed.ToString(CultureInfo.InvariantCulture) + ". " + summary;
            }
            catch (Exception failure)
            {
                statusLabel.Text = failure.Message;
            }
        }

        private void ResetDatabase()
        {
            bool confirmed = Alerts.Confirm(this, AlertKind.Warning, "Сбросить все правила?",
                "Удалятся закреплённые номера, правила «Скрывать всегда» и исключения. "
                + "Старый файл останется рядом, в его имени будет дата. Запомненные метки забываются отдельной кнопкой.",
                "Сбросить", "Отмена", true);
            if (!confirmed)
            {
                return;
            }
            try
            {
                string backup = SafePasteDatabase.Reset();
                LoadEntries();
                statusLabel.Text = backup == null ? "Правил и так не было." : "Правила сброшены, копия: " + backup;
            }
            catch (Exception failure)
            {
                statusLabel.Text = failure.Message;
            }
        }

        private void OpenFolder()
        {
            try
            {
                Process.Start("explorer.exe", "\"" + Paths.DataDirectory + "\"");
            }
            catch (Exception failure)
            {
                statusLabel.Text = failure.Message;
            }
        }
    }
}
