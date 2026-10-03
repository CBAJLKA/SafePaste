using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using System.Text.RegularExpressions;
using SafePaste.Detecting;
using SafePaste.Interop;
using SafePaste.Storage;
using SafePaste.Bridge;
using SafePaste.Mcp;
using SafePaste.Ui;

namespace SafePaste
{
    internal static class AppInfo
    {
        internal const string Version = "0.4";
    }

    /// <summary>Значок в трее, горячие клавиши и сам сценарий безопасной вставки.</summary>
    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem statusItem;
        private readonly SettingsActions settingsActions;
        private readonly MessageWindow window;
        private readonly System.Windows.Forms.Timer clearTimer;
        private readonly BridgeHost bridge;
        private readonly Control invoker;

        private SafePasteSettings settings;
        private string clearExpected;
        private bool clearImage;
        private bool reviewInProgress;
        private ReviewForm openReview;
        // Копия, которую сделал сам SafePaste по Ctrl+Shift+C: её не нужно открывать ещё и вкладкой проверки.
        private uint capturedSequence;
        // Последнее изменение буфера, уже предложенное окну проверки вкладкой.
        private uint offeredSequence;
        private DatabaseForm openDatabase;
        private AboutForm openAbout;
        private BridgeLabelsForm openLabels;
        private BridgeForm openBridge;
        private SettingsForm openSettings;

        /// <param name="autostart">Запуск при входе в Windows: тогда SafePaste стартует молча.</param>
        internal TrayApplication(bool autostart)
        {
            settings = SafePasteSettings.Load();

            // Мост работает в фоновых потоках, а окна подтверждения показывает поток интерфейса.
            invoker = new Control();
            IntPtr handle = invoker.Handle;
            bridge = new BridgeHost(delegate { return new ApprovalGate(PromptApproval); });

            window = new MessageWindow();
            window.HotkeyPressed = OnHotkey;
            window.ClipboardChanged = OnClipboardChanged;

            clearTimer = new System.Windows.Forms.Timer();
            clearTimer.Tick += OnClearTick;

            // Одно окно настроек на трей и шестерёнку окна проверки.
            settingsActions = new SettingsActions();
            settingsActions.ShowRules = ShowDatabase;
            settingsActions.ShowBridge = ShowBridge;
            settingsActions.DescribeBridge = DescribeBridge;
            settingsActions.StartsWithWindows = delegate { return Autostart.IsEnabled(Application.ExecutablePath); };
            settingsActions.SetStartsWithWindows = delegate(bool on)
            {
                if (on) Autostart.Enable(Application.ExecutablePath);
                else Autostart.Disable();
            };
            settingsActions.UseSettingsFile();

            tray = new NotifyIcon();
            tray.Icon = AppIcon.Value;
            tray.Text = "SafePaste";
            tray.ContextMenuStrip = BuildMenu(out statusItem);
            tray.DoubleClick += delegate { StartReview(false, true); };
            tray.Visible = true;

            // До стартовых предупреждений: агент мог запустить SafePaste и ждёт канал моста.
            StartBridge();

            Native.StartForegroundTracking();
            List<string> problems = RegisterHotkeys();
            SetStatus("Работает локально");

            if (problems.Count > 0)
            {
                Alerts.Show(null, AlertKind.Warning, "Горячие клавиши заняты",
                    "Не получилось назначить: " + string.Join(", ", problems.ToArray()) + ". "
                    + "Скорее всего, их уже использует другая программа. "
                    + "Проверку можно открыть из меню значка в трее.");
            }
            WarnAboutClipboardHistory();
            if (!autostart)
            {
                // При ручном запуске говорим, что программа работает: окна у неё нет, только значок.
                Notify("SafePaste запущен", problems.Count == 0
                    ? "Значок в трее рядом с часами. Проверка текста: Ctrl+Alt+Shift+V, быстрая вставка: Ctrl+Shift+V."
                    : "Значок в трее рядом с часами, проверку можно открыть из его меню.", ToolTipIcon.Info);
            }
        }

        private ContextMenuStrip BuildMenu(out ToolStripMenuItem status)
        {
            return TrayMenu.Create(delegate { StartReview(false, true); }, delegate { StartReview(true, true); },
                ShowRealValues, delegate { ShowSettings(null); }, ShowAbout, ExitThread,
                delegate { return settings.Mode; }, ChooseMode, out status);
        }

