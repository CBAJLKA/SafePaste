using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using SafePaste.Mcp;
using SafePaste.Storage;

namespace SafePaste.Ui
{
    /// <summary>One click connects a currently loaded local model; manual configuration is secondary.</summary>
    internal sealed class LocalModelForm : GlassForm
    {
        private readonly IBridgeControl bridge;
        private readonly WindowHeader header;
        private readonly GlassList models;
        private readonly GlassCard card;
        private readonly GlassLabel hint;
        private readonly GlassLabel status;
        private readonly GlassButton scanButton;
        private readonly GlassButton connectButton;
        private readonly GlassButton ollamaButton;
        private readonly GlassButton advancedButton;
        private readonly GlassButton saveButton;
        private readonly GlassButton closeButton;
        private readonly GlassLabel endpointCaption;
        private readonly GlassInput endpointInput;
        private readonly GlassLabel modelCaption;
        private readonly GlassInput modelInput;
        private bool advanced;
        private bool scanning;
        private bool populating;
        private bool connecting;
        private bool startingOllama;

        internal LocalModelForm(IBridgeControl bridge)
        {
            this.bridge = bridge;
            Text = "SafePaste: локальная модель";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(680), Dpi.S(520));
            MinimumSize = new Size(Dpi.S(560), Dpi.S(500));
            header = new WindowHeader("Локальная модель", "Выберите модель на этом компьютере");
            models = new GlassList();
            models.AccessibleName = "Найденные локальные модели";
            models.AddColumn("Сервер", 142, false);
            models.AddColumn("Модель", 0, false);
            models.Painter = PaintModel;
            models.TipProvider = delegate(object item) { return ((LocalModelChoice)item).Endpoint; };
            models.EmptyText = "Идёт поиск моделей...";
            models.SelectionChanged += delegate
            {
                connectButton.Enabled = models.SelectedItems.Count == 1;
                if (!populating && connectButton.Enabled) Connect();
            };
            models.ItemActivated += delegate { Connect(); };
            card = new GlassCard(new BlockHeader("Найденные модели", Theme.Text, null), models);
            hint = new GlassLabel("SafePaste ищет Ollama (все скачанные модели), LM Studio и llama.cpp только на этом компьютере. "
                + "Модель Ollama не обязательно загружать заранее: сервер загрузит её при первом запросе. "
                + "Для другого порта откройте «Дополнительно».",
                Theme.SmallFont, Theme.Secondary);
            hint.Wrap = true;
            status = new GlassLabel("", Theme.SmallFont, Theme.Secondary);
            status.Wrap = true;
            scanButton = new GlassButton(null, "Найти снова", ButtonKind.Glass, null);
            scanButton.Click += delegate { Scan(); };
            connectButton = new GlassButton(null, "Подключить", ButtonKind.Primary, null);
            connectButton.Enabled = false;
            connectButton.Click += delegate { Connect(); };
            ollamaButton = new GlassButton(null, "Запустить Ollama", ButtonKind.Primary, null);
            ollamaButton.Visible = false;
            ollamaButton.Click += delegate { StartOllama(); };
            advancedButton = new GlassButton(null, "Дополнительно", ButtonKind.Glass, null);
            advancedButton.Click += delegate { advanced = !advanced; ShowAdvanced(); };
            saveButton = new GlassButton(null, "Сохранить вручную", ButtonKind.Glass, null);
            saveButton.Click += delegate { SaveManual(); };
            closeButton = new GlassButton(null, "Закрыть", ButtonKind.Glass, null);
            closeButton.Click += delegate { Close(); };
            CancelButton = closeButton;
            endpointCaption = new GlassLabel("Полный адрес API (/v1/chat/completions)", Theme.SmallFont, Theme.Secondary);
            endpointInput = new GlassInput(); endpointInput.ShowGlyph = false;
            endpointInput.AccessibleName = "Адрес локальной модели";
            modelCaption = new GlassLabel("Имя модели для API", Theme.SmallFont, Theme.Secondary);
            modelInput = new GlassInput(); modelInput.ShowGlyph = false;
            modelInput.AccessibleName = "Имя локальной модели";
            BridgeSettings settings = bridge.Snapshot().Settings;
            endpointInput.Value = settings.LocalModel;
            modelInput.Value = settings.LocalModelId;
            Controls.Add(header); Controls.Add(card); Controls.Add(hint); Controls.Add(status);
            Controls.Add(scanButton); Controls.Add(connectButton); Controls.Add(ollamaButton); Controls.Add(advancedButton);
            Controls.Add(saveButton); Controls.Add(closeButton);
            Controls.Add(endpointCaption); Controls.Add(endpointInput); Controls.Add(modelCaption); Controls.Add(modelInput);
            ShowAdvanced();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Scan();
        }

