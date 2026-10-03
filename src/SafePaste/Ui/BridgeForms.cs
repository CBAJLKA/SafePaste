using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using SafePaste.Bridge;

namespace SafePaste.Ui
{
    internal sealed class BridgeTextForm : GlassForm
    {
        private readonly WindowHeader header;
        private readonly TextView text;
        private readonly GlassButton close;
        private readonly GlassButton toggle;
        private readonly string masked;
        private readonly Func<string, string> reveal;
        private bool revealed;

        internal BridgeTextForm(string title, string value) : this(title, value, null) { }

        /// <summary>reveal раскрывает метки по кнопке. Реальные значения остаются только на экране.</summary>
        internal BridgeTextForm(string title, string value, Func<string, string> reveal)
        {
            Text = "SafePaste: " + title;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(Dpi.S(500), Dpi.S(350));
            ClientSize = new Size(Dpi.S(760), Dpi.S(520));
            masked = value;
            this.reveal = reveal;
            header = new WindowHeader(title, "Текст показан локально");
            text = new TextView(); text.SetContent(value, null, false);
            close = new GlassButton(null, "Закрыть", ButtonKind.Primary, null);
            close.Click += delegate { Close(); };
            CancelButton = close;
            Controls.Add(header); Controls.Add(text); Controls.Add(close);
            if (reveal != null)
            {
                toggle = new GlassButton(null, "Реальные значения", ButtonKind.Glass, null);
                toggle.Click += delegate { Toggle(); };
                Controls.Add(toggle);
            }
        }

        private void Toggle()
        {
            try
            {
                string shown = revealed ? masked : reveal(masked);
                revealed = !revealed;
                text.SetContent(shown, null, false);
                header.Subtitle = revealed ? "Реальные значения видны только на этом экране" : "Текст показан локально";
                toggle.Caption = revealed ? "Показать метки" : "Реальные значения";
            }
            catch (Exception failure) { header.Subtitle = failure.Message; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (header == null) return;
            int p = Dpi.S(18), h = Dpi.S(38);
            header.SetBounds(p, p, Width - 2 * p, Dpi.S(64));
            text.SetBounds(p, Dpi.S(90), Width - 2 * p, Height - Dpi.S(90) - h - 2 * p);
            close.SetBounds(Width - p - Dpi.S(126), Height - h - p, Dpi.S(126), h);
            if (toggle != null) toggle.SetBounds(close.Left - Dpi.S(10) - Dpi.S(190), Height - h - p, Dpi.S(190), h);
        }
    }

    internal sealed class BridgeLabelsForm : GlassForm
    {
        private readonly LabelStore store;
        private readonly WindowHeader header;
        private readonly BlockHeader search;
        private readonly GlassList list;
        private readonly GlassCard card;
        private readonly GlassButton close;
        private readonly GlassButton delete;
        private readonly GlassButton reserve;
        private readonly GlassButton cleanup;
        private readonly GlassLabel status;
        private List<LabelEntry> entries;

        internal BridgeLabelsForm(LabelStore store)
        {
            this.store = store;
            Text = "SafePaste: метки моста";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(Dpi.S(620), Dpi.S(400));
            ClientSize = new Size(Dpi.S(900), Dpi.S(580));
            header = new WindowHeader("Метки моста", "Значения видны только на этом компьютере");
            search = new BlockHeader("Сохранённые метки", Theme.Text, "Поиск по метке или значению\nCtrl+F");
            search.SearchChanged += delegate { Render(); };
            list = new GlassList();
            list.AddColumn("Метка", 155, false);
            list.AddColumn("Тип", 120, false);
            list.AddColumn("Значение", 0, false);
            list.Painter = PaintCell;
            list.SelectionChanged += delegate { delete.Enabled = list.SelectedItems.Count > 0; reserve.Enabled = list.SelectedItems.Count > 0; };
            card = new GlassCard(search, list);
            close = new GlassButton(null, "Закрыть", ButtonKind.Primary, null);
            close.Click += delegate { Close(); };
            delete = new GlassButton(null, "Удалить", ButtonKind.Glass, null);
            delete.Click += delegate { DeleteSelected(); };
            reserve = new GlassButton(null, "Закрепить номер", ButtonKind.Glass, null);
            reserve.Click += delegate { ReserveSelected(); };
            cleanup = new GlassButton(null, "Очистить старые", ButtonKind.Glass, null);
            cleanup.Click += delegate { try { store.CleanupOld(); Reload(); } catch (Exception error) { status.Text = error.Message; } };
            status = new GlassLabel("", Theme.SmallFont, Theme.Secondary);
            Controls.Add(header); Controls.Add(card); Controls.Add(close);
            Controls.Add(delete); Controls.Add(reserve); Controls.Add(cleanup); Controls.Add(status);
            CancelButton = close;
            Reload();
        }

        private void Reload()
        {
            entries = store.Snapshot();
            Render();
            status.Text = "Меток: " + entries.Count.ToString(CultureInfo.InvariantCulture);
        }

        private void Render()
        {
            if (entries == null) return;
            string query = search.Query.Trim();
            List<object> visible = new List<object>();
            foreach (LabelEntry entry in entries)
                if (query.Length == 0 || (entry.Placeholder + " " + entry.Type + " " + entry.Value)
                    .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) visible.Add(entry);
            list.SetItems(visible, null);
            list.EmptyText = entries.Count == 0 ? "Меток пока нет." : "Ничего не найдено.";
            search.SetCounter(query.Length == 0 ? "" : visible.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static void PaintCell(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered)
        {
            LabelEntry entry = (LabelEntry)item;
            string value = column == 0 ? entry.Placeholder : column == 1 ? entry.Type : entry.Value;
            Theme.DrawText(graphics, value, column == 0 ? Theme.SmallMonoFont : Theme.UiFont,
                column == 0 ? Theme.Accent : Theme.Text, bounds);
        }

        private void DeleteSelected()
        {
            try
            {
                foreach (object item in list.SelectedItems) store.Delete(((LabelEntry)item).Placeholder);
                Reload();
            }
            catch (Exception error) { status.Text = error.Message; }
        }

        private void ReserveSelected()
        {
            try
            {
                foreach (object item in list.SelectedItems) store.Reserve(((LabelEntry)item).Placeholder);
                Reload();
                status.Text = "Номера закреплены в правилах SafePaste.";
            }
            catch (Exception error) { status.Text = error.Message; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (header == null) return;
            int p = Dpi.S(18), h = Dpi.S(38), y = Height - p - h;
            header.SetBounds(p, p, Width - 2 * p, Dpi.S(64));
            card.SetBounds(p, Dpi.S(90), Width - 2 * p, y - Dpi.S(102));
            close.SetBounds(Width - p - Dpi.S(120), y, Dpi.S(120), h);
            delete.SetBounds(close.Left - Dpi.S(130), y, Dpi.S(120), h);
            reserve.SetBounds(delete.Left - Dpi.S(170), y, Dpi.S(160), h);
            cleanup.SetBounds(reserve.Left - Dpi.S(170), y, Dpi.S(160), h);
            status.SetBounds(p, y, Math.Max(0, cleanup.Left - p - Dpi.S(8)), h);
        }
    }
}
