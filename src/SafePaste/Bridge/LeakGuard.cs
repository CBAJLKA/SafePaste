using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SafePaste.Detecting;

namespace SafePaste.Bridge
{
    public static class LeakGuard
    {
        public static string Check(string text, IList<LabelEntry> entries, out string warning)
        {
            warning = null;
            text = Rehide(text, entries, ref warning);
            foreach (LabelEntry entry in entries)
            {
                if (entry.Value == null) continue;
                string value = entry.Value;
                if (value.Length >= 8)
                {
                    string replaced = Regex.Replace(text, Regex.Escape(value), entry.Placeholder,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (replaced != text) { text = replaced; warning = "Повторно скрыто " + entry.Placeholder; }
                }
                if (value.Length >= 4)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(value);
                    string hex = BitConverter.ToString(bytes).Replace("-", string.Empty);
                    string[] encoded = { Convert.ToBase64String(bytes), Convert.ToBase64String(Encoding.Unicode.GetBytes(value)), hex, hex.ToLowerInvariant() };
                    foreach (string form in encoded)
                    {
                        if (form.Length < 8) continue;
                        string replaced = text.Replace(form, "[ENCODED:" + entry.Placeholder.Trim('[', ']') + "]");
                        if (replaced != text) { text = replaced; warning = "Обнаружена кодировка " + entry.Placeholder; }
                    }
                }
                string compactValue = Compact(value);
                if (compactValue.Length < 8) continue;
                List<int> offsets = new List<int>();
                StringBuilder compactText = new StringBuilder();
                for (int i = 0; i < text.Length; i++)
                {
                    if (char.IsLetterOrDigit(text[i])) { offsets.Add(i); compactText.Append(char.ToLowerInvariant(text[i])); }
                }
                int from = 0;
                while (true)
                {
                    int index = compactText.ToString().IndexOf(compactValue, from, StringComparison.Ordinal);
                    if (index < 0) break;
                    int start = offsets[index], end = offsets[index + compactValue.Length - 1] + 1;
                    string span = text.Substring(start, end - start);
                    if (span.Length > value.Length && span.IndexOf('[') < 0 && span.IndexOf(']') < 0)
                    {
                        text = text.Substring(0, start) + "[SPLIT:" + entry.Placeholder.Trim('[', ']') + "]" + text.Substring(end);
                        warning = "Обнаружено разделённое значение " + entry.Placeholder;
                        break;
                    }
                    from = index + 1;
                }
            }
            return text;
        }

        /// <summary>
        /// Значения из хранилища, которые пропустил детектор, скрываются повторно с границами слова:
        /// «srv-web» заменяется, а «srv-web01» и «xsrv-web» остаются. Без границ короткое значение
        /// заменялось бы внутри других слов, поэтому без них ниже ищутся только значения от 8 символов.
        /// Обычные слова («test», «Public») не ищутся, метки в тексте не трогаются.
        /// </summary>
        private static string Rehide(string text, IList<LabelEntry> entries, ref string warning)
        {
            ValueMatcher matcher = new ValueMatcher();
            List<LabelEntry> found = new List<LabelEntry>();
            foreach (LabelEntry entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Value) || entry.Value.Length < (entry.Secret ? 4 : 3))
                {
                    continue;
                }
                if (!entry.Secret && ContextNames.IsPlainWord(entry.Value))
                {
                    continue;
                }
                if (matcher.Add(entry.Value) >= 0)
                {
                    found.Add(entry);
                }
            }
            if (found.Count == 0)
            {
                return text;
            }
            List<ValueMatch> matches = matcher.Find(text);
            if (matches.Count == 0)
            {
                return text;
            }
            List<Match> labels = new List<Match>();
            foreach (Match label in Labels.Placeholder.Matches(text))
            {
                labels.Add(label);
            }
            StringBuilder result = new StringBuilder(text.Length);
            int position = 0;
            foreach (ValueMatch match in matches)
            {
                if (InsideLabel(labels, match.Start, match.Start + match.Length))
                {
                    continue;
                }
                string placeholder = found[match.Index].Placeholder;
                result.Append(text, position, match.Start - position);
                result.Append(placeholder);
                position = match.Start + match.Length;
                warning = "Повторно скрыто " + placeholder;
            }
            result.Append(text, position, text.Length - position);
            return result.ToString();
        }

        private static bool InsideLabel(List<Match> labels, int start, int end)
        {
            foreach (Match label in labels)
            {
                if (start < label.Index + label.Length && label.Index < end)
                {
                    return true;
                }
            }
            return false;
        }

        private static string Compact(string value)
        {
            StringBuilder result = new StringBuilder();
            foreach (char symbol in value) if (char.IsLetterOrDigit(symbol)) result.Append(char.ToLowerInvariant(symbol));
            return result.ToString();
        }
    }
}
