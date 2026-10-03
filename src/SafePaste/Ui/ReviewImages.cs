using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SafePaste.Bridge;
using SafePaste.Detecting;
using SafePaste.Imaging;
using SafePaste.Storage;

namespace SafePaste.Ui
{
    /// <summary>
    /// Картинка во вкладке окна проверки. Поля окна (sourceText, detections, choices, manualMarks) у такой
    /// вкладки относятся к распознанному тексту, поэтому список находок, меню, правила, закрепления и режимы
    /// работают так же, как у текста. Сама картинка показана в блоке «Картинка» с рамками находок,
    /// готовая картинка с метками справа. Распознавание идёт в фоне, пока оно идёт, вставлять нельзя.
    /// </summary>
    internal sealed partial class ReviewForm
    {
        private static readonly Color UnreadLegend = Color.FromArgb(196, 168, 255);

        /// <summary>Картинка вкладки: исходник, чтение и закрытые места.</summary>
        private sealed class ImageDoc
        {
            internal Bitmap Source;
            internal ImageReading Reading;
            internal string Problem;
            internal readonly List<ImageArea> Areas = new List<ImageArea>();
            internal bool ShowText;
            /// <summary>Готовая картинка для блока результата.</summary>
            internal Bitmap Preview;
            internal int Generation;
            internal bool Closed;
            internal long Fingerprint;
            /// <summary>Режим, по которому поставлены галочки непрочитанных мест.</summary>
            internal int AreaMode = -1;

            internal bool Pending
            {
                get { return Reading == null && Problem == null; }
            }

            internal void Release()
            {
                Closed = true;
                if (Source != null)
                {
                    Source.Dispose();
                    Source = null;
                }
                if (Preview != null)
                {
                    Preview.Dispose();
                    Preview = null;
                }
            }
        }

        private ImageDoc image;
        private ImageView imageView;
        private BlockHeader imageHeader;
        private Legend imageLegend;
        private GlassButton imageTextButton;
        private GlassButton imagePasteInButton;
        private GlassCard imageCard;
        private GlassButton sourceImageButton;
        private ImageView resultImageView;
        private BlockHeader resultImageHeader;
        private GlassCard resultImageCard;
        private GlassButton textPasteButton;
        // Нужна ли кнопка «Вставить текстом». Visible для раскладки не годится, как и у «Вставить все».
        private bool textPasteShown;
        private readonly List<RectangleF> imageMatches = new List<RectangleF>();
        private int imageMatch = -1;
        private bool buildingImage;

        /// <summary>Вставка картинки, когда в окне есть и другие вкладки: трей вставляет, окно остаётся.</summary>
        internal Action<Bitmap, int> PasteImageRequested;

        /// <summary>Готовая картинка, если Action = Paste. Освобождает её тот, кто вставляет.</summary>
        internal Bitmap ResultImage;

        /// <summary>Открыта вкладка с картинкой.</summary>
        internal bool IsImage
        {
            get { return image != null; }
        }

        /// <summary>Картинка открытой вкладки ещё читается.</summary>
        internal bool ImagePending
        {
            get { return image != null && image.Pending; }
        }

        private void BuildImageLayout()
        {
            imageView = new ImageView();
            imageView.AccessibleName = "Картинка";
            imageView.AllowDrawing = true;
            imageView.BusyText = "Читаю текст на картинке…";
            imageView.Placeholder = "Скопируйте картинку и нажмите кнопку со стрелками";
            imageView.HoverChanged += OnImageHover;
            imageView.ContextRequested += OnImageContext;
            imageView.AreaDrawn += OnAreaDrawn;
            imageHeader = new BlockHeader("Картинка", Theme.Text, "Поиск по распознанному тексту\nCtrl+F");
            imageHeader.SearchChanged += delegate { UpdateImageSearch(); };
            imageHeader.SearchStep += delegate(bool backwards) { StepImageMatch(backwards ? -1 : 1); };
            imageLegend = new Legend();
            // Шапки картинки и распознанного текста устроены одинаково: легенда, переключатель вида, стрелки.
            // Переключатель стоит на одном месте, и второй щелчок по нему не попадёт в «Взять из буфера».
            imageTextButton = new GlassButton(Glyphs.Text, null, ButtonKind.Plain, "Показать распознанный текст");
            imageTextButton.Click += delegate { ShowImageText(true); };
            imagePasteInButton = new GlassButton(Glyphs.Sync, null, ButtonKind.Plain, "Взять из буфера\nЗаменит картинку");
            imagePasteInButton.Click += delegate { LoadFromClipboard(); };
            imageHeader.AddTool(imageLegend, Dpi.S(10), true);
            imageHeader.AddTool(imageTextButton, 0);
            imageHeader.AddTool(imagePasteInButton, 0);
            imageCard = new GlassCard(imageHeader, imageView);
            imageCard.Visible = false;

            sourceImageButton = new GlassButton(Glyphs.Picture, null, ButtonKind.Plain, "Показать картинку");
            sourceImageButton.Click += delegate { ShowImageText(false); };
            sourceHeader.InsertTool(sourceImageButton, 0, pasteInButton);
            sourceHeader.ShowTool(sourceImageButton, false);

            resultImageView = new ImageView();
            resultImageView.AccessibleName = "Готовая картинка";
            resultImageView.Placeholder = "Готовая картинка появится, когда текст будет прочитан";
            resultImageView.HoverChanged += OnResultImageHover;
            resultImageView.ContextRequested += OnResultImageContext;
            resultImageHeader = new BlockHeader("Результат", Theme.Accent, "Поиск на готовой картинке недоступен");
            resultImageHeader.Searchable = false;
            resultImageCard = new GlassCard(resultImageHeader, resultImageView);
            resultImageCard.Visible = false;

            textPasteButton = new GlassButton(Glyphs.Text, null, ButtonKind.Glass, "Вставить распознанный текст вместо картинки\nCtrl+T");
            textPasteButton.Visible = false;
            textPasteButton.Click += delegate { PasteRecognizedText(); };

            Controls.Add(imageCard);
            Controls.Add(resultImageCard);
            Controls.Add(textPasteButton);
            imageCard.TabIndex = 0;
            resultImageCard.TabIndex = 2;
        }

