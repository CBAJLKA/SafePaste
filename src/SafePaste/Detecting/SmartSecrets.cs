using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Ищет значения после слов о пароле в обычной речи. Словарь задаёт контекст, а решение о значении
    /// опирается на его форму; грамматическое слово после «пароль» не становится секретом.
    /// </summary>
    internal static class SmartSecrets
    {
        // Русский, английский, украинский, немецкий, французский, испанский, португальский,
        // итальянский, польский и турецкий. Это явный словарь, а не обещание понимать любой язык.
        private static readonly Regex Marker = new Regex(
            @"(?<![\p{L}\p{N}_])(?:парол(?:ь|я|ю|ем|и)|пин[ -]?код|одноразов(?:ый|ого)[ \t]+код|код[ \t]+подтверждения|код[ \t]+доступа|секретн(?:ый|ого)[ \t]+ключ|" +
            @"код[ \t]+из[ \t]+(?:смс|sms)|смс[ -]?код|sms[ -]?code|кодовое[ \t]+слово|" +
            @"password[ \t]+di[ \t]+accesso|password|passwd|passphrase|passcode|pin[ -]?code|one[ -]?time[ \t]+code|verification[ \t]+code|otp|2fa[ \t]+code|secret[ \t]+key|api[ _-]?key|access[ _-]?token|token|" +
            @"ключ[ \t]+доступу|токен|" +
            @"passwort|kennwort|zugangscode|mot[ \t]+de[ \t]+passe|code[ \t]+secret|clé[ \t]+secrète|" +
            @"contraseña|contrasena|clave[ \t]+secreta|senha|palavra[ \t]+passe|chave[ \t]+secreta|" +
            @"parola[ \t]+d['’]ordine|hasło|haslo|kod[ \t]+dostępu|" +
            @"şifre|sifre|parola)(?![\p{L}\p{N}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(750));

        private static readonly HashSet<string> Connectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "is", "was", "are", "equals", "equal", "это", "це", "равен", "равно", "будет",
            "ist", "lautet", "est", "es", "é", "è", "e", "to", "jest", "wynosi", "eşittir"
        };

        private static readonly HashSet<string> Qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "от", "для", "for", "of", "für", "pour", "de", "do", "da", "di", "para", "per", "dla", "için"
        };

        private static readonly HashSet<string> Prose = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "не", "нет", "ещё", "уже", "должен", "должна", "должны", "нужен", "нужна", "неверный",
            "неверен", "изменён", "изменен", "устарел", "забыт", "подходит", "содержит", "минимум",
            "от", "для", "по", "доступа", "not", "no", "must", "should", "needs", "need", "required",
            "invalid", "incorrect", "expired", "changed", "forgotten", "contains", "minimum", "min",
            "for", "from", "of", "to", "new", "old", "and", "or", "has", "have", "be", "at",
            "muss", "nicht", "falsch", "für", "pour", "pas", "doit", "invalide", "de", "da", "di",
            "no", "debe", "inválida", "invalida", "não", "nao", "errada", "nie", "musi", "błędne",
            "yanlış", "yanlis", "gerekli", "geçersiz", "gecersiz", "true", "false", "null", "none"
        };

        internal static List<Detection> Find(string text)
        {
            List<Detection> found = new List<Detection>();
            foreach (Match marker in Marker.Matches(text))
            {
                int end = Math.Min(text.Length, marker.Index + marker.Length + 160);
                int pos = marker.Index + marker.Length;
                bool separated = false;
                while (pos < end && (text[pos] == ' ' || text[pos] == '\t')) pos++;
                while (pos < end && IsSeparator(text[pos]))
                {
                    separated = true;
                    pos++;
                    while (pos < end && (text[pos] == ' ' || text[pos] == '\t')) pos++;
                }
                // «Пароль:» в конце строки, а значение строкой ниже: берётся, только если оно там одно.
                bool nextLine = false;
                if (separated && pos < text.Length && (text[pos] == '\r' || text[pos] == '\n'))
                {
                    if (text[pos] == '\r') pos++;
                    if (pos < text.Length && text[pos] == '\n') pos++;
                    while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t')) pos++;
                    end = Math.Min(text.Length, pos + 160);
                    nextLine = true;
                }
                // «Пароль от почты ...» и «password for admin ...»: одно слово указывает объект,
                // а не значение. Дальше всё равно требуется явный кандидат, похожий на секрет.
                int qualifierEnd = pos;
                while (qualifierEnd < end && char.IsLetter(text[qualifierEnd])) qualifierEnd++;
                if (qualifierEnd > pos && Qualifiers.Contains(text.Substring(pos, qualifierEnd - pos)))
                {
                    pos = qualifierEnd;
                    while (pos < end && (text[pos] == ' ' || text[pos] == '\t')) pos++;
                    int objectEnd = pos;
                    while (objectEnd < end && (char.IsLetterOrDigit(text[objectEnd]) || text[objectEnd] == '_')) objectEnd++;
                    if (objectEnd - pos >= 2 && objectEnd - pos <= 32)
                    {
                        pos = objectEnd;
                        while (pos < end && (text[pos] == ' ' || text[pos] == '\t' || IsSeparator(text[pos]))) pos++;
                    }
                }
                // «Пароль пользователя: ...», «Password admin: ...»: слово с двоеточием подписывает значение,
                // а не является им. Значение ищется после двоеточия.
                int labelEnd = pos;
                while (labelEnd < end && char.IsLetter(text[labelEnd])) labelEnd++;
                if (labelEnd > pos && labelEnd - pos <= 32 && labelEnd < end && text[labelEnd] == ':')
                {
                    pos = labelEnd + 1;
                    while (pos < end && (text[pos] == ' ' || text[pos] == '\t')) pos++;
                }
                for (int count = 0; count < 2; count++)
                {
                    int wordEnd = pos;
                    while (wordEnd < end && char.IsLetter(text[wordEnd])) wordEnd++;
                    if (wordEnd == pos || !Connectors.Contains(text.Substring(pos, wordEnd - pos))
                        || (wordEnd < end && char.IsLetterOrDigit(text[wordEnd]))) break;
                    pos = wordEnd;
                    while (pos < end && (text[pos] == ' ' || text[pos] == '\t' || IsSeparator(text[pos]))) pos++;
                }
                if (pos >= end || text[pos] == '\r' || text[pos] == '\n') continue;

                char quote = ClosingQuote(text[pos]);
                bool quoted = quote != '\0';
                int start = quoted ? pos + 1 : pos;
                int stop = start;
                if (quoted)
                {
                    while (stop < end && text[stop] != quote && text[stop] != '\r' && text[stop] != '\n') stop++;
                    if (stop >= end || text[stop] != quote) continue;
                }
                else
                {
                    while (stop < end && !char.IsWhiteSpace(text[stop]) && !IsBoundary(text[stop])) stop++;
                    while (stop > start && (text[stop - 1] == '.' || text[stop - 1] == ',')) stop--;
                }
                if (stop <= start) continue;
                // «Password: [hidden]», «Пароль (не задан)»: заглушка в скобках, а не значение.
                if (quoted && (text[pos] == '(' || text[pos] == '[')
                    && ContextSecrets.IsReference(text.Substring(pos, stop + 1 - pos))) continue;
                string value = text.Substring(start, stop - start);
                if (!LooksLikeValue(value, quoted)) continue;
                if (nextLine && !RestOfLineEmpty(text, quoted ? stop + 1 : stop)) continue;
                Detection detection = new Detection(start, value.Length, value, "SECRET", Confidence.High, 100);
                detection.Locked = true;
                detection.Source = "Интеллектуальный анализ";
                detection.Reason = "Похоже на значение после обозначения секрета";
                found.Add(detection);
            }
            return found;
        }

        private static bool RestOfLineEmpty(string text, int position)
        {
            for (int index = position; index < text.Length && text[index] != '\n'; index++)
            {
                if (!char.IsWhiteSpace(text[index])) return false;
            }
            return true;
        }

        private static bool IsSeparator(char ch)
        {
            return ch == ':' || ch == '=' || ch == '-' || ch == '—' || ch == '–' || ch == '→';
        }

        private static bool IsBoundary(char ch)
        {
            return ch == ',' || ch == ';' || ch == '<' || ch == '>' || ch == '(' || ch == ')' || ch == '[' || ch == ']'
                || ch == '{' || ch == '}' || ch == '"' || ch == '\'' || ch == '«' || ch == '»';
        }

        private static char ClosingQuote(char ch)
        {
            switch (ch)
            {
                case '"': case '\'': case '`': return ch;
                case '«': return '»';
                case '(': return ')';
                case '[': return ']';
                case '“': return '”';
                case '‘': return '’';
                default: return '\0';
            }
        }

        private static bool LooksLikeValue(string value, bool quoted)
        {
            if (value.Length < 4 || value.Length > 128 || Prose.Contains(value) || ContextSecrets.IsReference(value)) return false;
            int letters = 0, digits = 0, symbols = 0, upper = 0, lower = 0;
            foreach (char ch in value)
            {
                if (char.IsControl(ch)) return false;
                if (char.IsLetter(ch))
                {
                    letters++;
                    if (char.IsUpper(ch)) upper++;
                    if (char.IsLower(ch)) lower++;
                }
                else if (char.IsDigit(ch)) digits++;
                else if (!char.IsWhiteSpace(ch)) symbols++;
            }
            if (quoted) return letters + digits >= 4;
            if (value.IndexOf(' ') >= 0) return false;
            if (digits > 0 && (letters > 0 || symbols > 0)) return value.Length >= 5;
            if (digits == value.Length) return value.Length >= 4 && value.Length <= 16;
            if (letters > 0 && symbols > 0) return value.Length >= 6;
            return upper > 0 && lower > 0 && value.Length >= 7;
        }
    }
}
