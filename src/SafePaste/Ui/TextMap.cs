using System;
using System.Collections.Generic;
using System.Text;

namespace SafePaste.Ui
{
    /// <summary>
    /// Текст в окне отличается от буфера обмена: перевод строки всегда один символ,
    /// табуляция раскрыта в пробелы, управляющие символы видны точкой.
    /// Класс переводит позиции между исходным текстом и показанным.
    /// </summary>
    internal sealed class TextMap
    {
        internal const int TabSize = 4;

        internal readonly string Original;
        internal readonly string Display;
        private readonly int[] displayToOriginal;
        private readonly int[] originalToDisplay;

        internal TextMap(string original)
        {
            Original = original ?? string.Empty;
            StringBuilder builder = new StringBuilder(Original.Length);
            List<int> toOriginal = new List<int>(Original.Length + 1);
            int[] toDisplay = new int[Original.Length + 1];
            int column = 0;
            for (int index = 0; index < Original.Length; index++)
            {
                char symbol = Original[index];
                toDisplay[index] = builder.Length;
                if (symbol == '\r' && index + 1 < Original.Length && Original[index + 1] == '\n')
                {
                    continue; // \r\n показывается одним переводом строки
                }
                if (symbol == '\r' || symbol == '\n')
                {
                    builder.Append('\n');
                    toOriginal.Add(index);
                    column = 0;
                    continue;
                }
                if (symbol == '\t')
                {
                    int spaces = TabSize - column % TabSize;
                    for (int step = 0; step < spaces; step++)
                    {
                        builder.Append(' ');
                        toOriginal.Add(index);
                    }
                    column += spaces;
                    continue;
                }
                builder.Append(symbol < ' ' || symbol == '\u007F' ? '\u00B7' : symbol);
                toOriginal.Add(index);
                column++;
            }
            toDisplay[Original.Length] = builder.Length;
            toOriginal.Add(Original.Length);
            Display = builder.ToString();
            displayToOriginal = toOriginal.ToArray();
            originalToDisplay = toDisplay;
        }

        internal int ToOriginal(int displayIndex)
        {
            if (displayIndex <= 0)
            {
                return 0;
            }
            if (displayIndex >= displayToOriginal.Length)
            {
                return Original.Length;
            }
            return displayToOriginal[displayIndex];
        }

        internal int ToDisplay(int originalIndex)
        {
            if (originalIndex <= 0)
            {
                return 0;
            }
            if (originalIndex >= originalToDisplay.Length)
            {
                return Display.Length;
            }
            return originalToDisplay[originalIndex];
        }

        /// <summary>
        /// Позиция курсора между символами. Курсор в конце строки стоит перед всей парой \r\n,
        /// поэтому вставка её не разрывает. Внутри раскрытой табуляции курсор относится к месту после неё.
        /// </summary>
        internal int CaretToOriginal(int displayIndex)
        {
            if (displayIndex <= 0)
            {
                return 0;
            }
            if (displayIndex >= Display.Length)
            {
                return Original.Length;
            }
            int low = 0;
            int high = originalToDisplay.Length - 1;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (originalToDisplay[middle] < displayIndex)
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

        /// <summary>Начало удаляемого фрагмента: если позиция внутри табуляции, удаляется вся табуляция.</summary>
        internal int RangeStartToOriginal(int displayIndex)
        {
            int found = CaretToOriginal(displayIndex);
            return found > 0 && ToDisplay(found) > displayIndex ? found - 1 : found;
        }

        /// <summary>Конец выделения: позиция сразу после последнего выделенного символа исходного текста.</summary>
        internal int EndToOriginal(int displayStart, int displayEnd)
        {
            if (displayEnd <= displayStart)
            {
                return ToOriginal(displayStart);
            }
            int last = ToOriginal(displayEnd - 1);
            return Math.Min(Original.Length, last + 1);
        }
    }
}