        // ---------------------------------------------------------------- мост

        private void StartBridge()
        {
            if (!BridgeSettings.Load().Enabled)
            {
                return;
            }
            try
            {
                bridge.Start();
            }
            catch (Exception)
            {
                Notify("Мост для агентов не запустился: " + bridge.Problem + " Подробности в окне моста.", ToolTipIcon.Warning);
            }
        }

        private string DescribeBridge()
        {
            try
            {
                BridgeStatus status = bridge.Snapshot();
                if (!status.Running)
                {
                    return status.Problem != null ? "Не запустился, откройте окно" : "Выключен";
                }
                string text = BridgeForm.Describe(status);
                foreach (BridgeSessionInfo session in status.Sessions)
                {
                    if (session.State.StartsWith("ждёт вашего", StringComparison.Ordinal))
                    {
                        return text + ", ждёт подтверждения";
                    }
                }
                return text;
            }
            catch (Exception)
            {
                return "Состояние недоступно";
            }
        }

        private void ShowBridge()
        {
            if (openBridge != null && !openBridge.IsDisposed)
            {
                openBridge.Activate();
                return;
            }
            openBridge = new BridgeForm(bridge, ShowLabels, RevealLabels);
            openBridge.FormClosed += delegate { openBridge = null; };
            openBridge.Show();
        }

