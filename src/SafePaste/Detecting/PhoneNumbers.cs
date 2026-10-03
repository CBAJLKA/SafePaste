using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Номера телефонов. Цифры с разделителями собираются в группы, затем проверяется вид номера:
    /// +7 916 123-45-67, 8 (495) 123-45-67, 89161234567, +44 20 7946 0958, (495) 123-45-67,
    /// 916 123-45-67, 202-555-0147. После подсказки («тел.», «Телефон:», «phone=», "mobile": ...)
    /// подходит и короткий местный номер: «тел. 22-33-44».
    /// Даты, время, IP-адреса, версии и длинные числа сюда не попадают: у них другие группы цифр.
    /// </summary>
    internal static class PhoneNumbers
    {
        private const int Priority = 67;
        private const int ContextPriority = 68;
        // Больше пятнадцати цифр в номере не бывает (E.164).
        private const int MaxDigits = 15;
        private const int MinContextDigits = 5;
        private const int MinWeakContextDigits = 7;
        private const int ContextWindow = 64;
        private const int MaxListGap = 8;
        private const string ContextSource = "Контекст";

        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(750);

        // Подсказка сразу перед номером. Ищется справа налево, поэтому привязана к концу строки.
        private static readonly Regex ContextRegex = new Regex(
            @"(?:^|[^\p{L}\p{N}_])(?<word>тел(?:ефон\p{L}*)?|тлф|моб(?:ильн\p{L}*)?|сотов\p{L}*|факс\p{L}*|" +
            @"контакт\p{L}*|(?:пере|по)?звон\p{L}*|в[ао]тсап\p{L}*|вайбер\p{L}*|whats[ ]?app|viber|msisdn|" +
            @"contact\p{L}*|[\p{L}\p{N}_]*(?:phone|mobile|fax)[\p{L}\p{N}_]*|tel(?:_?(?:no|num|number))?|mob|cell|call)" +
            @"\.?(?:[ \t]+(?:по|на|с|со|от|мне|нам|me|us|to|at|on|номер\p{L}*|number|no\.?|№)){0,3}" +
            @"[ \t:=>""'(\[#№\u2013-]*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft, Timeout);

        // Между номерами в списке: «8 916 123-45-67, 123-45-67», «... или ...».
        private static readonly Regex ListGapRegex = new Regex(
            @"\A[ \t]*(?:[,;/]|или|and|or|и)?[ \t]*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

        /// <summary>Группа цифр номера и разделитель перед ней.</summary>
        private struct DigitGroup
        {
            internal int End;
            internal int Digits;
            /// <summary>Цифр от начала номера до конца этой группы.</summary>
            internal int Total;
            /// <summary>Код в скобках: (495).</summary>
            internal bool Paren;
            /// <summary>Перед группой «-» или «.», такие группы одно целое.</summary>
            internal bool Hard;
            /// <summary>Перед группой только пробел: здесь номер может закончиться.</summary>
            internal bool Spaced;
            internal char Separator;
        }

        internal static List<Detection> Find(string text)
        {
            List<Detection> found = new List<Detection>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }
            List<DigitGroup> groups = new List<DigitGroup>();
            StringBuilder digits = new StringBuilder(32);
            int lastEnd = -1;
            int index = 0;
            while (index < text.Length)
            {
                if (!StartsHere(text, index))
                {
                    index++;
                    continue;
                }
                int runEnd = Parse(text, index, groups, digits);
                if (runEnd < 0)
                {
                    index++;
                    continue;
                }
                bool listed = lastEnd >= 0 && IsListGap(text, lastEnd, index);
                Detection detection = Pick(text, index, groups, digits.ToString(), listed);
                if (detection == null)
                {
                    // Число целиком не номер: его хвост тоже не проверяется.
                    index = runEnd;
                    continue;
                }
                found.Add(detection);
                // Список продолжает только надёжный номер, иначе догадка сделала бы надёжным соседа.
                lastEnd = detection.Confidence == Confidence.High ? detection.End : -1;
                index = detection.End;
            }
            return found;
        }

        /// <summary>Выбирает самое длинное начало числа, похожее на номер.</summary>
        private static Detection Pick(string text, int start, List<DigitGroup> groups, string digits, bool listed)
        {
            bool plus = text[start] == '+';
            // Пробел после групп, связанных «-» или «.», заканчивает номер: «123-45-67 2 шт».
            int limit = groups.Count;
            bool hard = false;
            for (int index = 1; index < groups.Count; index++)
            {
                if (groups[index].Hard)
                {
                    hard = true;
                }
                else if (hard && groups[index].Spaced)
                {
                    limit = index;
                    break;
                }
            }

            for (int count = limit; count >= 1; count--)
            {
                Confidence confidence;
                if (!CanEnd(text, groups, count) || !TryShape(groups, count, plus, digits, out confidence))
                {
                    continue;
                }
                Detection detection = Make(text, start, groups[count - 1].End, confidence);
                if (confidence != Confidence.High)
                {
                    bool weak;
                    string reason = FindContext(text, start, listed, out weak);
                    if (reason != null)
                    {
                        MarkContext(detection, reason);
                    }
                }
                return detection;
            }

            // Без узнаваемого вида номер принимается только после подсказки.
            if (groups[limit - 1].Total < MinContextDigits)
            {
                return null;
            }
            bool weakContext;
            string clue = FindContext(text, start, listed, out weakContext);
            if (clue == null)
            {
                return null;
            }
            int min = weakContext ? MinWeakContextDigits : MinContextDigits;
            for (int count = limit; count >= 1; count--)
            {
                int total = groups[count - 1].Total;
                if (total < min || total > MaxDigits || (plus && digits[0] == '0') || !CanEnd(text, groups, count))
                {
                    continue;
                }
                Detection detection = Make(text, start, groups[count - 1].End, Confidence.High);
                MarkContext(detection, clue);
                return detection;
            }
            return null;
        }

        /// <summary>
        /// Вид номера без подсказок вокруг. Российский номер с кодом страны и международный
        /// с «+» надёжны; номер без кода страны или без разделителей только вероятен.
        /// </summary>
        private static bool TryShape(List<DigitGroup> groups, int count, bool plus, string digits, out Confidence confidence)
        {
            confidence = Confidence.Medium;
            int total = groups[count - 1].Total;
            if (total > MaxDigits)
            {
                return false;
            }
            int paren = -1;
            for (int index = 0; index < count; index++)
            {
                if (groups[index].Paren)
                {
                    if (paren >= 0)
                    {
                        return false;
                    }
                    paren = index;
                }
            }
            // «+2026-09-28 10:00» из diff: дата, а не номер.
            if (count >= 3 && Sizes(groups, 4, 2, 2) && groups[1].Hard && groups[2].Hard)
            {
                return false;
            }
            // Через точки пишут и версии: такой номер только вероятен.
            bool dotted = false;
            for (int index = 1; index < count; index++)
            {
                dotted |= groups[index].Separator == '.';
            }
            char first = digits[0];
            char second = total > 1 ? digits[1] : '0';

            if (plus)
            {
                if (total < 8 || first == '0')
                {
                    return false;
                }
                // +7 916 123-45-67; +1 202 555 0147, +44 20 7946 0958, +14155552671.
                // «+10 20 30 40 50» из diff таблицы остаётся догадкой.
                if (!dotted && ((first == '7' && total == 11 && second >= '3')
                    || (total >= 10 && (count == 1 || (groups[0].Digits <= 3 && !Uniform(groups, count))))))
                {
                    confidence = Confidence.High;
                }
                return true;
            }

            // 8 (916) 123-45-67, 8-800-555-35-35, 7 495 1234567, 89161234567.
            if ((first == '8' || first == '7') && total == 11 && second >= '3')
            {
                if (count == 1)
                {
                    return true;
                }
                if (groups[0].Digits != 1 || (paren >= 0 && paren != 1)
                    || groups[1].Digits < 3 || groups[1].Digits > 5 || MinDigits(groups, 2, count) < 2)
                {
                    return false;
                }
                if (count >= 3 && !dotted)
                {
                    confidence = Confidence.High;
                }
                return true;
            }

            if (first < '2' || total != 10)
            {
                return false;
            }
            // (495) 123-45-67, (8452) 12-34-56, (202) 555-0147.
            if (paren == 0)
            {
                return count >= 2 && groups[0].Digits >= 3 && groups[0].Digits <= 5 && MinDigits(groups, 1, count) >= 2;
            }
            if (paren > 0)
            {
                return false;
            }
            // 916 123-45-67: так пишут мобильный без восьмёрки.
            if (count == 4 && first >= '3' && Sizes(groups, 3, 3, 2, 2) && groups[2].Hard && groups[3].Hard)
            {
                return true;
            }
            // 202-555-0147, 202.555.0147: похоже на номер, но это может быть и артикул.
            if (count == 3 && Sizes(groups, 3, 3, 4) && groups[1].Hard && groups[2].Hard
                && groups[1].Separator == groups[2].Separator)
            {
                confidence = Confidence.Low;
                return true;
            }
            return false;
        }

        private static Detection Make(string text, int start, int end, Confidence confidence)
        {
            Detection detection = new Detection(start, end - start, text.Substring(start, end - start), "PHONE",
                confidence, Priority);
            // Слабая догадка видна в окне проверки, но сама не скрывается.
            detection.Enabled = confidence != Confidence.Low;
            return detection;
        }

        private static void MarkContext(Detection detection, string reason)
        {
            detection.Confidence = Confidence.High;
            detection.Priority = ContextPriority;
            detection.Enabled = true;
            detection.Source = ContextSource;
            detection.Reason = reason;
        }

        /// <summary>Подсказка перед номером или соседний номер в том же списке.</summary>
        private static string FindContext(string text, int start, bool listed, out bool weak)
        {
            weak = false;
            int from = Math.Max(0, start - ContextWindow);
            if (start > 0)
            {
                int newline = text.LastIndexOf('\n', start - 1, start - from);
                if (newline >= 0)
                {
                    from = newline + 1;
                }
            }
            if (WordBefore(text, from, start))
            {
                Match match;
                try
                {
                    match = ContextRegex.Match(text, from, start - from);
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new DetectionTimeoutException();
                }
                if (match.Success)
                {
                    Group found = match.Groups["word"];
                    string word = found.Value;
                    string lower = word.ToLowerInvariant();
                    weak = lower.StartsWith("контакт", StringComparison.Ordinal)
                        || lower.StartsWith("contact", StringComparison.Ordinal)
                        || lower == "cell" || lower == "mob" || lower == "call";
                    if (word.Length > 24)
                    {
                        word = word.Substring(0, 24) + "...";
                    }
                    else if (text[found.Index + found.Length] == '.')
                    {
                        word += ".";
                    }
                    return "после «" + word + "»";
                }
            }
            if (listed)
            {
                weak = true;
                return "рядом с другим номером";
            }
            return null;
        }

        /// <summary>
        /// Перед номером, за двоеточием, кавычкой или скобкой, стоит слово или «тел.».
        /// Без этого регулярка подсказки не запускается: в логе чисел много, а подсказок мало.
        /// </summary>
        private static bool WordBefore(string text, int from, int start)
        {
            int cursor = start - 1;
            while (cursor >= from && " \t:=>\"'([#№\u2013-".IndexOf(text[cursor]) >= 0)
            {
                cursor--;
            }
            return cursor >= from && (char.IsLetter(text[cursor]) || text[cursor] == '.');
        }

        private static bool IsListGap(string text, int from, int to)
        {
            if (to - from > MaxListGap || to < from)
            {
                return false;
            }
            try
            {
                return ListGapRegex.IsMatch(text.Substring(from, to - from));
            }
            catch (RegexMatchTimeoutException)
            {
                throw new DetectionTimeoutException();
            }
        }

        // ---------------------------------------------------------------- разбор числа

        /// <summary>
        /// Номер начинается с «+», «(» или цифры, но не внутри слова, числа, пути или другого номера.
        /// </summary>
        private static bool StartsHere(string text, int index)
        {
            char symbol = text[index];
            if (symbol == '+' || symbol == '(')
            {
                if (index + 1 >= text.Length || !(IsDigit(text[index + 1]) || (symbol == '+' && text[index + 1] == '(')))
                {
                    return false;
                }
            }
            else if (!IsDigit(symbol))
            {
                return false;
            }
            if (index == 0)
            {
                return true;
            }
            char before = text[index - 1];
            if (char.IsLetterOrDigit(before) || before == '_' || before == '+' || before == ')' || IsHyphen(before))
            {
                return false;
            }
            switch (before)
            {
                case '/':
                case '\\':
                case '#':
                case '@':
                case '$':
                case '%':
                case '&':
                case '*':
                case '^':
                case '~':
                    return false;
                case '.':
                case ',':
                    // «1.89161234567» и «3,5» это числа; «тел.89161234567» номер.
                    return index < 2 || !IsDigit(text[index - 2]);
                default:
                    return true;
            }
        }

        /// <summary>После номера нет буквы, цифры и продолжения вида «-12», «.5», «:80», «/2».</summary>
        private static bool CanEnd(string text, List<DigitGroup> groups, int count)
        {
            if (count < groups.Count && !groups[count].Spaced)
            {
                return false; // «(495)1234567» и «123-45-67» не делятся
            }
            int end = groups[count - 1].End;
            if (end >= text.Length)
            {
                return true;
            }
            char after = text[end];
            if (char.IsLetterOrDigit(after) || after == '_' || after == '@')
            {
                return false;
            }
            bool digitNext = end + 1 < text.Length && IsDigit(text[end + 1]);
            return !(digitNext && (after == '.' || after == ':' || after == '/' || after == '\\' || IsHyphen(after)));
        }

        /// <summary>
        /// Собирает группы цифр, начиная с позиции. Разделители: пробел, «-», «.», скобки вокруг кода.
        /// Возвращает конец последней группы или -1, если с этой позиции число не читается.
        /// </summary>
        private static int Parse(string text, int start, List<DigitGroup> groups, StringBuilder digits)
        {
            groups.Clear();
            digits.Length = 0;
            int position = start;
            if (text[position] == '+')
            {
                position++;
            }
            bool hard = false;
            bool spaced = false;
            char separator = '\0';
            while (true)
            {
                bool paren = position < text.Length && text[position] == '(' && groups.Count <= 1;
                int digitsStart = paren ? position + 1 : position;
                int cursor = digitsStart;
                while (cursor < text.Length && IsDigit(text[cursor]))
                {
                    cursor++;
                }
                if (cursor == digitsStart || (paren && (cursor >= text.Length || text[cursor] != ')')))
                {
                    break;
                }
                digits.Append(text, digitsStart, cursor - digitsStart);
                DigitGroup group = new DigitGroup();
                group.Digits = cursor - digitsStart;
                group.Total = digits.Length;
                group.Paren = paren;
                group.Hard = hard;
                group.Spaced = spaced;
                group.Separator = separator;
                group.End = paren ? cursor + 1 : cursor;
                groups.Add(group);
                position = group.End;

                // Разделитель: до двух пробелов, один «-» или «.», снова до двух пробелов.
                int spaces = SkipSpaces(text, ref position);
                hard = position < text.Length && (text[position] == '.' || IsHyphen(text[position]));
                separator = hard ? text[position] : (spaces > 0 ? ' ' : '\0');
                if (hard)
                {
                    position++;
                    SkipSpaces(text, ref position);
                }
                spaced = !hard && spaces > 0;
                bool more = position < text.Length
                    && (IsDigit(text[position]) || (text[position] == '(' && groups.Count <= 1));
                if (!more)
                {
                    break;
                }
            }
            return groups.Count == 0 ? -1 : groups[groups.Count - 1].End;
        }

        private static int SkipSpaces(string text, ref int position)
        {
            int count = 0;
            while (count < 2 && position < text.Length && IsSpace(text[position]))
            {
                position++;
                count++;
            }
            return count;
        }

        private static bool Uniform(List<DigitGroup> groups, int count)
        {
            if (count < 3)
            {
                return false;
            }
            for (int index = 1; index < count; index++)
            {
                if (groups[index].Digits != groups[0].Digits)
                {
                    return false;
                }
            }
            return true;
        }

        private static int MinDigits(List<DigitGroup> groups, int from, int count)
        {
            int min = int.MaxValue;
            for (int index = from; index < count; index++)
            {
                min = Math.Min(min, groups[index].Digits);
            }
            return min;
        }

        private static bool Sizes(List<DigitGroup> groups, params int[] sizes)
        {
            for (int index = 0; index < sizes.Length; index++)
            {
                if (groups[index].Digits != sizes[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsDigit(char symbol)
        {
            return symbol >= '0' && symbol <= '9';
        }

        private static bool IsSpace(char symbol)
        {
            return symbol == ' ' || symbol == '\u00A0' || symbol == '\u202F' || symbol == '\u2009';
        }

        private static bool IsHyphen(char symbol)
        {
            return symbol == '-' || symbol == '\u2011' || symbol == '\u2012' || symbol == '\u2013';
        }
    }
}
