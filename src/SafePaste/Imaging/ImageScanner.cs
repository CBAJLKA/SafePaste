using System;
using System.Collections.Generic;
using System.Drawing;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Imaging
{
    internal enum AreaKind
    {
        /// <summary>Похоже на текст, но распознавание его не прочитало.</summary>
        Unread,
        /// <summary>Закрыто рамкой вручную.</summary>
        Manual,
        /// <summary>Непрочитанное после слова «пароль»: закрывается всегда.</summary>
        Tail,
        /// <summary>Контрольное чтение нашло здесь скрытое значение: закрывается всегда.</summary>
        Leak
    }

    /// <summary>Место на картинке без распознанного текста: закрывается целиком, без метки значения.</summary>
    internal sealed class ImageArea
    {
        internal RectangleF Bounds;
        internal readonly AreaKind Kind;
        internal bool Enabled;
        internal bool Quiet;

        internal ImageArea(RectangleF bounds, AreaKind kind, bool enabled)
        {
            Bounds = bounds;
            Kind = kind;
            Enabled = enabled;
        }

        /// <summary>Такое место нельзя оставить открытым.</summary>
        internal bool Locked
        {
            get { return Kind == AreaKind.Tail || Kind == AreaKind.Leak; }
        }

        internal bool Hidden
        {
            get { return Enabled || Locked; }
        }
    }

    /// <summary>
    /// Находки на прочитанной картинке. Сначала обычный детектор по распознанному тексту, затем то, что
    /// нужно именно картинке: хвост строки после слова «пароль» (значение распознаётся хуже всего) и
    /// значения из правил, прочитанные с ошибкой в знак. Здесь же находки переводятся в прямоугольники
    /// и проверяется готовая картинка.
    /// </summary>
    internal static class ImageScanner
    {
        /// <summary>Происхождение хвоста строки после слова «пароль»: его последний прямоугольник идёт до конца строки.</summary>
        internal const string TailSource = "Картинка";
        internal const string MonitoringSource = "Мониторинг на картинке";

        private const int MaxSimilarValues = 20000;

        private static readonly string[] SecretWords =
        {
            "password", "passwd", "pwd", "пароль", "parol", "secret", "token", "apikey", "api_key", "api-key",
            "accesskey", "access_key", "secretkey", "secret_key", "privatekey", "private_key", "authorization",
            "bearer", "токен", "секрет"
        };

        /// <summary>
        /// Находки на картинке для окна проверки: галочки по режиму, пересечения разобраны.
        /// tails: места после слова «пароль», где текста не прочитано, но строка продолжается.
        /// </summary>
        internal static List<Detection> Detect(ImageReading reading, SafePasteDatabase database, ControlMode mode,
            List<ImageArea> tails)
        {
            return Detect(reading, database, mode, tails, true);
        }

        internal static List<Detection> Detect(ImageReading reading, SafePasteDatabase database, ControlMode mode,
            List<ImageArea> tails, bool smartSecrets)
        {
            List<Detection> found = Detector.Scan(reading.Text, database, mode, false, smartSecrets);
            List<Detection> added = new List<Detection>();
            added.AddRange(SecretTails(reading, tails));
            added.AddRange(SimilarValues(reading, database, found));
            added.AddRange(MonitoringNames(reading, database, found));
            // Хвост после слова пароля, отмеченный «Распознано ошибочно», тоже остаётся открытым.
            added.RemoveAll(delegate(Detection detection)
            {
                return detection.Locked && database != null && database.IsNotSecret(detection.Value);
            });
            foreach (Detection detection in added)
            {
                detection.DefaultEnabled = true;
                detection.Enabled = ControlModes.HiddenInReview(detection, mode);
            }
            found.AddRange(added);
            return OverlapResolver.Resolve(found);
        }

        /// <summary>Имена узлов в уведомлениях мониторинга, которые обычный текстовый контекст не покрывает.</summary>
        private sealed class MonitoringValue
        {
            internal string Name;
            internal string Type;
            internal Confidence Confidence;
            internal string Reason;

            internal MonitoringValue(string name, string type, Confidence confidence, string reason)
            {
                Name = name;
                Type = type;
                Confidence = confidence;
                Reason = reason;
            }
        }

        internal static List<Detection> MonitoringNames(ImageReading reading, SafePasteDatabase database, List<Detection> found)
        {
            List<MonitoringValue> values = new List<MonitoringValue>();
            foreach (OcrLine line in reading.Lines)
            {
                for (int i = 0; i + 3 < line.Words.Count; i++)
                {
                    string label = WordCore(reading, line.Words[i]);
                    if (!string.Equals(label, "Linux", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(label, "Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string separator = WordCore(reading, line.Words[i + 2]);
                    if (!WordEndsWith(reading, line.Words[i], ':')
                        || (separator != "—" && separator != "–" && separator != "-")
                        || !HasMonitoringWord(reading, line))
                    {
                        continue;
                    }
                    string name = WordCore(reading, line.Words[i + 1]);
                    if (HostName(name))
                    {
                        values.Add(new MonitoringValue(name, "HOST", Confidence.High,
                            "имя узла после «" + label + ":» в уведомлении мониторинга"));
                    }
                }
                if (line.Words.Count == 0)
                {
                    continue;
                }
                OcrWord first = line.Words[0];
                string environment = WordCore(reading, first);
                int hyphen = environment.LastIndexOf('-');
                if (!WordEndsWith(reading, first, ':') || hyphen < 5 || !HostName(environment)
                    || !EnvironmentSuffix(environment.Substring(hyphen + 1)) || !HasHostMetrics(reading, line))
                {
                    continue;
                }
                values.Add(new MonitoringValue(environment, "HOST", Confidence.High,
                    "имя узла перед сообщением о метриках хоста"));
                string project = environment.Substring(0, hyphen);
                if (!ContextNames.IsPlainWord(project))
                {
                    values.Add(new MonitoringValue(project, "TEXT", Confidence.Medium,
                        "название из имени узла «" + environment + "»"));
                }
            }
            List<Detection> result = new List<Detection>();
            foreach (OcrWord word in reading.Words)
            {
                string token = WordCore(reading, word);
                if (token.Length == 0)
                {
                    continue;
                }
                string key = Canonical(token);
                foreach (MonitoringValue value in values)
                {
                    if (key != Canonical(value.Name) || (database != null && database.IsAllowed(value.Name))
                        || Covered(found, word.Start, word.Start + token.Length)
                        || Covered(result, word.Start, word.Start + token.Length))
                    {
                        continue;
                    }
                    Detection detection = new Detection(word.Start, token.Length, value.Name, value.Type,
                        value.Confidence, value.Type == "HOST" ? 69 : 66);
                    detection.Source = MonitoringSource;
                    detection.Reason = value.Reason;
                    result.Add(detection);
                    break;
                }
            }
            return result;
        }

        private static string WordCore(ImageReading reading, OcrWord word)
        {
            int length = word.Text.Length;
            while (length > 0 && ":.,;…".IndexOf(reading.Text[word.Start + length - 1]) >= 0)
            {
                length--;
            }
            return reading.Text.Substring(word.Start, length);
        }

        private static bool WordEndsWith(ImageReading reading, OcrWord word, char symbol)
        {
            return word.Text.Length > 0 && reading.Text[word.End - 1] == symbol;
        }

        private static bool HostName(string name)
        {
            if (name.Length < 4 || name.Length > 63 || !IsAsciiLetter(name[0]))
            {
                return false;
            }
            foreach (char symbol in name)
            {
                if (!IsAsciiLetter(symbol) && (symbol < '0' || symbol > '9') && symbol != '-' && symbol != '_')
                {
                    return false;
                }
            }
            return !ContextNames.IsPlainWord(name);
        }

        private static bool IsAsciiLetter(char symbol)
        {
            return symbol >= 'a' && symbol <= 'z' || symbol >= 'A' && symbol <= 'Z';
        }

        private static bool EnvironmentSuffix(string suffix)
        {
            return string.Equals(suffix, "prod", StringComparison.OrdinalIgnoreCase)
                || string.Equals(suffix, "stage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(suffix, "dev", StringComparison.OrdinalIgnoreCase)
                || string.Equals(suffix, "test", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasMonitoringWord(ImageReading reading, OcrLine line)
        {
            foreach (OcrWord word in line.Words)
            {
                string value = WordCore(reading, word);
                if (value.StartsWith("мониторинг", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("monitoring", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasHostMetrics(ImageReading reading, OcrLine line)
        {
            foreach (OcrWord word in line.Words)
            {
                string value = WordCore(reading, word);
                if (value.StartsWith("хост", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("host", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- хвост после слова «пароль»

        /// <summary>
        /// После слова «пароль», «token» и подобных с «:» или «=» закрывается всё до конца строки:
        /// само слово распознаётся хорошо, а значение часто нет. Если после слова ничего не прочитано,
        /// но чернила строки идут дальше, закрывается это место.
        /// </summary>
        internal static List<Detection> SecretTails(ImageReading reading, List<ImageArea> tails)
        {
            List<Detection> result = new List<Detection>();
            string text = reading.Text;
            for (int i = 0; i < reading.Words.Count; i++)
            {
                OcrWord word = reading.Words[i];
                int keywordEnd;
                if (!EndsWithSecretWord(text, word, out keywordEnd))
                {
                    continue;
                }
                int valueStart = keywordEnd;
                int lineEnd = LineEnd(reading, word.Line);
                bool separated = false;
                while (valueStart < lineEnd && (text[valueStart] == ' ' || text[valueStart] == ':' || text[valueStart] == '='))
                {
                    separated |= text[valueStart] != ' ';
                    valueStart++;
                }
                if (!separated && !IsBearer(text, word, keywordEnd))
                {
                    continue;
                }
                if (valueStart < lineEnd)
                {
                    Detection tail = new Detection(valueStart, lineEnd - valueStart, text.Substring(valueStart, lineEnd - valueStart),
                        "SECRET", Confidence.High, 99);
                    tail.Locked = true;
                    tail.Source = TailSource;
                    tail.Reason = "после слова «" + text.Substring(word.Start, keywordEnd - word.Start).TrimEnd(':', '=')
                        + "» закрыто до конца строки: значения на картинке распознаются с ошибками";
                    result.Add(tail);
                    continue;
                }
                RectangleF band = reading.LineBand(word.Line);
                float from = word.Bounds.Right + 1f;
                float right = word.Line < reading.LineInkRight.Length ? reading.LineInkRight[word.Line] : from;
                if (tails != null && right > from + band.Height * 0.5f)
                {
                    float pad = Math.Max(1.5f, band.Height * 0.18f);
                    tails.Add(new ImageArea(RectangleF.FromLTRB(from, band.Top - pad, right + pad, band.Bottom + pad), AreaKind.Tail, true));
                }
            }
            return result;
        }

        /// <summary>Слово кончается словом-подсказкой секрета: «password:», «DB_PASSWORD=», «Пароль».</summary>
        private static bool EndsWithSecretWord(string text, OcrWord word, out int keywordEnd)
        {
            keywordEnd = word.End;
            int start = word.Start;
            int end = word.End;
            // «password=Qwerty» одним словом: подсказка кончается на первом «=» или «:».
            for (int i = start; i < end; i++)
            {
                if (text[i] == '=' || text[i] == ':')
                {
                    end = i;
                    break;
                }
            }
            while (end > start && !char.IsLetterOrDigit(text[end - 1]))
            {
                end--;
            }
            int from = end;
            while (from > start && (char.IsLetterOrDigit(text[from - 1])))
            {
                from--;
            }
            if (end <= from)
            {
                return false;
            }
            string last = text.Substring(from, end - from).ToLowerInvariant();
            // Составное имя: DB_PASSWORD, api-token, mysql.pwd.
            int split = start;
            string whole = text.Substring(split, end - split).ToLowerInvariant();
            bool hit = IsSecretWord(last) || IsSecretWord(whole);
            if (!hit)
            {
                return false;
            }
            keywordEnd = end;
            return true;
        }

        /// <summary>
        /// Сравнение в приведённом виде (Canonical): распознавание путает латиницу с кириллицей («пapоль»),
        /// а в длинных словах ещё и один знак («passwcrd»).
        /// </summary>
        private static bool IsSecretWord(string candidate)
        {
            string value = Canonical(candidate);
            foreach (string word in SecretWords)
            {
                string key = Canonical(word);
                if (value == key || value.EndsWith("_" + key, StringComparison.Ordinal)
                    || value.EndsWith("-" + key, StringComparison.Ordinal) || value.EndsWith("." + key, StringComparison.Ordinal))
                {
                    return true;
                }
                if (key.Length >= 6 && value.Length >= 6 && Distance(value, key, 1) <= 1)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsBearer(string text, OcrWord word, int keywordEnd)
        {
            string value = text.Substring(word.Start, keywordEnd - word.Start).ToLowerInvariant();
            return value == "bearer" || value == "basic";
        }

        private static int LineEnd(ImageReading reading, int line)
        {
            int end = reading.Text.Length;
            OcrLine target = reading.Lines[line];
            OcrWord last = target.Words[target.Words.Count - 1];
            return Math.Min(end, last.End);
        }

        // ---------------------------------------------------------------- значения из правил с ошибкой

        /// <summary>
        /// Заученное и запомненное, прочитанное с ошибкой: «SRV-DBOI» вместо «SRV-DB01», «petrcv» вместо
        /// «petrov». Сравнение по приведённому виду (O и 0, l и 1, кириллица как латиница) с одной правкой.
        /// Находка получает настоящее значение, поэтому и метку ту же, что в тексте.
        /// </summary>
        internal static List<Detection> SimilarValues(ImageReading reading, SafePasteDatabase database, List<Detection> found)
        {
            List<Detection> result = new List<Detection>();
            if (database == null)
            {
                return result;
            }
            Dictionary<string, List<LearnedValue>> index = new Dictionary<string, List<LearnedValue>>(StringComparer.Ordinal);
            AddValues(index, database.Learned);
            // Запомненного бывает много: индекс строк без знака растёт в десяток раз, поэтому есть предел.
            if (database.Learned.Count + database.Remembered.Count <= MaxSimilarValues)
            {
                AddValues(index, database.Remembered);
            }
            if (index.Count == 0)
            {
                return result;
            }
            string text = reading.Text;
            foreach (OcrWord word in reading.Words)
            {
                int start = word.Start;
                int end = word.End;
                while (start < end && !char.IsLetterOrDigit(text[start]))
                {
                    start++;
                }
                while (end > start && !char.IsLetterOrDigit(text[end - 1]))
                {
                    end--;
                }
                if (end - start < 6 || Covered(found, start, end))
                {
                    continue;
                }
                string token = text.Substring(start, end - start);
                string key = Canonical(token);
                LearnedValue match = null;
                foreach (string variant in Variants(key))
                {
                    List<LearnedValue> candidates;
                    if (!index.TryGetValue(variant, out candidates))
                    {
                        continue;
                    }
                    foreach (LearnedValue candidate in candidates)
                    {
                        if (!database.IsAllowed(candidate.Value) && Distance(key, Canonical(candidate.Value), 1) <= 1)
                        {
                            match = candidate;
                            break;
                        }
                    }
                    if (match != null)
                    {
                        break;
                    }
                }
                if (match == null || string.Equals(match.Value, token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                bool learned = database.Learned.Contains(match);
                Detection detection = new Detection(start, end - start, match.Value, match.Type, Confidence.Learned, 73);
                detection.Source = learned ? Detection.LearnedSource : Detection.RememberedSource;
                detection.Reason = "на картинке прочитано как «" + token + "»";
                result.Add(detection);
            }
            return result;
        }

        private static void AddValues(Dictionary<string, List<LearnedValue>> index, List<LearnedValue> values)
        {
            foreach (LearnedValue value in values)
            {
                if (value == null || string.IsNullOrEmpty(value.Value) || value.Value.Length < 6 || value.Value.IndexOf(' ') >= 0)
                {
                    continue;
                }
                string key = Canonical(value.Value);
                foreach (string variant in Variants(key))
                {
                    List<LearnedValue> list;
                    if (!index.TryGetValue(variant, out list))
                    {
                        list = new List<LearnedValue>();
                        index.Add(variant, list);
                    }
                    if (!list.Contains(value))
                    {
                        list.Add(value);
                    }
                }
            }
        }

        /// <summary>Сама строка и все строки без одного знака: так находятся значения с одной правкой.</summary>
        private static IEnumerable<string> Variants(string key)
        {
            yield return key;
            for (int i = 0; i < key.Length; i++)
            {
                yield return key.Remove(i, 1);
            }
        }

        private static bool Covered(List<Detection> found, int start, int end)
        {
            foreach (Detection detection in found)
            {
                if ((detection.Enabled || detection.Locked) && detection.Start <= start && detection.End >= end)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Вид для сравнения: нижний регистр, кириллица как латиница, O как 0, l и I как 1.</summary>
        internal static string Canonical(string value)
        {
            char[] result = value.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < result.Length; i++)
            {
                char latin = ImageReader.LatinFor(result[i]);
                if (latin != '\0')
                {
                    result[i] = latin;
                }
                switch (result[i])
                {
                    case 'o':
                    case 'ø':
                    case 'θ':
                        result[i] = '0';
                        break;
                    case 'l':
                    case 'i':
                    case '|':
                        result[i] = '1';
                        break;
                }
            }
            return new string(result);
        }

        /// <summary>Расстояние правки, но не больше limit + 1: дальше считать незачем.</summary>
        internal static int Distance(string a, string b, int limit)
        {
            if (Math.Abs(a.Length - b.Length) > limit)
            {
                return limit + 1;
            }
            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                int best = current[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                    best = Math.Min(best, current[j]);
                }
                if (best > limit)
                {
                    return limit + 1;
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return Math.Min(previous[b.Length], limit + 1);
        }

        // ---------------------------------------------------------------- прямоугольники

        /// <summary>
        /// Прямоугольники находки на картинке, по одному на строку. Высота всегда по всей строке с запасом:
        /// так закрываются и выносные элементы букв. Если находка начинается или кончается внутри слова,
        /// граница считается по доле знаков и сдвигается на треть знака в сторону видимого текста.
        /// </summary>
        internal static List<RectangleF> BoxesFor(ImageReading reading, int start, int end, bool toLineEnd)
        {
            List<RectangleF> boxes = new List<RectangleF>();
            int line = -1;
            float left = 0;
            float right = 0;
            for (int i = reading.FirstWordEndingAfter(start); i < reading.Words.Count && reading.Words[i].Start < end; i++)
            {
                OcrWord word = reading.Words[i];
                float width = word.CharWidth;
                float x0 = word.Bounds.Left;
                float x1 = word.Bounds.Right;
                if (start > word.Start)
                {
                    x0 = word.Bounds.Left + width * (start - word.Start) - width * 0.34f;
                }
                if (end < word.End)
                {
                    x1 = word.Bounds.Left + width * (end - word.Start) + width * 0.34f;
                }
                if (word.Line != line)
                {
                    if (line >= 0)
                    {
                        boxes.Add(Box(reading, line, left, right));
                    }
                    line = word.Line;
                    left = x0;
                    right = x1;
                }
                else
                {
                    left = Math.Min(left, x0);
                    right = Math.Max(right, x1);
                }
            }
            if (line >= 0)
            {
                if (toLineEnd && line < reading.LineInkRight.Length)
                {
                    right = Math.Max(right, reading.LineInkRight[line]);
                }
                boxes.Add(Box(reading, line, left, right));
            }
            return boxes;
        }

        internal static List<RectangleF> BoxesFor(ImageReading reading, Detection detection)
        {
            return BoxesFor(reading, detection.Start, detection.End, detection.Source == TailSource);
        }

        private static RectangleF Box(ImageReading reading, int line, float left, float right)
        {
            RectangleF band = reading.LineBand(line);
            float pad = Math.Max(1.5f, band.Height * 0.18f);
            RectangleF box = RectangleF.FromLTRB(left - 1.5f, band.Top - pad, right + 1.5f, band.Bottom + pad);
            return RectangleF.Intersect(box, new RectangleF(0, 0, reading.Width, reading.Height));
        }

        // ---------------------------------------------------------------- контрольное чтение

        /// <summary>
        /// Места готовой картинки, где ещё читается скрытое значение. Пустой список: утечек нет.
        /// Значения короче 4 знаков не ищутся: они встречаются в любом тексте.
        /// </summary>
        internal static List<RectangleF> FindLeaks(ImageReading check, ICollection<string> hidden)
        {
            List<RectangleF> leaks = new List<RectangleF>();
            string text = Canonical(check.Text);
            foreach (string value in hidden)
            {
                if (string.IsNullOrEmpty(value) || value.Trim().Length < 4)
                {
                    continue;
                }
                string needle = Canonical(value.Trim());
                int at = text.IndexOf(needle, StringComparison.Ordinal);
                while (at >= 0)
                {
                    leaks.AddRange(BoxesFor(check, at, at + needle.Length, false));
                    at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
                }
            }
            return leaks;
        }
    }
}