        /// <summary>
        /// Вызывается из потока сессии моста. Окно показывает поток интерфейса, сессия ждёт ответа
        /// не дольше 2 минут; отключение агента закрывает окно.
        /// </summary>
        private int PromptApproval(string title, string details, WaitHandle cancel)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            int decision = 0;
            ApprovalForm form = null;
            try
            {
                invoker.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        form = new ApprovalForm(title, details);
                        // Немодальное окно освобождается само после закрытия.
                        form.FormClosed += delegate
                        {
                            decision = form.Decision;
                            done.Set();
                        };
                        form.Show();
                        form.Activate();
                        Notify("Агент ждёт подтверждения: " + title + ".", ToolTipIcon.Info);
                    }
                    catch (Exception)
                    {
                        done.Set();
                    }
                });
            }
            catch (Exception)
            {
                return 0; // приложение закрывается
            }
            if (WaitHandle.WaitAny(new WaitHandle[] { done, cancel }, ApprovalGate.TimeoutMs) == 0)
            {
                return decision;
            }
            try
            {
                invoker.BeginInvoke((MethodInvoker)delegate
                {
                    if (form != null && !form.IsDisposed) form.Close();
                });
            }
            catch (Exception)
            {
            }
            done.WaitOne(5000);
            return 0;
        }

        /// <summary>Режим общий для быстрой вставки и окна проверки, поэтому сразу сохраняется.</summary>
        private void ChooseMode(ControlMode mode)
        {
            settings = SafePasteSettings.Load();
            settings.Mode = mode;
            try
            {
                settings.Save();
            }
            catch (Exception)
            {
                // Не удалось записать настройки: режим всё равно действует до перезапуска.
            }
        }

        private List<string> RegisterHotkeys()
        {
            List<string> problems = new List<string>();
            uint common = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
            if (!Native.RegisterHotKey(window.Handle, Native.HotkeyReview, common | Native.MOD_ALT, Native.VK_V))
            {
                problems.Add("Ctrl+Alt+Shift+V (проверка)");
            }
            if (!Native.RegisterHotKey(window.Handle, Native.HotkeyQuick, common, Native.VK_V))
            {
                problems.Add("Ctrl+Shift+V (быстрая вставка)");
            }
            if (!Native.RegisterHotKey(window.Handle, Native.HotkeyDecrypt, common, Native.VK_C))
            {
                problems.Add("Ctrl+Shift+C (расшифровка)");
            }
            if (!Native.AddClipboardFormatListener(window.Handle))
            {
                problems.Add("наблюдение за буфером обмена");
            }
            return problems;
        }

        private void WarnAboutClipboardHistory()
        {
            if (!settings.ShowClipboardWarning)
            {
                return;
            }
            List<string> risks = ClipboardService.GetRisks();
            if (risks.Count == 0)
            {
                return;
            }
            bool silence = Alerts.ShowWithCheck(null, AlertKind.Warning, "Windows хранит копии буфера обмена",
                "Включена " + string.Join(" и ", risks.ToArray()) + ". "
                + "Всё, что вы копируете, сохраняется там, в том числе исходный текст до проверки. "
                + "SafePaste эти настройки не меняет.",
                "Больше не предупреждать");
            if (silence)
            {
                settings.ShowClipboardWarning = false;
                try
                {
                    settings.Save();
                }
                catch (Exception)
                {
                    // Не удалось записать настройки: не повод останавливать запуск.
                }
            }
        }

        // ---------------------------------------------------------------- события

        private void OnHotkey(int id)
        {
            if (id == Native.HotkeyReview)
            {
                StartReview(false, false);
            }
            else if (id == Native.HotkeyQuick)
            {
                StartReview(true, false);
            }
            else if (id == Native.HotkeyDecrypt)
            {
                StartDecrypt(false);
            }
        }

        private void OnClipboardChanged()
        {
            if (reviewInProgress)
            {
                OfferCopy();
                return;
            }
            try
            {
                SetStatus(Clipboard.ContainsText() ? "В буфере есть текст"
                    : ClipboardService.HasImage() ? "В буфере картинка" : "Буфер пуст");
            }
            catch (Exception) { SetStatus("Буфер занят другой программой"); }
        }

        /// <summary>
        /// Пока окно проверки открыто, новая копия в любой программе открывается в нём вкладкой.
        /// Свои записи SafePaste (результат, очистка) и копии, которые программа просит не отслеживать
        /// (менеджеры паролей), не открываются. В фоне, без открытого окна, копии не собираются.
        /// </summary>
        private void OfferCopy()
        {
            if (openReview == null || openReview.IsDisposed)
            {
                return;
            }
            uint sequence = Native.GetClipboardSequenceNumber();
            if (sequence == offeredSequence || sequence == capturedSequence || ClipboardService.IsOwnChange()
                || ClipboardService.IsExcludedFromMonitoring())
            {
                return;
            }
            offeredSequence = sequence;
            string text;
            string error;
            bool empty;
            if (ClipboardService.TryGetText(out text, out error, out empty))
            {
                openReview.AddCopiedText(text);
                return;
            }
            // Картинка без текста тоже открывается вкладкой; окно освобождает её само.
            Bitmap picture = empty ? ClipboardService.TryGetImage(out error) : null;
            if (picture != null)
            {
                openReview.AddCopiedImage(picture);
            }
        }

        // ---------------------------------------------------------------- сценарий вставки

        private void StartReview(bool quick, bool fromTray)
        {
            if (reviewInProgress)
            {
                if (openReview != null)
                {
                    openReview.Activate();
                }
                return;
            }
            reviewInProgress = true;
            Bitmap picture = null;
            try
            {
                IntPtr target = Native.GetPasteTarget(fromTray);
                string text;
                string error;
                bool empty;
                string notice = null;
                if (!ClipboardService.TryGetText(out text, out error, out empty))
                {
                    bool imageInClipboard = empty && ClipboardService.HasImage();
                    if (imageInClipboard)
                    {
                        picture = ClipboardService.TryGetImage(out error);
                    }
                    if (quick)
                    {
                        if (picture != null)
                        {
                            quick = false; // Картинку нужно показать: OCR может пропустить секрет.
                        }
                        else if (imageInClipboard)
                        {
                            Alerts.Show(null, AlertKind.Error, "Картинка недоступна",
                                error ?? "Не удалось прочитать картинку из буфера обмена.");
                        }
                        else if (empty)
                        {
                            Alerts.Show(null, AlertKind.Warning, "В буфере нет текста",
                                "Скопируйте текст (Ctrl+C) и нажмите сочетание ещё раз. "
                                + "Окно проверки (Ctrl+Alt+Shift+V) открывается и без текста.");
                        }
                        else
                        {
                            Alerts.Show(null, AlertKind.Error, "Буфер обмена недоступен", error + " Попробуйте ещё раз.");
                        }
                        if (quick)
                        {
                            return;
                        }
                    }
                    // Окно проверки открывается и без текста: его можно вставить или набрать прямо там.
                    text = string.Empty;
                    if (!empty || (picture == null && imageInClipboard))
                    {
                        notice = (error ?? "Картинку не удалось прочитать из буфера.")
                            + " Возьмите содержимое ещё раз кнопкой со стрелками.";
                    }
                }
                settings = SafePasteSettings.Load();
                if (text.Length > settings.MaxClipboardChars)
                {
                    Alerts.Show(null, AlertKind.Warning, "Слишком большой текст",
                        "В буфере " + text.Length.ToString(CultureInfo.InvariantCulture) + " символов, а предел "
                        + settings.MaxClipboardChars.ToString(CultureInfo.InvariantCulture)
                        + ". Вставка отменена. Предел можно поменять в settings.json (MaxClipboardChars).");
                    return;
                }

                SafePasteDatabase database = SafePasteDatabase.Load();
                LabelMemory.Load(database, settings, settings.Mode);
                if (quick)
                {
                    List<Detection> quickFound = Detector.Scan(text, database, settings.Mode, true, settings.SmartSecrets);
                    ReplacementResult quickResult = LabelMemory.Apply(text, quickFound, database, settings.SaveLabels);
                    PasteSanitized(quickResult.Text, target, quickResult.HiddenCount);
                    return;
                }

                List<Detection> found = Detector.Scan(text, database, settings.Mode, false, settings.SmartSecrets);
                ShowWindow(text, found, database, target, notice, false, picture);
                picture = null;
            }
            catch (Exception failure)
            {
                Alerts.Show(null, AlertKind.Error, "Вставка отменена", failure.Message);
            }
            finally
            {
                if (picture != null)
                {
                    picture.Dispose();
                }
                reviewInProgress = false;
            }
        }

        /// <summary>
        /// Окно проверки. Пока оно открыто, новые копии становятся вкладками (OfferCopy), вставка вкладки
        /// при других открытых вкладках идёт сразу, а окно остаётся в фоне. decrypt: окно открывается
        /// вкладкой расшифровки с этим текстом.
        /// </summary>
        private void ShowWindow(string text, List<Detection> found, SafePasteDatabase database, IntPtr target,
            string notice, bool decrypt, Bitmap picture = null)
        {
            bool canPaste = Native.IsPasteTarget(target);
            using (ReviewForm form = new ReviewForm(decrypt ? string.Empty : text, decrypt ? null : found, database, settings,
                Native.GetWindowTitle(target), canPaste))
            {
                form.ShowSettings = ShowSettings;
                form.PasteRequested = delegate(ReplacementResult result) { PasteSanitized(result.Text, target, result.HiddenCount); };
                form.PasteImageRequested = delegate(Bitmap result, int hiddenCount)
                {
                    try { PasteSanitizedImage(result, target, hiddenCount); }
                    finally { result.Dispose(); }
                };
                if (picture != null)
                {
                    form.AddImage(picture);
                }
                if (decrypt)
                {
                    form.AddDecryptText(text);
                }
                if (notice != null)
                {
                    form.ShowNotice(notice);
                }
                openReview = form;
                offeredSequence = Native.GetClipboardSequenceNumber();
                try
                {
                    form.ShowDialog();
                }
                finally
                {
                    openReview = null;
                }
                if (form.Action == ReviewAction.Paste && form.ResultImage != null)
                {
                    using (form.ResultImage)
                    {
                        PasteSanitizedImage(form.ResultImage, target, form.Result == null ? 0 : form.Result.HiddenCount);
                    }
                }
                else if (form.Action == ReviewAction.Paste && form.Result != null)
                {
                    PasteSanitized(form.Result.Text, target, form.Result.HiddenCount);
                }
            }
        }

        /// <summary>
        /// Ctrl+Shift+C: копирует выделенное в активном окне и показывает его с реальными значениями
        /// вместо меток. Копирует через Ctrl+Insert: в терминале, в отличие от Ctrl+C, он не прерывает
        /// команду. Если ничего не выделено, берётся то, что уже лежит в буфере.
        /// </summary>
        private void StartDecrypt(bool fromTray)
        {
            IntPtr target = Native.GetPasteTarget(fromTray);
            string text = fromTray ? null : CaptureSelection(target);
            string notice = null;
            if (text == null)
            {
                string error;
                bool empty;
                if (!ClipboardService.TryGetText(out text, out error, out empty))
                {
                    text = string.Empty;
                    notice = empty ? null : error;
                }
            }
            if (reviewInProgress)
            {
                if (openReview != null)
                {
                    openReview.AddDecryptText(text);
                    openReview.Activate();
                }
                return;
            }
            reviewInProgress = true;
            try
            {
                settings = SafePasteSettings.Load();
                if (text.Length > settings.MaxClipboardChars)
                {
                    Alerts.Show(null, AlertKind.Warning, "Слишком большой текст",
                        "В буфере " + text.Length.ToString(CultureInfo.InvariantCulture) + " символов, а предел "
                        + settings.MaxClipboardChars.ToString(CultureInfo.InvariantCulture) + ".");
                    return;
                }
                SafePasteDatabase database = SafePasteDatabase.Load();
                LabelMemory.Load(database, settings, settings.Mode);
                ShowWindow(text, null, database, target, notice, true);
            }
            catch (Exception failure)
            {
                Alerts.Show(null, AlertKind.Error, "Расшифровка не удалась", failure.Message);
            }
            finally
            {
                reviewInProgress = false;
            }
        }

        /// <summary>
        /// Копирует выделенное в окне target. Ждёт, пока отпустят Ctrl, Shift и C, иначе программа
        /// увидела бы Ctrl+Shift+Insert. null: окно своё или с правами администратора, либо буфер
        /// не изменился (ничего не выделено).
        /// </summary>
        private string CaptureSelection(IntPtr target)
        {
            if (target == IntPtr.Zero || Native.IsElevatedTarget(target)
                || !Native.WaitForKeysReleased(settings.KeyReleaseTimeoutMs, Native.VK_C))
            {
                return null;
            }
            uint before = Native.GetClipboardSequenceNumber();
            if (!Native.SendCtrlInsert())
            {
                return null;
            }
            Stopwatch watch = Stopwatch.StartNew();
            while (Native.GetClipboardSequenceNumber() == before && watch.ElapsedMilliseconds < 700)
            {
                Thread.Sleep(20);
            }
            if (Native.GetClipboardSequenceNumber() == before)
            {
                return null;
            }
            // Программа может класть форматы по очереди: даём ей закончить.
            Thread.Sleep(40);
            string text;
            string error;
            if (!ClipboardService.TryGetText(out text, out error))
            {
                return null;
            }
            capturedSequence = Native.GetClipboardSequenceNumber();
            return text;
        }

        /// <summary>
        /// Кладёт обезличенный текст в буфер и по возможности вставляет его в исходное окно.
        /// Если это невозможно, текст остаётся в буфере и пользователь вставляет его сам.
        /// </summary>
        private void PasteSanitized(string text, IntPtr target, int hiddenCount)
        {
            StopClearTimer();
            string error;
            if (!ClipboardService.TrySetText(text, out error))
            {
                Alerts.Show(null, AlertKind.Error, "Не удалось записать в буфер", error);
                return;
            }

            string problem = null;
            if (!Native.IsPasteTarget(target))
            {
                problem = "не нашлось окна для вставки";
            }
            else if (Native.IsElevatedTarget(target))
            {
                problem = "окно запущено от имени администратора";
            }
            else if (!Native.WaitForKeysReleased(settings.KeyReleaseTimeoutMs))
            {
                problem = "клавиши Ctrl/Shift не были отпущены";
            }
            else if (!Native.ActivateWindow(target, 800))
            {
                problem = "не удалось вернуть фокус исходному окну";
            }
            else
            {
                // Клиентам RDP нужно время, чтобы забрать новый буфер до нажатия Ctrl+V.
                Thread.Sleep(120);
                if (!Native.SendCtrlV())
                {
                    problem = "Windows не приняла нажатие Ctrl+V";
                }
            }

            if (problem != null)
            {
                Notify("Автовставка не выполнена: " + problem
                    + ". Результат в буфере, вставьте его сами (Ctrl+V).", ToolTipIcon.Warning);
                SetStatus("Результат в буфере, вставьте вручную");
                return;
            }

            StartClearTimer(text);
            SetStatus(hiddenCount > 0
                ? "Вставлено, скрыто: " + hiddenCount.ToString(CultureInfo.InvariantCulture)
                : "Вставлено без замен");
        }

        /// <summary>Кладёт готовую картинку в буфер и вставляет её в исходное окно.</summary>
        private void PasteSanitizedImage(Bitmap picture, IntPtr target, int hiddenCount)
        {
            StopClearTimer();
            string error;
            if (!ClipboardService.TrySetImage(picture, out error))
            {
                Alerts.Show(null, AlertKind.Error, "Не удалось записать картинку в буфер", error);
                return;
            }

            string problem = null;
            if (!Native.IsPasteTarget(target))
            {
                problem = "не нашлось окна для вставки";
            }
            else if (Native.IsElevatedTarget(target))
            {
                problem = "окно запущено от имени администратора";
            }
            else if (!Native.WaitForKeysReleased(settings.KeyReleaseTimeoutMs))
            {
                problem = "клавиши Ctrl/Shift не были отпущены";
            }
            else if (!Native.ActivateWindow(target, 800))
            {
                problem = "не удалось вернуть фокус исходному окну";
            }
            else
            {
                Thread.Sleep(120);
                if (!Native.SendCtrlV())
                {
                    problem = "Windows не приняла нажатие Ctrl+V";
                }
            }
            if (problem != null)
            {
                Notify("Автовставка картинки не выполнена: " + problem
                    + ". Результат в буфере, вставьте его сами (Ctrl+V).", ToolTipIcon.Warning);
                SetStatus("Картинка в буфере, вставьте вручную");
                return;
            }
            clearImage = true;
            clearTimer.Interval = settings.ClipboardClearDelayMs;
            clearTimer.Start();
            SetStatus(hiddenCount > 0
                ? "Картинка вставлена, скрыто: " + hiddenCount.ToString(CultureInfo.InvariantCulture)
                : "Картинка вставлена без замен");
        }

        private void StartClearTimer(string expected)
        {
            clearExpected = expected;
            clearTimer.Interval = settings.ClipboardClearDelayMs;
            clearTimer.Start();
        }

        private void StopClearTimer()
        {
            clearTimer.Stop();
            clearExpected = null;
            clearImage = false;
        }

        private void OnClearTick(object sender, EventArgs e)
        {
            clearTimer.Stop();
            if (clearExpected != null)
            {
                ClipboardService.ClearIfUnchanged(clearExpected);
                clearExpected = null;
            }
            else if (clearImage)
            {
                ClipboardService.ClearIfOwn();
                clearImage = false;
            }
        }

        // ---------------------------------------------------------------- окна и уведомления

        private void ShowDatabase()
        {
            if (openDatabase != null && !openDatabase.IsDisposed)
            {
                openDatabase.Activate();
                return;
            }
            openDatabase = new DatabaseForm();
            openDatabase.FormClosed += delegate { openDatabase = null; };
            openDatabase.Show();
        }

        /// <summary>
        /// Окно настроек, одно на всё приложение. Открытое до окна проверки заблокировано её модальным
        /// циклом, поэтому по шестерёнке его открываем заново: новое окно, созданное внутри цикла, работает.
        /// owner: окно проверки, над которым настройки встанут; null из меню значка.
        /// </summary>
        private void ShowSettings(Form owner)
        {
            if (openSettings != null && !openSettings.IsDisposed)
            {
                if (Native.IsWindowEnabled(openSettings.Handle))
                {
                    openSettings.Activate();
                    return;
                }
                openSettings.Close();
            }
            SettingsForm form = new SettingsForm(settingsActions);
            form.Changed += delegate
            {
                if (openReview != null && !openReview.IsDisposed)
                {
                    openReview.SettingsChanged();
                }
            };
            form.FormClosed += delegate
            {
                if (openSettings == form) openSettings = null;
            };
            openSettings = form;
            if (owner != null && !owner.IsDisposed)
            {
                form.Show(owner);
            }
            else
            {
                form.Show();
            }
        }

        private void ShowAbout()
        {
            if (openAbout != null && !openAbout.IsDisposed)
            {
                openAbout.Activate();
                return;
            }
            openAbout = new AboutForm();
            openAbout.FormClosed += delegate { openAbout = null; };
            openAbout.Show();
        }

        /// <summary>«Показать с реальными значениями»: окно в режиме расшифровки с текстом из буфера.</summary>
        private void ShowRealValues()
        {
            StartDecrypt(true);
        }

        /// <summary>
        /// Раскрывает закреплённые номера из правил и запомненные метки из labels.dat. Закрепление
        /// важнее: номер за ним не может достаться другому значению. Секреты остаются метками:
        /// их значения живут только в памяти моста и на экран трея не выводятся.
        /// </summary>
        private static string RevealLabels(string text)
        {
            LabelStore store = LabelStore.OpenIfExists();
            SafePasteDatabase database = SafePasteDatabase.Load();
            if (store == null && database.Reservations.Count == 0)
            {
                throw new InvalidOperationException("Запомненных меток и закреплённых номеров пока нет.");
            }
            Dictionary<string, string> reserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ReservedPlaceholder reservation in database.Reservations)
            {
                reserved[reservation.Placeholder] = reservation.Value;
            }
            string raw = Labels.Placeholder.Replace(text, delegate(Match match)
            {
                string value; bool secret;
                if (reserved.TryGetValue(match.Value, out value)) return value;
                if (store == null || !store.TryResolve(match.Value, out value, out secret) || secret) return match.Value;
                return value;
            });
            if (store != null) store.Flush();
            return Labels.Unescape(raw);
        }

        private void ShowLabels()
        {
            if (openLabels != null && !openLabels.IsDisposed) { openLabels.Activate(); return; }
            try
            {
                LabelStore store = LabelStore.OpenIfExists() ?? new LabelStore(true);
                openLabels = new BridgeLabelsForm(store);
                openLabels.FormClosed += delegate { openLabels = null; };
                openLabels.Show();
            }
            catch (Exception failure) { Alerts.Show(null, AlertKind.Error, "Не удалось открыть метки", failure.Message); }
        }

        private void SetStatus(string message)
        {
            statusItem.Text = message;
            string tip = "SafePaste: " + message;
            tray.Text = tip.Length <= 63 ? tip : tip.Substring(0, 63);
        }

        private void Notify(string message, ToolTipIcon icon)
        {
            Notify("SafePaste", message, icon);
        }

        private void Notify(string title, string message, ToolTipIcon icon)
        {
            try
            {
                tray.ShowBalloonTip(6000, title, message, icon);
            }
            catch (Exception)
            {
                // Уведомления могут быть отключены политикой, это не ошибка.
            }
        }

        protected override void ExitThreadCore()
        {
            // Агенты теряют связь с мостом, их команды останавливаются.
            bridge.Dispose();
            if (openBridge != null && !openBridge.IsDisposed)
            {
                openBridge.Close();
            }
            if (openSettings != null && !openSettings.IsDisposed)
            {
                openSettings.Close();
            }

            // Если очистка буфера ещё не отработала, выполняем её сразу.
            if (clearExpected != null)
            {
                ClipboardService.ClearIfUnchanged(clearExpected);
                clearExpected = null;
            }
            else if (clearImage)
            {
                ClipboardService.ClearIfOwn();
                clearImage = false;
            }
            clearTimer.Stop();
            clearTimer.Dispose();

            Native.UnregisterHotKey(window.Handle, Native.HotkeyReview);
            Native.UnregisterHotKey(window.Handle, Native.HotkeyQuick);
            Native.UnregisterHotKey(window.Handle, Native.HotkeyDecrypt);
            Native.RemoveClipboardFormatListener(window.Handle);
            Native.StopForegroundTracking();

            tray.Visible = false;
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            window.Dispose();
            invoker.Dispose();
            base.ExitThreadCore();
        }

        /// <summary>Невидимое окно: только приём WM_HOTKEY и WM_CLIPBOARDUPDATE.</summary>
        private sealed class MessageWindow : NativeWindow, IDisposable
        {
            internal Action<int> HotkeyPressed;
            internal Action ClipboardChanged;

            internal MessageWindow()
            {
                CreateParams parameters = new CreateParams();
                parameters.Caption = "SafePasteMessageWindow";
                parameters.X = 0;
                parameters.Y = 0;
                parameters.Width = 0;
                parameters.Height = 0;
                parameters.Style = 0; // без WS_VISIBLE: окна на экране нет
                CreateHandle(parameters);
            }

            protected override void WndProc(ref Message message)
            {
                try
                {
                    if (message.Msg == Native.WM_HOTKEY && HotkeyPressed != null)
                    {
                        HotkeyPressed(message.WParam.ToInt32());
                        return;
                    }
                    if (message.Msg == Native.WM_CLIPBOARDUPDATE && ClipboardChanged != null)
                    {
                        ClipboardChanged();
                    }
                }
                catch (Exception failure)
                {
                    Alerts.ShowSafe(null, AlertKind.Error, "Непредвиденная ошибка", failure.Message);
                }
                base.WndProc(ref message);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    DestroyHandle();
                }
            }
        }
    }
}
