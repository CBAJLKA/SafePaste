using System;
using System.Collections.Generic;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Поиск ФИО: «Иванов Иван Иванович», «Иван Иванович Иванов», «Иванов И.И.», «И. И. Иванов»,
    /// «Иван Петров» в любом падеже, заглавными буквами и латиницей. Одной заглавной буквы мало:
    /// с неё начинается любое предложение. Нужна опора: отчество, инициалы или имя из словаря
    /// рядом со словом, похожим на фамилию.
    /// </summary>
    internal static class PersonNames
    {
        internal const int Priority = 69;
        private const int MaxInitials = 3;

        // Буква «ё» в ключах заранее заменена на «е».
        private static readonly string[] MalePatronymicEndings = {
            "ович", "овича", "овичу", "овичем", "овиче", "евич", "евича", "евичу", "евичем", "евиче" };
        private static readonly string[] FemalePatronymicEndings = {
            "овна", "овны", "овне", "овну", "овной", "овною", "евна", "евны", "евне", "евну", "евной", "евною" };
        // «Ильинична», «Никитична»: то же окончание у слов вроде «логична», поэтому нужно имя рядом.
        private static readonly string[] WeakPatronymicEndings = { "ична", "ичны", "ичне", "ичну", "ичной", "ичною" };
        private static readonly string[] NominativePatronymicEndings = { "ович", "евич", "овна", "евна", "ична" };
        private static readonly string[] ShortMalePatronymics = { "ильич", "кузьмич", "лукич", "фомич", "никитич", "саввич" };
        private static readonly HashSet<string> WeakMalePatronymics = Forms(ShortMalePatronymics,
            new string[] { "", "а", "у", "ом", "е" });
        private static readonly string[] LatinPatronymicEndings = { "ovich", "evich", "ovitch", "evitch", "ovna", "evna" };

        // Фамилии с падежами: Иванов, Ивановой, Ильина, Достоевского, Черных, Шевченко, Ковальчук, Петросян.
        // Короткие «-ко» и «-ук» не берутся: так кончаются «только» и «ноутбук».
        private static readonly string[] SurnameEndings = {
            "ов", "ова", "ову", "овым", "ове", "овой", "овы", "овых",
            "ев", "ева", "еву", "евым", "еве", "евой", "евы", "евых",
            "ин", "ина", "ину", "иным", "ине", "иной", "ины", "иных",
            "ын", "ына", "ыну", "ыным", "ыне", "ыной",
            "ский", "ская", "ского", "скому", "ским", "ском", "скую", "ской",
            "цкий", "цкая", "цкого", "цкому", "цким", "цком", "цкую", "цкой",
            "ых", "их", "енко", "йко", "шко", "чко", "чук", "щук", "юк",
            "ян", "яна", "яну", "яном", "яне", "янц", "дзе", "швили", "берг", "штейн" };
        private static readonly string[] LatinSurnameEndings = {
            "ov", "ova", "ev", "eva", "in", "ina", "yn", "yna", "sky", "skiy", "skii", "ski", "skaya", "skaia",
            "tsky", "tskiy", "tskaya", "enko", "chuk", "yuk", "yan", "ian", "dze", "shvili", "ykh", "ikh" };

        // Падежи имён: «Ивана» от «Иван», «Марии» от «Мария», «Сергеем» от «Сергей», «Игоря» от «Игорь».
        private static readonly string[] NameCaseEndings = { "ою", "ею", "ой", "ей", "ом", "ем", "а", "я", "у", "ю", "е", "ы", "и" };
        private static readonly string[] NominativeTails = { "", "а", "я", "й", "ь" };

        // Глагол прошедшего времени перед именем: «Обновил Иван Петрович», «Сказала Мария».
        private static readonly string[] PastTenseEndings = { "л", "ла", "ли", "ло", "лся", "лась", "лось", "лись" };

        /// <summary>Слово с заглавной буквы или инициал вроде «И.».</summary>
        private sealed class Token
        {
            internal int Start;
            internal int End;
            internal bool Initial;
            internal bool Latin;
            internal bool Caps;
            internal string Key;
        }

        internal static List<Detection> Find(string text)
        {
            List<Detection> found = new List<Detection>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }
            List<Token> run = new List<Token>();
            int index = 0;
            while (index < text.Length)
            {
                if (!char.IsUpper(text[index]))
                {
                    index++;
                    continue;
                }
                Token previous = run.Count > 0 ? run[run.Count - 1] : null;
                Token token = ReadToken(text, index, previous);
                if (token == null)
                {
                    index = Math.Max(index + 1, LetterRunEnd(text, index));
                    continue;
                }
                if (previous != null && !Adjacent(text, previous, token))
                {
                    Analyze(text, run, found);
                    run.Clear();
                }
                run.Add(token);
                index = token.End;
            }
            Analyze(text, run, found);
            return found;
        }

        // ---------------------------------------------------------------- слова

        private static Token ReadToken(string text, int start, Token previous)
        {
            int length = text.Length;
            if (start > 0)
            {
                bool afterInitial = previous != null && previous.Initial && previous.End == start;
                if (!afterInitial && !IsLeftBoundary(text[start - 1]) && !AfterEscape(text, start))
                {
                    return null;
                }
            }
            int end = LetterRunEnd(text, start);
            // Двойная фамилия или имя через дефис: Петров-Водкин, Анна-Мария.
            if (end + 2 < length && text[end] == '-' && char.IsUpper(text[end + 1]) && char.IsLetter(text[end + 2]))
            {
                end = LetterRunEnd(text, end + 1);
            }
            if (end - start == 1)
            {
                return ReadInitial(text, start);
            }
            if (end < length)
            {
                char after = text[end];
                if (!IsRightBoundary(after) && !BeforeEscape(text, end))
                {
                    return null;
                }
                if (after == '.' && end + 1 < length && char.IsLetterOrDigit(text[end + 1]))
                {
                    return null; // имя файла или домена: Ivanov.docx, Petrov.local
                }
            }
            string word = text.Substring(start, end - start);
            bool latin;
            bool caps;
            if (!ReadScript(word, out latin) || !ReadCase(word, out caps))
            {
                return null;
            }
            Token token = new Token();
            token.Start = start;
            token.End = end;
            token.Latin = latin;
            token.Caps = caps;
            token.Key = Normalize(word);
            return token;
        }

        /// <summary>Инициал: заглавная буква и точка. «Т.е.» и «П.3» инициалами не считаются.</summary>
        private static Token ReadInitial(string text, int start)
        {
            int dot = start + 1;
            if (dot >= text.Length || text[dot] != '.')
            {
                return null;
            }
            if (dot + 1 < text.Length && (char.IsLower(text[dot + 1]) || char.IsDigit(text[dot + 1])))
            {
                return null;
            }
            char letter = text[start];
            bool latin = IsLatinLetter(letter);
            if (!latin && !IsCyrillicLetter(letter))
            {
                return null;
            }
            Token token = new Token();
            token.Start = start;
            token.End = dot + 1;
            token.Initial = true;
            token.Latin = latin;
            token.Key = Normalize(letter.ToString());
            return token;
        }

        private static int LetterRunEnd(string text, int start)
        {
            int end = start;
            while (end < text.Length && char.IsLetter(text[end]))
            {
                end++;
            }
            return end;
        }

        /// <summary>Перед словом пробел или знак препинания, а не часть адреса, пути или идентификатора.</summary>
        private static bool IsLeftBoundary(char symbol)
        {
            if (char.IsWhiteSpace(symbol))
            {
                return true;
            }
            switch (symbol)
            {
                case '(': case '[': case '{': case '"': case '\'': case ',': case ';': case ':': case '!': case '?':
                case '*': case '>': case '\u00AB': case '\u201E': case '\u201C': case '\u201D': case '\u2018':
                case '\u2019': case '\u2013': case '\u2014':
                    return true;
            }
            return false;
        }

        /// <summary>Перевод строки в экранированном тексте (JSON, журналы): «\nИванов И.И.\n».</summary>
        private static bool AfterEscape(string text, int start)
        {
            if (start < 2 || text[start - 2] != '\\')
            {
                return false;
            }
            char code = text[start - 1];
            return code == 'n' || code == 'r' || code == 't';
        }

        private static bool BeforeEscape(string text, int end)
        {
            if (end + 1 >= text.Length || text[end] != '\\')
            {
                return false;
            }
            char code = text[end + 1];
            return code == 'n' || code == 'r' || code == 't' || code == '"';
        }

        private static bool IsRightBoundary(char symbol)
        {
            if (char.IsWhiteSpace(symbol))
            {
                return true;
            }
            switch (symbol)
            {
                case '.': case ',': case ';': case ':': case '!': case '?': case ')': case ']': case '}': case '"':
                case '\'': case '*': case '<': case '-': case '\u00BB': case '\u201C': case '\u201D': case '\u2019':
                case '\u2013': case '\u2014':
                    return true;
            }
            return false;
        }

        /// <summary>Слово целиком кириллицей или целиком латиницей: смесь бывает только в подделках и коде.</summary>
        private static bool ReadScript(string word, out bool latin)
        {
            bool hasLatin = false;
            bool hasCyrillic = false;
            foreach (char symbol in word)
            {
                if (symbol == '-')
                {
                    continue;
                }
                if (IsLatinLetter(symbol))
                {
                    hasLatin = true;
                }
                else if (IsCyrillicLetter(symbol))
                {
                    hasCyrillic = true;
                }
                else
                {
                    latin = false;
                    return false;
                }
            }
            latin = hasLatin;
            return hasLatin != hasCyrillic;
        }

        /// <summary>«Иванов» или «ИВАНОВ». Слова вроде «DevOps» и «iPhone» к именам не относятся.</summary>
        private static bool ReadCase(string word, out bool caps)
        {
            caps = false;
            bool title = false;
            foreach (string part in word.Split('-'))
            {
                if (part.Length < 2 || !char.IsUpper(part[0]))
                {
                    return false;
                }
                bool lower = true;
                bool upper = true;
                for (int index = 1; index < part.Length; index++)
                {
                    lower &= char.IsLower(part[index]);
                    upper &= char.IsUpper(part[index]);
                }
                if (lower)
                {
                    title = true;
                }
                else if (upper)
                {
                    caps = true;
                }
                else
                {
                    return false;
                }
            }
            return title != caps;
        }

        /// <summary>Слова одного имени разделены пробелом, инициалы могут идти вплотную: «И.И.Иванов».</summary>
        private static bool Adjacent(string text, Token previous, Token next)
        {
            int gap = next.Start - previous.End;
            if (gap == 0)
            {
                return previous.Initial;
            }
            if (gap > 3)
            {
                return false;
            }
            for (int index = previous.End; index < next.Start; index++)
            {
                char symbol = text[index];
                if (symbol != ' ' && symbol != '\t' && symbol != '\u00A0')
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- разбор цепочки

        private static void Analyze(string text, List<Token> run, List<Detection> found)
        {
            int index = 0;
            while (index < run.Count)
            {
                int take;
                Confidence confidence;
                if (!Match(run, index, out take, out confidence))
                {
                    index++;
                    continue;
                }
                int start = run[index].Start;
                int end = run[index + take - 1].End;
                Detection detection = new Detection(start, end - start, text.Substring(start, end - start),
                    "PERSON", confidence, Priority);
                // Имя из словаря рядом с непохожим на фамилию словом: догадка, её скрывает строгий режим.
                detection.Enabled = confidence != Confidence.Low;
                found.Add(detection);
                index += take;
            }
        }

        private static bool Match(List<Token> run, int index, out int take, out Confidence confidence)
        {
            take = 0;
            confidence = Confidence.Low;
            Token first = run[index];
            if (first.Initial)
            {
                return MatchInitialsFirst(run, index, out take, out confidence);
            }
            if (!IsNameWord(first))
            {
                return false;
            }
            Token second = At(run, index + 1);
            Token third = At(run, index + 2);
            bool strong;

            // Фамилия Имя Отчество.
            if (IsWord(second) && IsWord(third) && second.Latin == first.Latin && third.Latin == first.Latin
                && IsPatronymic(third, out strong) && IsNameWord(second) && !IsPatronymic(second))
            {
                bool known = IsFirstName(second);
                bool nominative = IsNominativePatronymic(third) && IsNominativeFirstName(second);
                // Без привычной фамилии («Шойгу Сергей Кужугетович») нужен именительный падеж,
                // иначе «Машина Ивана Петровича» считалась бы одним именем.
                if ((strong || known) && (HasSurnameEnding(first) || (known && nominative)))
                {
                    take = 3;
                    confidence = Confidence.High;
                    return true;
                }
            }

            // Имя Отчество и иногда фамилия после них.
            if (IsWord(second) && second.Latin == first.Latin && IsPatronymic(second, out strong) && !IsPatronymic(first)
                && (IsFirstName(first) || (strong && IsNominativePatronymic(second))))
            {
                take = 2;
                if (IsWord(third) && third.Latin == first.Latin && IsNameWord(third) && !IsPatronymic(third)
                    && (HasSurnameEnding(third) || index + 3 == run.Count))
                {
                    take = 3;
                }
                confidence = Confidence.High;
                return true;
            }

            if (second != null && second.Initial && second.Latin == first.Latin)
            {
                int count = CountInitials(run, index + 1);
                Token after = At(run, index + 1 + count);
                // John F. Kennedy: имя, средний инициал, фамилия.
                if (first.Latin && IsFirstName(first) && IsWord(after) && after.Latin && IsNameWord(after))
                {
                    take = count + 2;
                    confidence = HasSurnameEnding(after) ? Confidence.High : Confidence.Medium;
                    return true;
                }
                // Фамилия И. О. Если и после инициалов стоит подходящее слово, а у этого нет окончания
                // фамилии, инициалы относятся к следующему: «Бухгалтер И.И. Кац».
                bool laterSurname = IsWord(after) && after.Latin == first.Latin && IsNameWord(after)
                    && !HasSurnameEnding(first);
                if (!laterSurname && InitialsConfidence(run, index + 1, count, first, out confidence))
                {
                    take = count + 1;
                    return true;
                }
                return false;
            }

            if (IsWord(second) && second.Latin == first.Latin && IsNameWord(second))
            {
                return MatchPair(first, second, out take, out confidence);
            }
            return false;
        }

        /// <summary>И. И. Иванов, I.I. Ivanov, J.R.R. Tolkien.</summary>
        private static bool MatchInitialsFirst(List<Token> run, int index, out int take, out Confidence confidence)
        {
            take = 0;
            confidence = Confidence.Low;
            int count = CountInitials(run, index);
            Token surname = At(run, index + count);
            if (!IsWord(surname) || surname.Latin != run[index].Latin || !IsNameWord(surname))
            {
                return false;
            }
            if (!InitialsConfidence(run, index, count, surname, out confidence))
            {
                return false;
            }
            take = count + 1;
            return true;
        }

        private static bool InitialsConfidence(List<Token> run, int start, int count, Token surname, out Confidence confidence)
        {
            confidence = Confidence.Low;
            if (!run[start].Latin)
            {
                if (count == 2)
                {
                    confidence = Confidence.High;
                    return true;
                }
                // Один инициал: «Приложение А.» не имя, поэтому нужна фамилия с привычным окончанием.
                if (count == 1 && HasSurnameEnding(surname))
                {
                    confidence = Confidence.Medium;
                    return true;
                }
                return false;
            }
            if (count > MaxInitials || IsAbbreviation(run, start, count))
            {
                return false;
            }
            if (count >= 2)
            {
                confidence = HasSurnameEnding(surname) ? Confidence.High : Confidence.Medium;
                return true;
            }
            if (HasSurnameEnding(surname))
            {
                confidence = Confidence.Medium;
                return true;
            }
            return false;
        }

        /// <summary>Имя и фамилия без отчества: «Иван Петров», «Петрова Анна», «John Smith».</summary>
        private static bool MatchPair(Token first, Token second, out int take, out Confidence confidence)
        {
            take = 2;
            confidence = Confidence.Low;
            bool firstName = IsFirstName(first);
            bool secondName = IsFirstName(second);
            if (!first.Latin)
            {
                if (firstName && !secondName && HasSurnameEnding(second))
                {
                    confidence = Confidence.High;
                    return true;
                }
                if (secondName && !firstName && HasSurnameEnding(first))
                {
                    // Фамилия впереди бывает в списках, и там имя в именительном падеже.
                    // «Машина Ивана» и «Логин Сергея» остаются догадкой.
                    confidence = IsNominativeFirstName(second) ? Confidence.High : Confidence.Low;
                    return true;
                }
                // «Сергей Шойгу»: имя в именительном падеже и слово без привычного окончания.
                if (firstName != secondName && (firstName ? IsNominativeFirstName(first) : IsNominativeFirstName(second)))
                {
                    return true;
                }
                return false;
            }
            if (firstName && !secondName && HasSurnameEnding(second))
            {
                confidence = Confidence.High;
                return true;
            }
            if (!firstName && HasSurnameEnding(first) && Lexicons.TranslitFirstNames.Contains(second.Key))
            {
                confidence = Confidence.High;
                return true;
            }
            if (firstName)
            {
                confidence = Confidence.Medium;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- признаки

        private static Token At(List<Token> run, int index)
        {
            return index < run.Count ? run[index] : null;
        }

        private static bool IsWord(Token token)
        {
            return token != null && !token.Initial;
        }

        private static int CountInitials(List<Token> run, int start)
        {
            int count = 0;
            while (start + count < run.Count && run[start + count].Initial && run[start + count].Latin == run[start].Latin)
            {
                count++;
            }
            return count;
        }

        private static bool IsAbbreviation(List<Token> run, int start, int count)
        {
            string letters = string.Empty;
            for (int index = start; index < start + count; index++)
            {
                letters += run[index].Key;
            }
            return Lexicons.InitialAbbreviations.Contains(letters);
        }

        /// <summary>
        /// Может ли слово быть частью ФИО. Заглавными буквами пишут и сокращения (ООО, ИНН, SQL),
        /// поэтому такому слову нужен признак имени, фамилии или отчества.
        /// </summary>
        private static bool IsNameWord(Token token)
        {
            if (token == null || token.Initial || Lexicons.NameStopWords.Contains(token.Key))
            {
                return false;
            }
            if (token.Caps)
            {
                return IsFirstName(token) || HasSurnameEnding(token) || IsPatronymic(token);
            }
            return !LooksLikeVerb(token);
        }

        /// <summary>Глагол прошедшего времени: «Обновил», «Сказала». Имена и фамилии на «-ла» не в счёт.</summary>
        private static bool LooksLikeVerb(Token token)
        {
            if (token.Latin || token.Key.Length < 5 || !EndsWithAny(token.Key, PastTenseEndings))
            {
                return false;
            }
            return !IsFirstName(token) && !HasSurnameEnding(token) && !IsPatronymic(token);
        }

        private static bool IsFirstName(Token token)
        {
            if (token.Latin)
            {
                return Lexicons.TranslitFirstNames.Contains(token.Key) || Lexicons.EnglishFirstNames.Contains(token.Key);
            }
            string key = token.Key;
            if (Lexicons.FirstNames.Contains(key))
            {
                return true;
            }
            foreach (string ending in NameCaseEndings)
            {
                if (key.Length - ending.Length < 2 || !key.EndsWith(ending, StringComparison.Ordinal))
                {
                    continue;
                }
                string stem = key.Substring(0, key.Length - ending.Length);
                if (Lexicons.FleetingNameStems.Contains(stem))
                {
                    return true;
                }
                foreach (string tail in NominativeTails)
                {
                    if (Lexicons.FirstNames.Contains(stem + tail))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsNominativeFirstName(Token token)
        {
            return token.Latin ? IsFirstName(token) : Lexicons.FirstNames.Contains(token.Key);
        }

        private static bool IsPatronymic(Token token)
        {
            bool strong;
            return IsPatronymic(token, out strong);
        }

        /// <summary>Отчество. strong: окончание, которое у обычных слов не встречается (-ович, -евна).</summary>
        private static bool IsPatronymic(Token token, out bool strong)
        {
            strong = false;
            if (token == null || token.Initial)
            {
                return false;
            }
            string key = token.Key;
            if (token.Latin)
            {
                strong = key.Length >= 7 && EndsWithAny(key, LatinPatronymicEndings);
                return strong;
            }
            if (key.Length >= 7 && (EndsWithAny(key, MalePatronymicEndings) || EndsWithAny(key, FemalePatronymicEndings)))
            {
                strong = true;
                return true;
            }
            return WeakMalePatronymics.Contains(key) || (key.Length >= 7 && EndsWithAny(key, WeakPatronymicEndings));
        }

        private static bool IsNominativePatronymic(Token token)
        {
            if (token.Latin)
            {
                return true; // латиницей отчество не склоняют
            }
            return EndsWithAny(token.Key, NominativePatronymicEndings)
                || Array.IndexOf(ShortMalePatronymics, token.Key) >= 0;
        }

        private static bool HasSurnameEnding(Token token)
        {
            if (token == null || token.Initial)
            {
                return false;
            }
            if (token.Latin)
            {
                return token.Key.Length >= 5 && EndsWithAny(token.Key, LatinSurnameEndings);
            }
            return token.Key.Length >= 4 && EndsWithAny(token.Key, SurnameEndings);
        }

        // ---------------------------------------------------------------- фамилия отдельно

        // Склонение фамилий: часть перед окончанием и окончания всех падежей обоих родов.
        private static readonly string[] PossessiveSuffixes = { "ов", "ев", "ин", "ын" };
        private static readonly string[] PossessiveEndings = { "", "а", "у", "ым", "е", "ой", "ою", "ы", "ых", "ыми" };
        private static readonly string[] AdjectiveSuffixes = { "ск", "цк" };
        private static readonly string[] AdjectiveEndings = { "ий", "ая", "ого", "ому", "им", "ом", "ую", "ой", "ою", "ие", "их", "ими" };
        private static readonly string[] ConsonantSuffixes = { "ук", "юк", "ян" };
        private static readonly string[] ConsonantEndings = { "", "а", "у", "ом", "е" };

        /// <summary>
        /// Фамилия из найденного ФИО во всех падежах: из «Иванову Ивану Ивановичу» получатся «Иванов»,
        /// «Иванова», «Иванову» и так далее. По ним скрывается фамилия, которая встречается отдельно:
        /// «Иванов Иван Иванович перезапустил службу... Иванову передали логи». Несклоняемые фамилии
        /// («Шевченко», «Цой») и латиница берутся как есть. Имя без фамилии ничего не даёт.
        /// </summary>
        internal static List<string> SurnameForms(string value)
        {
            List<string> forms = new List<string>();
            if (string.IsNullOrEmpty(value))
            {
                return forms;
            }
            string surname = null;
            Token surnameToken = null;
            int index = 0;
            while (index < value.Length)
            {
                if (!char.IsLetter(value[index]))
                {
                    index++;
                    continue;
                }
                int start = index;
                index = LetterRunEnd(value, index);
                if (index + 2 < value.Length && value[index] == '-' && char.IsUpper(value[index + 1]) && char.IsLetter(value[index + 2]))
                {
                    index = LetterRunEnd(value, index + 1);
                }
                string word = value.Substring(start, index - start);
                bool latin;
                bool caps;
                if (word.Length < 2 || !ReadScript(word, out latin) || !ReadCase(word, out caps))
                {
                    continue;
                }
                Token token = new Token();
                token.Latin = latin;
                token.Caps = caps;
                token.Key = Normalize(word);
                if (IsPatronymic(token) || IsFirstName(token) || Lexicons.NameStopWords.Contains(token.Key))
                {
                    continue;
                }
                if (surname != null)
                {
                    return forms; // два слова без признаков имени: неясно, какое из них фамилия
                }
                surname = word;
                surnameToken = token;
            }
            if (surname == null)
            {
                return forms;
            }
            forms.Add(surname);
            if (!surnameToken.Latin)
            {
                AddDeclension(forms, surname, surnameToken.Key);
            }
            return forms;
        }

        private static void AddDeclension(List<string> forms, string surname, string key)
        {
            string suffix = null;
            string[] endings = null;
            int tail = 0;
            ChooseDeclension(key, PossessiveSuffixes, PossessiveEndings, ref suffix, ref endings, ref tail);
            ChooseDeclension(key, AdjectiveSuffixes, AdjectiveEndings, ref suffix, ref endings, ref tail);
            ChooseDeclension(key, ConsonantSuffixes, ConsonantEndings, ref suffix, ref endings, ref tail);
            if (endings == null)
            {
                return;
            }
            string root = surname.Substring(0, surname.Length - tail);
            // «Семёнов» в тексте могут написать и через «е».
            string plain = root.Replace('ё', 'е').Replace('Ё', 'Е');
            foreach (string ending in endings)
            {
                AddForm(forms, root + suffix + ending);
                AddForm(forms, plain + suffix + ending);
            }
        }

        private static void ChooseDeclension(string key, string[] suffixes, string[] endings,
            ref string bestSuffix, ref string[] bestEndings, ref int bestTail)
        {
            foreach (string suffix in suffixes)
            {
                foreach (string ending in endings)
                {
                    int length = suffix.Length + ending.Length;
                    if (length <= bestTail || key.Length - length < 2 || !key.EndsWith(suffix + ending, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    bestSuffix = suffix;
                    bestEndings = endings;
                    bestTail = length;
                }
            }
        }

        private static void AddForm(List<string> forms, string form)
        {
            foreach (string existing in forms)
            {
                if (string.Equals(existing, form, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            forms.Add(form);
        }

        // ---------------------------------------------------------------- помощники

        private static bool EndsWithAny(string value, string[] endings)
        {
            foreach (string ending in endings)
            {
                if (value.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Normalize(string value)
        {
            return value.ToLowerInvariant().Replace('ё', 'е');
        }

        private static bool IsCyrillicLetter(char symbol)
        {
            return symbol >= '\u0400' && symbol <= '\u04FF';
        }

        private static bool IsLatinLetter(char symbol)
        {
            return (symbol >= 'A' && symbol <= 'Z') || (symbol >= 'a' && symbol <= 'z')
                || (symbol >= '\u00C0' && symbol <= '\u024F' && symbol != '\u00D7' && symbol != '\u00F7');
        }

        private static HashSet<string> Forms(string[] stems, string[] endings)
        {
            HashSet<string> forms = new HashSet<string>(StringComparer.Ordinal);
            foreach (string stem in stems)
            {
                foreach (string ending in endings)
                {
                    forms.Add(stem + ending);
                }
            }
            return forms;
        }
    }
}
