using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using SafePaste.Bridge;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Ui
{
    /// <summary>
    /// Вкладки и расшифровка. Пока окно открыто, каждый новый скопированный текст получает свою вкладку
    /// со своими находками, решениями и историей правок. Вкладка расшифровки показывает текст с метками
    /// (например, ответ ИИ), где метки заменены реальными значениями: её открывает Ctrl+Shift+C.
    /// </summary>
    internal sealed partial class ReviewForm
    {
        /// <summary>Сверх этого числа копии вкладками не открываются.</summary>
        internal const int MaxTabs = 12;

        /// <summary>Вкладка: всё, что окно помнит о своём тексте.</summary>
        private sealed class Document
        {
            internal bool Decrypt;
            internal string SourceText = string.Empty;
            internal List<Detection> Detections = new List<Detection>();
            internal EditHistory History = new EditHistory();
            internal Dictionary<string, bool> Choices = new Dictionary<string, bool>(StringComparer.Ordinal);
            internal List<Detection> ManualMarks = new List<Detection>();
            internal bool ScanPending;
            // Режим или правила сменились, пока вкладка была в фоне: при открытии она проверяется заново.
            internal bool Stale;
            internal DecodedText Decoded = new DecodedText();
            /// <summary>Картинка: тогда SourceText и находки относятся к её распознанному тексту.</summary>
            internal ImageDoc Image;
            internal int Caret;
            internal int TopIndex;
            internal float TopFraction;
        }

        private readonly List<Document> documents = new List<Document>();
        private int current;
        private bool decrypt;
        private DecodedText decoded = new DecodedText();
        private TabStrip tabStrip;
        private GlassButton pasteAllButton;
        // Нужна ли кнопка «Вставить все». Visible для раскладки не годится: пока окно не показано, он false.
        private bool pasteAllShown;

        /// <summary>
        /// Вставка вкладки, когда в окне есть и другие: трей вставляет текст в исходное окно,
        /// а окно проверки без этой вкладки остаётся в фоне.
        /// </summary>
        internal Action<ReplacementResult> PasteRequested;

        /// <summary>Что вставить после закрытия окна, если Action = Paste.</summary>
        internal ReplacementResult Result;

        internal int TabCount
        {
            get { return documents.Count; }
        }

        /// <summary>Открыта вкладка расшифровки.</summary>
        internal bool IsDecrypt
        {
            get { return decrypt; }
        }

        private void BuildTabs()
        {
            documents.Add(new Document());
            current = 0;
            tabStrip = new TabStrip();
            tabStrip.Visible = false;
            tabStrip.SelectedChanged += delegate { SelectDocument(tabStrip.SelectedIndex); };
            tabStrip.CloseRequested += delegate(int index) { CloseDocument(index); };
            pasteAllButton = new GlassButton(Glyphs.SelectAll, null, ButtonKind.Glass,
                "Вставить все вкладки одним текстом\nCtrl+Shift+Enter");
            pasteAllButton.Visible = false;
            pasteAllButton.Click += delegate { PasteAll(); };
            Controls.Add(tabStrip);
            Controls.Add(pasteAllButton);
        }

        // ---------------------------------------------------------------- вкладки

        /// <summary>Переносит состояние открытой вкладки из полей окна в её запись.</summary>
        private void SaveDocument()
        {
            if (current < 0 || current >= documents.Count)
            {
                return;
            }
            Document doc = documents[current];
            doc.Decrypt = decrypt;
            doc.SourceText = sourceText;
            doc.Detections = detections;
            doc.History = history;
            doc.Choices = choices;
            doc.ManualMarks = manualMarks;
            doc.ScanPending = scanPending;
            doc.Decoded = decoded;
            doc.Image = image;
            doc.Caret = preview.CaretIndex;
            doc.TopIndex = preview.TopIndex;
            doc.TopFraction = preview.TopFraction;
        }

        /// <summary>Открывает вкладку: её состояние становится состоянием окна.</summary>
        private void LoadDocument(int index)
        {
            scanTimer.Stop();
            GlassTip.HideAll();
            current = index;
            Document doc = documents[index];
            decrypt = doc.Decrypt;
            sourceText = doc.SourceText;
            detections = doc.Detections;
            history = doc.History;
            choices = doc.Choices;
            manualMarks = doc.ManualMarks;
            scanPending = doc.ScanPending;
            decoded = doc.Decoded ?? new DecodedText();
            image = doc.Image;
            map = new TextMap(decrypt ? decoded.Text : sourceText);
            ApplyDocumentMode();
            if (doc.Stale)
            {
                doc.Stale = false;
                Rescan(null);
            }
            else
            {
                RefreshAll(null);
            }
            preview.SetCaret(Math.Min(doc.Caret, preview.Content.Length));
            preview.ScrollToIndex(doc.TopIndex, doc.TopFraction);
        }

        private void SelectDocument(int index)
        {
            if (index == current || index < 0 || index >= documents.Count)
            {
                UpdateTabs();
                return;
            }
            FlushScan();
            SaveDocument();
            LoadDocument(index);
        }

        private void StepDocument(int direction)
        {
            if (documents.Count > 1)
            {
                SelectDocument((current + direction + documents.Count) % documents.Count);
            }
        }

        /// <summary>
        /// Новая копия из другой программы, пока окно открыто: своя вкладка того же вида, что открытая.
        /// Пустое окно просто принимает текст, повтор уже открытого текста открывает его вкладку.
        /// </summary>
        internal bool AddCopiedText(string text)
        {
            return OpenText(text, decrypt, "Новая копия открыта во вкладке.");
        }

        /// <summary>Ctrl+Shift+C: текст с метками открывается во вкладке расшифровки.</summary>
        internal bool AddDecryptText(string text)
        {
            return OpenText(text, true, null);
        }

        private bool OpenText(string text, bool decryptMode, string message)
        {
            text = text ?? string.Empty;
            if (text.Length > settings.MaxClipboardChars)
            {
                ShowStatus("Слишком большой текст: " + text.Length.ToString(CultureInfo.InvariantCulture)
                    + " символов, предел " + settings.MaxClipboardChars.ToString(CultureInfo.InvariantCulture) + ".");
                return false;
            }
            SaveDocument();
            for (int i = 0; i < documents.Count && text.Length > 0; i++)
            {
                if (documents[i].Decrypt == decryptMode && documents[i].Image == null && documents[i].SourceText == text)
                {
                    SelectDocument(i);
                    ShowStatus("Этот текст уже открыт во вкладке " + (i + 1).ToString(CultureInfo.InvariantCulture) + ".");
                    return true;
                }
            }
            bool blank = documents.Count == 1 && sourceText.Length == 0 && !history.CanUndo && image == null;
            if (blank && decrypt == decryptMode && text.Length == 0)
            {
                return true;
            }
            if (!blank && text.Length == 0)
            {
                return false;
            }
            if (!blank && documents.Count >= MaxTabs)
            {
                ShowStatus("Вкладок уже " + MaxTabs.ToString(CultureInfo.InvariantCulture) + ", закройте лишние (Ctrl+W).");
                return false;
            }
            Document doc = new Document();
            doc.Decrypt = decryptMode;
            doc.SourceText = text;
            try
            {
                if (decryptMode)
                {
                    doc.Decoded = LabelDecoder.Decode(text);
                }
                else
                {
                    doc.Detections = Detector.Scan(text, database, mode, false, settings.SmartSecrets);
                }
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return false;
            }
            FlushScan();
            SaveDocument();
            if (blank)
            {
                // Пустое окно без правок: текст ложится в него, а не в соседнюю вкладку.
                documents[0] = doc;
            }
            else
            {
                documents.Add(doc);
            }
            current = -1;
            LoadDocument(blank ? 0 : documents.Count - 1);
            if (message != null && documents.Count > 1)
            {
                ShowStatus(message);
            }
            return true;
        }

        /// <summary>Закрывает вкладку. Последняя вкладка закрывается вместе с окном.</summary>
        internal void CloseDocument(int index)
        {
            if (index < 0 || index >= documents.Count)
            {
                return;
            }
            if (documents.Count == 1)
            {
                Close();
                return;
            }
            if (index == current)
            {
                scanTimer.Stop();
                scanPending = false;
            }
            SaveDocument();
            ImageDoc closed = documents[index].Image;
            documents.RemoveAt(index);
            int next = index < current ? current - 1 : index == current ? Math.Min(index, documents.Count - 1) : current;
            current = -1;
            LoadDocument(next);
            if (closed != null)
            {
                closed.Release();
            }
        }

        /// <summary>Подписи вкладок: номер и первая строка, секреты закрыты точками.</summary>
        private void UpdateTabs()
        {
            if (tabStrip == null)
            {
                return;
            }
            List<TabInfo> items = new List<TabInfo>();
            for (int i = 0; i < documents.Count; i++)
            {
                bool live = i == current;
                Document doc = documents[i];
                bool isDecrypt = live ? decrypt : doc.Decrypt;
                string text = live ? sourceText : doc.SourceText;
                ImageDoc picture = live ? image : doc.Image;
                if (!isDecrypt)
                {
                    text = Replacer.MaskSecrets(text, live ? detections : doc.Detections);
                }
                TabInfo tab = new TabInfo();
                tab.Decrypt = isDecrypt;
                string line = FirstLine(text);
                if (picture != null && picture.Source != null)
                {
                    // У картинки в подписи размер, первая строка текста только в подсказке.
                    string size = picture.Source.Width.ToString(CultureInfo.InvariantCulture) + "×"
                        + picture.Source.Height.ToString(CultureInfo.InvariantCulture);
                    tab.Picture = true;
                    tab.Caption = (i + 1).ToString(CultureInfo.InvariantCulture) + " Картинка " + size;
                    tab.Tip = "Картинка " + size + (line.Length == 0 ? string.Empty : ": " + Shorten(line, 80))
                        + "\nCtrl+Tab: следующая вкладка, Ctrl+W: закрыть";
                    items.Add(tab);
                    continue;
                }
                tab.Caption = (i + 1).ToString(CultureInfo.InvariantCulture) + " " + (line.Length == 0 ? "пусто" : Shorten(line, 40));
                tab.Tip = (line.Length == 0 ? "Пустая вкладка" : Shorten(line, 90))
                    + "\n" + (isDecrypt ? "Расшифровка. " : string.Empty) + "Ctrl+Tab: следующая вкладка, Ctrl+W: закрыть";
                items.Add(tab);
            }
            tabStrip.Visible = documents.Count > 1;
            tabStrip.SetTabs(items, current);
            pasteAllShown = canPaste && !decrypt && PasteableCount() > 1;
            pasteAllButton.Visible = pasteAllShown;
            PerformLayout();
        }

        private static string FirstLine(string text)
        {
            foreach (string line in (text ?? string.Empty).Split('\n'))
            {
                string trimmed = line.Replace("\t", " ").Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            return string.Empty;
        }

        /// <summary>Сколько вкладок проверки с текстом: только их вставляет «Вставить все», картинки нет.</summary>
        private int PasteableCount()
        {
            int count = 0;
            for (int i = 0; i < documents.Count; i++)
            {
                bool isDecrypt = i == current ? decrypt : documents[i].Decrypt;
                bool isImage = (i == current ? image : documents[i].Image) != null;
                string text = i == current ? sourceText : documents[i].SourceText;
                if (!isDecrypt && !isImage && text.Length > 0)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>Режим открытой вкладки задаёт, какие части окна нужны.</summary>
        private void ApplyDocumentMode()
        {
            header.Title = decrypt ? "Расшифровка" : "Проверка перед вставкой";
            modeSwitch.Visible = !decrypt;
            resultSpine.Visible = !decrypt;
            resultCard.Visible = showResult && !decrypt;
            listSpine.Visible = !decrypt;
            listCard.Visible = showList && !decrypt;
            preview.ReadOnly = decrypt;
            preview.AccessibleName = decrypt ? "Реальные значения" : "Исходный текст";
            preview.Placeholder = decrypt
                ? "Выделите ответ с метками и нажмите Ctrl+Shift+C или вставьте его сюда (Ctrl+V)"
                : "Вставьте текст (Ctrl+V) или начните печатать";
            sourceHeader.Title = decrypt ? "Реальные значения" : "Исходный текст";
            pasteInButton.Tip = decrypt ? "Взять текст с метками из буфера\nЗаменит весь текст" : "Взять текст из буфера\nЗаменит весь текст";
            pasteButton.Visible = canPaste && !decrypt;
            copyButton.Kind = canPaste && !decrypt ? ButtonKind.Glass : ButtonKind.Primary;
            copyButton.Tip = decrypt ? "Копировать расшифрованный текст\nCtrl+S" : "Копировать результат\nCtrl+S";
            defaultStatus = decrypt
                ? "Метки заменены реальными значениями, они видны только здесь."
                : canPaste
                    ? "Вставка в «" + TargetName() + "»"
                    : "Окно для вставки не найдено. Скопируйте результат и вставьте его сами.";
            statusTimer.Stop();
            statusLabel.Text = defaultStatus;
            ApplyImageMode();
        }

        /// <summary>После смены режима или правил остальные вкладки проверяются заново, когда их откроют.</summary>
        private void MarkOthersStale(bool resetChoices)
        {
            for (int i = 0; i < documents.Count; i++)
            {
                if (i == current)
                {
                    continue;
                }
                documents[i].Stale = true;
                if (resetChoices)
                {
                    documents[i].Choices.Clear();
                }
            }
        }

        // ---------------------------------------------------------------- вставка

        /// <summary>
        /// Вставка открытой вкладки. Если есть и другие вкладки, окно остаётся в фоне без этой вкладки,
        /// а сочетание клавиш вернёт его. Последняя вкладка вставляется с закрытием окна, как раньше.
        /// Расшифровку в чужое окно SafePaste не вставляет: там обычно та самая переписка с ИИ.
        /// </summary>
        private void PasteCurrent()
        {
            if (image != null)
            {
                PasteImageCurrent();
                return;
            }
            if (decrypt || !canPaste)
            {
                CopyToClipboard();
                return;
            }
            ReplacementResult result;
            try
            {
                result = BuildPasteResult();
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return;
            }
            if (documents.Count > 1 && PasteRequested != null)
            {
                int pasted = current;
                PasteRequested(result);
                CloseDocument(pasted);
                return;
            }
            Result = result;
            Action = ReviewAction.Paste;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void PasteAll()
        {
            if (!canPaste || decrypt)
            {
                return;
            }
            ReplacementResult result;
            try
            {
                result = BuildAllResult();
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return;
            }
            if (result == null)
            {
                ShowStatus("Вставлять нечего: вкладки проверки пусты.");
                return;
            }
            Result = result;
            Action = ReviewAction.Paste;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// Все вкладки проверки одним текстом через пустую строку. Замены считаются за один проход
        /// по склеенному тексту: одно значение в разных вкладках получает одну метку, разные значения разные.
        /// Вкладки расшифровки не вставляются. null: вставлять нечего.
        /// </summary>
        internal ReplacementResult BuildAllResult()
        {
            int keep = current;
            for (int i = 0; i < documents.Count; i++)
            {
                bool isDecrypt = i == current ? decrypt : documents[i].Decrypt;
                bool isImage = (i == current ? image : documents[i].Image) != null;
                if (isDecrypt || isImage)
                {
                    continue;
                }
                if (i != current && (documents[i].Stale || documents[i].ScanPending))
                {
                    SelectDocument(i);
                }
                if (i == current)
                {
                    CheckEdited();
                }
            }
            if (current != keep)
            {
                SelectDocument(keep);
            }
            SaveDocument();
            StringBuilder text = new StringBuilder();
            List<Detection> all = new List<Detection>();
            foreach (Document doc in documents)
            {
                if (doc.Decrypt || doc.Image != null || doc.SourceText.Length == 0)
                {
                    continue;
                }
                if (text.Length > 0)
                {
                    text.Append("\r\n\r\n");
                }
                int offset = text.Length;
                text.Append(doc.SourceText);
                foreach (Detection detection in doc.Detections)
                {
                    Detection copy = detection.Clone();
                    copy.Start += offset;
                    all.Add(copy);
                }
            }
            if (text.Length == 0)
            {
                return null;
            }
            ReplacementResult result = LabelMemory.Apply(text.ToString(), all, database, SafePasteSettings.Load().SaveLabels);
            RememberStamps();
            return result;
        }

        // ---------------------------------------------------------------- расшифровка

        /// <summary>Расшифровка заново: метки могли запомниться или забыться, пока вкладка была открыта.</summary>
        private void Redecode(string message)
        {
            try
            {
                decoded = LabelDecoder.Decode(sourceText);
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return;
            }
            RefreshDecrypt(message);
        }

        private void RefreshDecrypt(string message)
        {
            map = new TextMap(decoded.Text);
            List<TextMark> marks = new List<TextMark>(decoded.Spans.Count);
            foreach (DecodedSpan span in decoded.Spans)
            {
                int start = map.ToDisplay(span.Start);
                int end = map.ToDisplay(span.Start + span.Length);
                if (end > start)
                {
                    marks.Add(new TextMark(start, end - start,
                        span.Kind == DecodedKind.Restored ? MarkKind.Restored : MarkKind.Missing, span));
                }
            }
            if (map.Display != preview.Content)
            {
                preview.SetContent(map.Display, marks, true);
            }
            else
            {
                preview.SetMarks(marks);
            }
            UpdateCounter(sourceHeader, preview);
            List<KeyValuePair<Color, string>> items = new List<KeyValuePair<Color, string>>();
            if (decoded.Spans.Count == 0)
            {
                items.Add(new KeyValuePair<Color, string>(Color.Empty, sourceText.Length == 0 ? string.Empty : "Меток нет"));
            }
            if (decoded.Restored > 0)
            {
                items.Add(new KeyValuePair<Color, string>(Theme.Accent,
                    "Расшифровано: " + decoded.Restored.ToString(CultureInfo.InvariantCulture)));
            }
            if (decoded.Missing > 0)
            {
                items.Add(new KeyValuePair<Color, string>(Theme.Warm,
                    "Не расшифровано: " + decoded.Missing.ToString(CultureInfo.InvariantCulture)));
            }
            legend.SetItems(items);
            sourceHeader.SetToolWidth(legend, legend.PreferredWidth);
            UpdateTabs();
            if (message != null)
            {
                ShowStatus(message);
            }
        }

        /// <summary>Подсказка к метке в расшифровке: откуда значение или почему его нет.</summary>
        private string[] DescribeDecoded(DecodedSpan span)
        {
            string kind = TypeNames.Describe(span.Type);
            if (span.Kind == DecodedKind.Restored)
            {
                string body = kind + (span.Pinned ? ", номер закреплён" : ", метка запомнена");
                if (span.Form == "SPLIT")
                {
                    body += ". В ответе значение было разбито на части";
                }
                else if (span.Form == "ENCODED")
                {
                    body += ". В ответе значение было в закодированном виде";
                }
                return new string[] { span.Label, body, null };
            }
            if (span.Kind == DecodedKind.Secret)
            {
                return new string[] { span.Label, "Не расшифровано: пароли и ключи SafePaste не сохраняет", null };
            }
            return new string[] { span.Label, "Не расшифровано: такой метки нет среди запомненных. "
                + "Она могла забыться или прийти из другой переписки", null };
        }

        private void ShowDecodedTip(DecodedSpan span)
        {
            string[] tip = DescribeDecoded(span);
            GlassTip.ShowRich(preview, preview.RectangleToScreen(preview.HoverBounds), tip[0], Theme.MonoFont,
                span.Kind == DecodedKind.Restored ? Theme.Accent : Theme.Warm, tip[1], tip[2], 250);
        }

        /// <summary>Меню расшифровки: только копирование выделенного, остальное делают кнопки.</summary>
        private GlassMenu BuildDecryptMenu()
        {
            GlassMenu menu = new GlassMenu();
            if (preview.SelectionLength > 0)
            {
                menu.AddItem("Копировать", "Ctrl+C", true, delegate { preview.CopySelection(); });
            }
            return menu;
        }

        /// <summary>Новый текст во вкладке расшифровки: вставлен, взят из буфера или перетащен.</summary>
        private void LoadDecryptText(string text, string message)
        {
            DecodedText next;
            try
            {
                next = LabelDecoder.Decode(text);
            }
            catch (Exception failure)
            {
                ShowStatus(failure.Message);
                return;
            }
            GlassTip.HideAll();
            history.Clear();
            sourceText = text;
            decoded = next;
            map = new TextMap(decoded.Text);
            preview.SetContent(string.Empty, null, false);
            RefreshDecrypt(message);
        }

        private void CopyDecrypted()
        {
            string error;
            if (!ClipboardService.TrySetText(decoded.Text, out error))
            {
                ShowStatus(error);
                return;
            }
            ShowStatus("Расшифрованный текст скопирован. Буфер сам не очистится.");
        }
    }
}
