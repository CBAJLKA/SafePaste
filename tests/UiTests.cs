using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using SafePaste.Detecting;
using SafePaste.Interop;
using SafePaste.Storage;
using SafePaste.Bridge;
using SafePaste.Mcp;
using SafePaste.Ui;
using SafePaste.Imaging;

namespace SafePaste.Tests
{
    /// <summary>
    /// Проверки интерфейса на вымышленных данных. Окна не показываются, реальные буфер обмена
    /// и база не затрагиваются. Снимки окон складываются в bin\ui-preview.
    /// </summary>
    internal static class UiTestProgram
    {
        private static int checks;
        private static readonly string Output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-preview");

        private const string Fixture = "# Incident report / production\r\n\r\nhostname: SRV-DB01\r\n"
            + "endpoint: https://api.example.com/v1/status\r\naddress: 10.24.8.16\r\n"
            + "user: operator@example.com\r\npassword: DemoSecret123!\r\n\r\n"
            + "Connection to SRV-DB01 failed.\r\nRetry from SW-CORE-01 in 30 seconds.\r\n";
        private const string Target = "Рабочий отчёт.txt - Блокнот";

        [STAThread]
        private static int Main()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "SafePaste-ui-" + Guid.NewGuid().ToString("N"));
            Paths.DataDirectory = sandbox;
            Theme.ForceSolid = true;
            try
            {
                Native.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Directory.CreateDirectory(Output);
                TestTextMap();
                TestTextView();
                TestReview();
                TestSearch();
                TestModes();
                TestPanels();
                TestSyncScroll();
                TestPasteIntoSource();
                TestEditing();
                TestEditingKeepsDecisions();
                TestModeRescan();
                TestRememberedPath();
                TestEmptyReview();
                TestDialogs();
                TestDatabase();
                TestBridgeForms();
                TestBridgeWindow();
                TestMarkMenus();
                TestRememberedLabels();
                TestTabs();
                TestImages();
                TestImageMonitoring();
                TestSecretFeedback();
                TestSameButtons();
                TestDecrypt();
                using (AboutForm about = new AboutForm())
                {
                    CheckTexts(about, "about");
                    Render(about, "about");
                }
                TestTray();
                TestSettings();
                TestMotion();
                TestSourceTexts();
                Console.WriteLine("UI checks passed: " + checks);
                Console.WriteLine("Previews: " + Output);
                return 0;
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine(failure);
                return 1;
            }
            finally
            {
                string resolved = Path.GetFullPath(sandbox).TrimEnd(Path.DirectorySeparatorChar);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (resolved.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(resolved).StartsWith("SafePaste-ui-", StringComparison.Ordinal)
                    && Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }

        private static ReviewForm NewReview(string text, SafePasteSettings settings, bool canPaste)
        {
            SafePasteDatabase database = new SafePasteDatabase();
            ReviewForm form = new ReviewForm(text, Detector.Scan(text, database, settings.Mode, false), database, settings,
                canPaste ? Target : string.Empty, canPaste);
            CreateHandles(form);
            return form;
        }

        // ---------------------------------------------------------------- компоненты

        private static void TestTextMap()
        {
            TextMap map = new TextMap("a\r\nb\tc\u0001d");
            Check(map.Display == "a\nb   c\u00B7d", "Display normalizes newlines, tabs and control characters");
            Check(map.ToDisplay(3) == 2 && map.ToOriginal(2) == 3, "CRLF becomes one display newline");
            Check(map.ToOriginal(3) == 4 && map.ToOriginal(5) == 4, "Expanded tab maps back to the tab");
            Check(map.ToDisplay(5) == 6, "Text after a tab keeps its place");
            Check(map.EndToOriginal(0, 2) == 3, "Selection that ends on a newline covers the whole CRLF");
        }

        private static void TestTextView()
        {
            using (TextView view = new TextView())
            {
                view.Size = new Size(Dpi.S(300), Dpi.S(200));
                string text = "first line\n" + new string('x', 200) + "\nlast";
                List<TextMark> marks = new List<TextMark>();
                marks.Add(new TextMark(0, 5, MarkKind.Hidden, null));
                view.SetContent(text, marks, false);
                Check(view.LineCount > 3, "Long lines wrap instead of scrolling sideways");
                Rectangle cell = view.CharBounds(2);
                int hit = view.CharIndexAt(new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2));
                Check(hit == 2, "Hit testing matches character bounds: " + hit);
                Check(view.MarkAt(hit) == marks[0], "Highlight under the mouse is found");
                view.Select(6, 4);
                Check(view.SelectedText == "line", "Selection returns the chosen text");
                view.Search("X");
                Check(view.MatchCount == 200, "Search ignores case and finds every match: " + view.MatchCount);
                view.Search(string.Empty);
                Check(view.MatchCount == 0, "Empty query clears matches");
                view.SetContent(string.Empty, null, false);
                Check(view.CharIndexAt(new Point(10, 10)) == -1, "Empty view has nothing under the mouse");

                view.SetContent("alpha beta SRV-DB01.", null, false);
                IntPtr handle = view.Handle;
                Rectangle first = view.CharBounds(0);
                Rectangle fifth = view.CharBounds(4);
                Mouse(view, "OnMouseDown", MouseButtons.Left, 1, first.Left + 1, first.Top + 2);
                Mouse(view, "OnMouseMove", MouseButtons.Left, 0, fifth.Right - 1, fifth.Top + 2);
                Mouse(view, "OnMouseUp", MouseButtons.Left, 1, fifth.Right - 1, fifth.Top + 2);
                Check(view.SelectedText == "alpha", "Dragging the mouse selects text: " + view.SelectedText);
                Rectangle name = view.CharBounds(13);
                Mouse(view, "OnMouseDown", MouseButtons.Left, 2, name.Left + 2, name.Top + 2);
                Mouse(view, "OnMouseUp", MouseButtons.Left, 2, name.Left + 2, name.Top + 2);
                Check(view.SelectedText == "SRV-DB01", "Double-click selects a whole name without the final dot: " + view.SelectedText);

                Check(view.ReadOnly, "Text view is read-only unless asked otherwise");
                TypeText(view, "x");
                Check(view.Content == "alpha beta SRV-DB01.", "Read-only view ignores typing");
                view.ReadOnly = false;
                view.SetCaret(5);
                TypeText(view, "!");
                Check(view.Content == "alpha! beta SRV-DB01." && view.CaretIndex == 6, "Typing inserts at the caret: " + view.Content);
                PressKeyDown(view, Keys.Back);
                Check(view.Content == "alpha beta SRV-DB01." && view.CaretIndex == 5, "Backspace removes the character before the caret");
                PressKeyDown(view, Keys.Control | Keys.Right);
                Check(view.CaretIndex == 6, "Ctrl+Right jumps to the next word: " + view.CaretIndex);
                PressKeyDown(view, Keys.Shift | Keys.End);
                Check(view.SelectedText == "beta SRV-DB01.", "Shift+End selects to the end of the line: " + view.SelectedText);
                PressKeyDown(view, Keys.Delete);
                Check(view.Content == "alpha ", "Delete removes the selection: " + view.Content);
                PressKeyDown(view, Keys.Enter);
                Check(view.Content == "alpha \n" && view.LineCount == 2, "Enter starts a new line");
                PressKeyDown(view, Keys.Up);
                Check(view.CaretIndex == 0, "Up moves to the line above: " + view.CaretIndex);
            }
        }

        // ---------------------------------------------------------------- окно проверки

        private static void TestReview()
        {
            using (ReviewForm form = NewReview(Fixture, new SafePasteSettings(), true))
            {
                TextView input = Field<TextView>(form, "preview");
                TextView output = Field<TextView>(form, "resultPreview");
                GlassList list = Field<GlassList>(form, "list");
                Check(!input.Content.Contains("DemoSecret123!"), "Source view masks secrets");
                Check(output.Content == new TextMap(form.BuildResult().Text).Display, "Result view matches the actual replacement");
                Check(form.AcceptButton == null, "Plain Enter cannot paste by accident");
                Check(!Field<bool>(form, "showResult") && !Field<bool>(form, "showList"),
                    "Only the source block is open by default");
                Render(form, "review");

                object locked = null;
                object optional = null;
                object mixed = null;
                for (int i = 0; i < list.Count; i++)
                {
                    object group = list.ItemAt(i);
                    string type = Field<string>(group, "Type");
                    if (type == "SECRET") locked = group;
                    if (type == "IP") optional = group;
                    if (type == "HOST" && Field<string>(group, "Value") == "SRV-DB01") mixed = group;
                }
                Check(locked != null && optional != null && mixed != null, "Fixture has locked, optional and mixed values");
                // SRV-DB01 найден по подсказке «hostname:» и ещё раз без неё: скрыты оба места, иначе
                // открытый повтор рядом выдал бы метку.
                Check(Property<bool>(mixed, "Enabled") && Property<bool>(mixed, "AllEnabled"), "A repeated value is hidden in every place");

                Detection host = FindDetection(Field<List<Detection>>(form, "detections"), "SRV-DB01", true);
                string[] tip = (string[])Call(form, "DescribeSourceMark", host);
                Check(tip[0] == host.Placeholder && tip[0].StartsWith("[HOST_"), "Hover tip shows the replacement: " + tip[0]);
                Check(tip[1].Contains("«hostname»"), "Hover tip explains why the value is hidden: " + tip[1]);

                string before = form.BuildResult().Text;
                Call(form, "ToggleGroup", locked);
                Check(form.BuildResult().Text == before, "Locked secrets cannot be toggled");
                Call(form, "ToggleGroup", optional);
                Check(form.BuildResult().Text.Contains("10.24.8.16"), "Optional value can be kept");
                Check(output.Content == new TextMap(form.BuildResult().Text).Display, "Result view updates after toggling");
                Call(form, "ToggleGroup", mixed);
                string kept = form.BuildResult().Text;
                Check(kept.IndexOf("SRV-DB01", StringComparison.Ordinal) != kept.LastIndexOf("SRV-DB01", StringComparison.Ordinal),
                    "Keeping a repeated value keeps every occurrence");
                Call(form, "ToggleGroup", mixed);
                Check(!form.BuildResult().Text.Contains("SRV-DB01"), "Hiding it again hides every occurrence");

                for (int i = 0; i < list.Count; i++)
                {
                    if (Field<string>(list.ItemAt(i), "Type") == "IP")
                    {
                        list.SelectIndex(i);
                    }
                }
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(list, new object[] { new KeyEventArgs(Keys.Space) });
                Check(!form.BuildResult().Text.Contains("10.24.8.16"), "Space in the list hides the selected value again");

                Segmented filter = Field<Segmented>(form, "filter");
                filter.SelectedIndex = 2;
                for (int i = 0; i < list.Count; i++)
                {
                    Check(!Property<bool>(list.ItemAt(i), "AllEnabled"), "Filter shows values that stay in the text");
                }
                filter.SelectedIndex = 0;

                int retry = input.Content.IndexOf("Retry", StringComparison.Ordinal);
                input.Select(retry, 5);
                Rectangle cell = input.CharBounds(retry + 1);
                using (GlassMenu menu = (GlassMenu)Call(form, "BuildPreviewMenu", new Point(cell.X + 2, cell.Y + 2)))
                {
                    Check(HasItem(menu, "Отметить") && HasItem(menu, "Пометить как"), "Right-click on a selection offers Mark and Mark as");
                    Check(((ToolStripMenuItem)FindItem(menu, "Отметить")).ShortcutKeyDisplayString == "HIDE_TEXT",
                        "Mark shows that the selection becomes HIDE_TEXT");
                    Check(!HasItem(menu, "Вырезать") && !HasItem(menu, "Отменить правку") && !HasItem(menu, "Найти")
                        && !HasItem(menu, "Заменить весь текст текстом из буфера"),
                        "The selection menu does not repeat editing, search and clipboard buttons");
                    Check(!HasItem(menu, "Не скрывать") && !HasItem(menu, "Никогда не скрывать"),
                        "Nothing is found in the selection, so there is nothing to keep");
                    Check(HasItem(menu, "Копировать") && HasItem(menu, "Вставить") == ClipboardService.HasText(),
                        "Copy is offered for a selection, paste only with text in the clipboard");
                    ToolStrip types = ((ToolStripMenuItem)FindItem(menu, "Пометить как")).DropDown;
                    List<string> names = ItemTexts(types);
                    Check(types is GlassMenu && names[0] == "Имя компьютера" && names.Contains("ФИО") && names.Contains("Пароль или секрет"),
                        "Mark as lists the types that fit, the most used first: " + string.Join(", ", names.ToArray()));
                    Check(!names.Contains("IP-адрес") && !names.Contains("Путь к файлу или папке") && !names.Contains("Почта"),
                        "Types that cannot apply to the selection are hidden");
                    Check(names[names.Count - 1] == "Отметить без сохранения", "Marking without saving closes the list");
                    CheckMenuTexts(menu, "selection menu");
                    CheckMenuTexts(types, "mark as menu");
                }
                Call(form, "MarkSelection", "HOST");
                Check(!form.BuildResult().Text.Contains("Retry"), "Manual marking maps CRLF offsets correctly");
                Check(output.Content == new TextMap(form.BuildResult().Text).Display, "Manual marking updates the result");

                int address = input.Content.IndexOf("operator@", StringComparison.Ordinal);
                Rectangle addressCell = input.CharBounds(address + 2);
                using (GlassMenu menu = (GlassMenu)Call(form, "BuildPreviewMenu", new Point(addressCell.X + 2, addressCell.Y + 2)))
                {
                    Check(HasItem(menu, "Скрывать всегда") && HasItem(menu, "Никогда не скрывать") && HasPrefix(menu, "Закрепить номер"),
                        "Right-click on a highlight offers the rule actions");
                    CheckMenuTexts(menu, "value menu");
                }
                list.SelectIndex(0);
                using (GlassMenu menu = (GlassMenu)Call(form, "BuildListMenu"))
                {
                    Check(menu != null && HasItem(menu, "Скрывать всегда"), "Right-click on a list row offers the rule actions");
                    CheckMenuTexts(menu, "list menu");
                }

                List<GlassButton> buttons = new List<GlassButton>();
                Collect(form, buttons);
                foreach (GlassButton button in buttons)
                {
                    Check(string.IsNullOrEmpty(button.Caption), "Review buttons are icons: " + button.Caption);
                    Check(!string.IsNullOrEmpty(button.Tip), "Every icon has a caption on hover: " + button.Glyph);
                    Check(button.Tip.IndexOf("Скрыть выделение", StringComparison.OrdinalIgnoreCase) < 0
                        && button.Tip.IndexOf("Действия с правилом", StringComparison.OrdinalIgnoreCase) < 0,
                        "No separate buttons for selection and rule actions");
                }
                GlassButton copy = Field<GlassButton>(form, "copyButton");
                Check(copy.Glyph == Glyphs.Paste && copy.Tip.StartsWith("Копировать результат"), "Copy result uses the clipboard icon");
                GlassButton gear = Field<GlassButton>(form, "settingsButton");
                GlassButton close = Field<GlassButton>(form, "closeButton");
                Check(gear.Glyph == Glyphs.Settings && gear.Tip == "Настройки" && gear.Right < close.Left
                    && gear.Left > Field<Segmented>(form, "modeSwitch").Right, "A small gear sits in the header next to the close button");
                GlassButton reload = Field<GlassButton>(form, "pasteInButton");
                Check(reload.Glyph == Glyphs.Sync && reload.Tip.StartsWith("Взять текст из буфера"),
                    "Taking text from the clipboard uses circular arrows");
                CheckGlassOnly(form);
                CheckTexts(form, "review");
                form.Size = form.MinimumSize;
                Render(form, "review-minimum");
            }
        }