        // ---------------------------------------------------------------- вкладка

        /// <summary>Картинка из буфера при открытии окна.</summary>
        internal bool AddImage(Bitmap picture)
        {
            return OpenImage(picture, null, false, null);
        }

        /// <summary>Новая копия картинки, пока окно открыто.</summary>
        internal bool AddCopiedImage(Bitmap picture)
        {
            return OpenImage(picture, "Новая картинка открыта во вкладке.", false, null);
        }

        /// <summary>Для проверок: картинка с готовым чтением, без распознавания.</summary>
        internal bool AddImageReading(Bitmap picture, ImageReading reading)
        {
            return OpenImage(picture, null, false, reading);
        }

        /// <summary>
        /// Картинка во вкладке. Пустое окно принимает её, повтор уже открытой картинки открывает её вкладку.
        /// replace: заменить картинку открытой вкладки (кнопка со стрелками). reading: готовое чтение, иначе
        /// распознавание запускается в фоне. Картинкой окно владеет и освобождает её само.
        /// </summary>
        private bool OpenImage(Bitmap picture, string message, bool replace, ImageReading reading)
        {
            if (picture == null)
            {
                return false;
            }
            if ((long)picture.Width * picture.Height > settings.MaxImagePixels)
            {
                ShowStatus("Слишком большая картинка: " + picture.Width.ToString(CultureInfo.InvariantCulture) + " на "
                    + picture.Height.ToString(CultureInfo.InvariantCulture) + " точек. Предел можно поменять в settings.json (MaxImagePixels).");
                picture.Dispose();
                return false;
            }
            long fingerprint = Fingerprint(picture);
            SaveDocument();
            for (int i = 0; i < documents.Count; i++)
            {
                ImageDoc open = i == current ? image : documents[i].Image;
                if (open != null && open.Fingerprint == fingerprint && open.Source != null && open.Source.Size == picture.Size)
                {
                    picture.Dispose();
                    SelectDocument(i);
                    ShowStatus("Эта картинка уже открыта во вкладке " + (i + 1).ToString(CultureInfo.InvariantCulture) + ".");
                    return true;
                }
            }
            bool blank = documents.Count == 1 && sourceText.Length == 0 && !history.CanUndo && image == null && !decrypt;
            bool swap = replace && image != null;
            if (!blank && !swap && documents.Count >= MaxTabs)
            {
                picture.Dispose();
                ShowStatus("Вкладок уже " + MaxTabs.ToString(CultureInfo.InvariantCulture) + ", закройте лишние (Ctrl+W).");
                return false;
            }
            ImageDoc doc = new ImageDoc();
            doc.Source = picture;
            doc.Fingerprint = fingerprint;
            Document record = new Document();
            record.Image = doc;
            FlushScan();
            SaveDocument();
            ImageDoc previous = swap ? image : null;
            int index;
            if (blank)
            {
                documents[0] = record;
                index = 0;
            }
            else if (swap)
            {
                documents[current] = record;
                index = current;
            }
            else
            {
                documents.Add(record);
                index = documents.Count - 1;
            }
            current = -1;
            LoadDocument(index);
            if (previous != null)
            {
                previous.Release();
            }
            if (reading != null)
            {
                FinishReading(doc, doc.Generation, reading, null);
            }
            else
            {
                StartReading(doc);
                ShowStatus(message ?? "Читаю текст на картинке. Это займёт секунду.");
            }
            return true;
        }

