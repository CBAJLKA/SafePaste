using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SafePaste.Storage;

namespace SafePaste.Detecting
{
    public interface IPlaceholderNumbers
    {
        int GetReservedIndex(string type, string value);
        bool IsIndexReserved(string type, int index);
    }

    public sealed class ReplacementResult
    {
        public string Text;
        public int HiddenCount;
        public int UniqueCount;
        public readonly List<Detection> Applied;

        public ReplacementResult(string text, int hiddenCount, int uniqueCount, List<Detection> applied)
        {
            Text = text;
            HiddenCount = hiddenCount;
            UniqueCount = uniqueCount;
            Applied = applied;
        }
    }

    /// <summary>
    /// Заменяет находки на заполнители. Одинаковые значения получают один и тот же номер,
    /// а закреплённые в базе всегда свой: 10.44.7.219 остаётся [IP_7] из вставки в вставку.
    /// </summary>
    public static class Replacer
    {
        public static ReplacementResult Apply(string text, IList<Detection> detections)
        {
            return Apply(text, detections, null);
        }

        public static ReplacementResult Apply(string text, IList<Detection> detections, IPlaceholderNumbers database)
        {
            return Apply(text, detections, database, false);
        }

        public static ReplacementResult Apply(string text, IList<Detection> detections, IPlaceholderNumbers database,
            bool keepLineBreaks)
        {
            if (text == null)
            {
                text = string.Empty;
            }

            List<Detection> active = new List<Detection>();
            foreach (Detection detection in detections)
            {
                detection.Placeholder = string.Empty;
                if (detection.Enabled)
                {
                    active.Add(detection);
                }
            }
            active.Sort(delegate(Detection left, Detection right) { return left.Start.CompareTo(right.Start); });

            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, HashSet<int>> used = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            StringBuilder builder = new StringBuilder(text.Length + 64);
            int position = 0;

            foreach (Detection detection in active)
            {
                if (detection.Start < position)
                {
                    // Пересечения разбираются заранее; если оно осталось, часть значения утекла бы в текст.
                    throw new InvalidOperationException("Внутренняя ошибка: замены пересекаются. Вставка отменена.");
                }
                string identity = detection.Type + "" + detection.Value.ToLowerInvariant();
                string placeholder;
                if (!map.TryGetValue(identity, out placeholder))
                {
                    int number = NextNumber(detection.Type, detection.Value, used, database);
                    placeholder = "[" + detection.Type + "_" + number.ToString(CultureInfo.InvariantCulture) + "]";
                    map.Add(identity, placeholder);
                }
                detection.Placeholder = placeholder;
                builder.Append(text, position, detection.Start - position);
                builder.Append(placeholder);
                if (keepLineBreaks)
                {
                    for (int index = detection.Start; index < detection.End; index++)
                    {
                        if (text[index] == '\r' || text[index] == '\n') builder.Append(text[index]);
                    }
                }
                position = detection.End;
            }
            builder.Append(text, position, text.Length - position);
            return new ReplacementResult(builder.ToString(), active.Count, map.Count, active);
        }

        /// <summary>
        /// Номер для нового значения: закреплённый в базе либо ближайший свободный.
        /// Чужие закреплённые номера пропускаются, чтобы [IP_7] всегда значил одно и то же.
        /// </summary>
        private static int NextNumber(string type, string value, Dictionary<string, HashSet<int>> used,
            IPlaceholderNumbers database)
        {
            HashSet<int> taken;
            if (!used.TryGetValue(type, out taken))
            {
                taken = new HashSet<int>();
                used.Add(type, taken);
            }
            int reserved = database == null ? 0 : database.GetReservedIndex(type, value);
            if (reserved > 0)
            {
                taken.Add(reserved);
                return reserved;
            }
            int number = 1;
            while (taken.Contains(number) || (database != null && database.IsIndexReserved(type, number)))
            {
                number++;
            }
            taken.Add(number);
            return number;
        }

        /// <summary>Закрывает секреты точками для показа на экране, сохраняя длину и переводы строк.</summary>
        public static string MaskSecrets(string text, IList<Detection> detections)
        {
            char[] buffer = null;
            foreach (Detection detection in detections)
            {
                if (!detection.Locked)
                {
                    continue;
                }
                if (buffer == null)
                {
                    buffer = text.ToCharArray();
                }
                for (int index = detection.Start; index < detection.End && index < buffer.Length; index++)
                {
                    if (buffer[index] != '\r' && buffer[index] != '\n')
                    {
                        buffer[index] = '•';
                    }
                }
            }
            return buffer == null ? text : new string(buffer);
        }
    }
}
