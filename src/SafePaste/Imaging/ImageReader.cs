using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace SafePaste.Imaging
{
    /// <summary>Прочитанная картинка: слова по строкам, текст для детектора и места, которые не прочитались.</summary>
    internal sealed class ImageReading
    {
        internal int Width;
        internal int Height;
        internal readonly List<OcrLine> Lines = new List<OcrLine>();
        /// <summary>Все слова по порядку текста.</summary>
        internal readonly List<OcrWord> Words = new List<OcrWord>();
        /// <summary>Текст для детектора: слова через пробел, строки через \r\n, похожие знаки исправлены.</summary>
        internal string Text = string.Empty;
        /// <summary>Текст как его вернуло распознавание, до исправления похожих знаков.</summary>
        internal string RawText = string.Empty;
        /// <summary>Где справа кончаются чернила каждой строки.</summary>
        internal float[] LineInkRight = new float[0];
        internal readonly List<Rectangle> Unread = new List<Rectangle>();
        /// <summary>Непрочитанные фрагменты служебного времени: в обычном режиме не требуют яркой рамки.</summary>
        internal readonly HashSet<int> QuietUnread = new HashSet<int>();
        internal string Languages = string.Empty;
        /// <summary>null: прочитано; иначе почему картинка не прочитана.</summary>
        internal string Problem;
        internal long Milliseconds;

        /// <summary>Вертикальная полоса строки: от верха самого высокого слова до низа самого низкого.</summary>
        internal RectangleF LineBand(int line)
        {
            return line >= 0 && line < Lines.Count ? Lines[line].Bounds : RectangleF.Empty;
        }

        /// <summary>Номер первого слова, которое кончается после позиции index.</summary>
        internal int FirstWordEndingAfter(int index)
        {
            int low = 0;
            int high = Words.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (Words[middle].End <= index)
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
    }

    /// <summary>
    /// Чтение картинки: распознавание русским и английским языком с увеличением, выбор чтения для каждого
    /// слова, текст для детектора, исправление похожих знаков и поиск непрочитанного текста.
    /// Вызывается в фоновом потоке, картинку не меняет.
    /// </summary>
    internal static class ImageReader
    {
        /// <summary>Картинку до этого размера перед чтением увеличиваем в 2 раза: мелкий шрифт читается лучше.</summary>
        internal const double UpscaleLimit = 4200000;

        internal static ImageReading Read(Bitmap image)
        {
            return Read(image, true);
        }

        /// <summary>findUnread: искать непрочитанный текст и концы строк (для контрольного чтения не нужно).</summary>
        internal static ImageReading Read(Bitmap image, bool findUnread)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ImageReading reading = new ImageReading();
            reading.Width = image.Width;
            reading.Height = image.Height;
            reading.Problem = WindowsOcr.Problem;
            List<OcrPage> pages = new List<OcrPage>();
            if (reading.Problem == null)
            {
                List<string> languages = WindowsOcr.Languages;
                reading.Languages = string.Join(", ", languages.ToArray());
                foreach (string language in languages)
                {
                    pages.Add(RecognizeScaled(image, language));
                }
            }
            List<OcrLine> lines = pages.Count == 0 ? new List<OcrLine>()
                : pages.Count == 1 ? pages[0].Lines
                : Merge(pages[0], pages[1]);
            Build(reading, lines);
            if (findUnread)
            {
                FindInk(reading, image);
            }
            reading.Milliseconds = watch.ElapsedMilliseconds;
            return reading;
        }

        /// <summary>Непрочитанный текст и концы строк по чернилам картинки.</summary>
        internal static void FindInk(ImageReading reading, Bitmap image)
        {
            InkMap ink = InkMap.Create(image);
            List<RectangleF> boxes = new List<RectangleF>(reading.Words.Count);
            foreach (OcrWord word in reading.Words)
            {
                boxes.Add(word.Bounds);
            }
            reading.Unread.Clear();
            reading.Unread.AddRange(ink.FindUnread(boxes));
            MarkTimestampChrome(reading);
            reading.LineInkRight = new float[reading.Lines.Count];
            for (int i = 0; i < reading.Lines.Count; i++)
            {
                OcrLine line = reading.Lines[i];
                RectangleF band = line.Bounds;
                float right = line.Words[line.Words.Count - 1].Bounds.Right;
                reading.LineInkRight[i] = Math.Max(right, ink.RightmostInk(band, right, band.Height * 3f));
            }
        }

        /// <summary>
        /// В строках уведомлений часы часто не читаются OCR: слева значок часов, справа id или начало времени.
        /// Пометка остаётся в Unread для строгого режима, но в обычном не отвлекает пунктиром.
        /// </summary>
        internal static void MarkTimestampChrome(ImageReading reading)
        {
            reading.QuietUnread.Clear();
            for (int i = 0; i < reading.Unread.Count; i++)
            {
                Rectangle area = reading.Unread[i];
                foreach (OcrWord clock in reading.Words)
                {
                    if ((clock.Text != "О" && clock.Text != "O" && clock.Text != "0")
                        || clock.Bounds.Height < area.Height * 1.1f || clock.Bounds.Right > area.Left
                        || area.Left - clock.Bounds.Right > area.Height * 2f || !SameBand(area, clock.Bounds))
                    {
                        continue;
                    }
                    bool timestamp = false;
                    foreach (OcrWord neighbor in reading.Words)
                    {
                        if (!SameBand(area, neighbor.Bounds))
                        {
                            continue;
                        }
                        if (string.Equals(neighbor.Text, "id", StringComparison.OrdinalIgnoreCase)
                            && neighbor.Bounds.Left >= area.Right && neighbor.Bounds.Left - area.Right <= area.Height * 3f)
                        {
                            timestamp = true;
                            break;
                        }
                        if (neighbor.Bounds.Left >= clock.Bounds.Right && neighbor.Bounds.Right <= area.Left + 2f
                            && area.Left - neighbor.Bounds.Right <= area.Height * 0.75f && ShortDigits(neighbor.Text))
                        {
                            timestamp = true;
                            break;
                        }
                    }
                    if (timestamp)
                    {
                        reading.QuietUnread.Add(i);
                        break;
                    }
                }
            }
            // Короткое время в правом углу того же уведомления.
            List<int> clockRows = new List<int>(reading.QuietUnread);
            for (int i = 0; i < reading.Unread.Count; i++)
            {
                Rectangle area = reading.Unread[i];
                if (reading.QuietUnread.Contains(i) || area.Left < reading.Width * 0.6f || area.Width > 60)
                {
                    continue;
                }
                foreach (int left in clockRows)
                {
                    if (SameBand(area, reading.Unread[left]))
                    {
                        reading.QuietUnread.Add(i);
                        break;
                    }
                }
            }
        }

        private static bool SameBand(Rectangle area, RectangleF word)
        {
            float overlap = Math.Min(area.Bottom, word.Bottom) - Math.Max(area.Top, word.Top);
            return overlap >= Math.Min(area.Height, word.Height) * 0.5f;
        }

        private static bool ShortDigits(string text)
        {
            if (text.Length == 0 || text.Length > 2)
            {
                return false;
            }
            foreach (char symbol in text)
            {
                if (symbol < '0' || symbol > '9')
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- распознавание

        private static OcrPage RecognizeScaled(Bitmap image, string language)
        {
            int max = WindowsOcr.MaxDimension;
            float scale = (double)image.Width * image.Height <= UpscaleLimit ? 2f : 1f;
            if (image.Width * scale > max)
            {
                scale = (float)max / image.Width;
            }
            int band = Math.Max(64, (int)Math.Floor(max / scale));
            if (image.Height <= band)
            {
                return RecognizeRegion(image, new Rectangle(0, 0, image.Width, image.Height), scale, language);
            }
            // Длинную картинку распознаём полосами с перекрытием; строка берётся из той полосы, где лежит её середина.
            OcrPage page = new OcrPage();
            page.Language = language;
            int overlap = Math.Min(200, band / 4);
            int step = band - overlap;
            for (int top = 0; top < image.Height; top += step)
            {
                int height = Math.Min(band, image.Height - top);
                bool last = top + height >= image.Height;
                OcrPage part = RecognizeRegion(image, new Rectangle(0, top, image.Width, height), scale, language);
                float coreTop = top == 0 ? 0f : top + overlap / 2f;
                float coreBottom = last ? image.Height : top + height - overlap / 2f;
                foreach (OcrLine line in part.Lines)
                {
                    RectangleF bounds = line.Bounds;
                    float middle = bounds.Top + bounds.Height / 2f;
                    if (middle >= coreTop && middle < coreBottom)
                    {
                        page.Lines.Add(line);
                    }
                }
                if (last)
                {
                    break;
                }
            }
            return page;
        }

        private static OcrPage RecognizeRegion(Bitmap image, Rectangle region, float scale, string language)
        {
            int width = Math.Max(1, (int)Math.Round(region.Width * scale));
            int height = Math.Max(1, (int)Math.Round(region.Height * scale));
            OcrPage page;
            using (Bitmap scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics graphics = Graphics.FromImage(scaled))
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    // Прозрачное считаем белым, края не размываем.
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, new Rectangle(0, 0, width, height), region.X, region.Y, region.Width, region.Height,
                        GraphicsUnit.Pixel, attributes);
                }
                page = WindowsOcr.Recognize(scaled, language);
            }
            foreach (OcrLine line in page.Lines)
            {
                foreach (OcrWord word in line.Words)
                {
                    RectangleF box = word.Bounds;
                    word.Bounds = new RectangleF(region.X + box.X / scale, region.Y + box.Y / scale, box.Width / scale, box.Height / scale);
                }
            }
            return page;
        }

        // ---------------------------------------------------------------- два чтения в одно

        /// <summary>
        /// Два чтения одной картинки в одно. Пересекающиеся слова образуют группу, и для группы выбирается
        /// одно чтение (Choose). Слова, которые нашло только второе чтение, встают в строку первого, если
        /// лежат на её высоте, иначе образуют свою строку.
        /// </summary>
        internal static List<OcrLine> Merge(OcrPage primary, OcrPage secondary)
        {
            List<OcrWord> first = Flatten(primary);
            List<OcrWord> second = Flatten(secondary);
            int total = first.Count + second.Count;
            int[] parent = new int[total];
            for (int i = 0; i < total; i++)
            {
                parent[i] = i;
            }
            for (int i = 0; i < first.Count; i++)
            {
                for (int j = 0; j < second.Count; j++)
                {
                    if (SameWord(first[i].Bounds, second[j].Bounds))
                    {
                        Union(parent, i, first.Count + j);
                    }
                }
            }
            Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
            List<int> order = new List<int>();
            for (int i = 0; i < total; i++)
            {
                int root = Find(parent, i);
                List<int> members;
                if (!groups.TryGetValue(root, out members))
                {
                    members = new List<int>();
                    groups.Add(root, members);
                    order.Add(root);
                }
                members.Add(i);
            }
            List<OcrLine> lines = new List<OcrLine>();
            foreach (OcrLine line in primary.Lines)
            {
                lines.Add(new OcrLine());
            }
            List<OcrWord> extra = new List<OcrWord>();
            foreach (int root in order)
            {
                List<OcrWord> mine = new List<OcrWord>();
                List<OcrWord> theirs = new List<OcrWord>();
                foreach (int member in groups[root])
                {
                    if (member < first.Count)
                    {
                        mine.Add(first[member]);
                    }
                    else
                    {
                        theirs.Add(second[member - first.Count]);
                    }
                }
                SortByX(mine);
                SortByX(theirs);
                if (theirs.Count == 0)
                {
                    foreach (OcrWord word in mine)
                    {
                        lines[word.Line].Words.Add(word);
                    }
                }
                else if (mine.Count == 0)
                {
                    extra.AddRange(theirs);
                }
                else if (Choose(Join(mine), Join(theirs)))
                {
                    foreach (OcrWord word in mine)
                    {
                        lines[word.Line].Words.Add(word);
                    }
                }
                else
                {
                    foreach (OcrWord word in theirs)
                    {
                        lines[mine[0].Line].Words.Add(word);
                    }
                }
            }
            Dictionary<int, OcrLine> added = new Dictionary<int, OcrLine>();
            foreach (OcrWord word in extra)
            {
                OcrLine host = null;
                foreach (OcrLine line in lines)
                {
                    if (line.Words.Count > 0 && OnBand(line.Bounds, word.Bounds))
                    {
                        host = line;
                        break;
                    }
                }
                if (host == null && !added.TryGetValue(word.Line, out host))
                {
                    host = new OcrLine();
                    added.Add(word.Line, host);
                    lines.Add(host);
                }
                host.Words.Add(word);
            }
            List<OcrLine> result = new List<OcrLine>();
            foreach (OcrLine line in lines)
            {
                if (line.Words.Count > 0)
                {
                    SortByX(line.Words);
                    result.Add(line);
                }
            }
            result.Sort(delegate(OcrLine a, OcrLine b)
            {
                RectangleF left = a.Bounds;
                RectangleF right = b.Bounds;
                return left.Top != right.Top ? left.Top.CompareTo(right.Top) : left.Left.CompareTo(right.Left);
            });
            return result;
        }

        private static List<OcrWord> Flatten(OcrPage page)
        {
            List<OcrWord> words = new List<OcrWord>();
            for (int i = 0; i < page.Lines.Count; i++)
            {
                foreach (OcrWord word in page.Lines[i].Words)
                {
                    word.Line = i;
                    words.Add(word);
                }
            }
            return words;
        }

        private static void SortByX(List<OcrWord> words)
        {
            words.Sort(delegate(OcrWord a, OcrWord b) { return a.Bounds.Left.CompareTo(b.Bounds.Left); });
        }

        /// <summary>Два прямоугольника описывают одно слово: пересечение не меньше 40% меньшего из них.</summary>
        private static bool SameWord(RectangleF a, RectangleF b)
        {
            RectangleF overlap = RectangleF.Intersect(a, b);
            if (overlap.Width <= 0 || overlap.Height <= 0)
            {
                return false;
            }
            float smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
            return overlap.Width * overlap.Height >= smaller * 0.4f;
        }

        private static bool OnBand(RectangleF line, RectangleF word)
        {
            float overlap = Math.Min(line.Bottom, word.Bottom) - Math.Max(line.Top, word.Top);
            return overlap >= word.Height * 0.5f;
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
            {
                parent[Math.Max(rootA, rootB)] = Math.Min(rootA, rootB);
            }
        }

        private static string Join(List<OcrWord> words)
        {
            StringBuilder text = new StringBuilder();
            bool[] ipJoins = IPv4Joins(words);
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0 && !ipJoins[i] && !Tight(words[i - 1], words[i]))
                {
                    text.Append(' ');
                }
                text.Append(words[i].Text);
            }
            return text.ToString();
        }

        /// <summary>
        /// Какое чтение взять: true первое (русское), false второе (английское). Слово с кириллицей берётся
        /// у русского чтения, кроме технических слов из букв, похожих на латиницу («согр.1оса1», «ае:1А»),
        /// и коротких сокращений вроде «МАС». Латиницу лучше читает английское; из двух чтений одной длины,
        /// которые различаются только похожими знаками (O и 0), берётся то, где больше цифр.
        /// </summary>
        internal static bool Choose(string mine, string theirs)
        {
            bool mineCyrillic = HasCyrillic(mine);
            bool theirsCyrillic = HasCyrillic(theirs);
            if (mineCyrillic && !theirsCyrillic)
            {
                return !LooksLatin(mine) || !HasLetterOrDigit(theirs);
            }
            if (theirsCyrillic && !mineCyrillic)
            {
                return LooksLatin(theirs) || !HasLetterOrDigit(mine);
            }
            if (string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (mine.Length == theirs.Length)
            {
                bool confusable = true;
                for (int i = 0; i < mine.Length && confusable; i++)
                {
                    if (mine[i] != theirs[i])
                    {
                        confusable = (char.IsDigit(mine[i]) && DigitFor(theirs[i]) == mine[i])
                            || (char.IsDigit(theirs[i]) && DigitFor(mine[i]) == theirs[i]);
                    }
                }
                if (confusable)
                {
                    return CountDigits(mine) >= CountDigits(theirs);
                }
            }
            return mineCyrillic;
        }

        /// <summary>
        /// Кириллица здесь, скорее всего, прочитанная латиница: все буквы похожи на латинские и внутри есть
        /// цифры или знаки адресов; либо это короткое слово заглавными буквами («МАС»).
        /// </summary>
        private static bool LooksLatin(string value)
        {
            string core = Core(value);
            if (core.Length == 0)
            {
                return false;
            }
            bool technical = false;
            bool upper = true;
            foreach (char symbol in core)
            {
                if (IsCyrillic(symbol) && LatinFor(symbol) == '\0')
                {
                    return false;
                }
                technical |= char.IsDigit(symbol) || ".:@/\\_-".IndexOf(symbol) >= 0;
                upper &= !char.IsLetter(symbol) || char.IsUpper(symbol);
            }
            return technical || (upper && core.Length <= 4);
        }

        private static string Core(string value)
        {
            int start = 0;
            int end = value.Length;
            while (start < end && Edge(value[start]))
            {
                start++;
            }
            while (end > start && Edge(value[end - 1]))
            {
                end--;
            }
            return value.Substring(start, end - start);
        }

        private static bool Edge(char symbol)
        {
            return char.IsWhiteSpace(symbol) || ",;:!?()[]{}\"'«»“”.".IndexOf(symbol) >= 0;
        }

        private static bool HasCyrillic(string value)
        {
            foreach (char symbol in value)
            {
                if (IsCyrillic(symbol))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasLetterOrDigit(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsLetterOrDigit(symbol))
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountDigits(string value)
        {
            int count = 0;
            foreach (char symbol in value)
            {
                if (char.IsDigit(symbol))
                {
                    count++;
                }
            }
            return count;
        }

        internal static bool IsCyrillic(char symbol)
        {
            return symbol >= 'Ѐ' && symbol <= 'ӿ';
        }

        /// <summary>Латинская буква, на которую похожа кириллическая, или '\0'.</summary>
        internal static char LatinFor(char symbol)
        {
            const string cyrillic = "АВЕКМНОРСТУХаеорсухгпті";
            const string latin = "ABEKMHOPCTYXaeopcyxrnmi";
            int index = cyrillic.IndexOf(symbol);
            return index < 0 ? '\0' : latin[index];
        }

        /// <summary>Цифра, за которую распознавание принимает эту букву, или '\0'.</summary>
        internal static char DigitFor(char symbol)
        {
            switch (symbol)
            {
                case 'O':
                case 'o':
                case 'D':
                case 'Q':
                case 'ø':
                case 'Ø':
                case 'θ':
                case 'О':
                case 'о':
                    return '0';
                case 'I':
                case 'l':
                case 'i':
                case '|':
                case '!':
                    return '1';
                case 'Z':
                case 'z':
                    return '2';
                case 'S':
                case 's':
                    return '5';
                case 'B':
                    return '8';
                case 'G':
                    return '6';
                case 'g':
                case 'q':
                    return '9';
                default:
                    return '\0';
            }
        }

        // ---------------------------------------------------------------- текст

        /// <summary>
        /// Слова соседние по картинке, но разрезанные распознаванием («00:1A:» и «2B:3C»): промежуток меньше
        /// трети ширины знака. Обычный пробел шире.
        /// </summary>
        internal static bool Tight(OcrWord left, OcrWord right)
        {
            float gap = right.Bounds.Left - left.Bounds.Right;
            float width = (left.CharWidth + right.CharWidth) / 2f;
            return gap < width * 0.34f;
        }

        /// <summary>OCR может разрезать IP на «1», «61» и «.104.45.49» с обычными пробелами между словами.</summary>
        private static bool[] IPv4Joins(List<OcrWord> words)
        {
            bool[] joins = new bool[words.Count];
            for (int start = 0; start < words.Count; start++)
            {
                StringBuilder candidate = new StringBuilder(words[start].Text);
                for (int end = start + 1; end < words.Count && end <= start + 3; end++)
                {
                    OcrWord left = words[end - 1];
                    OcrWord right = words[end];
                    float width = (left.CharWidth + right.CharWidth) / 2f;
                    if (right.Bounds.Left - left.Bounds.Right > width * 1.2f)
                    {
                        break;
                    }
                    candidate.Append(right.Text);
                    if (!ValidIPv4(candidate.ToString()))
                    {
                        continue;
                    }
                    for (int boundary = start + 1; boundary <= end; boundary++)
                    {
                        joins[boundary] = true;
                    }
                    break;
                }
            }
            return joins;
        }

        private static bool ValidIPv4(string value)
        {
            string[] octets = value.Split('.');
            if (octets.Length != 4)
            {
                return false;
            }
            foreach (string octet in octets)
            {
                if (octet.Length == 0 || octet.Length > 3)
                {
                    return false;
                }
                int number = 0;
                foreach (char digit in octet)
                {
                    if (digit < '0' || digit > '9')
                    {
                        return false;
                    }
                    number = number * 10 + digit - '0';
                }
                if (number > 255)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Строки, слова по порядку, текст для детектора и его исправленный вид.</summary>
        internal static void Build(ImageReading reading, List<OcrLine> lines)
        {
            reading.Lines.Clear();
            reading.Words.Clear();
            StringBuilder text = new StringBuilder();
            foreach (OcrLine source in lines)
            {
                if (source.Words.Count == 0)
                {
                    continue;
                }
                OcrLine line = new OcrLine();
                line.Words.AddRange(source.Words);
                SortByX(line.Words);
                bool[] ipJoins = IPv4Joins(line.Words);
                int index = reading.Lines.Count;
                if (index > 0)
                {
                    text.Append("\r\n");
                }
                for (int i = 0; i < line.Words.Count; i++)
                {
                    OcrWord word = line.Words[i];
                    if (i > 0 && !ipJoins[i] && !Tight(line.Words[i - 1], word))
                    {
                        text.Append(' ');
                    }
                    word.Line = index;
                    word.Start = text.Length;
                    text.Append(word.Text);
                    reading.Words.Add(word);
                }
                reading.Lines.Add(line);
            }
            reading.RawText = text.ToString();
            reading.Text = Normalize(reading.RawText);
            if (reading.LineInkRight.Length != reading.Lines.Count)
            {
                reading.LineInkRight = new float[reading.Lines.Count];
                for (int i = 0; i < reading.Lines.Count; i++)
                {
                    reading.LineInkRight[i] = reading.Lines[i].Bounds.Right;
                }
            }
        }

        /// <summary>
        /// Исправляет похожие знаки, не меняя длину текста: позиции слов остаются прежними.
        /// Техническое слово из кириллических букв, похожих на латиницу, становится латиницей
        /// («согр.1оса1»), в числовых частях IP и MAC буквы, похожие на цифры, становятся цифрами
        /// («1ø.44.7.219», «ØØ:IA:2B:3C:4D:5E»). Обычные слова не трогаются.
        /// </summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }
            char[] result = text.ToCharArray();
            int position = 0;
            while (position < result.Length)
            {
                while (position < result.Length && char.IsWhiteSpace(result[position]))
                {
                    position++;
                }
                int start = position;
                while (position < result.Length && !char.IsWhiteSpace(result[position]))
                {
                    position++;
                }
                if (position > start)
                {
                    NormalizeToken(result, start, position);
                }
            }
            return new string(result);
        }

        private static void NormalizeToken(char[] text, int start, int end)
        {
            while (start < end && Edge(text[start]))
            {
                start++;
            }
            while (end > start && Edge(text[end - 1]))
            {
                end--;
            }
            if (end <= start)
            {
                return;
            }
            string token = new string(text, start, end - start);
            if (HasCyrillic(token) && LooksLatin(token))
            {
                for (int i = start; i < end; i++)
                {
                    char latin = LatinFor(text[i]);
                    if (latin != '\0')
                    {
                        text[i] = latin;
                    }
                }
            }
            FixNumericGroups(text, start, end);
        }

        /// <summary>Части IP через точку и части MAC через двоеточие или дефис.</summary>
        private static void FixNumericGroups(char[] text, int start, int end)
        {
            foreach (char separator in new char[] { '.', ':', '-' })
            {
                List<int> starts = new List<int>();
                List<int> ends = new List<int>();
                int group = start;
                for (int i = start; i <= end; i++)
                {
                    if (i == end || text[i] == separator)
                    {
                        starts.Add(group);
                        ends.Add(i);
                        group = i + 1;
                    }
                }
                if (separator == '.' && starts.Count == 4)
                {
                    int digits = 0;
                    for (int g = 0; g < 4; g++)
                    {
                        if (AllDigits(text, starts[g], ends[g]))
                        {
                            digits++;
                        }
                    }
                    if (digits >= 2)
                    {
                        for (int g = 0; g < 4; g++)
                        {
                            MapGroup(text, starts[g], ends[g], 3, false);
                        }
                    }
                }
                else if (separator != '.' && starts.Count == 6)
                {
                    bool pairs = true;
                    for (int g = 0; g < 6 && pairs; g++)
                    {
                        pairs = ends[g] - starts[g] == 2;
                    }
                    if (pairs)
                    {
                        for (int g = 0; g < 6; g++)
                        {
                            MapGroup(text, starts[g], ends[g], 2, true);
                        }
                    }
                }
            }
        }

        private static bool AllDigits(char[] text, int start, int end)
        {
            if (end <= start)
            {
                return false;
            }
            for (int i = start; i < end; i++)
            {
                if (!char.IsDigit(text[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Меняет похожие буквы на цифры, если вся часть тогда станет числом (hex: шестнадцатеричным).
        /// Буквы, которые и так верны в шестнадцатеричном числе (A-F), не трогаются.
        /// </summary>
        private static void MapGroup(char[] text, int start, int end, int maxLength, bool hex)
        {
            int length = end - start;
            if (length < 1 || length > maxLength)
            {
                return;
            }
            char[] mapped = new char[length];
            for (int i = 0; i < length; i++)
            {
                char symbol = text[start + i];
                bool valid = hex ? Uri.IsHexDigit(symbol) : char.IsDigit(symbol);
                if (valid)
                {
                    mapped[i] = symbol;
                    continue;
                }
                char digit = DigitFor(symbol);
                if (digit == '\0' && !hex && (symbol == 'e' || symbol == 'a'))
                {
                    // Ноль с точкой или чертой в Consolas читается как e или a.
                    digit = '0';
                }
                if (digit == '\0')
                {
                    return;
                }
                mapped[i] = digit;
            }
            Array.Copy(mapped, 0, text, start, length);
        }
    }
}