        /// <summary>Отпечаток картинки: одинаковые копии не открываются второй вкладкой.</summary>
        private static long Fingerprint(Bitmap picture)
        {
            BitmapData data = picture.LockBits(new Rectangle(0, 0, picture.Width, picture.Height), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                ulong hash = 14695981039346656037UL;
                int[] row = new int[picture.Width];
                for (int y = 0; y < picture.Height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++)
                    {
                        hash = (hash ^ (uint)row[x]) * 1099511628211UL;
                    }
                }
                Array.Clear(row, 0, row.Length);
                return (long)hash;
            }
            finally
            {
                picture.UnlockBits(data);
            }
        }

        /// <summary>Распознавание в фоне. Картинка копируется: GDI+ не даёт рисовать и читать её из двух потоков.</summary>
        private void StartReading(ImageDoc doc)
        {
            doc.Generation++;
            int generation = doc.Generation;
            Bitmap copy = new Bitmap(doc.Source);
            if (!IsHandleCreated)
            {
                IntPtr handle = Handle;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                ImageReading reading = null;
                Exception failure = null;
                try
                {
                    reading = ImageReader.Read(copy);
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    copy.Dispose();
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate { FinishReading(doc, generation, reading, failure); });
                }
                catch (InvalidOperationException)
                {
                    // Окно уже закрыто.
                }
            });
        }

        private void FinishReading(ImageDoc doc, int generation, ImageReading reading, Exception failure)
        {
            if (doc.Closed || doc.Generation != generation || IsDisposed)
            {
                return;
            }
            if (reading == null)
            {
                reading = new ImageReading();
                reading.Width = doc.Source.Width;
                reading.Height = doc.Source.Height;
                ImageReader.Build(reading, new List<OcrLine>());
            }
            doc.Reading = reading;
            doc.Problem = failure != null ? "Картинку не удалось прочитать: " + failure.Message : reading.Problem;
            doc.Areas.RemoveAll(delegate(ImageArea area) { return area.Kind == AreaKind.Unread; });
            for (int i = 0; i < reading.Unread.Count; i++)
            {
                ImageArea area = new ImageArea(reading.Unread[i], AreaKind.Unread, false);
                area.Quiet = reading.QuietUnread.Contains(i);
                doc.Areas.Add(area);
            }
            doc.AreaMode = -1;
            string message = ReadMessage(doc);
            if (doc == image)
            {
                sourceText = reading.Text;
                map = new TextMap(sourceText);
                history.Clear();
                choices.Clear();
                manualMarks.Clear();
                ApplyImageMode();
                Rescan(message);
                return;
            }
            foreach (Document record in documents)
            {
                if (record.Image == doc)
                {
                    record.SourceText = reading.Text;
                    record.Stale = true;
                }
            }
        }

        private static string ReadMessage(ImageDoc doc)
        {
            if (doc.Problem != null)
            {
                return doc.Problem + " Закройте нужные места рамкой: ведите мышью по картинке.";
            }
            int unread = 0;
            foreach (ImageArea area in doc.Areas)
            {
                if (area.Kind == AreaKind.Unread && (!area.Quiet || area.Hidden))
                {
                    unread++;
                }
            }
            string text = doc.Reading.Lines.Count == 0 ? "Текста на картинке не нашлось."
                : "Прочитано строк: " + doc.Reading.Lines.Count.ToString(CultureInfo.InvariantCulture) + ".";
            if (unread > 0)
            {
                text += " Не прочитано мест: " + unread.ToString(CultureInfo.InvariantCulture) + ", они обведены пунктиром.";
            }
            return text;
        }

        /// <summary>Какие части окна нужны картинке. Вызывается в конце ApplyDocumentMode.</summary>
        private void ApplyImageMode()
        {
            bool picture = image != null;
            imageCard.Visible = picture && !image.ShowText;
            sourceCard.Visible = !picture || image.ShowText;
            resultImageCard.Visible = picture && showResult;
            if (picture)
            {
                resultCard.Visible = false;
            }
            sourceHeader.ShowTool(sourceImageButton, picture);
            textPasteShown = picture;
            textPasteButton.Visible = picture;
            if (!picture)
            {
                imageView.SetImage(null, null, false);
                resultImageView.SetImage(null, null, false);
                imageHeader.CloseSearch();
                PerformLayout();
                return;
            }
            header.Title = "Проверка картинки";
            preview.ReadOnly = true;
            preview.AccessibleName = "Распознанный текст";
            preview.Placeholder = image.Pending ? "Текст ещё читается" : "На картинке не нашлось текста";
            sourceHeader.Title = "Распознанный текст";
            pasteInButton.Tip = "Взять из буфера\nЗаменит картинку";
            copyButton.Tip = "Копировать картинку\nCtrl+S";
            textPasteButton.Tip = canPaste
                ? "Вставить распознанный текст вместо картинки\nCtrl+T"
                : "Копировать распознанный текст вместо картинки\nCtrl+T";
            defaultStatus = image.Problem != null ? image.Problem
                : canPaste ? "Вставка картинки в «" + TargetName() + "»"
                : "Окно для вставки не найдено. Скопируйте картинку и вставьте её сами.";
            if (!statusTimer.Enabled)
            {
                statusLabel.Text = defaultStatus;
            }
            PerformLayout();
        }

        /// <summary>Картинка занимает место исходного текста и результата. Вызывается в конце OnLayout.</summary>
        private void LayoutImageCards()
        {
            if (image == null)
            {
                return;
            }
            imageCard.Bounds = sourceCard.Bounds;
            resultImageCard.Bounds = resultCard.Bounds;
        }

        /// <summary>Кнопка «Вставить текстом» левее постоянных кнопок. Возвращает новую правую границу.</summary>
        private int LayoutTextPaste(int right, int footerTop)
        {
            if (!textPasteShown)
            {
                return right;
            }
            int size = Dpi.S(38);
            textPasteButton.SetBounds(right - size, footerTop + (FooterHeight - size) / 2, size, size);
            return textPasteButton.Left - Dpi.S(10);
        }

        private void ShowImageText(bool show)
        {
            if (image == null)
            {
                return;
            }
            GlassTip.HideAll();
            image.ShowText = show;
            ApplyImageMode();
            if (show)
            {
                preview.Focus();
            }
            else
            {
                imageView.Focus();
            }
        }

        // ---------------------------------------------------------------- проверка

        /// <summary>Находки картинки заново: чтение то же, меняются режим, правила и запомненное.</summary>
        private bool RescanImage(string message)
        {
            scanTimer.Stop();
            if (image.Reading == null)
            {
                scanPending = false;
                detections = new List<Detection>();
                RefreshImage(message);
                return true;
            }
            List<Detection> next;
            List<ImageArea> tails = new List<ImageArea>();
            try
            {
                next = ImageScanner.Detect(image.Reading, database, mode, tails, settings.SmartSecrets);
            }
            catch (Exception failure)
            {
                scanPending = true;
                ShowStatus(failure.Message);
                return false;
            }
            scanPending = false;
            detections = next;
            image.Areas.RemoveAll(delegate(ImageArea area) { return area.Kind == AreaKind.Tail; });
            image.Areas.AddRange(tails);
            if (image.AreaMode != (int)mode)
            {
                // Непрочитанное закрывает только строгий режим; в остальных оно обведено для проверки глазами.
                foreach (ImageArea area in image.Areas)
                {
                    if (area.Kind == AreaKind.Unread)
                    {
                        area.Enabled = mode == ControlMode.Strict;
                    }
                }
                image.AreaMode = (int)mode;
            }
            ReapplyManualMarks();
            ApplyChoices();
            RefreshImage(message);
            return true;
        }

        private void RefreshImage(string message)
        {
            detections = OverlapResolver.Resolve(detections);
            LabelMemory.Preview(sourceText, detections, database);
            BuildGroups();
            map = new TextMap(sourceText);
            string display = MaskedDisplay();
            if (display != preview.Content)
            {
                preview.SetContent(display, BuildSourceMarks(), true);
            }
            else
            {
                preview.SetMarks(BuildSourceMarks());
            }
            preview.Placeholder = image.Pending ? "Текст ещё читается" : "На картинке не нашлось текста";
            UpdateCounter(sourceHeader, preview);
            FindImageMatches();
            imageView.Busy = image.Pending;
            imageView.SetImage(image.Source, BuildImageMarks(), true);
            RefreshImagePreview();
            RebuildList();
            UpdateSummary();
            UpdateImageSummary();
            UpdateListSpine();
            UpdateTabs();
            if (message != null)
            {
                ShowStatus(message);
            }
        }

        /// <summary>Готовая картинка справа: перерисовывается, только когда блок результата открыт.</summary>
        private void RefreshImagePreview()
        {
            if (image == null)
            {
                return;
            }
            if (!showResult || image.Pending || image.Source == null)
            {
                resultImageView.SetImage(null, null, false);
                return;
            }
            Bitmap next;
            try
            {
                next = RenderImage();
            }
            catch (Exception failure)
            {
                ShowStatus("Не удалось собрать картинку: " + failure.Message);
                return;
            }
            Bitmap old = image.Preview;
            image.Preview = next;
            resultImageView.SetImage(next, BuildResultImageMarks(), true);
            if (old != null)
            {
                old.Dispose();
            }
        }

        private List<ImageMark> BuildImageMarks()
        {
            List<ImageMark> marks = new List<ImageMark>();
            if (image.Reading != null)
            {
                foreach (Detection detection in detections)
                {
                    ImageMarkKind kind = detection.Locked ? ImageMarkKind.Secret : detection.Enabled ? ImageMarkKind.Hidden : ImageMarkKind.Kept;
                    foreach (RectangleF box in ImageScanner.BoxesFor(image.Reading, detection))
                    {
                        marks.Add(new ImageMark(box, kind, detection));
                    }
                }
            }
            foreach (ImageArea area in image.Areas)
            {
                if (area.Quiet && !area.Hidden)
                {
                    continue;
                }
                ImageMarkKind kind = area.Kind == AreaKind.Manual ? ImageMarkKind.Manual
                    : area.Locked ? ImageMarkKind.Secret
                    : area.Enabled ? ImageMarkKind.UnreadHidden : ImageMarkKind.Unread;
                marks.Add(new ImageMark(area.Bounds, kind, area));
            }
            for (int i = 0; i < imageMatches.Count; i++)
            {
                marks.Add(new ImageMark(imageMatches[i], ImageMarkKind.Match, null));
            }
            return marks;
        }

        private List<ImageMark> BuildResultImageMarks()
        {
            List<ImageMark> marks = new List<ImageMark>();
            if (image.Reading != null)
            {
                foreach (Detection detection in detections)
                {
                    if (!detection.Enabled && !detection.Locked)
                    {
                        continue;
                    }
                    foreach (RectangleF box in ImageScanner.BoxesFor(image.Reading, detection))
                    {
                        marks.Add(new ImageMark(box, ImageMarkKind.Placeholder, detection));
                    }
                }
            }
            foreach (ImageArea area in image.Areas)
            {
                if (area.Hidden)
                {
                    marks.Add(new ImageMark(area.Bounds, ImageMarkKind.Placeholder, area));
                }
            }
            return marks;
        }

        private void UpdateImageSummary()
        {
            List<KeyValuePair<Color, string>> items = new List<KeyValuePair<Color, string>>();
            if (image.Pending)
            {
                items.Add(new KeyValuePair<Color, string>(Color.Empty, "Читаю текст"));
            }
            else
            {
                int hidden = 0;
                int secrets = 0;
                int kept = 0;
                int unread = 0;
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
                foreach (ImageArea area in image.Areas)
                {
                    if (area.Kind == AreaKind.Unread)
                    {
                        if (!area.Quiet || area.Hidden)
                        {
                            unread++;
                        }
                    }
                    else if (area.Locked)
                    {
                        secrets++;
                    }
                    else
                    {
                        hidden++;
                    }
                }
                if (hidden + secrets + kept + unread == 0)
                {
                    items.Add(new KeyValuePair<Color, string>(Color.Empty,
                        image.Problem != null ? "Не прочитано" : sourceText.Length == 0 ? "Текста нет" : "Ничего не найдено"));
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
                if (unread > 0)
                {
                    items.Add(new KeyValuePair<Color, string>(UnreadLegend, "Не прочитано: " + unread.ToString(CultureInfo.InvariantCulture)));
                }
            }
            imageLegend.SetItems(items);
            imageHeader.SetToolWidth(imageLegend, imageLegend.PreferredWidth);
        }

        // ---------------------------------------------------------------- готовая картинка

        /// <summary>Картинка с заглушками по текущим находкам. Метки берутся из Placeholder находок.</summary>
        private Bitmap RenderImage()
        {
            List<RedactBox> boxes = new List<RedactBox>();
            List<RectangleF> covered = new List<RectangleF>();
            if (image.Reading != null)
            {
                foreach (Detection detection in detections)
                {
                    if (!detection.Enabled && !detection.Locked)
                    {
                        continue;
                    }
                    List<RectangleF> parts = ImageScanner.BoxesFor(image.Reading, detection);
                    for (int i = 0; i < parts.Count; i++)
                    {
                        boxes.Add(new RedactBox(parts[i], i == 0 ? detection.Placeholder : null));
                        covered.Add(parts[i]);
                    }
                }
            }
            foreach (ImageArea area in image.Areas)
            {
                if (area.Hidden)
                {
                    boxes.Add(new RedactBox(area.Bounds, area.Kind == AreaKind.Tail ? "[SECRET]" : "[" + MarkTypes.Generic + "]"));
                    covered.Add(area.Bounds);
                }
            }
            // Метка может занять пустой фон рядом, но не видимое слово.
            List<RectangleF> visible = new List<RectangleF>();
            if (image.Reading != null)
            {
                foreach (OcrWord word in image.Reading.Words)
                {
                    bool hidden = false;
                    foreach (RectangleF box in covered)
                    {
                        RectangleF overlap = RectangleF.Intersect(box, word.Bounds);
                        if (overlap.Width * overlap.Height >= word.Bounds.Width * word.Bounds.Height * 0.5f)
                        {
                            hidden = true;
                            break;
                        }
                    }
                    if (!hidden)
                    {
                        visible.Add(word.Bounds);
                    }
                }
            }
            return ImageRedactor.Render(image.Source, boxes, visible, ImageRedactor.ParseStyle(settings.ImageStubStyle));
        }

        /// <summary>
        /// Картинка, которая уходит из окна: метки запоминаются, как у текста, потом готовая картинка
        /// читается ещё раз. Если скрытое значение на ней всё ещё читается, это место закрывается и проверка
        /// повторяется; если и так не помогло, вставка отменяется.
        /// </summary>
        private bool BuildImageResult(out Bitmap final, out ReplacementResult labels)
        {
            final = null;
            labels = null;
            if (image.Pending)
            {
                ShowStatus("Картинка ещё читается, подождите секунду.");
                return false;
            }
            if (buildingImage)
            {
                return false;
            }
            buildingImage = true;
            Cursor previousCursor = Cursor;
            try
            {
                labels = BuildPasteResult();
                int added = 0;
                for (int round = 0; ; round++)
                {
                    Bitmap candidate = RenderImage();
                    List<RectangleF> leaks;
                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        statusLabel.Text = "Проверяю готовую картинку.";
                        statusLabel.Update();
                        leaks = FindLeaks(candidate);
                    }
                    catch (Exception)
                    {
                        candidate.Dispose();
                        throw;
                    }
                    if (leaks.Count == 0)
                    {
                        final = candidate;
                        break;
                    }
                    candidate.Dispose();
                    foreach (RectangleF leak in leaks)
                    {
                        image.Areas.Add(new ImageArea(leak, AreaKind.Leak, true));
                        added++;
                    }
                    if (round >= 1)
                    {
                        RefreshImage("На готовой картинке ещё читается скрытое значение. Закройте это место рамкой и вставьте снова.");
                        return false;
                    }
                }
                int areas = 0;
                foreach (ImageArea area in image.Areas)
                {
                    if (area.Hidden)
                    {
                        areas++;
                    }
                }
                labels.HiddenCount += areas;
                if (added > 0)
                {
                    RefreshImage("Контрольное чтение нашло ещё мест: " + added.ToString(CultureInfo.InvariantCulture) + ", они закрыты.");
                }
                return true;
            }
            catch (Exception failure)
            {
                if (final != null)
                {
                    final.Dispose();
                    final = null;
                }
                ShowStatus(failure.Message);
                return false;
            }
            finally
            {
                Cursor = previousCursor;
                buildingImage = false;
            }
        }

        /// <summary>Где на готовой картинке ещё читается скрытое. Без распознавания проверять нечем.</summary>
        private List<RectangleF> FindLeaks(Bitmap candidate)
        {
            List<string> values = HiddenValues();
            if (values.Count == 0 || image.Reading == null || image.Problem != null || image.Reading.Lines.Count == 0)
            {
                return new List<RectangleF>();
            }
            ImageReading check = WindowsOcr.OnWorker<ImageReading>(delegate { return ImageReader.Read(candidate, false); });
            if (check == null || check.Problem != null)
            {
                throw new InvalidOperationException(check == null ? "Контрольное чтение картинки не выполнено."
                    : "Контрольное чтение картинки недоступно: " + check.Problem);
            }
            return ImageScanner.FindLeaks(check, values);
        }

        /// <summary>Скрытые значения для контрольного чтения. Оставленное пользователем не считается утечкой.</summary>
        private List<string> HiddenValues()
        {
            HashSet<string> kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Detection detection in detections)
            {
                if (!detection.Enabled && !detection.Locked)
                {
                    kept.Add(detection.Value);
                }
            }
            List<string> values = new List<string>();
            foreach (Detection detection in detections)
            {
                if (!detection.Enabled && !detection.Locked)
                {
                    continue;
                }
                if (detection.Source == ImageScanner.TailSource)
                {
                    foreach (string part in detection.Value.Split(' '))
                    {
                        if (part.Length >= 4)
                        {
                            values.Add(part);
                        }
                    }
                    continue;
                }
                if (!kept.Contains(detection.Value))
                {
                    values.Add(detection.Value);
                }
            }
            return values;
        }

        private void PasteImageCurrent()
        {
            if (!canPaste)
            {
                CopyImage();
                return;
            }
            Bitmap final;
            ReplacementResult labels;
            if (!BuildImageResult(out final, out labels))
            {
                return;
            }
            if (documents.Count > 1 && PasteImageRequested != null)
            {
                int pasted = current;
                PasteImageRequested(final, labels.HiddenCount);
                CloseDocument(pasted);
                return;
            }
            ResultImage = final;
            Result = labels;
            Action = ReviewAction.Paste;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CopyImage()
        {
            Bitmap final;
            ReplacementResult labels;
            if (!BuildImageResult(out final, out labels))
            {
                return;
            }
            string error;
            bool copied = ClipboardService.TrySetImage(final, out error);
            final.Dispose();
            ShowStatus(copied ? "Картинка скопирована. Буфер сам не очистится." : error);
        }

        /// <summary>
        /// Распознанный текст вместо картинки. Самый надёжный вариант для логов и ошибок: то, что не прочитано,
        /// в текст просто не попадает.
        /// </summary>
        private void PasteRecognizedText()
        {
            if (image == null)
            {
                return;
            }
            if (image.Pending)
            {
                ShowStatus("Картинка ещё читается, подождите секунду.");
                return;
            }
            if (sourceText.Length == 0)
            {
                ShowStatus("На картинке не нашлось текста.");
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
            if (!canPaste)
            {
                string error;
                ShowStatus(ClipboardService.TrySetText(result.Text, out error)
                    ? "Распознанный текст скопирован. Буфер сам не очистится." : error);
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

        // ---------------------------------------------------------------- подсказки и меню

        private void OnImageHover(object sender, EventArgs e)
        {
            ImageMark mark = imageView.HoveredMark;
            if (mark == null || mark.Tag == null)
            {
                GlassTip.HideFor(imageView);
                return;
            }
            Rectangle anchor = imageView.RectangleToScreen(imageView.HoverBounds);
            Detection detection = mark.Tag as Detection;
            if (detection != null)
            {
                string[] tip = DescribeSourceMark(detection);
                string body = tip[1];
                if (!detection.Locked)
                {
                    // У пароля значение уже в заголовке подсказки.
                    body += ". Значение: " + Shorten(detection.Value, 60);
                }
                GlassTip.ShowRich(imageView, anchor, tip[0], TipFont(detection), TipColor(detection), body, tip[2], 250);
                return;
            }
            ImageArea area = mark.Tag as ImageArea;
            if (area != null)
            {
                string[] tip = DescribeArea(area);
                GlassTip.ShowRich(imageView, anchor, tip[0], Theme.StrongFont,
                    area.Kind == AreaKind.Unread ? UnreadLegend : area.Locked ? Theme.Danger : Theme.Warm, tip[1], tip[2], 250);
            }
        }

        private static string[] DescribeArea(ImageArea area)
        {
            switch (area.Kind)
            {
                case AreaKind.Unread:
                    return new string[]
                    {
                        "Текст не прочитан",
                        "Здесь похоже на текст, но SafePaste не смог его прочитать. "
                            + (area.Enabled ? "Место будет закрыто." : "Оно останется как есть, проверьте его глазами."),
                        area.Enabled ? "Правый клик: не закрывать" : "Правый клик: закрыть"
                    };
                case AreaKind.Manual:
                    return new string[] { "Закрыто вручную", "Место будет закрыто заглушкой.", "Правый клик: убрать рамку" };
                case AreaKind.Tail:
                    return new string[] { "Скрыто после пароля",
                        "После слова пароля строка закрывается до конца, даже если значение не прочитано.", null };
                default:
                    return new string[] { "Закрыто после проверки",
                        "На готовой картинке здесь ещё читалось скрытое значение.", null };
            }
        }

        private void OnImageContext(object sender, MouseEventArgs e)
        {
            GlassTip.HideAll();
            ShowMenu(BuildImageMenu(imageView.MarkAt(e.Location)), imageView, e.Location);
        }

        /// <summary>Меню рамки: для находки те же действия, что в тексте; для места без текста свои.</summary>
        private GlassMenu BuildImageMenu(ImageMark mark)
        {
            GlassMenu menu = new GlassMenu();
            if (mark == null)
            {
                return menu;
            }
            Detection detection = mark.Tag as Detection;
            if (detection != null)
            {
                AddGroupActions(menu, GroupOf(detection));
                return menu;
            }
            ImageArea area = mark.Tag as ImageArea;
            if (area == null)
            {
                return menu;
            }
            switch (area.Kind)
            {
                case AreaKind.Unread:
                    menu.AddHeader("Текст не прочитан");
                    menu.AddItem(area.Enabled ? "Не закрывать" : "Закрыть", null, true, delegate { ToggleArea(area); });
                    break;
                case AreaKind.Manual:
                    menu.AddHeader("Закрыто вручную");
                    menu.AddItem("Убрать рамку", null, true, delegate { RemoveArea(area); });
                    break;
                case AreaKind.Tail:
                    menu.AddHeader("После пароля строка закрывается всегда");
                    break;
                default:
                    menu.AddHeader("Читалось на готовой картинке, закрыто");
                    break;
            }
            return menu;
        }

        private void ToggleArea(ImageArea area)
        {
            if (image == null || area.Locked)
            {
                return;
            }
            area.Enabled = !area.Enabled;
            RefreshImage(area.Enabled ? "Место будет закрыто." : "Место останется как есть.");
        }

        private void RemoveArea(ImageArea area)
        {
            if (image == null)
            {
                return;
            }
            image.Areas.Remove(area);
            RefreshImage("Рамка убрана.");
        }

        private void OnAreaDrawn(RectangleF area)
        {
            if (image == null || image.Source == null)
            {
                return;
            }
            GlassTip.HideAll();
            image.Areas.Add(new ImageArea(area, AreaKind.Manual, true));
            RefreshImage("Место закрыто. Правый клик по рамке уберёт её.");
        }

        private void OnResultImageHover(object sender, EventArgs e)
        {
            ImageMark mark = resultImageView.HoveredMark;
            if (mark == null || mark.Tag == null)
            {
                GlassTip.HideFor(resultImageView);
                return;
            }
            Rectangle anchor = resultImageView.RectangleToScreen(resultImageView.HoverBounds);
            Detection detection = mark.Tag as Detection;
            if (detection == null)
            {
                string[] tip = DescribeArea((ImageArea)mark.Tag);
                GlassTip.ShowRich(resultImageView, anchor, tip[0], Theme.StrongFont, Theme.Warm, tip[1], null, 250);
                return;
            }
            GlassTip.ShowRich(resultImageView, anchor, Shorten(detection.Value, 60), Theme.MonoFont,
                detection.IsSensitiveValue ? Theme.Danger : Theme.Warm,
                "Так было на картинке. " + TypeNames.Describe(detection.Type), "Правый клик: действия", 250);
        }

        private void OnResultImageContext(object sender, MouseEventArgs e)
        {
            GlassTip.HideAll();
            ShowMenu(BuildImageMenu(resultImageView.MarkAt(e.Location)), resultImageView, e.Location);
        }

        /// <summary>Строка списка находок показывает своё место на картинке.</summary>
        private void RevealInImage(Detection first)
        {
            if (image == null || image.Reading == null)
            {
                return;
            }
            List<RectangleF> boxes = ImageScanner.BoxesFor(image.Reading, first);
            if (boxes.Count > 0)
            {
                imageView.Reveal(boxes[0]);
            }
        }

        // ---------------------------------------------------------------- поиск по картинке

        private void UpdateImageSearch()
        {
            if (image == null)
            {
                return;
            }
            FindImageMatches();
            imageMatch = imageMatches.Count > 0 ? 0 : -1;
            imageView.SetMarks(BuildImageMarks());
            ShowImageMatch();
        }

        /// <summary>Совпадения поиска в распознанном тексте, переведённые в прямоугольники картинки.</summary>
        private void FindImageMatches()
        {
            imageMatches.Clear();
            string query = imageHeader.Query.Trim();
            if (image == null || image.Reading == null || query.Length == 0)
            {
                imageHeader.SetCounter(string.Empty);
                return;
            }
            string masked = Replacer.MaskSecrets(sourceText, detections);
            int at = masked.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            while (at >= 0 && imageMatches.Count < 500)
            {
                imageMatches.AddRange(ImageScanner.BoxesFor(image.Reading, at, at + query.Length, false));
                at = masked.IndexOf(query, at + Math.Max(1, query.Length), StringComparison.OrdinalIgnoreCase);
            }
            if (imageMatch >= imageMatches.Count)
            {
                imageMatch = imageMatches.Count - 1;
            }
        }

        private void StepImageMatch(int direction)
        {
            if (imageMatches.Count == 0)
            {
                return;
            }
            imageMatch = ((imageMatch < 0 ? 0 : imageMatch + direction) + imageMatches.Count) % imageMatches.Count;
            ShowImageMatch();
        }

        private void ShowImageMatch()
        {
            if (imageHeader.Query.Trim().Length == 0)
            {
                imageHeader.SetCounter(string.Empty);
                return;
            }
            if (imageMatches.Count == 0)
            {
                imageHeader.SetCounter("нет");
                return;
            }
            imageHeader.SetCounter((imageMatch + 1).ToString(CultureInfo.InvariantCulture) + " из "
                + imageMatches.Count.ToString(CultureInfo.InvariantCulture));
            imageView.Reveal(imageMatches[imageMatch]);
        }

        // ---------------------------------------------------------------- освобождение

        /// <summary>Картинки всех вкладок освобождаются вместе с окном.</summary>
        private void ReleaseImages()
        {
            foreach (Document record in documents)
            {
                if (record.Image != null)
                {
                    record.Image.Release();
                }
            }
            if (image != null)
            {
                image.Release();
            }
        }
    }
}
