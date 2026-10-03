using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SafePaste.Ui
{
    internal delegate void SubmitHandler(bool backwards);

    /// <summary>
    /// Однострочное поле ввода на стекле. Обычный TextBox рисует непрозрачный фон,
    /// и на стекле через него просвечивал бы рабочий стол.
    /// </summary>
    internal sealed class GlassInput : GlassControl
    {
        private string value = string.Empty;
        private int caret;
        private int anchor;
        private float offset;
        private float[] positions = new float[] { 0f };
        private bool caretVisible;
        private bool selecting;
        private readonly Timer blink;
        private string placeholder = string.Empty;
        private string counter = string.Empty;
        private bool showGlyph = true;

        internal event EventHandler ValueChanged;
        internal event SubmitHandler Submitted;
        internal event EventHandler Cancelled;

        internal GlassInput()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.IBeam;
            AccessibleRole = AccessibleRole.Text;
            blink = new Timer();
            blink.Interval = SystemInformation.CaretBlinkTime > 0 ? SystemInformation.CaretBlinkTime : 530;
            blink.Tick += delegate
            {
                caretVisible = !caretVisible;
                Invalidate();
            };
        }

        internal string Value
        {
            get { return value; }
            set { SetValue(value ?? string.Empty, (value ?? string.Empty).Length); }
        }

        internal string Placeholder
        {
            get { return placeholder; }
            set { placeholder = value ?? string.Empty; AccessibleName = placeholder; Invalidate(); }
        }

        /// <summary>Счётчик справа внутри поля, например «2 из 5».</summary>
        internal string Counter
        {
            get { return counter; }
            set { counter = value ?? string.Empty; Invalidate(); }
        }

        /// <summary>Лупа слева нужна полю поиска; обычное поле настроек обходится без неё.</summary>
        internal bool ShowGlyph
        {
            get { return showGlyph; }
            set { showGlyph = value; MeasurePositions(); EnsureCaretVisible(); Invalidate(); }
        }

        internal void SelectAll()
        {
            anchor = 0;
            caret = value.Length;
            Invalidate();
        }

        private float GlyphWidth
        {
            get { return Dpi.F(30); }
        }

        private float TextLeft
        {
            get { return showGlyph ? GlyphWidth : Dpi.F(14); }
        }

        private float TextRight
        {
            get
            {
                float counterWidth = counter.Length == 0 ? 0 : Theme.Measure(counter, Theme.SmallFont, 0).Width + Dpi.F(12);
                return Width - Dpi.F(10) - counterWidth;
            }
        }

        private void SetValue(string next, int nextCaret)
        {
            bool changed = next != value;
            value = next;
            caret = Math.Max(0, Math.Min(value.Length, nextCaret));
            anchor = caret;
            MeasurePositions();
            EnsureCaretVisible();
            RestartBlink();
            Invalidate();
            if (changed)
            {
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                if (ValueChanged != null)
                {
                    ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        private void MeasurePositions()
        {
            positions = new float[value.Length + 1];
            for (int i = 1; i <= value.Length; i++)
            {
                positions[i] = Theme.Measure(value.Substring(0, i), Font, 0).Width;
            }
        }

        private void EnsureCaretVisible()
        {
            float room = Math.Max(Dpi.F(20), TextRight - TextLeft);
            float x = positions[caret];
            if (x - offset > room)
            {
                offset = x - room;
            }
            if (x - offset < 0)
            {
                offset = x;
            }
            if (positions[value.Length] - offset < room)
            {
                offset = Math.Max(0, positions[value.Length] - room);
            }
        }

        private int IndexAt(float x)
        {
            float target = x - TextLeft + offset;
            int best = 0;
            float distance = float.MaxValue;
            for (int i = 0; i < positions.Length; i++)
            {
                float current = Math.Abs(positions[i] - target);
                if (current < distance)
                {
                    distance = current;
                    best = i;
                }
            }
            return best;
        }

        private int SelectionStart
        {
            get { return Math.Min(anchor, caret); }
        }

        private int SelectionLength
        {
            get { return Math.Abs(caret - anchor); }
        }

        private void Insert(string text)
        {
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            string next = value.Remove(SelectionStart, SelectionLength).Insert(SelectionStart, text);
            SetValue(next, SelectionStart + text.Length);
        }

        private void MoveCaret(int position, bool extend)
        {
            caret = Math.Max(0, Math.Min(value.Length, position));
            if (!extend)
            {
                anchor = caret;
            }
            EnsureCaretVisible();
            RestartBlink();
            Invalidate();
        }

        private int WordStart(int from)
        {
            int position = from;
            while (position > 0 && char.IsWhiteSpace(value[position - 1])) position--;
            while (position > 0 && !char.IsWhiteSpace(value[position - 1])) position--;
            return position;
        }

        private int WordEnd(int from)
        {
            int position = from;
            while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
            while (position < value.Length && !char.IsWhiteSpace(value[position])) position++;
            return position;
        }

        private void RestartBlink()
        {
            caretVisible = true;
            if (Focused)
            {
                blink.Stop();
                blink.Start();
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    if (Cancelled != null)
                    {
                        Cancelled(this, EventArgs.Empty);
                    }
                    return true;
                case Keys.Enter:
                case Keys.Shift | Keys.Enter:
                    if (Submitted != null)
                    {
                        Submitted((keyData & Keys.Shift) != 0);
                    }
                    return true;
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    string text;
                    string error;
                    if (ClipboardService.TryGetText(out text, out error))
                    {
                        Insert(text);
                    }
                    return true;
                case Keys.Control | Keys.C:
                case Keys.Control | Keys.Insert:
                    CopySelection();
                    return true;
                case Keys.Control | Keys.X:
                    CopySelection();
                    if (SelectionLength > 0)
                    {
                        Insert(string.Empty);
                    }
                    return true;
                case Keys.Control | Keys.A:
                    SelectAll();
                    return true;
                case Keys.Control | Keys.Back:
                    if (SelectionLength == 0)
                    {
                        anchor = WordStart(caret);
                    }
                    Insert(string.Empty);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CopySelection()
        {
            if (SelectionLength == 0)
            {
                return;
            }
            string error;
            ClipboardService.TrySetText(value.Substring(SelectionStart, SelectionLength), out error);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool extend = e.Shift;
            switch (e.KeyCode)
            {
                case Keys.Left:
                    if (!extend && SelectionLength > 0 && !e.Control)
                    {
                        MoveCaret(SelectionStart, false);
                    }
                    else
                    {
                        MoveCaret(e.Control ? WordStart(caret) : caret - 1, extend);
                    }
                    break;
                case Keys.Right:
                    if (!extend && SelectionLength > 0 && !e.Control)
                    {
                        MoveCaret(SelectionStart + SelectionLength, false);
                    }
                    else
                    {
                        MoveCaret(e.Control ? WordEnd(caret) : caret + 1, extend);
                    }
                    break;
                case Keys.Home:
                    MoveCaret(0, extend);
                    break;
                case Keys.End:
                    MoveCaret(value.Length, extend);
                    break;
                case Keys.Back:
                    if (SelectionLength == 0 && caret > 0)
                    {
                        anchor = caret - 1;
                    }
                    Insert(string.Empty);
                    break;
                case Keys.Delete:
                    if (SelectionLength == 0 && caret < value.Length)
                    {
                        anchor = caret + 1;
                    }
                    Insert(string.Empty);
                    break;
                default:
                    base.OnKeyDown(e);
                    return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!char.IsControl(e.KeyChar))
            {
                Insert(e.KeyChar.ToString());
                e.Handled = true;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused)
            {
                Focus();
            }
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            if (e.Clicks >= 2)
            {
                SelectAll();
                return;
            }
            MoveCaret(IndexAt(e.X), (ModifierKeys & Keys.Shift) != 0);
            selecting = true;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (selecting)
            {
                MoveCaret(IndexAt(e.X), true);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            selecting = false;
            Capture = false;
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
            blink.Stop();
            caretVisible = false;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            EnsureCaretVisible();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = bounds.Height / 2f;
            using (GraphicsPath path = Theme.Round(bounds, radius))
            {
                using (SolidBrush fill = new SolidBrush(Focused ? Theme.FillHover : Theme.Fill))
                {
                    graphics.FillPath(fill, path);
                }
                if (Focused)
                {
                    using (Pen pen = new Pen(Color.FromArgb(170, Theme.Accent), Dpi.F(1.2f)))
                    {
                        graphics.DrawPath(pen, path);
                    }
                }
                else
                {
                    Theme.StrokeEdge(graphics, path, bounds, Theme.EdgeTop, Theme.EdgeBottom);
                }
            }
            if (showGlyph)
            {
                Theme.DrawGlyph(graphics, Glyphs.Search, Theme.SmallIconFont, Theme.Tertiary,
                    new RectangleF(Dpi.F(6), 0, GlyphWidth - Dpi.F(8), Height));
            }
            if (counter.Length > 0)
            {
                Theme.DrawText(graphics, counter, Theme.SmallFont, Theme.Tertiary,
                    new RectangleF(TextRight, 0, Width - TextRight - Dpi.F(10), Height),
                    StringAlignment.Far, StringAlignment.Center, false);
            }
            RectangleF area = new RectangleF(TextLeft, 0, Math.Max(0, TextRight - TextLeft), Height);
            GraphicsState state = graphics.Save();
            graphics.SetClip(area);
            if (value.Length == 0)
            {
                Theme.DrawText(graphics, placeholder, Font, Theme.Tertiary, area);
            }
            else
            {
                if (SelectionLength > 0 && Focused)
                {
                    float from = TextLeft + positions[SelectionStart] - offset;
                    float to = TextLeft + positions[SelectionStart + SelectionLength] - offset;
                    Theme.FillRound(graphics, new RectangleF(from, Height * 0.2f, to - from, Height * 0.6f), Dpi.F(2), Theme.TextSelection);
                }
                Theme.DrawText(graphics, value, Font, Theme.Text,
                    new RectangleF(TextLeft - offset, 0, positions[value.Length] + Dpi.F(4), Height));
            }
            if (Focused && caretVisible)
            {
                float x = TextLeft + positions[caret] - offset;
                using (Pen pen = new Pen(Theme.Accent, Dpi.F(1.5f)))
                {
                    graphics.DrawLine(pen, x, Height * 0.24f, x, Height * 0.76f);
                }
            }
            graphics.Restore(state);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new InputAccessible(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                blink.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class InputAccessible : ControlAccessibleObject
        {
            private readonly GlassInput input;

            internal InputAccessible(GlassInput input)
                : base(input)
            {
                this.input = input;
            }

            public override AccessibleRole Role
            {
                get { return AccessibleRole.Text; }
            }

            public override string Value
            {
                get { return input.value; }
                set { input.Value = value; }
            }
        }
    }

    /// <summary>
    /// Шапка блока: название и рядом значок поиска. Нажатие прячет название и открывает поле,
    /// поиск идёт сразу при вводе. Справа стоят инструменты блока.
    /// </summary>
    internal sealed class BlockHeader : GlassControl
    {
        private string title;
        private readonly Color titleColor;
        private readonly GlassButton searchButton;
        private readonly GlassInput input;
        private readonly GlassButton closeButton;
        private readonly List<Control> tools = new List<Control>();
        private readonly Dictionary<Control, int> toolWidths = new Dictionary<Control, int>();
        private readonly List<Control> optionalTools = new List<Control>();
        private readonly List<Control> hiddenTools = new List<Control>();
        private bool searching;
        private bool searchable = true;

        internal event EventHandler SearchChanged;
        internal event SubmitHandler SearchStep;

        internal BlockHeader(string title, Color titleColor, string searchTip)
        {
            this.title = title;
            this.titleColor = titleColor;
            AccessibleName = title;
            searchButton = new GlassButton(Glyphs.Search, null, ButtonKind.Plain, searchTip);
            searchButton.Click += delegate { OpenSearch(); };
            input = new GlassInput();
            input.Placeholder = "Поиск";
            input.Visible = false;
            input.ValueChanged += delegate { RaiseSearchChanged(); };
            input.Submitted += delegate(bool backwards)
            {
                if (SearchStep != null)
                {
                    SearchStep(backwards);
                }
            };
            input.Cancelled += delegate { CloseSearch(); };
            closeButton = new GlassButton(Glyphs.Clear, null, ButtonKind.Plain, "Закрыть поиск\nEsc");
            closeButton.Visible = false;
            closeButton.Click += delegate { CloseSearch(); };
            Controls.Add(searchButton);
            Controls.Add(input);
            Controls.Add(closeButton);
        }

        internal string Title
        {
            get { return title; }
            set
            {
                title = value ?? string.Empty;
                AccessibleName = title;
                PerformLayout();
                Invalidate();
            }
        }

        internal bool Searching
        {
            get { return searching; }
        }

        /// <summary>false: в блоке нечего искать, лупа не показывается.</summary>
        internal bool Searchable
        {
            get { return searchable; }
            set
            {
                searchable = value;
                if (!value)
                {
                    CloseSearch();
                }
                searchButton.Visible = value && !searching;
                PerformLayout();
            }
        }

        internal string Query
        {
            get { return searching ? input.Value : string.Empty; }
        }

        internal GlassInput Input
        {
            get { return input; }
        }

        internal GlassButton SearchButton
        {
            get { return searchButton; }
        }

        internal void SetCounter(string text)
        {
            input.Counter = text;
        }

        /// <summary>Инструмент справа; при width = 0 ширина как у кнопки.</summary>
        internal void AddTool(Control tool, int width)
        {
            AddTool(tool, width, false);
        }

        /// <summary>optional: инструмент прячется, если иначе не помещаются название и поиск.</summary>
        internal void AddTool(Control tool, int width, bool optional)
        {
            tools.Add(tool);
            toolWidths[tool] = width;
            if (optional)
            {
                optionalTools.Add(tool);
            }
            Controls.Add(tool);
            PerformLayout();
        }

        /// <summary>
        /// Инструмент слева от другого. Нужен, чтобы одинаковые кнопки стояли в одном порядке в разных блоках:
        /// переключатель вида всегда левее кнопки «взять из буфера».
        /// </summary>
        internal void InsertTool(Control tool, int width, Control before)
        {
            int index = tools.IndexOf(before);
            tools.Insert(index < 0 ? tools.Count : index, tool);
            toolWidths[tool] = width;
            Controls.Add(tool);
            PerformLayout();
        }

        internal void SetToolWidth(Control tool, int width)
        {
            toolWidths[tool] = width;
            PerformLayout();
        }

        /// <summary>Показать или спрятать инструмент; спрятанный не занимает места.</summary>
        internal void ShowTool(Control tool, bool show)
        {
            if (show)
            {
                hiddenTools.Remove(tool);
            }
            else if (!hiddenTools.Contains(tool))
            {
                hiddenTools.Add(tool);
            }
            tool.Visible = show;
            PerformLayout();
        }

        internal void OpenSearch()
        {
            if (!searchable)
            {
                return;
            }
            if (!searching)
            {
                searching = true;
                searchButton.Visible = false;
                input.Visible = true;
                closeButton.Visible = true;
                PerformLayout();
                Invalidate();
            }
            input.Focus();
            input.SelectAll();
            if (input.Value.Length > 0)
            {
                RaiseSearchChanged();
            }
        }

        internal void CloseSearch()
        {
            if (!searching)
            {
                return;
            }
            bool refocus = input.Focused;
            searching = false;
            input.Value = string.Empty;
            input.Counter = string.Empty;
            input.Visible = false;
            closeButton.Visible = false;
            searchButton.Visible = searchable;
            PerformLayout();
            Invalidate();
            RaiseSearchChanged();
            if (refocus && Parent != null)
            {
                Parent.SelectNextControl(this, true, true, true, true);
            }
        }

        private void RaiseSearchChanged()
        {
            if (SearchChanged != null)
            {
                SearchChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (closeButton == null)
            {
                return; // вызов из конструктора базового класса
            }
            int height = Height;
            int button = Dpi.S(30);
            int top = (height - button) / 2;
            int right = Width;
            int titleWidth = (int)Math.Ceiling(Theme.Measure(title, Theme.StrongFont, 0).Width);
            int needed = searching ? Dpi.S(160) : titleWidth + Dpi.S(6) + button + Dpi.S(12);
            int all = 0;
            foreach (Control tool in tools)
            {
                if (!hiddenTools.Contains(tool))
                {
                    all += (toolWidths[tool] > 0 ? toolWidths[tool] : button) + Dpi.S(6);
                }
            }
            bool compact = Width - all < needed;
            // Visible здесь не годится: пока окно не показано, он false у всех дочерних элементов.
            for (int i = tools.Count - 1; i >= 0; i--)
            {
                Control tool = tools[i];
                if (hiddenTools.Contains(tool) || (compact && optionalTools.Contains(tool)))
                {
                    tool.SetBounds(right, 0, 0, 0);
                    continue;
                }
                int width = toolWidths[tool] > 0 ? toolWidths[tool] : button;
                int toolHeight = toolWidths[tool] > 0 && tool is Segmented ? Dpi.S(28) : button;
                right -= width;
                tool.SetBounds(right, (height - toolHeight) / 2, width, toolHeight);
                right -= Dpi.S(6);
            }
            if (searching)
            {
                int inputRight = right - button - Dpi.S(4);
                input.SetBounds(0, (height - Dpi.S(30)) / 2, Math.Max(Dpi.S(60), inputRight), Dpi.S(30));
                closeButton.SetBounds(input.Right + Dpi.S(4), top, button, button);
                return;
            }
            if (!searchable)
            {
                searchButton.SetBounds(Math.Min(titleWidth + Dpi.S(6), Math.Max(0, right)), top, 0, button);
                return;
            }
            searchButton.SetBounds(Math.Min(titleWidth + Dpi.S(6), Math.Max(0, right - button)), top, button, button);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (searching)
            {
                return;
            }
            Theme.PrepareText(e.Graphics);
            Theme.DrawText(e.Graphics, title, Theme.StrongFont, titleColor,
                new RectangleF(0, 0, Math.Max(0, searchButton.Left), Height));
        }
    }
}