        private static void TestSearch()
        {
            using (ReviewForm form = NewReview(Fixture, new SafePasteSettings(), true))
            {
                BlockHeader source = Field<BlockHeader>(form, "sourceHeader");
                TextView preview = Field<TextView>(form, "preview");
                Check(!source.Searching, "Search starts collapsed");
                Check(source.SearchButton.Left > 0 && source.SearchButton.Left < source.Width / 2, "Search button sits next to the title");
                source.OpenSearch();
                Check(source.Searching, "Search button turns the title into a field");
                source.Input.Value = "srv";
                Check(preview.MatchCount == 2 && preview.CurrentMatch == 0, "Typing searches at once, Enter is not needed");
                Check(source.Input.Counter == "1 из 2", "Counter shows the current match: " + source.Input.Counter);
                PressKey(source.Input, Keys.Enter);
                Check(preview.CurrentMatch == 1 && source.Input.Counter == "2 из 2", "Enter jumps to the next match");
                Render(form, "review-search");
                PressKey(source.Input, Keys.Escape);
                Check(!source.Searching && preview.MatchCount == 0, "Esc closes the search and clears highlights");

                Call(form, "ToggleResult");
                BlockHeader result = Field<BlockHeader>(form, "resultHeader");
                result.OpenSearch();
                result.Input.Value = "host_";
                Check(Field<TextView>(form, "resultPreview").MatchCount >= 1, "Result block has its own search");

                BlockHeader findings = Field<BlockHeader>(form, "listHeader");
                findings.OpenSearch();
                findings.Input.Value = "operator";
                Check(Field<GlassList>(form, "list").Count == 1, "Findings list filters while typing");
                findings.CloseSearch();
                Check(Field<GlassList>(form, "list").Count > 1, "Closing the list search shows everything again");
            }
        }

        private static void TestModes()
        {
            SafePasteSettings settings = new SafePasteSettings();
            using (ReviewForm form = NewReview(Fixture, settings, true))
            {
                Segmented modes = Field<Segmented>(form, "modeSwitch");
                Check(modes.SelectedIndex == (int)ControlMode.Balanced, "Review opens in the normal mode by default");
                Check(form.BuildResult().Text.Contains("SW-CORE-01"), "Normal mode keeps guesses");

                modes.SelectedIndex = (int)ControlMode.Strict;
                string strict = form.BuildResult().Text;
                Check(!strict.Contains("SW-CORE-01") && !strict.Contains("SRV-DB01"), "Strict mode hides everything found");
                Check(settings.Mode == ControlMode.Strict && SafePasteSettings.Load().Mode == ControlMode.Strict,
                    "Chosen mode is saved and used by quick paste too");

                Call(form, "ApplyMode", ControlMode.Light);
                string light = form.BuildResult().Text;
                Check(light.Contains("10.24.8.16") && light.Contains("SW-CORE-01") && !light.Contains("DemoSecret123!"),
                    "Light mode hides only secrets and your rules");
                Check(modes.SelectedIndex == (int)ControlMode.Light, "Mode switch follows the current mode");
                Render(form, "review-light");

                PressKey(form, Keys.Control | Keys.D1);
                Check(modes.SelectedIndex == (int)ControlMode.Strict, "Ctrl+1 turns on the strict mode");
                PressKey(form, Keys.Control | Keys.D2);
                Check(modes.SelectedIndex == (int)ControlMode.Balanced && form.BuildResult().Text.Contains("SW-CORE-01"),
                    "Ctrl+2 returns to the normal mode");
                Check(modes.Tips != null && modes.Tips.Length == 3, "Every mode has a caption on hover");
                foreach (string tip in modes.Tips)
                {
                    CheckText(tip, "mode tip");
                }
            }
            SafePasteSettings strictSettings = new SafePasteSettings();
            strictSettings.Mode = ControlMode.Strict;
            using (ReviewForm form = NewReview(Fixture, strictSettings, true))
            {
                Check(Field<Segmented>(form, "modeSwitch").SelectedIndex == (int)ControlMode.Strict
                    && !form.BuildResult().Text.Contains("SW-CORE-01"), "Review opens in the mode chosen before");
            }
            File.Delete(Paths.SettingsFile);
        }

        private static void TestPanels()
        {
            SafePasteSettings settings = new SafePasteSettings();
            using (ReviewForm form = NewReview(Fixture, settings, true))
            {
                form.Location = new Point(0, 0);
                int width = form.Width;
                int height = form.Height;
                GlassCard source = Field<GlassCard>(form, "sourceCard");
                int sourceWidth = source.Width;
                Spine spine = Field<Spine>(form, "resultSpine");
                Check(spine.Height == source.Height && spine.Left > source.Right, "Result button runs the full height to the right");
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(spine, new object[] { EventArgs.Empty });
                GlassCard result = Field<GlassCard>(form, "resultCard");
                Check(Field<bool>(form, "showResult") && form.Width > width, "Right spine opens the result and widens the window");
                Check(result.Left > source.Right && result.Width > 0, "Result sits to the right of the source");
                Size opened = form.Size;
                form.Size = form.MinimumSize;
                BlockHeader sourceHeader = Field<BlockHeader>(form, "sourceHeader");
                Legend legend = Field<Legend>(form, "legend");
                Check(legend.Width == 0 || legend.Left >= sourceHeader.SearchButton.Right,
                    "Summary never covers the title and the search button");
                Render(form, "review-narrow");
                form.Size = opened;
                Call(form, "ToggleList");
                GlassCard findings = Field<GlassCard>(form, "listCard");
                Check(form.Height > height && findings.Top > source.Bottom, "Bottom spine opens the list below and makes the window taller");
                Check(settings.ReviewShowResult && settings.ReviewShowFindings, "Open panels are remembered");
                Check(SafePasteSettings.Load().ReviewShowResult, "Layout is saved to settings.json");
                Render(form, "review-expanded");
                bool fitted = form.Width == width + sourceWidth + Dpi.S(10);
                Call(form, "ToggleResult");
                Call(form, "ToggleList");
                if (fitted)
                {
                    Check(form.Width == width && form.Height == height, "Closing both panels restores the window size");
                }
                Check(!settings.ReviewShowResult && !settings.ReviewShowFindings, "Closed panels are remembered too");
            }
        }

