using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SafePaste.Ui
{
    internal enum MarkKind
    {
        Hidden,
        Kept,
        Secret,
        Placeholder,
        /// <summary>Расшифровка: реальное значение на месте метки, подсветка едва заметная.</summary>
        Restored,
        /// <summary>Расшифровка: метку расшифровать не удалось.</summary>
        Missing
    }

    /// <summary>Подсвеченный фрагмент; позиции считаются в показанном тексте.</summary>
    internal sealed class TextMark
    {
        internal readonly int Start;
        internal readonly int Length;
        internal readonly MarkKind Kind;
        internal readonly object Tag;

        internal TextMark(int start, int length, MarkKind kind, object tag)
        {
            Start = start;
            Length = length;
            Kind = kind;
            Tag = tag;
        }

        internal int End
        {
            get { return Start + Length; }
        }
    }

    internal delegate void TextDropHandler(string text);

    internal delegate void HistoryHandler(bool redo);

    /// <summary>
    /// Правка текста: заменить Length символов с позиции Start (в показанном тексте) на Text.
    /// Владелец применяет её сам и ставит Handled, иначе правку применяет сам TextView.
    /// </summary>
    internal sealed class TextEditEventArgs : EventArgs
    {
        internal readonly int Start;
        internal readonly int Length;
        internal readonly string Text;
        internal bool Handled;

        internal TextEditEventArgs(int start, int length, string text)
        {
            Start = start;
            Length = length;
            Text = text;
        }
    }

    /// <summary>
    /// Текст с подсветкой. Нарисован вручную: стандартные поля Windows не умеют
    /// прозрачный фон, и на стекле сквозь них просвечивал бы рабочий стол.
    /// Шрифт моноширинный, поэтому перенос строк и попадание мышью считаются просто.
    /// По умолчанию только просмотр; без ReadOnly текст можно править с клавиатуры.
    /// </summary>
    internal sealed class TextView : GlassControl
    {
        private const int MaxMatches = 5000;

        private string text = string.Empty;
        private List<TextMark> marks = new List<TextMark>();
        private readonly List<int> lines = new List<int>();
        private readonly List<int> matches = new List<int>();
        private string query = string.Empty;
        private int currentMatch = -1;
        private float charWidth;
        private int lineHeight;
        private float textTop;
        private int layoutWidth = -1;
        private int scroll;
        private int anchor;
        private int caret;
        private TextMark hovered;
        private int hoverIndex = -1;
        private bool selecting;
        private bool thumbDragging;
        private int thumbGrab;
        private bool barHovered;
        private readonly Timer dragTimer;
        private Point dragPoint;
        private DateTime lastDoubleClick = DateTime.MinValue;
        private string placeholder = string.Empty;
        private bool readOnly = true;
        private readonly Timer blinkTimer;
        private bool caretVisible;
        // Курсор в конце строки, перенесённой по ширине: он рисуется в конце этой строки, а не в начале следующей.
        private bool caretAtLineEnd;
        // Столбец, к которому возвращаются стрелки вверх и вниз после короткой строки.
        private int desiredColumn = -1;

        internal event EventHandler Scrolled;
        internal event EventHandler HoverChanged;
        internal event EventHandler SelectionChanged;
        internal event MouseEventHandler ContextRequested;
        internal event TextDropHandler TextDropped;
        internal event EventHandler<TextEditEventArgs> EditRequested;
        internal event HistoryHandler HistoryRequested;

        internal TextView()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Font = Theme.MonoFont;
            Cursor = Cursors.IBeam;
            AccessibleRole = AccessibleRole.Text;
            dragTimer = new Timer();
            dragTimer.Interval = 40;
            dragTimer.Tick += OnDragTick;
            blinkTimer = new Timer();
            blinkTimer.Interval = SystemInformation.CaretBlinkTime > 0 ? SystemInformation.CaretBlinkTime : 530;
            blinkTimer.Tick += delegate
            {
                caretVisible = !caretVisible;
                Invalidate();
            };
            Measure();
            lines.Add(0);
        }

        /// <summary>Только просмотр (результат) или правка (исходный текст).</summary>
        internal bool ReadOnly
        {
            get { return readOnly; }
            set
            {
                readOnly = value;
                RestartBlink();
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            }
        }

        private static int PadLeft
        {
            get { return Dpi.S(14); }
        }

        private static int PadRight
        {
            get { return Dpi.S(20); }
        }

        private static int PadTop
        {
            get { return Dpi.S(4); }
        }

        private static int PadBottom
        {
            get { return Dpi.S(12); }
        }

        // ---------------------------------------------------------------- содержимое

        internal string Content
        {
            get { return text; }
        }

        internal List<TextMark> Marks
        {
            get { return marks; }
        }

        internal string Placeholder
        {
            get { return placeholder; }
            set
            {
                placeholder = value ?? string.Empty;
                Invalidate();
            }
        }

        internal int LineCount
        {
            get { return lines.Count; }
        }

        internal int LineHeight
        {
            get { return lineHeight; }
        }

        internal void SetContent(string value, List<TextMark> newMarks, bool keepScroll)
        {
            text = value ?? string.Empty;
            marks = newMarks ?? new List<TextMark>();
            hovered = null;
            hoverIndex = -1;
            caretAtLineEnd = false;
            desiredColumn = -1;
            if (keepScroll)
            {
                anchor = Math.Min(anchor, text.Length);
                caret = Math.Min(caret, text.Length);
            }
            else
            {
                anchor = 0;
                caret = 0;
                scroll = 0;
            }
            Relayout();
            FindMatches(false);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }

        /// <summary>Меняет только подсветку: текст тот же, прокрутка и выделение остаются.</summary>
        internal void SetMarks(List<TextMark> newMarks)
        {
            marks = newMarks ?? new List<TextMark>();
            TextMark before = hovered;
            hovered = hoverIndex >= 0 ? MarkAt(hoverIndex) : null;
            Invalidate();
            if ((before != null || hovered != null) && HoverChanged != null)
            {
                HoverChanged(this, EventArgs.Empty);
            }
        }

        private void Measure()
        {
            charWidth = Math.Max(1f, Theme.Measure(new string('M', 64), Font, 0).Width / 64f);
            float fontHeight = Theme.LineHeight(Font);
            lineHeight = Math.Max(1, (int)Math.Ceiling(fontHeight * 1.34f));
            textTop = (lineHeight - fontHeight) / 2f;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Measure();
            Relayout();
            Invalidate();
        }

        /// <summary>Раскладка по строкам с переносом по словам: без горизонтальной прокрутки.</summary>
        private void Relayout()
        {
            layoutWidth = ClientSize.Width;
            lines.Clear();
            lines.Add(0);
            if (layoutWidth <= 0)
            {
                layoutWidth = -1;
                return;
            }
            int columns = Math.Max(8, (int)Math.Floor((layoutWidth - PadLeft - PadRight) / charWidth));
            int length = text.Length;
            int start = 0;
            while (true)
            {
                int newline = text.IndexOf('\n', start);
                int stop = newline < 0 ? length : newline;
                int lineStart = start;
                while (stop - lineStart > columns)
                {
                    int limit = lineStart + columns;
                    int cut = limit;
                    int floor = lineStart + columns / 2;
                    for (int index = limit; index > floor; index--)
                    {
                        if (IsBreak(text[index - 1]))
                        {
                            cut = index;
                            break;
                        }
                    }
                    lines.Add(cut);
                    lineStart = cut;
                }
                if (newline < 0)
                {
                    break;
                }
                start = newline + 1;
                lines.Add(start);
            }
            ClampScroll();
        }

        private static bool IsBreak(char symbol)
        {
            return symbol == ' ' || symbol == '/' || symbol == '\\' || symbol == ',' || symbol == ';'
                || symbol == '|' || symbol == '&' || symbol == '?' || symbol == '=';
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ClientSize.Width != layoutWidth)
            {
                int top = TopIndex;
                float fraction = TopFraction;
                Relayout();
                ScrollToIndex(top, fraction);
            }
            ClampScroll();
            Invalidate();
        }

        private int ContentHeight
        {
            get { return lines.Count * lineHeight + PadTop + PadBottom; }
        }

        internal int MaxScroll
        {
            get { return Math.Max(0, ContentHeight - ClientSize.Height); }
        }

        internal int ScrollOffset
        {
            get { return scroll; }
        }

        private void ClampScroll()
        {
            int max = MaxScroll;
            if (scroll > max)
            {
                scroll = max;
            }
            if (scroll < 0)
            {
                scroll = 0;
            }
        }

        private int LineOf(int index)
        {
            int found = lines.BinarySearch(index);
            if (found >= 0)
            {
                return found;
            }
            return Math.Max(0, ~found - 1);
        }

        /// <summary>Конец видимой строки без перевода строки.</summary>
        private int LineEnd(int line)
        {
            int start = lines[line];
            int end = line + 1 < lines.Count ? lines[line + 1] : text.Length;
            if (end > start && text[end - 1] == '\n')
            {
                end--;
            }
            return end;
        }

        // ---------------------------------------------------------------- прокрутка

        private int TopLine
        {
            get
            {
                if (lineHeight <= 0 || scroll <= PadTop)
                {
                    return 0;
                }
                return Math.Min(lines.Count - 1, (scroll - PadTop) / lineHeight);
            }
        }

        /// <summary>Первый символ верхней видимой строки: по нему выравнивается соседний блок.</summary>
        internal int TopIndex
        {
            get { return lines[TopLine]; }
        }

        internal float TopFraction
        {
            get { return lineHeight <= 0 ? 0f : (scroll - PadTop - TopLine * lineHeight) / (float)lineHeight; }
        }

        internal void ScrollToIndex(int index, float fraction)
        {
            int line = LineOf(Math.Max(0, Math.Min(text.Length, index)));
            SetScroll(PadTop + (int)Math.Round((line + fraction) * lineHeight), false);
        }

        internal void SetScroll(int value, bool notify)
        {
            int next = Math.Max(0, Math.Min(MaxScroll, value));
            if (next == scroll)
            {
                return;
            }
            scroll = next;
            Invalidate();
            if (notify && Scrolled != null)
            {
                Scrolled(this, EventArgs.Empty);
            }
        }

        private void ScrollBy(int delta)
        {
            GlassTip.HideFor(this);
            SetScroll(scroll + delta, true);
            UpdateHover(PointToClient(Cursor.Position));
        }

        private int PageSize
        {
            get { return Math.Max(lineHeight, ClientSize.Height - lineHeight * 2); }
        }

        /// <summary>Показывает фрагмент; если он не виден, ставит его в верхнюю треть.</summary>
        internal void RevealRange(int start, int length)
        {
            if (lineHeight <= 0)
            {
                return;
            }
            int line = LineOf(Math.Max(0, Math.Min(text.Length, start)));
            int top = PadTop + line * lineHeight;
            if (top >= scroll && top + lineHeight <= scroll + ClientSize.Height)
            {
                return;
            }
            SetScroll(top - ClientSize.Height / 3, true);
        }

        // ---------------------------------------------------------------- выделение

        internal int SelectionStart
        {
            get { return Math.Min(anchor, caret); }
        }

        internal int SelectionLength
        {
            get { return Math.Abs(caret - anchor); }
        }

        internal string SelectedText
        {
            get { return SelectionLength == 0 ? string.Empty : text.Substring(SelectionStart, SelectionLength); }
        }

        internal void Select(int start, int length)
        {
            start = Math.Max(0, Math.Min(text.Length, start));
            anchor = start;
            caret = Math.Max(start, Math.Min(text.Length, start + length));
            Invalidate();
            RaiseSelectionChanged();
        }

        internal void SelectAll()
        {
            Select(0, text.Length);
        }

        internal void CopySelection()
        {
            string selected = SelectedText;
            if (selected.Length == 0)
            {
                return;
            }
            string error;
            ClipboardService.TrySetText(selected.Replace("\n", Environment.NewLine), out error);
        }

        internal int CaretIndex
        {
            get { return caret; }
        }

        /// <summary>Ставит курсор без выделения и прокручивает к нему.</summary>
        internal void SetCaret(int index)
        {
            caret = Math.Max(0, Math.Min(text.Length, index));
            anchor = caret;
            caretAtLineEnd = false;
            desiredColumn = -1;
            AfterCaretMove();
        }

        // ---------------------------------------------------------------- правка

        /// <summary>Заменяет выделение текстом: набор с клавиатуры, вставка, удаление.</summary>
        internal void ReplaceSelection(string value)
        {
            RequestEdit(SelectionStart, SelectionLength, value);
        }

        internal void CutSelection()
        {
            if (readOnly || SelectionLength == 0)
            {
                return;
            }
            CopySelection();
            ReplaceSelection(string.Empty);
        }

        internal void PasteFromClipboard()
        {
            string value;
            string error;
            if (readOnly || !ClipboardService.TryGetText(out value, out error) || string.IsNullOrEmpty(value))
            {
                return;
            }
            ReplaceSelection(value.Replace("\r\n", "\n").Replace('\r', '\n'));
        }

        private void RequestEdit(int start, int length, string value)
        {
            if (readOnly)
            {
                return;
            }
            start = Math.Max(0, Math.Min(text.Length, start));
            length = Math.Max(0, Math.Min(text.Length - start, length));
            value = value ?? string.Empty;
            if (length == 0 && value.Length == 0)
            {
                return;
            }
            GlassTip.HideFor(this);
            TextEditEventArgs args = new TextEditEventArgs(start, length, value);
            if (EditRequested != null)
            {
                EditRequested(this, args);
            }
            if (!args.Handled)
            {
                // Владельца нет: подсветка к новому тексту уже не относится.
                SetContent(text.Remove(start, length).Insert(start, value), null, true);
                SetCaret(start + value.Length);
            }
        }

        private void RaiseHistory(bool redo)
        {
            if (!readOnly && HistoryRequested != null)
            {
                HistoryRequested(redo);
            }
        }

        private void MoveCaret(int position, bool extend)
        {
            MoveCaret(position, extend, false);
        }

        private void MoveCaret(int position, bool extend, bool lineEnd)
        {
            caret = Math.Max(0, Math.Min(text.Length, position));
            if (!extend)
            {
                anchor = caret;
            }
            caretAtLineEnd = lineEnd;
            desiredColumn = -1;
            AfterCaretMove();
        }

        /// <summary>Стрелки вверх и вниз: та же колонка на соседней строке, даже если по пути строка короче.</summary>
        private void MoveVertical(int delta, bool extend)
        {
            int line = CaretLine();
            int column = desiredColumn >= 0 ? desiredColumn : caret - lines[line];
            int target = Math.Max(0, Math.Min(lines.Count - 1, line + delta));
            int end = LineEnd(target);
            int position = Math.Min(lines[target] + column, end);
            caret = position;
            if (!extend)
            {
                anchor = caret;
            }
            caretAtLineEnd = position == end && IsSoftBreak(target);
            desiredColumn = column;
            AfterCaretMove();
        }

        private void AfterCaretMove()
        {
            EnsureCaretVisible();
            RestartBlink();
            Invalidate();
            RaiseSelectionChanged();
        }

        /// <summary>Строка, на которой нарисован курсор, с учётом переноса по ширине.</summary>
        private int CaretLine()
        {
            int line = LineOf(caret);
            if (caretAtLineEnd && line > 0 && lines[line] == caret && IsSoftBreak(line - 1))
            {
                line--;
            }
            return line;
        }

        /// <summary>Строка перенесена по ширине окна, а не переводом строки.</summary>
        private bool IsSoftBreak(int line)
        {
            if (line + 1 >= lines.Count)
            {
                return false;
            }
            int next = lines[line + 1];
            return next > 0 && next <= text.Length && text[next - 1] != '\n';
        }

        private void EnsureCaretVisible()
        {
            if (lineHeight <= 0 || ClientSize.Height <= 0)
            {
                return;
            }
            int top = PadTop + CaretLine() * lineHeight;
            if (top < scroll + PadTop)
            {
                SetScroll(top - PadTop, true);
            }
            else if (top + lineHeight > scroll + ClientSize.Height - PadBottom)
            {
                SetScroll(top + lineHeight - ClientSize.Height + PadBottom, true);
            }
        }

        private void RestartBlink()
        {
            caretVisible = true;
            blinkTimer.Stop();
            if (!readOnly && Focused)
            {
                blinkTimer.Start();
            }
        }

        private int PageLines
        {
            get { return Math.Max(1, PageSize / Math.Max(1, lineHeight)); }
        }

        private static bool IsWordLetter(char symbol)
        {
            return char.IsLetterOrDigit(symbol) || symbol == '_';
        }

        private int WordLeft(int from)
        {
            int position = from;
            while (position > 0 && !IsWordLetter(text[position - 1]))
            {
                position--;
            }
            while (position > 0 && IsWordLetter(text[position - 1]))
            {
                position--;
            }
            return position;
        }

        private int WordRight(int from)
        {
            int position = from;
            while (position < text.Length && IsWordLetter(text[position]))
            {
                position++;
            }
            while (position < text.Length && !IsWordLetter(text[position]))
            {
                position++;
            }
            return position;
        }

        /// <summary>Шаг курсора на символ; пара суррогатов (эмодзи) проходится целиком.</summary>
        private int PreviousStop(int index)
        {
            if (index <= 0)
            {
                return 0;
            }
            int position = index - 1;
            if (position > 0 && char.IsLowSurrogate(text[position]) && char.IsHighSurrogate(text[position - 1]))
            {
                position--;
            }
            return position;
        }

        private int NextStop(int index)
        {
            if (index >= text.Length)
            {
                return text.Length;
            }
            int position = index + 1;
            if (position < text.Length && char.IsLowSurrogate(text[position]) && char.IsHighSurrogate(text[position - 1]))
            {
                position++;
            }
            return position;
        }

        private void RaiseSelectionChanged()
        {
            if (SelectionChanged != null)
            {
                SelectionChanged(this, EventArgs.Empty);
            }
        }

        private void SelectWordAt(int index)
        {
            if (text.Length == 0)
            {
                return;
            }
            int position = Math.Max(0, Math.Min(index, text.Length - 1));
            if (!IsWordChar(text[position]) && position > 0 && IsWordChar(text[position - 1]))
            {
                position--;
            }
            if (!IsWordChar(text[position]))
            {
                Select(position, 1);
                return;
            }
            int start = position;
            int end = position + 1;
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }
            while (end < text.Length && IsWordChar(text[end]))
            {
                end++;
            }
            // Точка или дефис в конце: это знак препинания, а не часть имени.
            while (end > start + 1 && (text[end - 1] == '.' || text[end - 1] == '-'))
            {
                end--;
            }
            Select(start, end - start);
        }

        private void SelectLineAt(int index)
        {
            int line = LineOf(Math.Max(0, Math.Min(index, text.Length)));
            int start = lines[line];
            Select(start, LineEnd(line) - start);
        }

        private static bool IsWordChar(char symbol)
        {
            return char.IsLetterOrDigit(symbol) || symbol == '_' || symbol == '-' || symbol == '.'
                || symbol == '@' || symbol == '\\' || symbol == '$';
        }

        // ---------------------------------------------------------------- поиск

        internal string Query
        {
            get { return query; }
        }

        internal int MatchCount
        {
            get { return matches.Count; }
        }

        internal int CurrentMatch
        {
            get { return currentMatch; }
        }

        internal void Search(string value)
        {
            query = value ?? string.Empty;
            FindMatches(true);
            Invalidate();
        }

        internal void StepMatch(int direction)
        {
            if (matches.Count == 0)
            {
                return;
            }
            currentMatch = (currentMatch + direction + matches.Count) % matches.Count;
            RevealRange(matches[currentMatch], query.Length);
            Invalidate();
        }

        private void FindMatches(bool reveal)
        {
            int previous = currentMatch >= 0 && currentMatch < matches.Count ? matches[currentMatch] : -1;
            matches.Clear();
            currentMatch = -1;
            if (query.Length == 0 || text.Length == 0)
            {
                return;
            }
            int position = 0;
            while (matches.Count < MaxMatches && position < text.Length)
            {
                int found = text.IndexOf(query, position, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }
                matches.Add(found);
                position = found + query.Length;
            }
            if (matches.Count == 0)
            {
                return;
            }
            int from = reveal ? TopIndex : previous;
            currentMatch = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                if (matches[i] >= from)
                {
                    currentMatch = i;
                    break;
                }
            }
            if (reveal)
            {
                RevealRange(matches[currentMatch], query.Length);
            }
        }

        // ---------------------------------------------------------------- попадание мышью

        /// <summary>Символ под точкой или -1, если там нет текста.</summary>
        internal int CharIndexAt(Point point)
        {
            if (text.Length == 0 || lineHeight <= 0)
            {
                return -1;
            }
            int y = point.Y + scroll - PadTop;
            if (y < 0)
            {
                return -1;
            }
            int line = y / lineHeight;
            if (line >= lines.Count)
            {
                return -1;
            }
            float x = point.X - PadLeft;
            if (x < 0)
            {
                return -1;
            }
            int start = lines[line];
            int index = start + (int)(x / charWidth);
            return index < LineEnd(line) ? index : -1;
        }

        /// <summary>Ближайшая к точке позиция курсора между символами.</summary>
        internal int CaretIndexAt(Point point)
        {
            if (text.Length == 0 || lineHeight <= 0)
            {
                return 0;
            }
            int y = point.Y + scroll - PadTop;
            int line = y < 0 ? 0 : Math.Min(lines.Count - 1, y / lineHeight);
            int start = lines[line];
            int column = (int)Math.Round((point.X - PadLeft) / charWidth);
            int index = start + Math.Max(0, column);
            int end = LineEnd(line);
            return index > end ? end : index;
        }

        /// <summary>Прямоугольник символа в координатах элемента.</summary>
        internal Rectangle CharBounds(int index)
        {
            int line = LineOf(Math.Max(0, Math.Min(index, text.Length)));
            float top = PadTop + line * lineHeight - scroll;
            return Rectangle.Round(new RectangleF(PadLeft + (index - lines[line]) * charWidth, top, charWidth, lineHeight));
        }

        internal TextMark MarkAt(int index)
        {
            int low = 0;
            int high = marks.Count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                TextMark mark = marks[middle];
                if (index < mark.Start)
                {
                    high = middle - 1;
                }
                else if (index >= mark.End)
                {
                    low = middle + 1;
                }
                else
                {
                    return mark;
                }
            }
            return null;
        }

        internal TextMark HoveredMark
        {
            get { return hovered; }
        }

        /// <summary>Где на экране подсвечено значение под мышью: к нему привязывается подсказка.</summary>
        internal Rectangle HoverBounds
        {
            get
            {
                if (hovered == null || hoverIndex < 0)
                {
                    return Rectangle.Empty;
                }
                int line = LineOf(hoverIndex);
                int start = lines[line];
                int from = Math.Max(hovered.Start, start);
                int to = Math.Max(from + 1, Math.Min(hovered.End, LineEnd(line)));
                float top = PadTop + line * lineHeight - scroll;
                return Rectangle.Round(new RectangleF(PadLeft + (from - start) * charWidth, top, (to - from) * charWidth, lineHeight));
            }
        }

        private void UpdateHover(Point location)
        {
            if (!ClientRectangle.Contains(location))
            {
                SetHover(null, -1);
                return;
            }
            bool overBar = MaxScroll > 0 && Track.Contains(location);
            int index = overBar ? -1 : CharIndexAt(location);
            TextMark mark = index >= 0 ? MarkAt(index) : null;
            SetHover(mark, mark == null ? -1 : index);
        }

        private void SetHover(TextMark mark, int index)
        {
            hoverIndex = index;
            if (mark == hovered)
            {
                return;
            }
            hovered = mark;
            Invalidate();
            if (HoverChanged != null)
            {
                HoverChanged(this, EventArgs.Empty);
            }
        }

        private static int FirstEndingAfter(List<TextMark> items, int position)
        {
            int low = 0;
            int high = items.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (items[middle].End <= position)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        private int FirstMatchEndingAfter(int position)
        {
            int low = 0;
            int high = matches.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (matches[middle] + query.Length <= position)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        // ---------------------------------------------------------------- отрисовка

        private static Color FillFor(MarkKind kind)
        {
            switch (kind)
            {
                case MarkKind.Hidden: return Theme.HiddenFill;
                case MarkKind.Secret: return Theme.SecretFill;
                case MarkKind.Placeholder: return Theme.PlaceholderFill;
                case MarkKind.Restored: return Theme.RestoredFill;
                case MarkKind.Missing: return Theme.MissingFill;
                default: return Theme.KeptFill;
            }
        }

        private static Color InkFor(MarkKind kind)
        {
            switch (kind)
            {
                case MarkKind.Hidden: return Theme.Warm;
                case MarkKind.Secret: return Theme.Danger;
                case MarkKind.Placeholder: return Theme.Accent;
                case MarkKind.Missing: return Theme.Warm;
                default: return Theme.Text;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            if (layoutWidth != ClientSize.Width)
            {
                Relayout();
            }
            if (text.Length == 0)
            {
                Theme.DrawText(graphics, placeholder, Theme.UiFont, Theme.Tertiary,
                    new RectangleF(PadLeft, 0, Math.Max(0, Width - PadLeft - PadRight), Height),
                    StringAlignment.Center, StringAlignment.Center, true);
                PaintCaret(graphics);
                return;
            }
            int first = TopLine;
            int last = Math.Min(lines.Count - 1, (scroll + ClientSize.Height - PadTop) / lineHeight);
            int selectionStart = SelectionStart;
            int selectionEnd = selectionStart + SelectionLength;
            int markIndex = FirstEndingAfter(marks, lines[first]);
            int matchIndex = FirstMatchEndingAfter(lines[first]);
            using (System.Drawing.StringFormat format = Theme.CreateFormat(StringAlignment.Near, StringAlignment.Near, false))
            {
                format.Trimming = StringTrimming.None;
                for (int line = first; line <= last; line++)
                {
                    int start = lines[line];
                    int end = LineEnd(line);
                    float top = PadTop + line * lineHeight - scroll;
                    int nextStart = line + 1 < lines.Count ? lines[line + 1] : text.Length + 1;

                    for (int m = markIndex; m < marks.Count && marks[m].Start < end; m++)
                    {
                        TextMark mark = marks[m];
                        int from = Math.Max(mark.Start, start);
                        int to = Math.Min(mark.End, end);
                        if (to > from)
                        {
                            PaintPill(graphics, from - start, to - start, top, mark);
                        }
                    }
                    for (int m = matchIndex; m < matches.Count && matches[m] < end; m++)
                    {
                        int from = Math.Max(matches[m], start);
                        int to = Math.Min(matches[m] + query.Length, end);
                        if (to > from && m != currentMatch)
                        {
                            Theme.FillRound(graphics, CellRect(from - start, to - start, top, 1f), Dpi.F(3), Theme.MatchFill);
                        }
                    }
                    if (selectionEnd > selectionStart && selectionStart <= end && selectionEnd > start)
                    {
                        int from = Math.Max(selectionStart, start);
                        int to = Math.Min(selectionEnd, end);
                        float extra = selectionEnd > end && end < nextStart ? 0.5f : 0f;
                        RectangleF area = CellRect(from - start, to - start, top, 0f);
                        area.Width += extra * charWidth;
                        Theme.FillRound(graphics, area, Dpi.F(2), Theme.TextSelection);
                    }

                    PaintRuns(graphics, format, start, end, top, markIndex);

                    if (currentMatch >= 0)
                    {
                        int match = matches[currentMatch];
                        int from = Math.Max(match, start);
                        int to = Math.Min(match + query.Length, end);
                        if (to > from)
                        {
                            Theme.FillRound(graphics, CellRect(from - start, to - start, top, 1f), Dpi.F(3), Theme.MatchCurrent);
                            using (SolidBrush ink = new SolidBrush(Theme.MatchInk))
                            {
                                DrawRun(graphics, format, ink, from, to, start, top);
                            }
                        }
                    }

                    while (markIndex < marks.Count && marks[markIndex].End <= nextStart)
                    {
                        markIndex++;
                    }
                    while (matchIndex < matches.Count && matches[matchIndex] + query.Length <= nextStart)
                    {
                        matchIndex++;
                    }
                }
            }
            PaintCaret(graphics);
            PaintScrollBar(graphics);
        }

        private void PaintCaret(Graphics graphics)
        {
            if (readOnly || !Focused || !caretVisible || lineHeight <= 0)
            {
                return;
            }
            int line = CaretLine();
            float x = PadLeft + (caret - lines[line]) * charWidth;
            float top = PadTop + line * lineHeight - scroll;
            using (Pen pen = new Pen(Theme.Accent, Dpi.F(1.5f)))
            {
                graphics.DrawLine(pen, x, top + Dpi.F(3), x, top + lineHeight - Dpi.F(3));
            }
        }

        private RectangleF CellRect(int fromColumn, int toColumn, float top, float inset)
        {
            return new RectangleF(PadLeft + fromColumn * charWidth - Dpi.F(inset), top + Dpi.F(1),
                (toColumn - fromColumn) * charWidth + Dpi.F(inset * 2), lineHeight - Dpi.F(2));
        }

        private void PaintPill(Graphics graphics, int fromColumn, int toColumn, float top, TextMark mark)
        {
            RectangleF area = CellRect(fromColumn, toColumn, top, 2f);
            Color fill = FillFor(mark.Kind);
            if (mark == hovered)
            {
                fill = Color.FromArgb(Math.Min(255, fill.A * 2 + 12), fill);
            }
            Theme.FillRound(graphics, area, Dpi.F(5), fill);
            if (mark == hovered)
            {
                using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Round(area, Dpi.F(5)))
                using (Pen pen = new Pen(Color.FromArgb(150, InkFor(mark.Kind)), Dpi.F(1)))
                {
                    graphics.DrawPath(pen, path);
                }
            }
        }

        private void PaintRuns(Graphics graphics, System.Drawing.StringFormat format, int start, int end, float top, int markIndex)
        {
            using (SolidBrush normal = new SolidBrush(Theme.Text))
            {
                int position = start;
                int index = markIndex;
                while (position < end)
                {
                    while (index < marks.Count && marks[index].End <= position)
                    {
                        index++;
                    }
                    TextMark mark = index < marks.Count ? marks[index] : null;
                    if (mark != null && mark.Start <= position)
                    {
                        int runEnd = Math.Min(mark.End, end);
                        using (SolidBrush ink = new SolidBrush(InkFor(mark.Kind)))
                        {
                            DrawRun(graphics, format, ink, position, runEnd, start, top);
                        }
                        position = runEnd;
                    }
                    else
                    {
                        int runEnd = mark != null ? Math.Min(mark.Start, end) : end;
                        DrawRun(graphics, format, normal, position, runEnd, start, top);
                        position = runEnd;
                    }
                }
            }
        }

        private void DrawRun(Graphics graphics, System.Drawing.StringFormat format, Brush brush, int from, int to, int lineStart, float top)
        {
            if (to <= from)
            {
                return;
            }
            string run = text.Substring(from, to - from);
            if (run.Trim().Length == 0)
            {
                return;
            }
            graphics.DrawString(run, Font, brush, PadLeft + (from - lineStart) * charWidth, top + textTop, format);
        }

        private Rectangle Track
        {
            get
            {
                int width = Dpi.S(12);
                return new Rectangle(ClientSize.Width - width - Dpi.S(2), Dpi.S(4), width, Math.Max(0, ClientSize.Height - Dpi.S(8)));
            }
        }

        private Rectangle Thumb
        {
            get
            {
                int max = MaxScroll;
                if (max <= 0)
                {
                    return Rectangle.Empty;
                }
                Rectangle track = Track;
                int height = (int)Math.Max(Dpi.S(28), (long)track.Height * ClientSize.Height / Math.Max(1, ContentHeight));
                height = Math.Min(height, track.Height);
                int y = track.Top + (int)((long)(track.Height - height) * scroll / max);
                return new Rectangle(track.Left, y, track.Width, height);
            }
        }

        private void PaintScrollBar(Graphics graphics)
        {
            Rectangle thumb = Thumb;
            if (thumb.IsEmpty)
            {
                return;
            }
            float width = barHovered || thumbDragging ? Dpi.F(8) : Dpi.F(5);
            RectangleF bar = new RectangleF(thumb.Right - width - Dpi.F(2), thumb.Top, width, thumb.Height);
            int alpha = thumbDragging ? 150 : barHovered ? 115 : 72;
            Theme.FillRound(graphics, bar, width / 2f, Color.FromArgb(alpha, 255, 255, 255));
        }

        // ---------------------------------------------------------------- мышь и клавиатура

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            GlassTip.HideFor(this);
            if (!Focused)
            {
                Focus();
            }
            if (e.Button == MouseButtons.Right)
            {
                int index = CharIndexAt(e.Location);
                int start = SelectionStart;
                if (SelectionLength == 0 || index < start || index >= start + SelectionLength)
                {
                    int position = CaretIndexAt(e.Location);
                    anchor = position;
                    caret = position;
                    caretAtLineEnd = IsLineEndPoint(e.Location, position);
                    desiredColumn = -1;
                    RestartBlink();
                    Invalidate();
                    RaiseSelectionChanged();
                }
                return;
            }
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            if (MaxScroll > 0 && Track.Contains(e.Location))
            {
                Rectangle thumb = Thumb;
                if (thumb.Contains(e.Location))
                {
                    thumbDragging = true;
                    thumbGrab = e.Y - thumb.Top;
                }
                else
                {
                    ScrollBy(e.Y < thumb.Top ? -PageSize : PageSize);
                }
                Capture = true;
                Invalidate();
                return;
            }
            int caretIndex = CaretIndexAt(e.Location);
            if (e.Clicks >= 2)
            {
                lastDoubleClick = DateTime.UtcNow;
                SelectWordAt(CharIndexAt(e.Location) >= 0 ? CharIndexAt(e.Location) : caretIndex);
                return;
            }
            if ((DateTime.UtcNow - lastDoubleClick).TotalMilliseconds < SystemInformation.DoubleClickTime)
            {
                lastDoubleClick = DateTime.MinValue;
                SelectLineAt(caretIndex);
                return;
            }
            if ((ModifierKeys & Keys.Shift) != 0)
            {
                caret = caretIndex;
            }
            else
            {
                anchor = caretIndex;
                caret = caretIndex;
            }
            caretAtLineEnd = IsLineEndPoint(e.Location, caretIndex);
            desiredColumn = -1;
            RestartBlink();
            selecting = true;
            Capture = true;
            Invalidate();
        }

        /// <summary>Щелчок правее конца перенесённой строки ставит курсор в её конец, а не в начало следующей.</summary>
        private bool IsLineEndPoint(Point point, int index)
        {
            if (lineHeight <= 0 || text.Length == 0)
            {
                return false;
            }
            int y = point.Y + scroll - PadTop;
            int line = y < 0 ? 0 : Math.Min(lines.Count - 1, y / lineHeight);
            return index == LineEnd(line) && IsSoftBreak(line);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDragging)
            {
                Rectangle track = Track;
                Rectangle thumb = Thumb;
                int room = Math.Max(1, track.Height - thumb.Height);
                SetScroll((int)((long)(e.Y - thumbGrab - track.Top) * MaxScroll / room), true);
                return;
            }
            if (selecting)
            {
                dragPoint = e.Location;
                caret = CaretIndexAt(e.Location);
                caretAtLineEnd = IsLineEndPoint(e.Location, caret);
                if (e.Y < 0 || e.Y > ClientSize.Height)
                {
                    dragTimer.Start();
                }
                else
                {
                    dragTimer.Stop();
                }
                Invalidate();
                return;
            }
            bool overBar = MaxScroll > 0 && Track.Contains(e.Location);
            if (overBar != barHovered)
            {
                barHovered = overBar;
                Invalidate();
            }
            Cursor = overBar ? Cursors.Default : Cursors.IBeam;
            UpdateHover(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                bool wasSelecting = selecting;
                selecting = false;
                thumbDragging = false;
                dragTimer.Stop();
                Capture = false;
                Invalidate();
                if (wasSelecting)
                {
                    RaiseSelectionChanged();
                }
            }
            else if (e.Button == MouseButtons.Right && ContextRequested != null)
            {
                ContextRequested(this, e);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (barHovered)
            {
                barHovered = false;
                Invalidate();
            }
            SetHover(null, -1);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = SystemInformation.MouseWheelScrollLines;
            int delta = step < 0 ? PageSize : Math.Max(1, step) * lineHeight;
            ScrollBy(-e.Delta * delta / 120);
        }

        private void OnDragTick(object sender, EventArgs e)
        {
            if (!selecting)
            {
                dragTimer.Stop();
                return;
            }
            int outside = dragPoint.Y < 0 ? dragPoint.Y : dragPoint.Y > ClientSize.Height ? dragPoint.Y - ClientSize.Height : 0;
            if (outside == 0)
            {
                dragTimer.Stop();
                return;
            }
            int speed = Math.Max(lineHeight / 2, Math.Min(lineHeight * 3, Math.Abs(outside)));
            SetScroll(scroll + Math.Sign(outside) * speed, true);
            caret = CaretIndexAt(dragPoint);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
                case Keys.Left:
                case Keys.Right:
                case Keys.Enter:
                    return !readOnly;
            }
            return base.IsInputKey(keyData);
        }

        /// <summary>
        /// Сочетания, которые окно иначе забрало бы себе: в поле правки Ctrl+V вставляет текст
        /// в место курсора, а не заменяет его целиком.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!readOnly && Focused)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.V:
                    case Keys.Shift | Keys.Insert:
                        PasteFromClipboard();
                        return true;
                    case Keys.Control | Keys.X:
                    case Keys.Shift | Keys.Delete:
                        CutSelection();
                        return true;
                    case Keys.Control | Keys.Z:
                        RaiseHistory(false);
                        return true;
                    case Keys.Control | Keys.Y:
                    case Keys.Control | Keys.Shift | Keys.Z:
                        RaiseHistory(true);
                        return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool handled = readOnly ? HandleViewKey(e) : HandleEditKey(e);
            if (handled)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        /// <summary>Только просмотр: клавиши прокручивают текст.</summary>
        private bool HandleViewKey(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:
                    ScrollBy(-lineHeight);
                    return true;
                case Keys.Down:
                    ScrollBy(lineHeight);
                    return true;
                case Keys.PageUp:
                    ScrollBy(-PageSize);
                    return true;
                case Keys.PageDown:
                    ScrollBy(PageSize);
                    return true;
                case Keys.Home:
                    ScrollBy(-scroll);
                    return true;
                case Keys.End:
                    ScrollBy(MaxScroll - scroll);
                    return true;
                case Keys.A:
                    if (e.Control)
                    {
                        SelectAll();
                    }
                    return e.Control;
                case Keys.C:
                case Keys.Insert:
                    if (e.Control)
                    {
                        CopySelection();
                    }
                    return e.Control;
            }
            return false;
        }

        /// <summary>Правка: клавиши двигают курсор, Shift расширяет выделение, Ctrl шагает по словам.</summary>
        private bool HandleEditKey(KeyEventArgs e)
        {
            bool extend = e.Shift;
            switch (e.KeyCode)
            {
                case Keys.Left:
                    if (!extend && !e.Control && SelectionLength > 0)
                    {
                        MoveCaret(SelectionStart, false);
                    }
                    else
                    {
                        MoveCaret(e.Control ? WordLeft(caret) : PreviousStop(caret), extend);
                    }
                    return true;
                case Keys.Right:
                    if (!extend && !e.Control && SelectionLength > 0)
                    {
                        MoveCaret(SelectionStart + SelectionLength, false);
                    }
                    else
                    {
                        MoveCaret(e.Control ? WordRight(caret) : NextStop(caret), extend);
                    }
                    return true;
                case Keys.Up:
                case Keys.Down:
                    if (e.Control)
                    {
                        ScrollBy(e.KeyCode == Keys.Up ? -lineHeight : lineHeight);
                    }
                    else
                    {
                        MoveVertical(e.KeyCode == Keys.Up ? -1 : 1, extend);
                    }
                    return true;
                case Keys.PageUp:
                    MoveVertical(-PageLines, extend);
                    return true;
                case Keys.PageDown:
                    MoveVertical(PageLines, extend);
                    return true;
                case Keys.Home:
                    MoveCaret(e.Control ? 0 : lines[CaretLine()], extend);
                    return true;
                case Keys.End:
                    if (e.Control)
                    {
                        MoveCaret(text.Length, extend);
                    }
                    else
                    {
                        int line = CaretLine();
                        MoveCaret(LineEnd(line), extend, IsSoftBreak(line));
                    }
                    return true;
                case Keys.Back:
                    if (SelectionLength > 0)
                    {
                        ReplaceSelection(string.Empty);
                    }
                    else if (caret > 0)
                    {
                        int from = e.Control ? WordLeft(caret) : PreviousStop(caret);
                        RequestEdit(from, caret - from, string.Empty);
                    }
                    return true;
                case Keys.Delete:
                    if (SelectionLength > 0)
                    {
                        ReplaceSelection(string.Empty);
                    }
                    else if (caret < text.Length)
                    {
                        int to = e.Control ? WordRight(caret) : NextStop(caret);
                        RequestEdit(caret, to - caret, string.Empty);
                    }
                    return true;
                case Keys.Enter:
                    if (e.Control || e.Alt)
                    {
                        return false; // Ctrl+Enter вставляет результат, это решает окно
                    }
                    ReplaceSelection("\n");
                    return true;
                case Keys.A:
                    if (e.Control)
                    {
                        SelectAll();
                    }
                    return e.Control;
                case Keys.C:
                case Keys.Insert:
                    if (e.Control)
                    {
                        CopySelection();
                    }
                    return e.Control;
            }
            return false;
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (readOnly || e.Handled || char.IsControl(e.KeyChar))
            {
                return;
            }
            ReplaceSelection(e.KeyChar.ToString());
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RestartBlink();
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            blinkTimer.Stop();
            Invalidate();
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            bool accepts = TextDropped != null && (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text));
            e.Effect = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string value = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string;
            if (!string.IsNullOrEmpty(value) && TextDropped != null)
            {
                TextDropped(value);
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TextViewAccessible(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                dragTimer.Dispose();
                blinkTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Экранный диктор читает текст так же, как у обычного поля.</summary>
        private sealed class TextViewAccessible : ControlAccessibleObject
        {
            private readonly TextView view;

            internal TextViewAccessible(TextView view)
                : base(view)
            {
                this.view = view;
            }

            public override AccessibleRole Role
            {
                get { return AccessibleRole.Text; }
            }

            public override AccessibleStates State
            {
                get { return view.readOnly ? base.State | AccessibleStates.ReadOnly : base.State; }
            }

            public override string Value
            {
                get { return view.text.Length > 20000 ? view.text.Substring(0, 20000) : view.text; }
                set { }
            }
        }
    }
}