        private void Scan()
        {
            if (scanning) return;
            scanning = true;
            scanButton.Enabled = false;
            connectButton.Enabled = false;
            status.Text = "Ищем модели на этом компьютере...";
            models.EmptyText = "Идёт поиск моделей...";
            models.SetItems(new object[0], null);
            string configured = bridge.Snapshot().Settings.LocalModel;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<LocalModelChoice> found = LocalModelDiscovery.Scan(configured);
                OllamaState ollama = LocalModelDiscovery.DetectOllama();
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate { ScanFinished(found, ollama); }); }
                catch (InvalidOperationException) { }
            });
        }

        private void ScanFinished(List<LocalModelChoice> found, OllamaState ollama)
        {
            if (IsDisposed) return;
            scanning = false;
            scanButton.Enabled = true;
            // Моделей нет, а Ollama установлена, но не запущена: вместо «Подключить» предлагаем её запустить.
            bool offerStart = ollama == OllamaState.Stopped && found.Count == 0 && !startingOllama;
            ollamaButton.Visible = offerStart;
            connectButton.Visible = !offerStart;
            BridgeSettings settings = bridge.Snapshot().Settings;
            List<object> items = new List<object>();
            foreach (LocalModelChoice choice in found) items.Add(choice);
            populating = true;
            try
            {
                models.SetItems(items, delegate(object item)
                {
                    LocalModelChoice choice = (LocalModelChoice)item;
                    return choice.Endpoint == settings.LocalModel && choice.ModelId == settings.LocalModelId;
                });
            }
            finally { populating = false; }
            connectButton.Enabled = models.SelectedItems.Count == 1;
            models.EmptyText = "Моделей не найдено. Запустите Ollama, LM Studio или llama.cpp и нажмите «Найти снова».";
            if (found.Count > 0)
            {
                status.Text = "Найдено моделей: " + found.Count + ". Щёлкните по нужной модели, чтобы подключить её.";
            }
            else if (ollama == OllamaState.Stopped)
            {
                status.Text = "Ollama установлена, но не запущена. Нажмите «Запустить Ollama», поиск повторится сам.";
            }
            else if (ollama == OllamaState.Running)
            {
                status.Text = "Ollama работает, но скачанных моделей нет. Скачайте модель, например: ollama pull qwen2.5";
            }
            else
            {
                status.Text = "Модели не найдены. Проверьте рекомендации ниже.";
            }
        }

        /// <summary>Запускает установленную Ollama и ищет модели снова, когда сервер ответит.</summary>
        private void StartOllama()
        {
            if (startingOllama) return;
            try { LocalModelDiscovery.StartOllama(); }
            catch (Exception failure)
            {
                status.Text = "Не удалось запустить Ollama: " + failure.Message;
                return;
            }
            startingOllama = true;
            ollamaButton.Enabled = false;
            status.Text = "Запускаем Ollama...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Сервер поднимается несколько секунд; ждём не дольше 20.
                for (int i = 0; i < 40 && LocalModelDiscovery.DetectOllama() != OllamaState.Running; i++)
                    Thread.Sleep(500);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        startingOllama = false;
                        ollamaButton.Enabled = true;
                        Scan();
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void Connect()
        {
            if (connecting || models.SelectedItems.Count != 1) return;
            connecting = true;
            LocalModelChoice choice = (LocalModelChoice)models.SelectedItems[0];
            try
            {
                bridge.UpdateSettings(delegate(BridgeSettings next)
                {
                    next.LocalModel = choice.Endpoint;
                    next.LocalModelId = choice.ModelId;
                    next.AllowLocalModel = true;
                });
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception failure) { status.Text = "Не удалось подключить: " + failure.Message; connecting = false; }
        }

        private void SaveManual()
        {
            string endpoint = endpointInput.Value.Trim();
            string id = modelInput.Value.Trim();
            if (!BridgeSettings.IsLocalEndpoint(endpoint))
            {
                status.Text = "Укажите адрес API на этом компьютере, например http://127.0.0.1:1234/v1/chat/completions.";
                return;
            }
            if (id.Length == 0)
            {
                status.Text = "Укажите имя модели, которое принимает сервер API.";
                return;
            }
            try
            {
                bridge.UpdateSettings(delegate(BridgeSettings next)
                {
                    next.LocalModel = endpoint;
                    next.LocalModelId = id;
                    next.AllowLocalModel = true;
                });
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception failure) { status.Text = "Не удалось сохранить: " + failure.Message; }
        }

        private void ShowAdvanced()
        {
            endpointCaption.Visible = advanced;
            endpointInput.Visible = advanced;
            modelCaption.Visible = advanced;
            modelInput.Visible = advanced;
            saveButton.Visible = advanced;
            advancedButton.Caption = advanced ? "Скрыть дополнительно" : "Дополнительно";
            PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (header == null) return;
            int p = Dpi.S(18), gap = Dpi.S(10), h = Dpi.S(38), bottom = Height - p - h;
            header.SetBounds(p, p, Width - 2 * p, Dpi.S(60));
            int extra = advanced ? Dpi.S(130) : 0;
            card.SetBounds(p, Dpi.S(86), Width - 2 * p, Math.Max(Dpi.S(100), bottom - Dpi.S(86) - Dpi.S(110) - extra));
            hint.SetBounds(p + Dpi.S(4), card.Bottom + gap, Width - 2 * p - Dpi.S(8), Dpi.S(48));
            if (advanced)
            {
                int y = hint.Bottom + Dpi.S(4);
                int half = (Width - 2 * p - gap) / 2;
                endpointCaption.SetBounds(p, y, half, Dpi.S(22));
                modelCaption.SetBounds(p + half + gap, y, half, Dpi.S(22));
                endpointInput.SetBounds(p, y + Dpi.S(23), half, Dpi.S(32));
                modelInput.SetBounds(p + half + gap, y + Dpi.S(23), half, Dpi.S(32));
                saveButton.SetBounds(Width - p - Dpi.S(180), y + Dpi.S(62), Dpi.S(180), Dpi.S(34));
            }
            status.SetBounds(p + Dpi.S(4), bottom - Dpi.S(42), Width - 2 * p - Dpi.S(8), Dpi.S(38));
            scanButton.SetBounds(p, bottom, Dpi.S(120), h);
            advancedButton.SetBounds(scanButton.Right + gap, bottom, Dpi.S(176), h);
            closeButton.SetBounds(Width - p - Dpi.S(110), bottom, Dpi.S(110), h);
            connectButton.SetBounds(closeButton.Left - gap - Dpi.S(134), bottom, Dpi.S(134), h);
            int start = ollamaButton.PreferredWidth(h);
            ollamaButton.SetBounds(closeButton.Left - gap - start, bottom, start, h);
        }

        private static void PaintModel(Graphics graphics, RectangleF bounds, object item, int column, bool selected, bool hovered)
        {
            LocalModelChoice choice = (LocalModelChoice)item;
            if (column == 0)
            {
                Theme.DrawText(graphics, choice.Provider, Theme.SmallFont, Theme.Accent, bounds);
                return;
            }
            // У Ollama видно, в памяти ли модель: скачанная ответит чуть позже, пока сервер её загрузит.
            string state = choice.Provider == "Ollama" ? (choice.Loaded ? "в памяти" : "скачана") : null;
            float stateWidth = state == null ? 0f : Theme.Measure(state, Theme.SmallFont, 0).Width + Dpi.F(12);
            Theme.DrawText(graphics, choice.Name, Theme.UiFont, Theme.Text,
                new RectangleF(bounds.X, bounds.Y, Math.Max(0f, bounds.Width - stateWidth), bounds.Height));
            if (state != null)
            {
                Theme.DrawText(graphics, state, Theme.SmallFont, choice.Loaded ? Theme.Secondary : Theme.Tertiary, bounds,
                    StringAlignment.Far, StringAlignment.Center, false);
            }
        }
    }
}