        private static void TestSyncScroll()
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < 200; i++)
            {
                text.Append("line " + i + " host SRV-" + i + " at 10.0." + (i % 250) + ".1\r\n");
            }
            SafePasteSettings settings = new SafePasteSettings();
            settings.ReviewShowResult = true;
            using (ReviewForm form = NewReview(text.ToString(), settings, true))
            {
                TextView source = Field<TextView>(form, "preview");
                TextView result = Field<TextView>(form, "resultPreview");
                source.SetScroll(source.LineHeight * 120, true);
                int sourceLine = LineNumber(source.Content, source.TopIndex);
                int resultLine = LineNumber(result.Content, result.TopIndex);
                Check(sourceLine > 100 && sourceLine == resultLine, "Result follows the source when scrolling: " + sourceLine + " vs " + resultLine);
                result.SetScroll(result.LineHeight * 40, true);
                Check(LineNumber(source.Content, source.TopIndex) == LineNumber(result.Content, result.TopIndex),
                    "Source follows the result when scrolling");
            }
        }

        private static void TestPasteIntoSource()
        {
            using (ReviewForm form = NewReview(Fixture, new SafePasteSettings(), true))
            {
                TextView preview = Field<TextView>(form, "preview");
                Check(preview.AllowDrop, "Text can be dropped onto the source block");
                Call(form, "LoadText", "Новый текст: сервер 10.1.2.3", "Текст взят из буфера.");
                Check(preview.Content == "Новый текст: сервер 10.1.2.3", "New text replaces the checked text");
                string result = form.BuildResult().Text;
                Check(result.Contains("[IP_1]") && !result.Contains("10.1.2.3"), "New text is checked right away: " + result);
            }
        }

        /// <summary>Текст правится прямо в окне, а результат перед вставкой всегда проверен заново.</summary>
        private static void TestEditing()
        {
            using (ReviewForm form = NewReview(Fixture, new SafePasteSettings(), true))
            {
                TextView input = Field<TextView>(form, "preview");
                Check(!input.ReadOnly && Field<TextView>(form, "resultPreview").ReadOnly, "Source text is editable, the result is not");

                input.SetCaret(input.Content.Length);
                TypeText(input, "Backup to 10.9.8.7");
                Check(Field<string>(form, "sourceText").EndsWith("Backup to 10.9.8.7"), "Typed text goes into the checked text");
                Check(Field<bool>(form, "scanPending"), "While typing, the check waits for a pause");
                string result = form.BuildResult().Text;
                Check(!result.Contains("10.9.8.7") && result.Contains("Backup to [IP_"), "Paste checks the edited text first: " + result);
                Check(!Field<bool>(form, "scanPending"), "After the check nothing is pending");

                PressKeyDown(input, Keys.Enter);
                TypeText(input, "x");
                Check(Field<string>(form, "sourceText").EndsWith("10.9.8.7\r\nx"), "Enter keeps Windows line breaks in the text");
                PressKeyDown(input, Keys.Back);
                PressKeyDown(input, Keys.Back);
                Check(Field<string>(form, "sourceText").EndsWith("10.9.8.7"), "Backspace removes a whole CRLF line break at once");
                Call(form, "StepHistory", false);
                Check(Field<string>(form, "sourceText").EndsWith("10.9.8.7\r\nx"), "Ctrl+Z brings the erased text back");
                Call(form, "StepHistory", true);
                Check(Field<string>(form, "sourceText").EndsWith("10.9.8.7"), "Ctrl+Y repeats the edit");

                // Правка внутри пароля: он остаётся закрытым точками, пока текст не проверен заново.
                int secret = input.Content.IndexOf('\u2022');
                Check(secret > 0, "Fixture shows the password as dots");
                input.SetCaret(secret + 3);
                TypeText(input, "Z");
                Check(input.Content[secret + 3] == '\u2022' && input.Content.IndexOf("Demo", StringComparison.Ordinal) < 0,
                    "Typing inside a password keeps it hidden");
                Check(!form.BuildResult().Text.Contains("DemZoSecret123!") && !form.BuildResult().Text.Contains("Secret123"),
                    "Edited password is still replaced");

                using (GlassMenu menu = (GlassMenu)Call(form, "BuildPreviewMenu", new Point(2, 2)))
                {
                    Check(!HasItem(menu, "Отменить правку") && !HasItem(menu, "Найти") && HasItem(menu, "Вставить") == ClipboardService.HasText(),
                        "Context menu has no undo and search, paste only with text in the clipboard");
                    CheckMenuTexts(menu, "editing menu");
                }
                input.SelectAll();
                input.ReplaceSelection("Новый текст: сервер 10.1.2.3");
                Check(!Field<bool>(form, "scanPending") && form.BuildResult().Text == "Новый текст: сервер [IP_1]",
                    "Replacing the whole text checks it at once: " + form.BuildResult().Text);
                Render(form, "review-edited");
            }
            using (ReviewForm form = NewReview(string.Empty, new SafePasteSettings(), false))
            {
                TypeText(Field<TextView>(form, "preview"), "ping 10.0.0.1");
                Check(form.BuildResult().Text == "ping [IP_1]", "Text can be typed into an empty window");
            }
        }

        /// <summary>Правка опечатки не сбрасывает решения: оставленное остаётся, отмеченное остаётся скрытым.</summary>
        private static void TestEditingKeepsDecisions()
        {
            using (ReviewForm form = NewReview(Fixture, new SafePasteSettings(), true))
            {
                TextView input = Field<TextView>(form, "preview");
                GlassList list = Field<GlassList>(form, "list");
                for (int i = 0; i < list.Count; i++)
                {
                    if (Field<string>(list.ItemAt(i), "Type") == "IP")
                    {
                        Call(form, "ToggleGroup", list.ItemAt(i));
                    }
                }
                Check(form.BuildResult().Text.Contains("10.24.8.16"), "Address is kept by the user");
                int retry = input.Content.IndexOf("Retry", StringComparison.Ordinal);
                input.Select(retry, 5);
                Call(form, "MarkSelection", "SECRET");
                Check(!form.BuildResult().Text.Contains("Retry"), "Selection is marked as a secret");

                input.SetCaret(0);
                TypeText(input, "Draft. ");
                string result = form.BuildResult().Text;
                Check(result.StartsWith("Draft. ") && result.Contains("10.24.8.16"), "Kept value stays kept after an edit: " + result);
                Check(!result.Contains("Retry"), "Marked secret stays hidden after an edit");
                Call(form, "ApplyMode", ControlMode.Strict);
                result = form.BuildResult().Text;
                Check(!result.Contains("10.24.8.16") && !result.Contains("Retry"), "Changing the mode sets the checkmarks again");
            }
        }

        /// <summary>В строгом режиме путь скрывается целиком, в обычном только узел и папка.</summary>
        private static void TestModeRescan()
        {
            const string text = "Файл \\\\fs01\\Share\\docs\\plan.xlsx готов";
            using (ReviewForm form = NewReview(text, new SafePasteSettings(), true))
            {
                string balanced = form.BuildResult().Text;
                Check(balanced == "Файл \\\\[HOST_1]\\[SHARE_1]\\docs\\plan.xlsx готов", "Normal mode hides the server and the share: " + balanced);
                Call(form, "ApplyMode", ControlMode.Strict);
                Check(form.BuildResult().Text == "Файл [PATH_1] готов", "Strict mode hides the whole path: " + form.BuildResult().Text);
                Detection path = FindDetection(Field<List<Detection>>(form, "detections"), "\\\\fs01\\Share\\docs\\plan.xlsx", true);
                string[] tip = (string[])Call(form, "DescribeSourceMark", path);
                Check(tip[0] == "[PATH_1]", "Hover over the path shows its replacement");
                Call(form, "ApplyMode", ControlMode.Balanced);
                Check(form.BuildResult().Text == balanced, "Normal mode brings the parts back");
            }
            using (ReviewForm form = NewReview("Лог в D:\\Logs\\app.log", new SafePasteSettings(), true))
            {
                Detection path = FindDetection(Field<List<Detection>>(form, "detections"), "D:\\Logs\\app.log", false);
                string[] tip = (string[])Call(form, "DescribeSourceMark", path);
                Check(tip[1].Contains("строгом режиме"), "Kept path explains that the strict mode hides it: " + tip[1]);
            }
        }

        /// <summary>Путь, отмеченный целиком, при следующей проверке находится целиком.</summary>
        private static void TestRememberedPath()
        {
            const string path = @"\\domain\folder\it\tratata";
            const string text = "Отчёт: " + path + "\\report.docx\r\n";
            SafePasteDatabase database = new SafePasteDatabase();
            database.Save();
            using (ReviewForm form = new ReviewForm(text, Detector.Scan(text, database, false), database,
                new SafePasteSettings(), Target, true))
            {
                CreateHandles(form);
                Check(form.BuildResult().Text.Contains("tratata"), "Without a rule only the server and share are hidden");
                TextView input = Field<TextView>(form, "preview");
                int start = input.Content.IndexOf(path, StringComparison.Ordinal);
                input.Select(start, path.Length);
                Call(form, "MarkSelection", "HOST");
                Check(!form.BuildResult().Text.Contains("tratata"), "Marked path is hidden at once");
            }
            SafePasteDatabase reloaded = SafePasteDatabase.Load();
            Check(reloaded.Learned.Exists(delegate(LearnedValue value) { return value.Value == path; }), "Marked path is remembered");
            using (ReviewForm again = new ReviewForm(text, Detector.Scan(text, reloaded, false), reloaded,
                new SafePasteSettings(), Target, true))
            {
                string result = again.BuildResult().Text;
                Check(!result.Contains("tratata") && !result.Contains("domain") && result.Contains("[HOST_1]\\report.docx"),
                    "Remembered path is found whole next time: " + result);
            }
            SafePasteDatabase.Reset();
        }

        private static void TestEmptyReview()
        {
            using (ReviewForm form = NewReview("Обычный текст без находок.", new SafePasteSettings(), false))
            {
                Render(form, "review-empty");
                Check(Field<GlassList>(form, "list").Count == 0, "Text without findings has an empty list");
                Check(!Field<GlassButton>(form, "pasteButton").Enabled, "Missing target disables paste");
                Check(Field<GlassButton>(form, "copyButton").Kind == ButtonKind.Primary, "Without a target, copy becomes the main action");
                Check(form.BuildResult().Text == "Обычный текст без находок.", "Nothing found means nothing changes");
            }
        }

        // ---------------------------------------------------------------- диалоги, база, трей

        private static void TestDialogs()
        {
            using (GlassDialog dialog = new GlassDialog(AlertKind.Warning, "Windows хранит копии буфера обмена",
                "Включена история буфера обмена (Win+V). SafePaste эти настройки не меняет.",
                "Понятно", null, "Больше не предупреждать", false))
            {
                CreateHandles(dialog);
                Check(dialog.FormBorderStyle == FormBorderStyle.None, "Warnings use the app's own window instead of MessageBox");
                GlassCheck check = null;
                foreach (Control control in dialog.Controls)
                {
                    if (control is GlassCheck) check = (GlassCheck)control;
                }
                Check(check != null && !dialog.Checked, "Warning offers to stop showing it");
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(check, new object[] { EventArgs.Empty });
                Check(dialog.Checked, "Checkbox reports its state");
                CheckGlassOnly(dialog);
                CheckTexts(dialog, "dialog");
                Render(dialog, "dialog-warning");
            }
            using (GlassDialog dialog = new GlassDialog(AlertKind.Warning, "В буфере нет текста",
                "Скопируйте текст (Ctrl+C) и нажмите сочетание ещё раз.", "Понятно", null, null, false))
            {
                CreateHandles(dialog);
                Render(dialog, "dialog-empty-clipboard");
            }
            using (GlassDialog confirm = new GlassDialog(AlertKind.Warning, "Сбросить все правила?",
                "Удалятся закреплённые номера, запомненные значения и исключения.", "Сбросить", "Отмена", null, true))
            {
                Check(((GlassButton)confirm.AcceptButton).Caption == "Отмена", "Enter does not confirm a destructive action");
            }
        }

        private static void TestDatabase()
        {
            SafePasteDatabase database = new SafePasteDatabase();
            database.AddLearned("SRV-DB01", "HOST");
            database.AddLearned("10.24.8.16", "IP");
            database.Reserve("IP", "10.24.8.16", 7);
            database.AddAllowed("example.com");
            database.Save();
            using (DatabaseForm form = new DatabaseForm())
            {
                CreateHandles(form);
                Render(form, "rules");
                GlassList list = Field<GlassList>(form, "list");
                BlockHeader search = Field<BlockHeader>(form, "searchHeader");
                Check(list.Count == 4, "All stored rule kinds are shown");
                Check(!Field<GlassButton>(form, "deleteButton").Enabled, "Delete is disabled without selection");
                search.OpenSearch();
                search.Input.Value = "srv-db";
                Check(list.Count == 1, "Rule search ignores case");
                search.CloseSearch();
                Field<Segmented>(form, "filter").SelectedIndex = 2;
                Check(list.Count == 1, "Exceptions filter");
                list.SelectIndex(0);
                Call(form, "DeleteSelected");
                Check(!SafePasteDatabase.Load().IsAllowed("example.com"), "Deleting a selected exception is saved");
                Check(SafePasteDatabase.Load().Learned.Count == 2, "Deleting an exception keeps other rules");
                Field<Segmented>(form, "filter").SelectedIndex = 0;
                search.OpenSearch();
                search.Input.Value = "No match";
                CheckGlassOnly(form);
                CheckTexts(form, "rules");
                Render(form, "rules-empty-search");
            }
        }

        private static void TestBridgeForms()
        {
            LabelStore labels = new LabelStore(false);
            string label = labels.Hide("demo.internal.example", "FQDN");
            using (BridgeLabelsForm form = new BridgeLabelsForm(labels))
            {
                CreateHandles(form);
                CheckTexts(form, "bridge-labels");
                Render(form, "bridge-labels");
            }
            using (BridgeTextForm form = new BridgeTextForm("Текст с реальными значениями", "Сервер: demo.internal.example"))
            {
                CreateHandles(form);
                CheckTexts(form, "bridge-text");
                Render(form, "bridge-text");
            }
            using (ApprovalForm form = new ApprovalForm("Полный PowerShell", "Write-Output '" + label + "'"))
            {
                CreateHandles(form);
                CheckTexts(form, "bridge-approval");
                Render(form, "bridge-approval");
            }
            using (BridgeTextForm form = new BridgeTextForm("Вызов sp_read", "1| server=" + label,
                delegate(string text) { return text.Replace(label, "demo.internal.example"); }))
            {
                CreateHandles(form);
                TextView view = Field<TextView>(form, "text");
                Call(form, "Toggle");
                Check(view.Content.Contains("demo.internal.example"), "Call details reveal labels on request");
                Check(Field<GlassButton>(form, "toggle").Caption == "Показать метки", "Reveal button switches back");
                Render(form, "bridge-call-revealed");
                Call(form, "Toggle");
                Check(view.Content.Contains(label) && !view.Content.Contains("demo.internal.example"), "Labels come back");
                CheckTexts(form, "bridge-call");
            }
        }

        private sealed class FakeBridge : IBridgeControl
        {
            internal readonly BridgeStatus Status = new BridgeStatus();
            internal int Stopped = -1;
            internal int Disconnected = -1;
            internal int Enabled = -1;
            private int version;

            public BridgeStatus Snapshot()
            {
                BridgeStatus copy = new BridgeStatus();
                copy.Running = Status.Running;
                copy.Problem = Status.Problem;
                copy.Settings = Status.Settings.Clone();
                copy.Sessions = new List<BridgeSessionInfo>(Status.Sessions);
                copy.Recent = new List<BridgeSessionInfo>(Status.Recent);
                copy.Calls = new List<BridgeCallInfo>(Status.Calls);
                copy.Signature = version.ToString();
                return copy;
            }

            public void SetEnabled(bool enabled)
            {
                Enabled = enabled ? 1 : 0;
                Status.Running = enabled;
                if (!enabled) Status.Sessions.Clear();
                version++;
            }

            public void UpdateSettings(Action<BridgeSettings> change)
            {
                BridgeSettings next = Status.Settings.Clone();
                change(next);
                next.Validate();
                Status.Settings.Roots = next.Roots;
                Status.Settings.AllowFull = next.AllowFull;
                Status.Settings.AllowEdits = next.AllowEdits;
                Status.Settings.AllowLocalModel = next.AllowLocalModel;
                Status.Settings.LocalModel = next.LocalModel;
                Status.Settings.LocalModelId = next.LocalModelId;
                Status.Settings.PageChars = next.PageChars;
                version++;
            }

            public void StopTask(int session) { Stopped = session; version++; }

            public void Disconnect(int session)
            {
                Disconnected = session;
                BridgeSessionInfo found = Status.Sessions.Find(delegate(BridgeSessionInfo item) { return item.Id == session; });
                if (found != null)
                {
                    Status.Sessions.Remove(found);
                    BridgeSessionInfo gone = found.Copy();
                    gone.Closed = true;
                    gone.State = "отключился в 10:30:00";
                    Status.Recent.Insert(0, gone);
                }
                version++;
            }

            public void ClearLog() { Status.Calls.Clear(); version++; }
        }

        private static BridgeCallInfo DemoCall(int id, string tool, string request, string outcome, string response)
        {
            BridgeCallInfo call = new BridgeCallInfo();
            call.Id = id;
            call.Session = 3;
            call.Client = "Codex 0.144.4";
            call.Started = new DateTime(2026, 9, 23, 10, 20, id);
            call.Duration = TimeSpan.FromMilliseconds(400 * id);
            call.Tool = tool;
            call.Request = request;
            call.Outcome = outcome;
            call.Response = response;
            return call;
        }

        private static void TestBridgeWindow()
        {
            FakeBridge fake = new FakeBridge();
            fake.Status.Running = true;
            fake.Status.Settings.Roots.Add("D:\\Ops\\notes");
            BridgeSessionInfo codex = new BridgeSessionInfo();
            codex.Id = 3;
            codex.Client = "Codex 0.144.4";
            codex.Folder = "D:\\Private";
            codex.Connected = new DateTime(2026, 9, 23, 10, 15, 0);
            codex.Calls = 12;
            codex.State = "команда 7 идёт";
            codex.Task = 7;
            BridgeSessionInfo claude = new BridgeSessionInfo();
            claude.Id = 4;
            claude.Client = "Claude Code 2.1";
            claude.Folder = "D:\\Private\\web";
            claude.Connected = new DateTime(2026, 9, 23, 10, 18, 0);
            claude.Calls = 3;
            claude.State = "ждёт вашего подтверждения";
            fake.Status.Sessions.Add(codex);
            fake.Status.Sessions.Add(claude);
            BridgeSessionInfo check = new BridgeSessionInfo();
            check.Id = 2;
            check.Client = "Codex 1";
            check.Folder = "D:\\Code\\ClipBoardChecker";
            check.Connected = new DateTime(2026, 9, 23, 10, 5, 0);
            check.Calls = 1;
            check.Closed = true;
            check.Ended = new DateTime(2026, 9, 23, 10, 5, 1);
            check.State = "отключился в 10:05:01";
            fake.Status.Recent.Add(check);
            fake.Status.Calls.Add(DemoCall(1, "sp_read", "D:\\Ops\\notes\\hosts.txt", "готово", "1| server=[FQDN_1]\n2| address=[IP_1]"));
            fake.Status.Calls.Add(DemoCall(2, "sp_run", "full: Restart-Service '[HOST_2]'", "отклонено", "Команда отклонена пользователем."));
            fake.Status.Calls.Add(DemoCall(3, "sp_run", "Test-NetConnection -ComputerName '[FQDN_1]' -Port 445", "идёт", ""));
            int labels = 0;
            using (BridgeForm form = new BridgeForm(fake, delegate { labels++; }, delegate(string text) { return text; }))
            {
                CreateHandles(form);
                Render(form, "bridge-agents");
                GlassList sessions = Field<GlassList>(form, "sessionsList");
                Check(sessions.Count == 3 && ((BridgeSessionInfo)sessions.ItemAt(2)).Closed,
                    "Connected agents come first, a short connection that already ended stays visible");
                Check(Field<WindowHeader>(form, "header").Subtitle == "Работает, агентов: 2", "Window header counts only connected agents");
                sessions.SelectIndex(2);
                Check(!Field<GlassButton>(form, "stopButton").Enabled && !Field<GlassButton>(form, "disconnectButton").Enabled,
                    "An agent that already left cannot be stopped or disconnected");
                Check(Field<Segmented>(form, "power").SelectedIndex == 0, "Power switch shows a running bridge");
                Check(!Field<GlassButton>(form, "stopButton").Enabled, "Nothing to stop without a selected agent");
                sessions.SelectIndex(0);
                Check(Field<GlassButton>(form, "stopButton").Enabled, "A running command can be stopped");
                Call(form, "StopSelected");
                Check(fake.Stopped == 3, "Stop reaches the bridge");
                Check(Field<GlassLabel>(form, "statusLabel").Text.Contains("остановлена"), "Stop is confirmed in the footer");
                sessions.SelectIndex(1);
                Check(!Field<GlassButton>(form, "stopButton").Enabled, "An idle agent has nothing to stop");
                Call(form, "DisconnectSelected");
                Check(fake.Disconnected == 4 && sessions.Count == 3 && ((BridgeSessionInfo)sessions.ItemAt(1)).Closed
                    && ((BridgeSessionInfo)sessions.ItemAt(1)).Id == 4, "A disconnected agent moves to the recent ones");
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Field<GlassButton>(form, "labelsButton"), new object[] { EventArgs.Empty });
                Check(labels == 1, "Labels window opens from the bridge window");

                Field<Segmented>(form, "tabs").SelectedIndex = 1;
                Render(form, "bridge-log");
                GlassList log = Field<GlassList>(form, "logList");
                Check(log.Count == 3 && ((BridgeCallInfo)log.ItemAt(0)).Id == 3, "Newest call comes first");
                BlockHeader logSearch = Field<BlockHeader>(form, "logHeader");
                logSearch.OpenSearch();
                logSearch.Input.Value = "restart";
                Check(log.Count == 1, "Log search looks into requests");
                logSearch.CloseSearch();
                log.SelectIndex(1);
                Check(Field<GlassLabel>(form, "statusLabel").Text == "Ответ: Команда отклонена пользователем.",
                    "Selecting a call shows its answer in the footer");
                log.SelectIndex(0);
                Check(Field<GlassLabel>(form, "statusLabel").Text.StartsWith("Вызовов в журнале", StringComparison.Ordinal),
                    "A call without an answer yet keeps the summary");
                string details = BridgeForm.DescribeCall(fake.Status.Calls[0]);
                Check(details.Contains("Запрос агента:") && details.Contains("[FQDN_1]") && details.Contains("0,4 с"), "Call details show request and answer");
                Call(form, "ClearLog");
                Check(log.Count == 0 && !Field<GlassButton>(form, "clearButton").Enabled, "Log can be cleared");

                Field<Segmented>(form, "tabs").SelectedIndex = 2;
                Render(form, "bridge-settings");
                GlassList roots = Field<GlassList>(form, "rootsList");
                Check(roots.Count == 1, "Folders are listed");
                Call(form, "AddRoot", "C:\\Temp\\bridge");
                Check(fake.Status.Settings.Roots.Count == 2 && roots.Count == 2, "A folder can be added");
                Call(form, "AddRoot", "relative\\folder");
                Check(fake.Status.Settings.Roots.Count == 2 && Field<GlassLabel>(form, "statusLabel").Text.Contains("не добавлена"),
                    "A relative folder is refused");
                roots.SelectIndex(1);
                Call(form, "RemoveRootsNow", new List<string> { "C:\\Temp\\bridge" });
                Check(fake.Status.Settings.Roots.Count == 1, "A folder can be removed");
                Field<Segmented>(form, "fullSwitch").SelectedIndex = 1;
                Check(!fake.Status.Settings.AllowFull, "Full PowerShell can be forbidden from the window");
                Field<Segmented>(form, "editsSwitch").SelectedIndex = 1;
                Check(!fake.Status.Settings.AllowEdits, "File edits can be forbidden from the window");
                Check(Field<GlassButton>(form, "modelButton").Caption == "Найти и подключить ИИ",
                    "Model setup is available from settings");
                using (LocalModelForm modelForm = new LocalModelForm(fake))
                {
                    CreateHandles(modelForm);
                    Check(!Field<GlassInput>(modelForm, "endpointInput").Visible,
                        "Manual address starts behind Advanced");
                    Render(modelForm, "bridge-model-search");
                    typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(Field<GlassButton>(modelForm, "advancedButton"), new object[] { EventArgs.Empty });
                    Check(Field<GlassButton>(modelForm, "advancedButton").Caption == "Скрыть дополнительно",
                        "Advanced reveals manual address and model ID");
                    Render(modelForm, "bridge-model-advanced");
                    Field<GlassInput>(modelForm, "endpointInput").Value = "http://10.0.0.5:8081/v1/chat/completions";
                    Call(modelForm, "SaveManual");
                    Check(fake.Status.Settings.LocalModel == BridgeSettings.DefaultLocalModel
                        && Field<GlassLabel>(modelForm, "status").Text.Contains("на этом компьютере"),
                        "A remote model address is refused");
                    Field<GlassInput>(modelForm, "endpointInput").Value = "http://127.0.0.1:9000/v1/chat/completions";
                    Field<GlassInput>(modelForm, "modelInput").Value = "my-llm";
                    Call(modelForm, "SaveManual");
                    Check(fake.Status.Settings.LocalModelId == "my-llm"
                        && fake.Status.Settings.LocalModel.Contains(":9000/"), "Advanced saves address and model ID");
                }
                using (LocalModelForm modelForm = new LocalModelForm(fake))
                {
                    CreateHandles(modelForm);
                    Call(modelForm, "ScanFinished", new List<LocalModelChoice>(), OllamaState.Stopped);
                    Check(Shown(Field<GlassButton>(modelForm, "ollamaButton"))
                        && !Shown(Field<GlassButton>(modelForm, "connectButton"))
                        && Field<GlassLabel>(modelForm, "status").Text.Contains("не запущена"),
                        "Installed but stopped Ollama can be started from the window");
                    Render(modelForm, "bridge-model-ollama-stopped");
                    Call(modelForm, "ScanFinished", new List<LocalModelChoice>(), OllamaState.Running);
                    Check(!Shown(Field<GlassButton>(modelForm, "ollamaButton"))
                        && Field<GlassLabel>(modelForm, "status").Text.Contains("ollama pull"),
                        "Running Ollama without models suggests downloading one");
                    Call(modelForm, "ScanFinished", new List<LocalModelChoice> {
                        new LocalModelChoice { Provider = "Ollama", Name = "demo:7b", ModelId = "demo:7b",
                            Endpoint = "http://127.0.0.1:11434/v1/chat/completions", Loaded = false },
                        new LocalModelChoice { Provider = "Ollama", Name = "qwen2.5:3b", ModelId = "qwen2.5:3b",
                            Endpoint = "http://127.0.0.1:11434/v1/chat/completions" }
                    }, OllamaState.Running);
                    GlassList choices = Field<GlassList>(modelForm, "models");
                    Check(choices.Count == 2 && Shown(Field<GlassButton>(modelForm, "connectButton")),
                        "Discovered models appear in the list");
                    Render(modelForm, "bridge-model-found");
                    choices.SelectIndex(0);
                    Check(fake.Status.Settings.LocalModelId == "demo:7b"
                        && fake.Status.Settings.LocalModel.Contains(":11434/")
                        && fake.Status.Settings.AllowLocalModel, "Selecting a model connects it in one click");
                }
                GlassInput page = Field<GlassInput>(form, "pageInput");
                page.Value = "50";
                Call(form, "ApplyPage");
                Check(fake.Status.Settings.PageChars == 12000, "A tiny page size is refused");
                page.Value = "20000";
                Call(form, "ApplyPage");
                Check(fake.Status.Settings.PageChars == 20000, "Page size is saved");
                fake.Status.Settings.Roots.Clear();
                Call(form, "RefreshNow");
                Check(Field<GlassLabel>(form, "settingsNote").Text.Contains("все файлы"), "An empty folder list warns about full access");
                Render(form, "bridge-settings-open");
                Call(form, "ApplyPower", false);
                Check(fake.Enabled == 0 && Field<WindowHeader>(form, "header").Subtitle == "Выключен", "Bridge can be switched off");
                Field<Segmented>(form, "tabs").SelectedIndex = 0;
                Render(form, "bridge-off");
                CheckGlassOnly(form);
                CheckTexts(form, "bridge-window");
            }
            BridgeStatus failed = new BridgeStatus();
            failed.Problem = "Канал моста уже занят.";
            Check(BridgeForm.Describe(failed) == "Не запустился. Канал моста уже занят.", "Start problem is shown");
        }

        /// <summary>
        /// Меню правого клика: «Отметить», «Пометить как» с подходящими типами, отметка без сохранения
        /// и только применимые действия.
        /// </summary>
        private static void TestMarkMenus()
        {
            List<string> ip = MarkTypes.For("10.24.8.16", null);
            Check(ip[0] == "IP" && !ip.Contains("PERSON") && !ip.Contains("PATH"), "An address is offered as IP first: " + string.Join(",", ip.ToArray()));
            List<string> person = MarkTypes.For("Иванов Иван Иванович", null);
            Check(person[0] == "PERSON" && !person.Contains("HOST") && !person.Contains("USER") && !person.Contains("IP"),
                "A full name is offered as PERSON first: " + string.Join(",", person.ToArray()));
            List<string> path = MarkTypes.For(@"D:\Logs\app.log", null);
            Check(path[0] == "PATH" && !path.Contains("HOST"), "A path is offered as PATH first: " + string.Join(",", path.ToArray()));
            List<string> secret = MarkTypes.For("line one\nline two", null);
            Check(secret.Count == 3 && secret.Contains("SECRET") && secret.Contains("PRIVATE_KEY"), "Several lines can only be a secret or a key");
            Dictionary<string, int> usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            usage["SERIAL"] = 3;
            List<string> used = MarkTypes.For("srv-db-01", usage);
            Check(used[0] == "SERIAL" && used[1] == "HOST", "Frequently chosen types go first: " + string.Join(",", used.ToArray()));
            foreach (string type in MarkTypes.All)
            {
                Check(TypeNames.Describe(type) != type || type == "GUID" || type == "SID", "Every type has a readable name: " + type);
            }

            SafePasteSettings settings = new SafePasteSettings();
            using (ReviewForm form = NewReview(Fixture, settings, true))
            {
                TextView input = Field<TextView>(form, "preview");
                SafePasteDatabase database = Field<SafePasteDatabase>(form, "database");
                int retry = input.Content.IndexOf("Retry", StringComparison.Ordinal);
                input.Select(retry, 5);
                Call(form, "MarkSelection", MarkTypes.Generic, false);
                string result = form.BuildResult().Text;
                Check(result.Contains("[HIDE_TEXT_1] from") && !result.Contains("Retry"), "Marking without saving hides the text: " + result);
                Check(!database.IsLearned("Retry", MarkTypes.Generic), "Marking without saving keeps the rules untouched");
                Detection once = FindDetection(Field<List<Detection>>(form, "detections"), "Retry", true);
                Check(once.Transient && ((string[])Call(form, "DescribeSourceMark", once))[1].Contains("без сохранения"),
                    "The hint says the mark is only for this time");

                int connection = input.Content.IndexOf("Connection", StringComparison.Ordinal);
                input.Select(connection, 10);
                Call(form, "MarkSelection", MarkTypes.Generic, true);
                result = form.BuildResult().Text;
                Check(database.IsLearned("Connection", MarkTypes.Generic) && result.Contains("[HIDE_TEXT_1] to")
                    && result.Contains("[HIDE_TEXT_2] from"), "Mark hides the selection as HIDE_TEXT and remembers the rule: " + result);
                Detection marked = FindDetection(Field<List<Detection>>(form, "detections"), "Connection", true);
                using (GlassMenu menu = new GlassMenu())
                {
                    Call(form, "AddGroupActions", menu, Call(form, "GroupOf", marked));
                    Check(!HasItem(menu, "Скрывать всегда") && HasItem(menu, "Никогда не скрывать"),
                        "A value that is already in the rules is not offered to be remembered again");
                }

                int seconds = input.Content.IndexOf("seconds", StringComparison.Ordinal);
                input.Select(seconds, 7);
                Call(form, "MarkSelectionAs", "TOKEN");
                Check(!database.IsLearned("seconds", "TOKEN") && form.BuildResult().Text.Contains("[TOKEN_1]"),
                    "Tokens are hidden but never written into the rules");
                Check(settings.MarkTypeUsage["TOKEN"] == 1 && SafePasteSettings.Load().MarkTypeUsage["TOKEN"] == 1,
                    "The chosen type is counted for the menu order");
                Detection token = FindDetection(Field<List<Detection>>(form, "detections"), "seconds", true);
                using (GlassMenu menu = new GlassMenu())
                {
                    Call(form, "AddGroupActions", menu, Call(form, "GroupOf", token));
                    Check(!HasPrefix(menu, "Закрепить номер") && !HasItem(menu, "Скрывать всегда"),
                        "A token marked by hand cannot be pinned or remembered");
                }

                int secretAt = input.Content.IndexOf('\u2022');
                input.Select(secretAt + 2, 4);
                Rectangle secretCell = input.CharBounds(secretAt + 3);
                using (GlassMenu menu = (GlassMenu)Call(form, "BuildPreviewMenu", new Point(secretCell.X + 2, secretCell.Y + 2)))
                {
                    Check(!HasItem(menu, "Отметить") && !HasItem(menu, "Пометить как") && HasItem(menu, "Распознано ошибочно"),
                        "A selection inside a password cannot be marked, but the password itself can be released");
                }

                int address = input.Content.IndexOf("10.24.8.16", StringComparison.Ordinal);
                Detection ipDetection = FindDetection(Field<List<Detection>>(form, "detections"), "10.24.8.16", true);
                Call(form, "ToggleGroup", Call(form, "GroupOf", ipDetection));
                using (GlassMenu menu = new GlassMenu())
                {
                    Call(form, "AddGroupActions", menu, Call(form, "GroupOf", FindDetection(Field<List<Detection>>(form, "detections"), "10.24.8.16", false)));
                    Check(HasItem(menu, "Скрыть") && !HasPrefix(menu, "Закрепить номер"), "A kept value cannot get a pinned number");
                }
                input.Select(address, 0);
                Call(form, "ToggleResult");
                TextView output = Field<TextView>(form, "resultPreview");
                output.Select(0, 0);
                using (GlassMenu menu = (GlassMenu)Call(form, "BuildResultMenu", new Point(2, 2)))
                {
                    Check(!menu.Tidy(), "The result menu is not shown when nothing applies");
                }

                input.Select(input.Content.IndexOf("from", StringComparison.Ordinal), 4);
                Rectangle fromCell = input.CharBounds(input.SelectionStart + 1);
                using (GlassMenu menu = (GlassMenu)Call(form, "BuildPreviewMenu", new Point(fromCell.X + 2, fromCell.Y + 2)))
                {
                    ToolStripDropDown types = ((ToolStripMenuItem)FindItem(menu, "Пометить как")).DropDown;
                    Check(ItemTexts(types)[0] == "Токен", "The type chosen before comes first next time");
                    RenderMenu(menu, "menu-selection");
                    RenderMenu(types, "menu-mark-as");
                }
            }
            using (ReviewForm form = NewReview(string.Empty, new SafePasteSettings(), true))
            {
                form.ShowNotice("Не удалось прочитать буфер обмена.");
                Check(Field<GlassLabel>(form, "statusLabel").Text == "Не удалось прочитать буфер обмена.",
                    "An empty window explains why there is no text");
            }
            File.Delete(Paths.SettingsFile);
            File.Delete(Paths.DatabaseFile);
        }

        /// <summary>Метки вставки запоминаются, в следующий раз находятся без подсказок и видны в правилах.</summary>
        private static void TestRememberedLabels()
        {
            const string first = "hostname: web-prod\r\npassword: DemoSecret123!\r\nRetry later";
            using (ReviewForm form = NewReview(first, new SafePasteSettings(), true))
            {
                TextView input = Field<TextView>(form, "preview");
                input.Select(input.Content.IndexOf("Retry", StringComparison.Ordinal), 5);
                Call(form, "MarkSelection", MarkTypes.Generic, false);
                Check(!File.Exists(Paths.LabelsFile), "Checking the result writes nothing");
                string pasted = form.BuildPasteResult().Text;
                Check(File.Exists(Paths.LabelsFile) && pasted.StartsWith("hostname: [HOST_1]"), "Paste remembers the labels: " + pasted);
            }
            List<LabelEntry> stored = LabelMemory.Snapshot();
            Check(stored.Count == 1 && stored[0].Value == "web-prod", "Only the hidden value is remembered, not the secret or the one-time mark");

            SafePasteSettings settings = new SafePasteSettings();
            SafePasteDatabase database = new SafePasteDatabase();
            LabelMemory.Load(database, settings, settings.Mode);
            const string second = "Сервис web-prod снова упал";
            using (ReviewForm form = new ReviewForm(second, Detector.Scan(second, database, settings.Mode, false), database,
                settings, Target, true))
            {
                CreateHandles(form);
                Check(form.BuildResult().Text == "Сервис [HOST_1] снова упал", "A remembered value is hidden without hints, with the same number");
                Detection remembered = FindDetection(Field<List<Detection>>(form, "detections"), "web-prod", true);
                Check(((string[])Call(form, "DescribeSourceMark", remembered))[1].Contains("Метка запомнена раньше"),
                    "The hint tells that the label was remembered before");
                Call(form, "ApplyMode", ControlMode.Light);
                Check(form.BuildResult().Text == second, "In the light mode remembered values are not searched");
                Call(form, "ApplyMode", ControlMode.Balanced);
                Check(form.BuildResult().Text == "Сервис [HOST_1] снова упал", "Back in the normal mode the value is hidden again");
                Call(form, "Allow", Call(form, "GroupOf", FindDetection(Field<List<Detection>>(form, "detections"), "web-prod", true)));
                Check(LabelMemory.Snapshot().Count == 0 && form.BuildResult().Text == second,
                    "Never hide forgets the remembered label as well");
            }
            File.Delete(Paths.DatabaseFile);

            using (ReviewForm form = NewReview(first, new SafePasteSettings(), true))
            {
                SafePasteSettings.Update(delegate(SafePasteSettings latest) { latest.SaveLabels = false; });
                form.BuildPasteResult();
                Check(LabelMemory.Snapshot().Count == 0, "With saving off a paste remembers nothing");
                SafePasteSettings.Update(delegate(SafePasteSettings latest) { latest.SaveLabels = true; });
                form.BuildPasteResult();
            }
            using (DatabaseForm rules = new DatabaseForm())
            {
                CreateHandles(rules);
                GlassList list = Field<GlassList>(rules, "list");
                Field<Segmented>(rules, "filter").SelectedIndex = 4;
                Check(list.Count == 1 && Field<GlassButton>(rules, "forgetButton").Enabled, "Remembered labels have their own filter");
                Render(rules, "rules-remembered");
                list.SelectIndex(0);
                Call(rules, "DeleteSelected");
                Check(LabelMemory.Snapshot().Count == 0 && list.Count == 0 && !Field<GlassButton>(rules, "forgetButton").Enabled,
                    "A remembered label can be deleted from the rules window");
                CheckGlassOnly(rules);
                CheckTexts(rules, "rules-remembered");
            }
            File.Delete(Paths.LabelsFile);
            File.Delete(Paths.SettingsFile);
        }

        /// <summary>Новые копии открываются вкладками, у каждой свои решения; вставка вкладки не закрывает окно.</summary>
        private static void TestTabs()
        {
            using (ReviewForm form = NewReview(Fixture, new SafePasteSettings(), true))
            {
                TabStrip tabs = Field<TabStrip>(form, "tabStrip");
                Check(form.TabCount == 1 && !Shown(tabs), "One text needs no tabs");
                int singleTabContentTop = Field<GlassCard>(form, "sourceCard").Top;
                Check(form.AddCopiedText("Второй текст: сервер 10.1.2.3"), "A new copy is accepted");
                Check(form.TabCount == 2 && Shown(tabs) && tabs.SelectedIndex == 1, "A new copy opens in its own tab");
                WindowHeader header = Field<WindowHeader>(form, "header");
                GlassCard sourceCard = Field<GlassCard>(form, "sourceCard");
                Segmented modeSwitch = Field<Segmented>(form, "modeSwitch");
                Check(Shown(header) && header.Title == "Проверка перед вставкой" && header.MouseTransparent,
                    "The window title stays visible and draggable with multiple tabs");
                Check(tabs.Top >= header.Top && tabs.Bottom <= header.Bottom && tabs.Left >= header.Right
                    && tabs.Right <= modeSwitch.Left && sourceCard.Top == singleTabContentTop,
                    "Tabs share the top header row without covering the title or changing the content position");
                Check(form.BuildResult().Text == "Второй текст: сервер [IP_1]", "The new tab is checked right away");
                Check(tabs[1].Caption.StartsWith("2 Второй текст") && tabs[0].Caption.StartsWith("1 # Incident"),
                    "Tabs are numbered and show the first line: " + tabs[1].Caption);
                Check(Shown(Field<GlassButton>(form, "pasteAllButton")), "Paste all appears with two texts to paste");
                CheckGlassOnly(form);
                CheckTexts(form, "review-tabs");
                Render(form, "review-tabs");
                Size normalSize = form.Size;
                form.Size = form.MinimumSize;
                Check(tabs.Top >= header.Top && tabs.Bottom <= header.Bottom && tabs.Left >= header.Right
                    && tabs.Right <= Field<GlassButton>(form, "settingsButton").Left
                    && modeSwitch.Top >= header.Bottom && sourceCard.Top > modeSwitch.Bottom,
                    "A narrow window keeps tabs and the title in the header without overlapping controls");
                Render(form, "review-tabs-minimum");
                form.Size = normalSize;

                Call(form, "ToggleGroup", Call(form, "GroupOf", FindDetection(Field<List<Detection>>(form, "detections"), "10.1.2.3", true)));
                Check(form.BuildResult().Text.Contains("10.1.2.3"), "The second tab keeps its own decision");
                Call(form, "SelectDocument", 0);
                Check(Field<string>(form, "sourceText") == Fixture && !form.BuildResult().Text.Contains("10.24.8.16"),
                    "The first tab is untouched by decisions in the second");
                Call(form, "SelectDocument", 1);
                Check(form.BuildResult().Text.Contains("10.1.2.3"), "Switching back keeps the decision");

                Check(form.AddCopiedText(Fixture) && form.TabCount == 2 && tabs.SelectedIndex == 0,
                    "Copying an open text again only switches to its tab");
                Call(form, "ApplyMode", ControlMode.Strict);
                form.AddCopiedText("Файл \\\\fs01\\Share\\docs\\plan.xlsx готов");
                Check(form.BuildResult().Text == "Файл [PATH_1] готов", "A tab opened after a mode change follows the mode");
                Call(form, "SelectDocument", 1);
                Check(form.BuildResult().Text == "Второй текст: сервер [IP_1]", "A tab in the background is checked again after a mode change");
                Call(form, "ApplyMode", ControlMode.Balanced);

                ReplacementResult pasted = null;
                form.PasteRequested = delegate(ReplacementResult result) { pasted = result; };
                Call(form, "PasteCurrent");
                Check(pasted != null && pasted.Text == "Второй текст: сервер [IP_1]" && form.TabCount == 2 && !form.IsDisposed
                    && form.Action == ReviewAction.Cancel, "Pasting a tab keeps the window with the other tabs");
                Call(form, "CloseDocument", 1);
                Check(form.TabCount == 1 && !Shown(tabs), "Closing a tab hides the strip when one text is left");
                Check(Shown(header) && sourceCard.Top == singleTabContentTop,
                    "The title remains visible and the content moves back after closing tabs");
            }
            // Вставка запомнила метки: дальше номера должны начинаться с чистого листа.
            File.Delete(Paths.LabelsFile);

            using (ReviewForm form = NewReview(string.Empty, new SafePasteSettings(), true))
            {
                Check(form.AddCopiedText("password: Hunter2Secret!\r\nping 10.0.0.1") && form.TabCount == 1
                    && form.BuildResult().Text.EndsWith("ping [IP_1]"), "An empty window takes the first copy instead of opening a tab");
                TabStrip tabs = Field<TabStrip>(form, "tabStrip");
                Check(!tabs[0].Caption.Contains("Hunter2Secret") && !tabs[0].Tip.Contains("Hunter2Secret") && tabs[0].Caption.Contains("•"),
                    "Tab captions show secrets as dots: " + tabs[0].Caption);
                for (int i = 0; i < ReviewForm.MaxTabs + 2; i++)
                {
                    form.AddCopiedText("Текст " + i.ToString(CultureInfo.InvariantCulture));
                }
                Check(form.TabCount == ReviewForm.MaxTabs, "The number of tabs is limited");
                Render(form, "review-many-tabs");
            }

            using (ReviewForm form = NewReview("Узел srv-a.corp.local, адрес 10.9.9.1", new SafePasteSettings(), true))
            {
                form.AddCopiedText("Ответ от 10.9.9.1 и 10.9.9.2");
                ReplacementResult all = form.BuildAllResult();
                Check(all.Text == "Узел [FQDN_1], адрес [IP_1]\r\n\r\nОтвет от [IP_1] и [IP_2]",
                    "Paste all numbers values across tabs as one text: " + all.Text);
            }
            File.Delete(Paths.LabelsFile);
            File.Delete(Paths.SettingsFile);
        }

        /// <summary>Картинка во вкладке: находка, закрытие и передача готового растра для вставки.</summary>
        private static void TestImages()
        {
            ImageReading reading = new ImageReading();
            reading.Width = 480;
            reading.Height = 90;
            OcrLine line = new OcrLine();
            line.Words.Add(new OcrWord("password:", new RectangleF(12, 25, 105, 25)));
            line.Words.Add(new OcrWord("DemoSecret123!", new RectangleF(125, 25, 190, 25)));
            ImageReader.Build(reading, new List<OcrLine> { line });
            using (Bitmap picture = new Bitmap(480, 90))
            using (Graphics graphics = Graphics.FromImage(picture))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.Black, 140, 30, 24, 12);
                using (Bitmap redacted = ImageRedactor.Render(picture,
                    new List<RedactBox> { new RedactBox(new RectangleF(125, 25, 190, 25), null) },
                    new List<RectangleF>(), StubStyle.Text))
                {
                    Check(picture.GetPixel(145, 35).ToArgb() != redacted.GetPixel(145, 35).ToArgb()
                        && redacted.GetPixel(145, 35).ToArgb() == Color.White.ToArgb(),
                        "Image redaction replaces original pixels with an opaque fill");
                }
                using (ReviewForm form = NewReview(string.Empty, new SafePasteSettings(), true))
                {
                    Check(form.AddImageReading(new Bitmap(picture), reading) && form.IsImage && !form.ImagePending,
                        "A copied image opens in the review window");
                    List<Detection> found = Field<List<Detection>>(form, "detections");
                    Check(FindDetection(found, "DemoSecret123!", true) != null,
                        "The recognized secret is marked for hiding");
                    Render(form, "review-image");
                    Call(form, "ToggleResult");
                    Render(form, "review-image-result");
                    Check(form.AddCopiedText("Следующая вкладка: 10.1.2.3") && form.TabCount == 2,
                        "Text copied after an image opens in a separate tab");
                    Call(form, "SelectDocument", 0);
                    Bitmap pasted = null;
                    int hidden = 0;
                    form.PasteImageRequested = delegate(Bitmap result, int count) { pasted = result; hidden = count; };
                    try
                    {
                        Call(form, "PasteCurrent");
                        Check(pasted != null && hidden > 0 && form.TabCount == 1 && !form.IsDisposed,
                            "Pasting an image tab returns a bitmap and keeps the other tab open");
                    }
                    finally
                    {
                        if (pasted != null) pasted.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Пароль: значение видно при наведении, его можно оставить в этот раз или отметить «Распознано ошибочно».
        /// Отметка пишется в правила и видна в «Правилах и исключениях».
        /// </summary>
        private static void TestSecretFeedback()
        {
            SafePasteSettings settings = new SafePasteSettings();
            using (ReviewForm form = NewReview(Fixture, settings, false))
            {
                Detection secret = FindDetection(Field<List<Detection>>(form, "detections"), "DemoSecret123!", true);
                Check(secret.Locked, "Fixture password is locked");
                string[] tip = (string[])Call(form, "DescribeSourceMark", secret);
                Check(tip[0] == "DemoSecret123!" && tip[1].Contains("[SECRET_1]") && tip[2].Contains("ошибку"),
                    "Hovering a password shows its value: " + tip[0]);
                CheckText(tip[1], "password tip");
                CheckText(tip[2], "password tip hint");
                Check(Field<TextView>(form, "preview").Content.IndexOf("DemoSecret123!", StringComparison.Ordinal) < 0,
                    "The password itself stays dotted in the text");
                object group = Call(form, "GroupOf", secret);
                Check(((string)Call(form, "ListTip", group)).Contains("DemoSecret123!"), "The list tip shows the password too");
                using (GlassMenu menu = new GlassMenu())
                {
                    Call(form, "AddGroupActions", menu, group);
                    Check(HasItem(menu, "Не скрывать в этот раз") && HasItem(menu, "Распознано ошибочно") && HasItem(menu, "DemoSecret123!"),
                        "A password menu offers keeping it and marking a mistake");
                    CheckMenuTexts(menu, "password menu");
                    RenderMenu(menu, "menu-password");
                }

                Call(form, "ReleaseSecret", group, false);
                Check(form.BuildResult().Text.Contains("password: DemoSecret123!"), "Kept this time, the password stays in the result");
                Check(Field<TextView>(form, "preview").Content.Contains("DemoSecret123!"), "A kept password is no longer dotted");
                Call(form, "Rescan", (string)null);
                Check(form.BuildResult().Text.Contains("DemoSecret123!"), "The decision survives a new check");
                Check(!SafePasteDatabase.Load().IsNotSecret("DemoSecret123!"), "Kept this time is not written into the rules");

                Detection kept = FindDetection(Field<List<Detection>>(form, "detections"), "DemoSecret123!", false);
                Call(form, "ToggleGroup", Call(form, "GroupOf", kept));
                Detection again = FindDetection(Field<List<Detection>>(form, "detections"), "DemoSecret123!", true);
                Check(again.Locked && !form.BuildResult().Text.Contains("DemoSecret123!"), "Hiding it again locks the password again");

                Call(form, "ReleaseSecret", Call(form, "GroupOf", again), true);
                Check(SafePasteDatabase.Load().IsNotSecret("DemoSecret123!") && form.BuildResult().Text.Contains("DemoSecret123!"),
                    "A mistake is remembered in the rules and the value stays");
                Render(form, "review-password-released");
            }
            Check(!Detector.Scan(Fixture, SafePasteDatabase.Load(), ControlMode.Strict, false).Exists(delegate(Detection item)
            {
                return item.Locked && item.Value == "DemoSecret123!";
            }), "The next check does not take the marked value for a password");
            using (DatabaseForm rules = new DatabaseForm())
            {
                bool listed = false;
                foreach (object entry in Field<System.Collections.IList>(rules, "entries"))
                {
                    listed |= Field<string>(entry, "Rule") == "Не пароль" && Field<string>(entry, "Value") == "DemoSecret123!";
                }
                Check(listed, "The rules window lists the value marked as a mistake");
                CheckTexts(rules, "rules with a mistake");
            }
            File.Delete(Paths.DatabaseFile);
        }

        /// <summary>Одинаковые кнопки стоят на одних местах и двигаются одинаково во всех окнах и вкладках.</summary>
        private static void TestSameButtons()
        {
            using (DatabaseForm rules = new DatabaseForm())
            using (AboutForm about = new AboutForm())
            {
                Check(Field<GlassButton>(rules, "closeButton").HoverTurn == 90f && Field<GlassButton>(about, "closeButton").HoverTurn == 90f,
                    "Close buttons turn on hover in every window");
            }
            ImageReading reading = new ImageReading();
            reading.Width = 480;
            reading.Height = 90;
            OcrLine line = new OcrLine();
            line.Words.Add(new OcrWord("host", new RectangleF(12, 25, 50, 25)));
            line.Words.Add(new OcrWord("10.1.2.3", new RectangleF(70, 25, 120, 25)));
            ImageReader.Build(reading, new List<OcrLine> { line });
            using (Bitmap picture = new Bitmap(480, 90))
            using (ReviewForm form = NewReview(string.Empty, new SafePasteSettings(), true))
            {
                Check(form.AddImageReading(new Bitmap(picture), reading) && form.AddCopiedText("ping 10.9.9.9") && form.TabCount == 2,
                    "An image tab and a text tab are open");
                Call(form, "SelectDocument", 0);
                form.PerformLayout();
                GlassButton copy = Field<GlassButton>(form, "copyButton");
                GlassButton paste = Field<GlassButton>(form, "pasteButton");
                Rectangle copyOnImage = copy.Bounds;
                Rectangle pasteOnImage = paste.Bounds;
                GlassButton imageSync = Field<GlassButton>(form, "imagePasteInButton");
                GlassButton textSync = Field<GlassButton>(form, "pasteInButton");
                Rectangle toggleOnImage = Field<GlassButton>(form, "imageTextButton").Bounds;
                Rectangle syncOnImage = imageSync.Bounds;
                Check(imageSync.HoverTurn == textSync.HoverTurn && imageSync.HoverTurn != 0f, "Both clipboard buttons turn on hover");
                Call(form, "ShowImageText", true);
                form.PerformLayout();
                Check(Field<GlassButton>(form, "sourceImageButton").Bounds == toggleOnImage && textSync.Bounds == syncOnImage,
                    "Switching between the picture and its text keeps the header buttons in place");
                Call(form, "ShowImageText", false);
                Call(form, "SelectDocument", 1);
                form.PerformLayout();
                Check(copy.Bounds == copyOnImage && paste.Bounds == pasteOnImage,
                    "Copy and paste keep their places on text and image tabs");
                Check(textSync.Bounds == syncOnImage, "The clipboard button keeps its place on a text tab");
            }
        }

        /// <summary>Контекст мониторинга и IP, который OCR разрезал на несколько слов.</summary>
        private static void TestImageMonitoring()
        {
            ImageReading reading = new ImageReading();
            reading.Width = 440;
            reading.Height = 180;
            OcrLine alert = new OcrLine();
            alert.Words.AddRange(new OcrWord[]
            {
                new OcrWord("Linux:", new RectangleF(10, 10, 50, 16)),
                new OcrWord("nodefalcon", new RectangleF(66, 10, 85, 16)),
                new OcrWord("—", new RectangleF(158, 10, 12, 16)),
                new OcrWord("мониторинг", new RectangleF(177, 10, 95, 16))
            });
            OcrLine repeat = new OcrLine();
            repeat.Words.Add(new OcrWord("nodefalcon", new RectangleF(10, 40, 85, 16)));
            OcrLine title = new OcrLine();
            title.Words.AddRange(new OcrWord[]
            {
                new OcrWord("AzimuthDock", new RectangleF(10, 70, 100, 16)),
                new OcrWord("—", new RectangleF(117, 70, 12, 16)),
                new OcrWord("прод", new RectangleF(136, 70, 40, 16))
            });
            OcrLine metrics = new OcrLine();
            metrics.Words.AddRange(new OcrWord[]
            {
                new OcrWord("azimuthdock-prod:", new RectangleF(10, 100, 151, 16)),
                new OcrWord("метрики", new RectangleF(169, 100, 70, 16)),
                new OcrWord("хоста", new RectangleF(247, 100, 48, 16))
            });
            OcrLine address = new OcrLine();
            address.Words.AddRange(new OcrWord[]
            {
                new OcrWord("1", new RectangleF(10, 130, 6, 16)),
                new OcrWord("0", new RectangleF(22, 130, 9, 16)),
                new OcrWord(".44.7.219", new RectangleF(36, 130, 84, 16))
            });
            ImageReader.Build(reading, new List<OcrLine> { alert, repeat, title, metrics, address });
            Check(reading.Text.Contains("10.44.7.219") && !reading.Text.Contains("1 0.44.7.219"),
                "OCR fragments of one IP address are joined");
            List<Detection> found = ImageScanner.Detect(reading, new SafePasteDatabase(), ControlMode.Balanced,
                new List<ImageArea>());
            int nodes = 0;
            bool environment = false;
            bool project = false;
            Detection ip = null;
            foreach (Detection detection in found)
            {
                if (detection.Type == "HOST" && detection.Value == "nodefalcon") nodes++;
                if (detection.Type == "HOST" && detection.Value == "azimuthdock-prod") environment = true;
                if (detection.Type == "TEXT" && detection.Value == "azimuthdock") project = true;
                if (detection.Type == "IP" && detection.Value == "10.44.7.219") ip = detection;
            }
            Check(nodes == 2 && environment && project && ip != null,
                "Monitoring names, project title and complete IP are detected");
            RectangleF hostBox = ImageScanner.BoxesFor(reading, found.Find(delegate(Detection item)
            {
                return item.Type == "HOST" && item.Value == "nodefalcon";
            }))[0];
            RectangleF ipBox = ImageScanner.BoxesFor(reading, ip)[0];
            Check(hostBox.Width < 100 && ipBox.Left <= 10 && ipBox.Right >= 120,
                "Monitoring labels cover only their word, while IP covers every fragment");

            ImageReading chrome = new ImageReading();
            chrome.Width = 712;
            chrome.Height = 300;
            OcrLine clockRow = new OcrLine();
            clockRow.Words.AddRange(new OcrWord[]
            {
                new OcrWord("О", new RectangleF(38, 184, 26, 24)),
                new OcrWord("id", new RectangleF(173, 188, 15, 16))
            });
            ImageReader.Build(chrome, new List<OcrLine> { clockRow });
            chrome.Unread.Add(new Rectangle(70, 187, 78, 18));
            chrome.Unread.Add(new Rectangle(584, 194, 36, 18));
            chrome.Unread.Add(new Rectangle(250, 250, 80, 18));
            ImageReader.MarkTimestampChrome(chrome);
            Check(chrome.QuietUnread.Contains(0) && chrome.QuietUnread.Contains(1) && !chrome.QuietUnread.Contains(2),
                "Unrecognized clock text is quiet, unrelated unread text remains visible");
            using (ReviewForm form = NewReview(string.Empty, new SafePasteSettings(), true))
            {
                using (Bitmap picture = new Bitmap(chrome.Width, chrome.Height))
                {
                    Check(form.AddImageReading(new Bitmap(picture), chrome), "Clock screenshot opens for review");
                }
                List<ImageMark> normal = (List<ImageMark>)Call(form, "BuildImageMarks");
                Check(normal.FindAll(delegate(ImageMark mark) { return mark.Kind == ImageMarkKind.Unread; }).Count == 1,
                    "Ordinary mode only outlines the unrelated unread area");
                Call(form, "ApplyMode", ControlMode.Strict);
                List<ImageMark> strict = (List<ImageMark>)Call(form, "BuildImageMarks");
                Check(strict.FindAll(delegate(ImageMark mark) { return mark.Kind == ImageMarkKind.UnreadHidden; }).Count == 3,
                    "Strict mode still covers clock text along with other unread areas");
            }
        }

        /// <summary>Вкладка расшифровки: метки заменены реальными значениями, чего нет среди запомненных, помечено.</summary>
        private static void TestDecrypt()
        {
            if (File.Exists(Paths.LabelsFile)) File.Delete(Paths.LabelsFile);
            LabelStore store = new LabelStore(true);
            string host = store.Hide("web-prod", "HOST");
            SafePasteDatabase database = new SafePasteDatabase();
            database.Reserve("IP", "10.44.7.219", 7);
            database.Save();
            const string answer = "Проверь связь:\r\nping {0}\r\nping [IP_7]\r\nА [HOST_9] и [SECRET_1] я не знаю.";
            string text = string.Format(CultureInfo.InvariantCulture, answer, host);
            using (ReviewForm form = NewReview(string.Empty, new SafePasteSettings(), true))
            {
                Check(form.AddDecryptText(text) && form.IsDecrypt && form.TabCount == 1, "Decryption opens in the empty window");
                TextView preview = Field<TextView>(form, "preview");
                Check(preview.Content.Contains("ping web-prod\nping 10.44.7.219") && preview.Content.Contains("А [HOST_9] и [SECRET_1]"),
                    "Labels are replaced with real values: " + preview.Content);
                Check(preview.ReadOnly, "Decrypted text is read only");
                Check(Field<WindowHeader>(form, "header").Title == "Расшифровка" && Field<BlockHeader>(form, "sourceHeader").Title == "Реальные значения",
                    "The header softly shows the decryption mode");
                Check(!Shown(Field<Segmented>(form, "modeSwitch")) && !Shown(Field<GlassButton>(form, "pasteButton"))
                    && Field<GlassButton>(form, "copyButton").Kind == ButtonKind.Primary, "Decryption copies and never pastes into the chat");
                Check(Field<Legend>(form, "legend").Summary == "Расшифровано: 2, Не расшифровано: 2", "Legend counts restored and missing labels");
                int missing = preview.Content.IndexOf("[HOST_9]", StringComparison.Ordinal);
                TextMark mark = preview.MarkAt(missing + 1);
                Check(mark != null && mark.Kind == MarkKind.Missing, "A label that cannot be decrypted is highlighted");
                string[] tip = (string[])Call(form, "DescribeDecoded", mark.Tag);
                Check(tip[0] == "[HOST_9]" && tip[1].StartsWith("Не расшифровано"), "Hovering explains why: " + tip[1]);
                TextMark restored = preview.MarkAt(preview.Content.IndexOf("web-prod", StringComparison.Ordinal) + 1);
                Check(restored != null && restored.Kind == MarkKind.Restored
                    && ((string[])Call(form, "DescribeDecoded", restored.Tag))[0] == host, "A restored value shows its label on hover");
                TextMark secret = preview.MarkAt(preview.Content.IndexOf("[SECRET_1]", StringComparison.Ordinal) + 1);
                Check(((string[])Call(form, "DescribeDecoded", secret.Tag))[1].Contains("пароли и ключи"), "Secrets are explained");
                CheckGlassOnly(form);
                CheckTexts(form, "review-decrypt");
                Render(form, "review-decrypt");

                form.AddCopiedText("Ещё ответ: [IP_7]");
                Check(form.TabCount == 2 && form.IsDecrypt && Field<TabStrip>(form, "tabStrip")[1].Decrypt,
                    "A copy made in the decryption tab opens as decryption too");
                Call(form, "SelectDocument", 0);
                form.AddDecryptText("Ещё ответ: [IP_7]");
                Check(form.TabCount == 2 && Field<TabStrip>(form, "tabStrip").SelectedIndex == 1, "The same answer is not opened twice");
                Check(!Shown(Field<GlassButton>(form, "pasteAllButton")), "Decrypted tabs are never pasted all together");
            }
            using (ReviewForm form = NewReview("Сервер 10.1.2.3", new SafePasteSettings(), true))
            {
                form.AddDecryptText("Ответ: [IP_7]");
                Check(form.TabCount == 2 && form.IsDecrypt, "Ctrl+Shift+C adds a decryption tab to an open check");
                Call(form, "SelectDocument", 0);
                Check(!form.IsDecrypt && Shown(Field<Segmented>(form, "modeSwitch")) && Field<WindowHeader>(form, "header").Title
                    == "Проверка перед вставкой", "Switching back restores the check mode");
                Check(form.BuildAllResult().Text == "Сервер [IP_1]", "Paste all skips decryption tabs");
                Render(form, "review-mixed-tabs");
            }
            File.Delete(Paths.LabelsFile);
            File.Delete(Paths.DatabaseFile);
            File.Delete(Paths.SettingsFile);
        }

        /// <summary>Видимость, заданная самому элементу: Visible у дочерних элементов непоказанного окна всегда false.</summary>
        private static bool Shown(Control control)
        {
            MethodInfo state = typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new Type[] { typeof(int) }, null);
            return (bool)state.Invoke(control, new object[] { 0x00000002 });
        }

        private static void RenderMenu(ToolStrip menu, string name)
        {
            CreateHandles(menu);
            menu.PerformLayout();
            using (Bitmap bitmap = new Bitmap(Math.Max(1, menu.Width), Math.Max(1, menu.Height)))
            {
                menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
                bitmap.Save(Path.Combine(Output, name + ".png"), ImageFormat.Png);
            }
        }

        private static void TestTray()
        {
            int review = 0, quick = 0, reveal = 0, settings = 0, about = 0, exit = 0;
            ControlMode mode = ControlMode.Balanced;
            ToolStripMenuItem status;
            using (ContextMenuStrip menu = TrayMenu.Create(delegate { review++; }, delegate { quick++; }, delegate { reveal++; },
                delegate { settings++; }, delegate { about++; }, delegate { exit++; }, delegate { return mode; },
                delegate(ControlMode chosen) { mode = chosen; }, out status))
            {
                Check(!HasItem(menu, "Правила и исключения") && !HasItem(menu, "Мост для агентов")
                    && !HasItem(menu, "Запоминать метки"), "Tray menu keeps settings out of the top level");
                TrayActionItem settingsItem = (TrayActionItem)FindItem(menu, "Настройки");
                Check(settingsItem != null && !settingsItem.HasDropDownItems, "Settings open a window, not a submenu");
                Check(HasItem(menu, "Показать с реальными значениями"), "Revealing labels is a top level action");
                TrayActionItem revealItem = (TrayActionItem)FindItem(menu, "Показать с реальными значениями");
                Check(revealItem.AccessibleDescription.Contains("Ctrl+Shift+C"), "Reveal item shows its shortcut");
                revealItem.Description = null;
                Check(revealItem.Description == " ", "Empty description keeps the second line");
                revealItem.Description = "Раскрыть метки в тексте из буфера";
                TrayModeItem modes = null;
                foreach (ToolStripItem item in menu.Items)
                {
                    if (item is TrayModeItem) modes = (TrayModeItem)item;
                }
                Check(modes != null, "Tray menu lets you pick the mode");
                modes.Choose((int)ControlMode.Strict);
                Check(mode == ControlMode.Strict, "Tray mode switch changes the mode");
                MethodInfo dialogKey = typeof(TrayModeItem).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
                dialogKey.Invoke(modes, new object[] { Keys.Right });
                Check(mode == ControlMode.Balanced, "Arrow keys switch the mode in the tray menu");
                status.Text = "Работает локально";
                menu.PerformLayout();
                CreateHandles(menu);
                using (Bitmap bitmap = new Bitmap(menu.Width, menu.Height))
                {
                    menu.DrawToBitmap(bitmap, menu.ClientRectangle);
                    bitmap.Save(Path.Combine(Output, "tray.png"), ImageFormat.Png);
                }
                foreach (ToolStripItem item in menu.Items)
                {
                    if (item is TrayActionItem) item.PerformClick();
                }
                Check(review == 1 && quick == 1 && reveal == 1 && settings == 1 && about == 1 && exit == 1,
                    "Every tray action invokes its own callback once");
                Check(status.Enabled && status is TrayHeaderItem, "The header with the app name is clickable");
                status.PerformClick();
                Check(review == 2, "Clicking the app name opens the review window");
                Check(status.AccessibleName == "Открыть SafePaste" && status.AccessibleDescription == "Работает локально",
                    "The header reads as an open action with the status");
                status.Text = "В буфере есть текст";
                Check(status.AccessibleDescription == "В буфере есть текст", "Status changes reach the header description");
                Check(menu is GlassMenu, "Tray menu uses the glass style");
                foreach (ToolStripItem item in menu.Items)
                {
                    CheckText(item.Text, "tray item");
                    CheckText(item.AccessibleDescription, "tray item description");
                }
            }
        }

        /// <summary>Окно настроек: группы строк, выключатели сохраняют сразу, строки-переходы открывают окна.</summary>
        private static void TestSettings()
        {
            int rules = 0, bridge = 0;
            bool start = false, warn = true, save = true, hide = true, smart = true;
            SettingsActions actions = new SettingsActions();
            actions.ShowRules = delegate { rules++; };
            actions.ShowBridge = delegate { bridge++; };
            actions.DescribeBridge = delegate { return "Работает, агентов: 2"; };
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
            using (SettingsForm form = new SettingsForm(actions))
            {
                CreateHandles(form);
                int changed = 0;
                form.Changed += delegate { changed++; };
                List<SettingRow> rows = form.Rows;
                List<string> titles = new List<string>();
                foreach (SettingRow row in rows) titles.Add(row.Text);
                Check(string.Join("|", titles.ToArray()) == "Запускать при входе в Windows|Предупреждать о журнале буфера|"
                    + "Интеллектуальный анализ|Запоминать метки|Скрывать запомненное|Правила и исключения|Мост для агентов",
                    "Settings list every option in one window: " + string.Join("|", titles.ToArray()));
                SettingRow autostart = rows[0];
                Check(autostart.IsSwitch && !autostart.On && autostart.AccessibleDescription.StartsWith("Выключено", StringComparison.Ordinal),
                    "Autostart starts off");
                Render(form, "settings");
                autostart.Perform();
                Check(start && autostart.On && changed == 1, "The autostart switch saves at once");
                Check(autostart.Description.Contains("без сообщения"), "Autostart explains the silent start");
                rows[1].Perform();
                Check(!warn && !rows[1].On, "The clipboard warning can be turned off");
                Check(rows[2].On && rows[2].AccessibleDescription.Contains("RU EN UK DE FR ES PT IT PL TR"),
                    "Smart analysis lists languages in settings");
                rows[3].Perform();
                Check(!save && !rows[3].On && hide, "The save switch turns only saving off");
                rows[4].Perform();
                Check(!hide && !rows[4].On, "The hide switch turns hiding off");
                rows[2].Perform();
                Check(!smart && !rows[2].On, "The analysis switch changes its setting");
                save = true;
                form.ReloadAll();
                Check(rows[3].On, "Switches show a state changed elsewhere after reload");
                rows[5].Perform();
                rows[6].Perform();
                Check(rules == 1 && bridge == 1 && changed == 5, "Link rows open their windows without counting as changes");
                Check(rows[6].Description == "Работает, агентов: 2", "Bridge status is shown in settings");
                // Строка отдельно от окна: окно показало бы на ошибке модальное сообщение.
                using (SettingRow broken = SettingRow.Switch("Запоминать метки", "Включено", "Выключено",
                    delegate { return true; }, delegate { throw new IOException("Файл занят."); }))
                {
                    bool failed = false;
                    broken.Failed += delegate(string message) { failed = message == "Файл занят."; };
                    broken.Perform();
                    Check(failed && broken.On, "A failed save is reported and the switch keeps the real state");
                }
                PressKey(form, Keys.Down);
                Render(form, "settings-changed");
                CheckGlassOnly(form);
                CheckTexts(form, "settings");
                foreach (SettingRow row in rows)
                {
                    CheckText(row.Description, "settings row description");
                    CheckText(row.AccessibleDescription, "settings row description");
                }
                Check(form.ClientSize.Height <= Dpi.S(640), "Settings fit on a laptop screen: " + form.ClientSize.Height);
            }
            using (SettingsForm offline = new SettingsForm(SettingsActions.Offline()))
            {
                CreateHandles(offline);
                Check(offline.Rows.Count == 7, "Offline settings build the same window");
            }
        }

        /// <summary>Анимации: значения на месте, пока окно не показано, цвета смешиваются без потемнения.</summary>
        private static void TestMotion()
        {
            using (GlassButton button = new GlassButton(Glyphs.Settings, null, ButtonKind.Plain, "Настройки"))
            {
                Tween tween = new Tween(button, 0f);
                tween.To(90f, 400);
                Check(tween.Value == 90f && tween.Target == 90f, "A hidden control snaps to the end of the animation");
                tween.Snap(0f);
                Check(tween.Value == 0f, "Snap sets the value at once");
            }
            Color half = Theme.Blend(Color.Transparent, Color.FromArgb(40, 255, 255, 255), 0.5f);
            Check(half.A == 20 && half.R == 255 && half.G == 255 && half.B == 255, "Fading in from transparent keeps the hue");
            Check(Theme.Blend(Color.Red, Color.Blue, 0f) == Color.Red && Theme.Blend(Color.Red, Color.Blue, 1f) == Color.Blue,
                "Blend ends are exact");
            using (ReviewForm form = NewReview(Fixture, new SafePasteSettings(), true))
            {
                GlassButton gear = Field<GlassButton>(form, "settingsButton");
                GlassButton close = Field<GlassButton>(form, "closeButton");
                Check(gear.Width > close.Width && gear.GlyphFont.Size > close.GlyphFont.Size, "The gear is larger than the close button");
                Check(gear.HoverTurn != 0f && close.HoverTurn != 0f, "The gear and the close button turn on hover");
                Form opened = null;
                form.ShowSettings = delegate(Form owner) { opened = owner; };
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(gear, new object[] { EventArgs.Empty });
                Check(opened == form, "The gear opens the settings window over the review window");
                Mouse(gear, "OnMouseEnter", MouseButtons.None, 0, 0, 0);
                Render(form, "review-gear-hover");
                Mouse(gear, "OnMouseLeave", MouseButtons.None, 0, 0, 0);
            }
        }

        /// <summary>В строках интерфейса нет длинных тире, точек-разделителей и двойных пробелов.</summary>
        private static void TestSourceTexts()
        {
            string root = FindSources();
            if (root == null)
            {
                Console.WriteLine("Sources not found, text style check skipped.");
                return;
            }
            List<string> files = new List<string>(Directory.GetFiles(Path.Combine(root, "Ui"), "*.cs"));
            files.Add(Path.Combine(root, "TrayApplication.cs"));
            files.Add(Path.Combine(root, "Program.cs"));
            files.Add(Path.Combine(root, "ClipboardService.cs"));
            Regex literal = new Regex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"");
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("//"))
                    {
                        continue;
                    }
                    foreach (Match match in literal.Matches(line))
                    {
                        CheckText(match.Value, Path.GetFileName(file) + ":" + (i + 1).ToString());
                    }
                }
                string code = File.ReadAllText(file, Encoding.UTF8);
                if (!file.EndsWith("GlassDialog.cs", StringComparison.OrdinalIgnoreCase))
                {
                    Check(code.IndexOf("MessageBox.Show", StringComparison.Ordinal) < 0,
                        "Standard Windows message boxes are gone: " + Path.GetFileName(file));
                }
            }
        }

        // ---------------------------------------------------------------- помощники

        private static string FindSources()
        {
            DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, Path.Combine("src", "SafePaste"));
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
            return null;
        }

        private static void CheckText(string text, string where)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            Check(text.IndexOf('\u2014') < 0, "No em dash in UI text (" + where + "): " + text);
            Check(text.IndexOf('\u00B7') < 0, "No middle dot separators in UI text (" + where + "): " + text);
            Check(text.IndexOf("  ", StringComparison.Ordinal) < 0, "No double spaces in UI text (" + where + "): " + text);
        }

        private static void CheckTexts(Control root, string where)
        {
            CheckText(root.Text, where);
            CheckText(root.AccessibleName, where);
            GlassButton button = root as GlassButton;
            if (button != null)
            {
                CheckText(button.Tip, where + " tip");
            }
            foreach (Control child in root.Controls)
            {
                CheckTexts(child, where);
            }
        }

        private static void CheckMenuTexts(ToolStrip menu, string where)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                CheckText(item.Text, where);
            }
        }

        /// <summary>Только свои элементы: стандартные поля и списки на стекле просвечивали бы.</summary>
        private static void CheckGlassOnly(Control root)
        {
            foreach (Control child in root.Controls)
            {
                Check(child is GlassControl, "Only glass-aware controls are used: " + child.GetType().Name);
                CheckGlassOnly(child);
            }
        }

        private static void Collect(Control root, List<GlassButton> buttons)
        {
            foreach (Control child in root.Controls)
            {
                GlassButton button = child as GlassButton;
                if (button != null)
                {
                    buttons.Add(button);
                }
                Collect(child, buttons);
            }
        }

        private static bool HasItem(ToolStrip menu, string text)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item.Text == text) return true;
            }
            return false;
        }

        private static ToolStripItem FindItem(ToolStrip menu, string text)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item.Text == text) return item;
            }
            return null;
        }

        private static List<string> ItemTexts(ToolStrip menu)
        {
            List<string> texts = new List<string>();
            foreach (ToolStripItem item in menu.Items)
            {
                if (!(item is ToolStripSeparator)) texts.Add(item.Text);
            }
            return texts;
        }

        private static bool HasPrefix(ToolStrip menu, string text)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item.Text != null && item.Text.StartsWith(text, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static Detection FindDetection(List<Detection> detections, string value, bool enabled)
        {
            foreach (Detection detection in detections)
            {
                if (detection.Value == value && detection.Enabled == enabled) return detection;
            }
            throw new InvalidOperationException("Detection not found: " + value);
        }

        private static int LineNumber(string text, int index)
        {
            int count = 0;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        private static void Mouse(Control control, string handler, MouseButtons button, int clicks, int x, int y)
        {
            MethodInfo method = control.GetType().GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(control, new object[] { new MouseEventArgs(button, clicks, x, y, 0) });
        }

        private static void TypeText(Control control, string value)
        {
            MethodInfo press = typeof(Control).GetMethod("OnKeyPress", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (char symbol in value)
            {
                press.Invoke(control, new object[] { new KeyPressEventArgs(symbol) });
            }
        }

        private static void PressKeyDown(Control control, Keys key)
        {
            MethodInfo down = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
            down.Invoke(control, new object[] { new KeyEventArgs(key) });
        }

        private static void PressKey(Control control, Keys key)
        {
            Message message = Message.Create(control.Handle, 0x0100, new IntPtr((int)key), IntPtr.Zero);
            MethodInfo process = control.GetType().GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
            process.Invoke(control, new object[] { message, key });
        }

        private static void Render(Form form, string name)
        {
            CreateHandles(form);
            form.PerformLayout();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(Output, name + ".png"), ImageFormat.Png);
            }
            Check(form.Width > 0 && form.Height > 0, name + " renders");
        }

        private static void CreateHandles(Control control)
        {
            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls) CreateHandles(child);
            control.PerformLayout();
        }

        private static T Field<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new MissingFieldException(target.GetType().Name, name);
            }
            return (T)field.GetValue(target);
        }

        private static T Property<T>(object target, string name)
        {
            return (T)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target, null);
        }

        private static object Call(object target, string name, params object[] args)
        {
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (method.Name == name && method.GetParameters().Length == args.Length)
                {
                    return method.Invoke(target, args);
                }
            }
            throw new MissingMethodException(target.GetType().Name, name);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }
}
