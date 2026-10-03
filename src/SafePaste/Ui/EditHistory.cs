using System;
using System.Collections.Generic;

namespace SafePaste.Ui
{
    /// <summary>Одна правка исходного текста: на месте Removed с позиции Start стал Inserted.</summary>
    internal sealed class TextEdit
    {
        internal int Start;
        internal string Removed;
        internal string Inserted;
        internal DateTime Time;
    }

    /// <summary>
    /// История правок для Ctrl+Z и Ctrl+Y. Буквы, набранные подряд, и серия Backspace или Delete
    /// склеиваются в одну правку, как в обычном редакторе.
    /// </summary>
    internal sealed class EditHistory
    {
        private const int Limit = 200;
        private const double MergeMilliseconds = 1500;

        private readonly List<TextEdit> undo = new List<TextEdit>();
        private readonly List<TextEdit> redo = new List<TextEdit>();

        internal bool CanUndo
        {
            get { return undo.Count > 0; }
        }

        internal bool CanRedo
        {
            get { return redo.Count > 0; }
        }

        internal void Clear()
        {
            undo.Clear();
            redo.Clear();
        }

        internal void Add(int start, string removed, string inserted)
        {
            redo.Clear();
            DateTime now = DateTime.UtcNow;
            TextEdit last = undo.Count > 0 ? undo[undo.Count - 1] : null;
            if (last != null && (now - last.Time).TotalMilliseconds < MergeMilliseconds && Merge(last, start, removed, inserted))
            {
                last.Time = now;
                return;
            }
            TextEdit edit = new TextEdit();
            edit.Start = start;
            edit.Removed = removed;
            edit.Inserted = inserted;
            edit.Time = now;
            undo.Add(edit);
            if (undo.Count > Limit)
            {
                undo.RemoveAt(0);
            }
        }

        private static bool Merge(TextEdit last, int start, string removed, string inserted)
        {
            // Перевод строки начинает новую правку, как в обычном редакторе.
            bool typing = removed.Length == 0 && inserted.Length == 1 && inserted != "\n" && inserted != "\r";
            if (typing && last.Removed.Length == 0 && start == last.Start + last.Inserted.Length
                && last.Inserted.IndexOf('\n') < 0 && last.Inserted.IndexOf('\r') < 0)
            {
                last.Inserted += inserted;
                return true;
            }
            bool erasing = inserted.Length == 0 && removed.Length > 0 && last.Inserted.Length == 0;
            if (erasing && start + removed.Length == last.Start)
            {
                last.Start = start; // Backspace подряд
                last.Removed = removed + last.Removed;
                return true;
            }
            if (erasing && start == last.Start)
            {
                last.Removed += removed; // Delete подряд
                return true;
            }
            return false;
        }

        /// <summary>Правка, которую надо откатить, или null.</summary>
        internal TextEdit Undo()
        {
            if (undo.Count == 0)
            {
                return null;
            }
            TextEdit edit = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            edit.Time = DateTime.MinValue; // после отмены следующая правка не склеивается с этой
            redo.Add(edit);
            return edit;
        }

        /// <summary>Правка, которую надо повторить, или null.</summary>
        internal TextEdit Redo()
        {
            if (redo.Count == 0)
            {
                return null;
            }
            TextEdit edit = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            edit.Time = DateTime.MinValue;
            undo.Add(edit);
            return edit;
        }
    }
}
