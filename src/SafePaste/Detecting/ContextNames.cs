using System;
using System.Collections.Generic;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Имена, которые выдаёт только слово рядом: «кластер Orion», «базу crm_prod», «на сервере Phoenix»,
    /// ООО «Ромашка», «ping phoenix», «phoenix [10.0.0.5]», строка файла hosts, «-ComputerName web01».
    /// Само имя похоже на обычное слово, поэтому решение складывается из признаков: подсказка рядом
    /// и вид слова (цифры, подчёркивание, латиница посреди русского текста, заглавная буква, кавычки).
    /// Строчное русское слово без цифр и кавычек не берётся никогда: «сервер упал» остаётся как есть.
    /// Надёжной (High, её скрывает и быстрая вставка) находка становится при явном контексте (ключ
    /// «cluster:», аргумент команды, IP рядом, форма организации) или при виде идентификатора. Слово-имя
    /// после подсказки остаётся Medium: чаще всего это продукт, которого нет в словаре, и скрывать его
    /// стоит в окне проверки, где человек увидит ошибку. После глагола («зайди на X») слово без цифр
    /// только догадка.
    /// </summary>
    internal static class ContextNames
    {
        internal const int Priority = 66;
        internal const string ContextSource = "Контекст";

        private const double NounWeight = 2.5;
        private const double VerbWeight = 2.0;
        private const double StrongWeight = 4.5;
        private const double MediumScore = 3.0;
        private const double HighScore = 4.5;
        private const int MaxGap = 6;
        private const int MaxQuoted = 60;

        // Падежные окончания, которые снимаются, чтобы узнать продукт или общее слово: «Яндексе», «Интернету».
        private static readonly string[] CaseEndings = {
            "ами", "ями", "ах", "ях", "ам", "ям", "ов", "ев", "ей", "ом", "ем", "ой", "ою", "ую",
            "а", "я", "у", "ю", "е", "и", "ы", "ь" };
        private static readonly string[] StemTails = { "", "и", "а", "ь", "й" };
        private static readonly string[] ListWords = { "и", "или", "and", "or" };
        private static readonly string[] CmdletVerbs = {
            "get-", "set-", "new-", "remove-", "enable-", "disable-", "unlock-", "add-", "search-", "move-",
            "rename-", "reset-", "test-", "restart-", "stop-", "start-", "invoke-", "enter-" };

        /// <summary>Слово: буквы и цифры, внутри могут быть «_» и «-».</summary>
        private sealed class Token
        {
            internal int Start;
            internal int End;
            internal int Line;
            internal string Text;
            internal string Key;
            internal int Letters;
            internal int Upper;
            internal int LatinLetters;
            internal int CyrillicLetters;
            internal bool Digit;
            internal bool Underscore;
            internal bool Hyphen;
            internal bool Caps;
            internal bool Title;
            internal bool Hump;
            internal bool Flag;

            internal bool Latin
            {
                get { return LatinLetters > 0; }
            }

            internal bool Cyrillic
            {
                get { return CyrillicLetters > 0; }
            }

            internal bool Letter
            {
                get { return Letters > 0; }
            }
        }

        /// <summary>Подсказка перед словом: какой тип у имени и насколько ей можно верить.</summary>
        private sealed class Clue
        {
            internal string Type;
            internal double Weight;
            internal bool Strong;
            internal bool Verb;
            internal bool NeedsName;
            internal string Reason;

            internal static Clue Make(string type, double weight, bool strong, string reason)
            {
                Clue clue = new Clue();
                clue.Type = type;
                clue.Weight = weight;
                clue.Strong = strong;
                clue.Reason = reason;
                return clue;
            }
        }

        internal static List<Detection> Find(string text)
        {
            List<Detection> found = new List<Detection>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }
            List<Token> tokens = Tokenize(text);
            if (tokens.Count == 0)
            {
                return found;
            }
            int lines = tokens[tokens.Count - 1].Line + 1;
            int[] cyrillic = new int[lines];
            int[] latin = new int[lines];
            foreach (Token token in tokens)
            {
                cyrillic[token.Line] += token.CyrillicLetters;
                latin[token.Line] += token.LatinLetters;
            }
            HashSet<int> hostsNames = HostsLineNames(text);

            int covered = -1;
            int lastIndex = -1;
            Clue lastClue = null;
            for (int index = 0; index < tokens.Count; index++)
            {
                Token token = tokens[index];
                if (token.Start < covered)
                {
                    continue;
                }
                Clue clue = FindClue(text, tokens, index, hostsNames);
                if (clue == null && lastClue != null)
                {
                    clue = Continue(text, tokens, index, lastIndex, lastClue);
                }
                if (clue == null)
                {
                    continue;
                }
                int end = token.End;
                int last = index;
                bool quoted = false;
                int close = ClosingQuote(text, token.Start);
                if (close >= token.End)
                {
                    end = close;
                    quoted = true;
                    while (last + 1 < tokens.Count && tokens[last + 1].End <= close)
                    {
                        last++;
                    }
                }
                Detection detection = Judge(text, tokens, index, last, end, quoted, clue, cyrillic, latin);
                if (detection == null)
                {
                    continue;
                }
                found.Add(detection);
                covered = end;
                lastIndex = last;
                lastClue = clue;
            }
            return found;
        }

        /// <summary>
        /// Обычное слово, продукт или сокращение: «test», «Exchange», «DNS», «Интернету». Такие значения
        /// не распространяются по тексту и не считаются именами после подсказки.
        /// </summary>
        internal static bool IsPlainWord(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }
            return IsPlainKey(value.ToLowerInvariant().Replace('ё', 'е'));
        }

        // ---------------------------------------------------------------- подсказки

        private static Clue FindClue(string text, List<Token> tokens, int index, HashSet<int> hostsNames)
        {
            Token token = tokens[index];
            if (token.Flag)
            {
                return null;
            }
            Clue best = null;
            if (hostsNames.Contains(token.Start))
            {
                best = Better(best, Clue.Make("HOST", StrongWeight, true, "строка файла hosts"));
            }
            if (FollowedByAddress(text, token.End))
            {
                best = Better(best, Clue.Make("HOST", StrongWeight, true, "рядом IP-адрес в скобках"));
            }

            Token previous = index > 0 ? tokens[index - 1] : null;
            bool keyValue;
            if (previous != null && previous.Line == token.Line && ReadGap(text, previous.End, token.Start, out keyValue))
            {
                string type;
                if (previous.Flag)
                {
                    type = ParameterType(text, tokens, index - 1);
                    if (type != null)
                    {
                        best = Better(best, Clue.Make(type, StrongWeight, true,
                            "параметр " + text.Substring(previous.Start - 1, previous.End - previous.Start + 1)));
                    }
                }
                else if (!keyValue && IsDomainController(text, tokens, index))
                {
                    // «контроллер домена DC01»: имя узла, а не домена.
                    best = Better(best, Clue.Make("HOST", NounWeight, false,
                        "после «" + tokens[index - 2].Text + " " + previous.Text + "»"));
                }
                else if (AnchorType(previous, out type))
                {
                    best = Better(best, keyValue
                        ? Clue.Make(type, StrongWeight, true, "ключ «" + previous.Text + "»")
                        : Clue.Make(type, NounWeight, false, "после слова «" + previous.Text + "»"));
                }
                if (!keyValue && !previous.Flag)
                {
                    best = Better(best, WordClue(text, tokens, index, previous));
                }
            }

            string commandType;
            string command;
            if (CommandBefore(text, tokens, index, out commandType, out command))
            {
                best = Better(best, Clue.Make(commandType, StrongWeight, true, "аргумент команды " + command));
            }

            Token next = index + 1 < tokens.Count ? tokens[index + 1] : null;
            if (next != null && next.Line == token.Line && ContextLexicons.LegalFormsAfter.Contains(next.Key)
                && token.Latin && !token.Cyrillic && (token.Title || token.Caps) && OnlySpacesOrComma(text, token.End, next.Start))
            {
                best = Better(best, Clue.Make("TEXT", StrongWeight, true, "перед «" + next.Text + "»"));
            }
            return best;
        }

        /// <summary>Подсказки без двоеточия: правовая форма, название в кавычках, глагол доступа, предлог.</summary>
        private static Clue WordClue(string text, List<Token> tokens, int index, Token previous)
        {
            Token token = tokens[index];
            if (ContextLexicons.LegalFormsBefore.Contains(previous.Key))
            {
                Clue legal = Clue.Make("TEXT", StrongWeight, true, "после «" + previous.Text + "»");
                legal.NeedsName = true;
                return legal;
            }
            if (ContextLexicons.QuotedOrgNouns.Contains(previous.Key) && ClosingQuote(text, token.Start) >= token.End)
            {
                return Clue.Make("TEXT", StrongWeight, true, "название в кавычках после «" + previous.Text + "»");
            }
            if (ContextLexicons.AccessVerbs.Contains(previous.Key))
            {
                return VerbClue("после «" + previous.Text + "»");
            }
            Token before = index > 1 ? tokens[index - 2] : null;
            if (before == null || before.Line != token.Line || !OnlySpaces(text, before.End, previous.Start))
            {
                return null;
            }
            if (ContextLexicons.Prepositions.Contains(previous.Key)
                && (ContextLexicons.AccessVerbs.Contains(before.Key) || ContextLexicons.AccessProtocols.Contains(before.Key)))
            {
                return VerbClue("после «" + before.Text + " " + previous.Text + "»");
            }
            return null;
        }

        private static bool IsDomainController(string text, List<Token> tokens, int index)
        {
            if (index < 2)
            {
                return false;
            }
            Token previous = tokens[index - 1];
            Token before = tokens[index - 2];
            return before.Line == previous.Line && OnlySpaces(text, before.End, previous.Start)
                && previous.Key.StartsWith("домен", StringComparison.Ordinal)
                && before.Key.StartsWith("контроллер", StringComparison.Ordinal);
        }

        private static Clue VerbClue(string reason)
        {
            Clue clue = Clue.Make("HOST", VerbWeight, false, reason);
            clue.Verb = true;
            return clue;
        }

        /// <summary>Следующее значение того же списка: «серверы web01, web02 и web03».</summary>
        private static Clue Continue(string text, List<Token> tokens, int index, int lastIndex, Clue lastClue)
        {
            if (lastIndex < 0)
            {
                return null;
            }
            Token token = tokens[index];
            Token last = tokens[lastIndex];
            if (last.Line != token.Line || IsKey(text, token))
            {
                return null; // «Database=testdb;Username=pguser»: Username здесь ключ, а не следующее значение
            }
            bool listed = false;
            if (lastIndex == index - 1)
            {
                listed = OnlySpacesOrComma(text, last.End, token.Start) && HasComma(text, last.End, token.Start);
            }
            else if (lastIndex == index - 2)
            {
                Token middle = tokens[index - 1];
                listed = Array.IndexOf(ListWords, middle.Key) >= 0
                    && OnlySpacesOrComma(text, last.End, middle.Start) && OnlySpaces(text, middle.End, token.Start);
            }
            if (!listed)
            {
                return null;
            }
            Clue clue = Clue.Make(lastClue.Type, lastClue.Weight, lastClue.Strong, "в том же списке");
            clue.Verb = lastClue.Verb;
            clue.NeedsName = lastClue.NeedsName;
            return clue;
        }

        /// <summary>Сразу за словом «=» или «:»: это ключ пары «ключ=значение».</summary>
        private static bool IsKey(string text, Token token)
        {
            int position = token.End;
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t'))
            {
                position++;
            }
            return position < text.Length && (text[position] == '=' || text[position] == ':');
        }

        private static Clue Better(Clue current, Clue candidate)
        {
            if (candidate == null)
            {
                return current;
            }
            if (current == null || candidate.Weight > current.Weight)
            {
                return candidate;
            }
            return current;
        }

        /// <summary>Существительное-подсказка, в том числе в составном ключе: cluster_name, projectId, db-name.</summary>
        private static bool AnchorType(Token token, out string type)
        {
            if (ContextLexicons.Nouns.TryGetValue(token.Key, out type))
            {
                return true;
            }
            type = null;
            List<string> parts = KeyParts(token.Text);
            if (parts.Count < 2 || parts.Count > 3)
            {
                return false;
            }
            int anchors = 0;
            foreach (string part in parts)
            {
                string partType;
                if (ContextLexicons.Nouns.TryGetValue(part, out partType))
                {
                    anchors++;
                    type = partType;
                }
                else if (!ContextLexicons.KeySuffixes.Contains(part))
                {
                    return false;
                }
            }
            return anchors == 1;
        }

        private static List<string> KeyParts(string text)
        {
            List<string> parts = new List<string>();
            int start = 0;
            for (int index = 1; index <= text.Length; index++)
            {
                bool boundary = index == text.Length || text[index] == '_' || text[index] == '-'
                    || (char.IsUpper(text[index]) && char.IsLower(text[index - 1]));
                if (!boundary)
                {
                    continue;
                }
                string part = text.Substring(start, index - start).Trim('_', '-');
                if (part.Length > 0)
                {
                    parts.Add(part.ToLowerInvariant().Replace('ё', 'е'));
                }
                start = index;
            }
            return parts;
        }

        /// <summary>Тип значения после ключа: -ComputerName web01, -Identity ivanov, mstsc /v:phoenix.</summary>
        private static string ParameterType(string text, List<Token> tokens, int flagIndex)
        {
            Token flag = tokens[flagIndex];
            if (ContextLexicons.HostParameters.Contains(flag.Key))
            {
                return "HOST";
            }
            if (ContextLexicons.UserParameters.Contains(flag.Key))
            {
                return "USER";
            }
            if (flag.Key == "identity")
            {
                string cmdlet = CmdletOnLine(tokens, flagIndex);
                return cmdlet == null ? null : ContextLexicons.IdentityType(cmdlet);
            }
            if (flag.Key == "v" && flag.End < text.Length && text[flag.End] == ':')
            {
                for (int index = flagIndex - 1; index >= 0 && tokens[index].Line == flag.Line; index--)
                {
                    if (tokens[index].Key == "mstsc" || tokens[index].Key == "xfreerdp")
                    {
                        return "HOST";
                    }
                }
            }
            return null;
        }

        private static string CmdletOnLine(List<Token> tokens, int flagIndex)
        {
            for (int index = flagIndex - 1; index >= 0 && tokens[index].Line == tokens[flagIndex].Line; index--)
            {
                if (IsCmdlet(tokens[index]))
                {
                    return tokens[index].Key;
                }
            }
            return null;
        }

        private static bool IsCmdlet(Token token)
        {
            if (token.Flag || token.Key.IndexOf('-') <= 0)
            {
                return false;
            }
            foreach (string verb in CmdletVerbs)
            {
                if (token.Key.StartsWith(verb, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Первый аргумент сетевой команды: «ping -n 4 web01», «Test-NetConnection phoenix -Port 1433»,
        /// «Get-ADUser ivanov». Ключи, числа и имена файлов между командой и словом пропускаются.
        /// </summary>
        private static bool CommandBefore(string text, List<Token> tokens, int index, out string type, out string command)
        {
            type = null;
            command = null;
            Token token = tokens[index];
            for (int back = index - 1, steps = 0; back >= 0 && steps < 5; back--, steps++)
            {
                Token current = tokens[back];
                if (current.Line != token.Line)
                {
                    return false;
                }
                if (!current.Flag)
                {
                    // «SSH» заглавными это название протокола в тексте («# SSH Basic style»), а не команда.
                    if (ContextLexicons.HostCommands.Contains(current.Key) && !current.Caps)
                    {
                        type = "HOST";
                        command = current.Text;
                        return true;
                    }
                    if (IsCmdlet(current))
                    {
                        string identity = ContextLexicons.IdentityType(current.Key);
                        if (identity == null)
                        {
                            return false;
                        }
                        type = identity;
                        command = current.Text;
                        return true;
                    }
                }
                bool skippable = current.Flag || !current.Letter || IsDotted(text, current)
                    || (back > 0 && tokens[back - 1].Flag && tokens[back - 1].Line == token.Line);
                if (!skippable)
                {
                    return false;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- решение

        private static Detection Judge(string text, List<Token> tokens, int index, int last, int end, bool quoted,
            Clue clue, int[] cyrillic, int[] latin)
        {
            Token first = tokens[index];
            if (quoted ? RejectedQuoted(text, tokens, index, last, end) : Rejected(text, first))
            {
                return null;
            }
            if (clue.NeedsName && !quoted && !first.Title && !first.Caps)
            {
                return null;
            }

            bool digit = false;
            bool letter = false;
            bool underscore = false;
            bool hyphen = false;
            bool affix = false;
            int latinLetters = 0;
            for (int position = index; position <= last; position++)
            {
                Token token = tokens[position];
                digit |= token.Digit;
                letter |= token.Letter;
                underscore |= token.Underscore;
                hyphen |= token.Hyphen;
                affix |= HasHostAffix(token);
                latinLetters += token.LatinLetters;
            }

            List<string> reasons = new List<string>();
            reasons.Add(clue.Reason);
            double score = clue.Weight;
            bool strongShape = false;
            if (digit && letter)
            {
                score += 2;
                strongShape = true;
                reasons.Add("буквы и цифры");
            }
            if (underscore)
            {
                score += 1.5;
                strongShape = true;
                reasons.Add("подчёркивание");
            }
            if (affix)
            {
                score += 1.5;
                strongShape = true;
                reasons.Add("похоже на имя компьютера");
            }
            else if (hyphen)
            {
                score += 0.5;
            }
            bool russianLine = cyrillic[first.Line] > latin[first.Line] - latinLetters;
            if (first.Latin && !first.Cyrillic && russianLine)
            {
                score += 1.5;
                reasons.Add("латиница в русском тексте");
            }
            // В английском тексте заглавными пишут каждое слово заголовка («Host Process»), поэтому там
            // заглавная буква ничего не говорит.
            if (first.Title && (first.Cyrillic || russianLine) && !IsSentenceStart(text, quoted ? first.Start - 1 : first.Start))
            {
                score += 1;
                reasons.Add("с заглавной буквы");
            }
            if (first.Caps && first.Letters >= 3)
            {
                score += 1;
                reasons.Add("заглавными буквами");
            }
            if (quoted)
            {
                score += 2;
                strongShape |= !clue.Verb;
                reasons.Add("в кавычках");
            }
            if (first.Hump && !digit)
            {
                score -= 1; // ClickHouse, SharePoint: так чаще пишут продукты
            }
            if (score < MediumScore)
            {
                return null;
            }

            Confidence confidence;
            if (clue.Verb && !strongShape)
            {
                confidence = Confidence.Low;
            }
            else if (score >= HighScore && (clue.Strong || strongShape))
            {
                confidence = Confidence.High;
            }
            else
            {
                confidence = Confidence.Medium;
            }
            int start = first.Start;
            string value = text.Substring(start, end - start).TrimEnd();
            if (value.Length == 0)
            {
                return null;
            }
            Detection detection = new Detection(start, value.Length, value, clue.Type, confidence, Priority);
            detection.Source = ContextSource;
            detection.Reason = string.Join(", ", reasons.ToArray());
            detection.Enabled = confidence != Confidence.Low;
            return detection;
        }

        /// <summary>Слово не может быть именем: обычное слово, число, часть адреса, пути или кода.</summary>
        private static bool Rejected(string text, Token token)
        {
            if (token.Flag || token.Text.Length < 3 || !token.Letter || IsMeasure(token) || IsPlainKey(token.Key))
            {
                return true;
            }
            if (token.Cyrillic && !token.Latin && !token.Digit && !token.Underscore)
            {
                if (token.Upper == 0)
                {
                    return true; // строчное русское слово: «сервер упал»
                }
                if (token.Caps && token.Letters <= 4)
                {
                    return true; // сокращение: СУБД, ФИАС
                }
            }
            char before = token.Start > 0 ? text[token.Start - 1] : ' ';
            switch (before)
            {
                case '.': case '\\': case '/': case '@': case '$': case '%': case '&': case '#': case '~':
                    return true;
            }
            if (token.End < text.Length)
            {
                char after = text[token.End];
                if (after == '@' || after == '\\' || after == '/' || after == '(')
                {
                    return true;
                }
                if (after == '.' && token.End + 1 < text.Length && char.IsLetterOrDigit(text[token.End + 1]))
                {
                    return true; // имя файла, домена или обращение к полю в коде
                }
            }
            return false;
        }

        private static bool RejectedQuoted(string text, List<Token> tokens, int index, int last, int end)
        {
            if (end - tokens[index].Start < 2)
            {
                return true;
            }
            for (int position = index; position <= last; position++)
            {
                Token token = tokens[position];
                if (token.Letter && !IsPlainKey(token.Key) && !IsMeasure(token))
                {
                    return false;
                }
            }
            return true; // «Проект», «Default», «2024»
        }

        private static bool IsPlainKey(string key)
        {
            if (IsListed(key))
            {
                return true;
            }
            bool cyrillic = false;
            foreach (char symbol in key)
            {
                if (IsCyrillicLetter(symbol))
                {
                    cyrillic = true;
                    break;
                }
            }
            if (!cyrillic)
            {
                return false;
            }
            foreach (string ending in CaseEndings)
            {
                if (key.Length - ending.Length < 2 || !key.EndsWith(ending, StringComparison.Ordinal))
                {
                    continue;
                }
                string stem = key.Substring(0, key.Length - ending.Length);
                foreach (string tail in StemTails)
                {
                    if (IsListed(stem + tail))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsListed(string key)
        {
            return ContextLexicons.Known.Contains(key) || ContextLexicons.Generic.Contains(key)
                || Lexicons.UserStopWords.Contains(key) || Lexicons.NameStopWords.Contains(key)
                || ContextLexicons.Nouns.ContainsKey(key) || ContextLexicons.AccessVerbs.Contains(key)
                || ContextLexicons.Prepositions.Contains(key) || ContextLexicons.HostCommands.Contains(key)
                || ContextLexicons.LegalFormsBefore.Contains(key) || ContextLexicons.LegalFormsAfter.Contains(key);
        }

        /// <summary>Число с единицей: 10ms, 5GB, 64bit, 2nd.</summary>
        private static bool IsMeasure(Token token)
        {
            if (!char.IsDigit(token.Key[0]))
            {
                return false;
            }
            int position = 0;
            while (position < token.Key.Length && (char.IsDigit(token.Key[position]) || token.Key[position] == '-'))
            {
                position++;
            }
            return position < token.Key.Length && ContextLexicons.Units.Contains(token.Key.Substring(position));
        }

        private static bool HasHostAffix(Token token)
        {
            if (!token.Hyphen && !token.Underscore)
            {
                return false;
            }
            string[] parts = token.Key.Split('-', '_');
            if (parts.Length < 2)
            {
                return false;
            }
            foreach (string part in parts)
            {
                if (ContextLexicons.HostAffixes.Contains(part))
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- структура текста

        /// <summary>Имена в строках файла hosts: «10.0.0.5 phoenix phoenix.corp.local». Имена с точкой ловит FQDN.</summary>
        private static HashSet<int> HostsLineNames(string text)
        {
            HashSet<int> names = new HashSet<int>();
            int lineStart = 0;
            while (lineStart < text.Length)
            {
                int lineEnd = text.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }
                ReadHostsLine(text, lineStart, lineEnd, names);
                lineStart = lineEnd + 1;
            }
            return names;
        }

        private static void ReadHostsLine(string text, int start, int end, HashSet<int> names)
        {
            int position = start;
            while (position < end && (text[position] == ' ' || text[position] == '\t'))
            {
                position++;
            }
            int after = ReadIPv4(text, position);
            if (after < 0)
            {
                after = ReadIPv6(text, position, end);
            }
            if (after < 0 || after >= end || (text[after] != ' ' && text[after] != '\t'))
            {
                return;
            }
            List<int> fields = new List<int>();
            position = after;
            while (position < end)
            {
                char symbol = text[position];
                if (symbol == ' ' || symbol == '\t' || symbol == '\r')
                {
                    position++;
                    continue;
                }
                if (symbol == '#')
                {
                    break;
                }
                int fieldStart = position;
                bool dotted = false;
                bool letter = false;
                while (position < end && !char.IsWhiteSpace(text[position]))
                {
                    char current = text[position];
                    bool ascii = (current >= 'a' && current <= 'z') || (current >= 'A' && current <= 'Z');
                    if (!ascii && !char.IsDigit(current) && current != '-' && current != '_' && current != '.')
                    {
                        return; // не имя узла: строка из обычного текста
                    }
                    dotted |= current == '.';
                    letter |= ascii;
                    position++;
                }
                if (!dotted && letter)
                {
                    fields.Add(fieldStart);
                }
            }
            foreach (int field in fields)
            {
                names.Add(field);
            }
        }

        /// <summary>После слова идёт IP в скобках: «phoenix [10.0.0.5]», «Phoenix (10.44.7.219)».</summary>
        private static bool FollowedByAddress(string text, int end)
        {
            int position = end;
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t'))
            {
                position++;
            }
            if (position >= text.Length || (text[position] != '[' && text[position] != '('))
            {
                return false;
            }
            char close = text[position] == '[' ? ']' : ')';
            position++;
            while (position < text.Length && text[position] == ' ')
            {
                position++;
            }
            position = ReadIPv4(text, position);
            if (position < 0)
            {
                return false;
            }
            while (position < text.Length && text[position] == ' ')
            {
                position++;
            }
            return position < text.Length && text[position] == close;
        }

        /// <summary>Конец адреса IPv4, который начинается в этом месте, или -1.</summary>
        private static int ReadIPv4(string text, int position)
        {
            if (position > 0 && (char.IsLetterOrDigit(text[position - 1]) || text[position - 1] == '.'))
            {
                return -1;
            }
            for (int octet = 0; octet < 4; octet++)
            {
                if (octet > 0)
                {
                    if (position >= text.Length || text[position] != '.')
                    {
                        return -1;
                    }
                    position++;
                }
                int digits = 0;
                int value = 0;
                while (position < text.Length && char.IsDigit(text[position]) && digits < 4)
                {
                    value = (value * 10) + (text[position] - '0');
                    digits++;
                    position++;
                }
                if (digits == 0 || digits > 3 || value > 255)
                {
                    return -1;
                }
            }
            if (position < text.Length && (char.IsLetterOrDigit(text[position])
                || (text[position] == '.' && position + 1 < text.Length && char.IsDigit(text[position + 1]))))
            {
                return -1;
            }
            return position;
        }

        /// <summary>
        /// Адрес IPv6 в начале строки hosts: «::1», «fe80::1%eth0». Время из журнала («12:30:45») адресом
        /// не считается: в адресе есть «::» или шестнадцатеричная буква.
        /// </summary>
        private static int ReadIPv6(string text, int position, int end)
        {
            int colons = 0;
            bool shortened = false;
            bool hexLetter = false;
            int start = position;
            while (position < end)
            {
                char symbol = text[position];
                bool letter = (symbol >= 'a' && symbol <= 'f') || (symbol >= 'A' && symbol <= 'F');
                if (symbol == ':')
                {
                    colons++;
                    shortened |= position > start && text[position - 1] == ':';
                }
                else if (!letter && !char.IsDigit(symbol) && symbol != '.' && symbol != '%')
                {
                    break;
                }
                hexLetter |= letter;
                position++;
            }
            return colons >= 2 && (shortened || hexLetter) ? position : -1;
        }

        // ---------------------------------------------------------------- помощники

        private static List<Token> Tokenize(string text)
        {
            List<Token> tokens = new List<Token>();
            int line = 0;
            int index = 0;
            while (index < text.Length)
            {
                char symbol = text[index];
                if (symbol == '\n')
                {
                    line++;
                    index++;
                    continue;
                }
                if (!char.IsLetterOrDigit(symbol))
                {
                    index++;
                    continue;
                }
                int start = index;
                while (index < text.Length)
                {
                    char current = text[index];
                    if (char.IsLetterOrDigit(current) || current == '_')
                    {
                        index++;
                        continue;
                    }
                    if (current == '-' && index + 1 < text.Length && char.IsLetterOrDigit(text[index + 1]))
                    {
                        index++;
                        continue;
                    }
                    break;
                }
                tokens.Add(ReadToken(text, start, index, line));
            }
            return tokens;
        }

        private static Token ReadToken(string text, int start, int end, int line)
        {
            Token token = new Token();
            token.Start = start;
            token.End = end;
            token.Line = line;
            token.Text = text.Substring(start, end - start);
            token.Key = token.Text.ToLowerInvariant().Replace('ё', 'е');
            bool lowerSeen = false;
            foreach (char symbol in token.Text)
            {
                if (char.IsDigit(symbol))
                {
                    token.Digit = true;
                }
                else if (symbol == '_' || symbol == '-')
                {
                    token.Underscore |= symbol == '_';
                    token.Hyphen |= symbol == '-';
                    lowerSeen = false; // Petrov-Vodkin и web_Srv не горбы CamelCase
                }
                else if (char.IsLetter(symbol))
                {
                    token.Letters++;
                    if (IsLatinLetter(symbol))
                    {
                        token.LatinLetters++;
                    }
                    else if (IsCyrillicLetter(symbol))
                    {
                        token.CyrillicLetters++;
                    }
                    if (char.IsUpper(symbol))
                    {
                        token.Upper++;
                        token.Hump |= lowerSeen;
                    }
                    else
                    {
                        lowerSeen = true;
                    }
                }
            }
            token.Caps = token.Letters >= 2 && token.Upper == token.Letters;
            token.Title = token.Letters >= 2 && char.IsUpper(token.Text[0]) && !token.Caps && !token.Hump;
            if (start > 0 && (text[start - 1] == '-' || text[start - 1] == '/'))
            {
                char beforeSign = start > 1 ? text[start - 2] : ' ';
                token.Flag = char.IsWhiteSpace(beforeSign) || beforeSign == '-';
            }
            return token;
        }

        /// <summary>Промежуток между подсказкой и словом: пробелы, кавычки и не больше одного «:» или «=».</summary>
        private static bool ReadGap(string text, int from, int to, out bool keyValue)
        {
            keyValue = false;
            int length = to - from;
            if (length <= 0 || length > MaxGap)
            {
                return false;
            }
            int separators = 0;
            for (int position = from; position < to; position++)
            {
                char symbol = text[position];
                if (symbol == ':' || symbol == '=')
                {
                    separators++;
                }
                else if (symbol != ' ' && symbol != '\t' && symbol != (char)0x00A0 && !IsQuote(symbol))
                {
                    return false;
                }
            }
            keyValue = separators == 1;
            return separators <= 1;
        }

        /// <summary>
        /// Если перед словом открывающая кавычка, место закрывающей на той же строке, иначе -1:
        /// «Ромашка», "orion", 'phoenix'.
        /// </summary>
        private static int ClosingQuote(string text, int start)
        {
            if (start == 0)
            {
                return -1;
            }
            char open = text[start - 1];
            char close;
            switch (open)
            {
                case '«': close = '»'; break;
                case '“': close = '”'; break;
                case '„': close = '“'; break;
                case '"': close = '"'; break;
                case '\'': close = '\''; break;
                default: return -1;
            }
            int limit = Math.Min(text.Length, start + MaxQuoted);
            for (int position = start; position < limit; position++)
            {
                char symbol = text[position];
                if (symbol == '\n' || symbol == '\r')
                {
                    return -1;
                }
                if (symbol == close || (open == '“' && symbol == '“'))
                {
                    return position;
                }
            }
            return -1;
        }

        private static bool IsQuote(char symbol)
        {
            switch (symbol)
            {
                case '"': case '\'': case '«': case '»': case '“': case '”': case '„': case '`':
                    return true;
            }
            return false;
        }

        private static bool IsSentenceStart(string text, int start)
        {
            int position = start - 1;
            while (position >= 0)
            {
                char symbol = text[position];
                if (symbol == ' ' || symbol == '\t' || symbol == (char)0x00A0 || IsQuote(symbol) || symbol == '(')
                {
                    position--;
                    continue;
                }
                if (symbol == '-' || symbol == '*' || symbol == '•')
                {
                    // Пункт списка: «- Phoenix упал».
                    int before = position - 1;
                    while (before >= 0 && (text[before] == ' ' || text[before] == '\t'))
                    {
                        before--;
                    }
                    return before < 0 || text[before] == '\n' || text[before] == '\r';
                }
                return symbol == '.' || symbol == '!' || symbol == '?' || symbol == '\n' || symbol == '\r';
            }
            return true;
        }

        private static bool IsDotted(string text, Token token)
        {
            if (token.Start > 0 && text[token.Start - 1] == '.')
            {
                return true;
            }
            return token.End + 1 < text.Length && text[token.End] == '.' && char.IsLetterOrDigit(text[token.End + 1]);
        }

        private static bool OnlySpaces(string text, int from, int to)
        {
            if (to - from <= 0 || to - from > 3)
            {
                return false;
            }
            for (int position = from; position < to; position++)
            {
                if (text[position] != ' ' && text[position] != '\t' && text[position] != (char)0x00A0)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool OnlySpacesOrComma(string text, int from, int to)
        {
            if (to - from <= 0 || to - from > 4)
            {
                return false;
            }
            int commas = 0;
            for (int position = from; position < to; position++)
            {
                char symbol = text[position];
                if (symbol == ',' || symbol == ';')
                {
                    commas++;
                }
                else if (symbol != ' ' && symbol != '\t' && symbol != (char)0x00A0)
                {
                    return false;
                }
            }
            return commas <= 1;
        }

        private static bool HasComma(string text, int from, int to)
        {
            for (int position = from; position < to; position++)
            {
                if (text[position] == ',' || text[position] == ';')
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsLatinLetter(char symbol)
        {
            return (symbol >= 'A' && symbol <= 'Z') || (symbol >= 'a' && symbol <= 'z')
                || (symbol >= (char)0x00C0 && symbol <= (char)0x024F && symbol != (char)0x00D7 && symbol != (char)0x00F7);
        }

        private static bool IsCyrillicLetter(char symbol)
        {
            return symbol >= (char)0x0400 && symbol <= (char)0x04FF;
        }
    }
}
