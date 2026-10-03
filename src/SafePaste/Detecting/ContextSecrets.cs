using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Секреты, которые выдаёт не ключ перед значением, а соседство и форма: пара «логин:пароль»,
    /// пароли столбиком под заголовком «Пароли», одиночная строка, похожая на пароль, base64 от
    /// «логин:пароль» после Basic, номер карты с верной контрольной цифрой, «учётка: логин / пароль».
    /// </summary>
    internal static class ContextSecrets
    {
        private const string Source = "Контекст";
        private const RegexOptions Options = RegexOptions.CultureInvariant;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(750);

        // login:password отдельным словом: в начале строки, после пробела или кавычки.
        private static readonly Regex Pair = new Regex(
            @"(?<![^\s""'`])(?<login>\[(?:USER|EMAIL)_\d+\]|[\p{L}\p{N}_$][\p{L}\p{N}._$@\\-]{0,127}):(?<value>[^\s:""'`]{3,128})(?=[\s""'`]|$)",
            Options, Timeout);

        // Basic и base64 от «логин:пароль»: заголовок без Authorization, auth в config.json Docker, _auth в .npmrc.
        private static readonly Regex Basic = new Regex(
            @"(?:\bBasic[ \t]+|""auth""[ \t]*:[ \t]*""|(?<![\w])_auth[ \t]*=[ \t]*)(?<value>[A-Za-z0-9+/]{6,}={0,2})(?![A-Za-z0-9+/=])",
            Options | RegexOptions.IgnoreCase, Timeout);

        private static readonly Regex GroupedCard = new Regex(
            @"(?<![\d-])(?:\d{4}(?:[ -]\d{4}){3}(?:[ -]?\d{1,3})?|\d{4}[ -]\d{6}[ -]\d{4,5})(?![\d-])",
            Options, Timeout);

        private static readonly Regex PlainCard = new Regex(
            @"(?<![\p{L}\p{N}_.-])\d{13,19}(?![\p{L}\p{N}_-]|\.\d)", Options, Timeout);

        private static readonly Regex CardWords = new Regex(
            @"(?:card|карт|\bcc|\bpan\b|visa|master|amex|maestro|мир\b|кредитк|credit|debit)",
            Options | RegexOptions.IgnoreCase, Timeout);

        // «учётка: admin / P@ss», «логин/пароль: admin / P@ss», «creds admin:P@ss».
        private static readonly Regex CredentialPhrase = new Regex(
            @"(?<![\p{L}])(?:логин[ \t]*(?:/|и|,|&)[ \t]*пароль|login[ \t]*(?:/|and|&|,)[ \t]*password|user(?:name)?[ \t]*(?:/|and|&|,)[ \t]*pass(?:word)?|" +
            @"уч[её]тн(?:ая|ые|ой)[ \t]+(?:запись|записи|данные)|уч[её]тк[аиуе]|креды|кредлы|credentials|creds|доступы?)(?![\p{L}])" +
            @"[ \t]*[:=-]?[ \t]*(?<user>[^\s/|:,;]{2,64})[ \t]*(?<sep>/|\||:|,|;|[ \t]+)[ \t]*(?<value>[^\s]{3,128})",
            Options | RegexOptions.IgnoreCase, Timeout);

        // Заголовок списка паролей: «# Passwords», «Пароли:», «-- Recovery codes».
        private static readonly Regex HeadingWords = new Regex(
            @"(?<![\p{L}])(?:passwords?|passwd|парол\p{L}*|secrets?|секрет\p{L}*|credentials?|creds|кред\p{L}*|уч[её]тк\p{L}*|pins?|пин[ -]?код\p{L}*|коды?[ \t]+восстановления|recovery[ \t]+codes?|backup[ \t]+codes?)(?![\p{L}])",
            Options | RegexOptions.IgnoreCase, Timeout);

        private static readonly Regex Bullet = new Regex(@"^(?:[-*+•]|\d{1,3}[.)])[ \t]+", Options, Timeout);

        private static readonly Regex Email = new Regex(@"^[\w.+-]+@[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+$", Options, Timeout);

        /// <summary>Схемы адресов и подписи, после которых двоеточие не отделяет пароль.</summary>
        private static readonly HashSet<string> NotLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http", "https", "ftp", "ftps", "sftp", "ssh", "file", "mailto", "tel", "urn", "data", "javascript", "ldap",
            "ldaps", "smb", "nfs", "git", "svn", "jdbc", "redis", "rediss", "mongodb", "postgresql",
            "amqp", "amqps", "mqtt", "ws", "wss", "s3", "gs", "hdfs", "about", "chrome", "edge", "ms-settings", "steam",
            "magnet", "sip", "xmpp", "irc", "vnc", "rdp", "telnet", "news", "nntp", "webcal", "feed", "blob", "otpauth",
            "error", "warning", "warn", "info", "debug", "trace", "fatal", "note", "todo", "fixme", "hack", "step",
            "example", "image", "from", "to", "cc", "bcc", "subject", "date", "re", "fwd", "tag", "label", "version",
            "localhost", "host", "port", "server", "node", "pod", "container", "service", "namespace", "ошибка", "пример",
            // Подписи полей в логах и таблицах: «Build:Release2024», «Status:Active2024».
            "build", "category", "status", "state", "stage", "type", "kind", "class", "level", "mode", "result",
            "region", "zone", "release", "branch", "commit", "project", "sheet", "table", "column", "field", "section",
            "page", "row", "item", "group", "format", "source", "target", "channel", "profile", "phase", "priority",
            "severity", "env", "environment", "platform", "model", "product", "edition", "tier", "location", "site"
        };

        /// <summary>Учётные записи, которые почти всегда стоят слева от пароля.</summary>
        private static readonly HashSet<string> AccountNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "admin", "administrator", "root", "user", "sa", "guest", "test", "tester", "oracle", "postgres", "mysql",
            "ubuntu", "debian", "centos", "ec2-user", "pi", "ftp", "backup", "operator", "support", "service", "svc",
            "demo", "manager", "superuser", "sysadmin", "dbadmin", "elastic", "kibana", "grafana", "jenkins", "deploy",
            "ansible", "vagrant", "docker", "admin1", "adm", "webadmin", "netadmin", "cisco", "ubnt", "mikrotik",
            "vpn", "ldap", "smtp", "mail", "zabbix", "nagios", "monitor", "api", "bot", "sys", "system", "sysdba"
        };

        /// <summary>Слова, которые стоят на месте значения, но паролем не являются.</summary>
        private static readonly HashSet<string> NotValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "null", "none", "nil", "undefined", "true", "false", "empty", "string", "str", "int", "bool", "boolean",
            "required", "optional", "default", "yes", "no", "on", "off", "enabled", "disabled", "unknown", "n/a"
        };

        /// <summary>Заглушки вместо значения: «Password: TBD», «Пароль: нет», «password:\n  - item» из примеров.</summary>
        private static readonly HashSet<string> Placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tbd", "tba", "todo", "redacted", "censored", "omitted", "unset", "notset", "нет", "пусто", "отсутствует",
            "item", "items", "value", "example", "sample", "foo", "bar", "baz"
        };

        /// <summary>
        /// Слова, с которых начинается фраза, а не пароль: «Пароль: не менее 8 символов», «Token: expired»,
        /// «Password: see vault». Частых паролей (admin, qwerty, password, secret) здесь нет.
        /// </summary>
        private static readonly HashSet<string> ProseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "не", "ни", "нет", "без", "для", "от", "до", "из", "под", "над", "при", "про", "через", "или", "но", "же",
            "см", "смотри", "смотрите", "тот", "та", "то", "те", "такой", "такая", "такое", "тоже", "также", "так",
            "как", "его", "её", "ее", "их", "мой", "твой", "наш", "ваш", "свой", "этот", "эта", "это", "один", "одна",
            "другой", "любой", "какой", "который", "обязательно", "обязателен", "обязательный", "нужен", "нужна",
            "требуется", "изменён", "изменен", "изменили", "сменён", "сменен", "сменили", "истёк", "истек", "истекает",
            "устарел", "сброшен", "сбрасывается", "задаётся", "задается", "задан", "выдаётся", "выдается", "выдаёт",
            "выдает", "указан", "указано", "известен", "неизвестен", "неверный", "верный", "правильный", "скрыт",
            "скрыто", "включён", "включен", "выключен", "отключён", "отключен", "есть", "был", "была", "будет",
            "прежний", "старый", "новый", "общий", "стандартный", "временный", "ниже", "выше", "далее", "здесь", "там",
            "спросить", "спросите", "уточнить", "уточните", "узнать", "узнайте", "лежит", "хранится",
            "not", "no", "same", "see", "the", "an", "in", "at", "on", "by", "from", "with", "is", "are", "was",
            "were", "will", "be", "been", "has", "have", "must", "should", "can", "may", "need", "needs", "use",
            "used", "ask", "contact", "click", "press", "check", "enter", "type", "set", "sent", "provided", "given",
            "changed", "expired", "expires", "invalid", "incorrect", "wrong", "unknown", "unchanged", "hidden",
            "masked", "removed", "below", "above", "attached", "inside", "stored", "saved", "managed", "generated",
            "rotate", "rotated", "your", "my", "our", "their", "its", "this", "that", "these", "those", "it", "as",
            "per", "via", "for", "to", "of", "and", "or", "if", "when", "only", "also", "still", "old", "previous",
            "current", "temporary", "minimum", "min"
        };

        private static readonly Regex Shortcut = new Regex(@"^(?:ctrl|alt|shift|win|cmd|fn|super|option)\+\S",
            Options | RegexOptions.IgnoreCase, Timeout);

        private static readonly Regex CyrillicWord = new Regex(@"^[\p{IsCyrillic}'-]+$", Options, Timeout);

        private static readonly Regex VersionNumber = new Regex(@"\d\.\d", Options, Timeout);

        private static readonly string[] ReferencePrefixes =
        {
            "${", "{{", "$(", "#{", "%{", "$env:", "process.env", "os.environ", "os.getenv", "getenv(", "System.getenv",
            "Environment.GetEnvironmentVariable", "ENV[", "env(", "secrets.", "vars.", "var.", "local.", "data.",
            "module.", "lookup(", "vault(", "!vault", "!Ref", "!Sub", "!GetAtt", "Fn::", "ref+", "op://", "keyvault:",
            "@Microsoft.KeyVault(", "sm://", "ssm:", "arn:aws:secretsmanager", "vault:"
        };

        internal static List<Detection> Find(string text, bool smart)
        {
            List<Detection> found = new List<Detection>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }
            FindPairs(text, found);
            FindBasic(text, found);
            FindCards(text, found);
            if (smart)
            {
                FindLines(text, found);
                FindCredentialPhrases(text, found);
            }
            return found;
        }

        /// <summary>
        /// Значение не секрет, а ссылка на него: ${DB_PASSWORD}, {{ vault_pw }}, os.getenv("X"), var.db_password,
        /// &lt;password&gt;, ********. Такие значения скрывать незачем: они ничего не выдают.
        /// </summary>
        internal static bool IsReference(string value)
        {
            if (value == null)
            {
                return true;
            }
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || NotValues.Contains(trimmed))
            {
                return true;
            }
            foreach (string prefix in ReferencePrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (trimmed.Length > 2 && trimmed[0] == '%' && trimmed[trimmed.Length - 1] == '%'
                && IsIdentifier(trimmed.Substring(1, trimmed.Length - 2), false))
            {
                return true; // %DB_PASSWORD%
            }
            if (trimmed.Length > 1 && trimmed[0] == '$' && IsIdentifier(trimmed.Substring(1), true))
            {
                return true; // $DB_PASSWORD
            }
            if (trimmed.Length > 2 && trimmed[0] == '<' && trimmed[trimmed.Length - 1] == '>')
            {
                return true; // <password>
            }
            if (IsPlaceholder(trimmed))
            {
                return true;
            }
            return trimmed.Length >= 3 && (Repeated(trimmed, '*') || (trimmed.Length >= 4 && (Repeated(trimmed, 'x') || Repeated(trimmed, 'X'))));
        }

        /// <summary>
        /// Заглушка на месте значения: «-», «...», «???», «(empty)», «[hidden]», «TBD», «Пароль: нет»,
        /// сочетание клавиш «Ctrl+Alt+Del».
        /// </summary>
        private static bool IsPlaceholder(string value)
        {
            if (Placeholders.Contains(value) || Shortcut.IsMatch(value))
            {
                return true;
            }
            bool marks = true;
            foreach (char symbol in value)
            {
                if (symbol != '.' && symbol != '?' && symbol != '_' && symbol != (char)0x2026
                    && char.GetUnicodeCategory(symbol) != System.Globalization.UnicodeCategory.DashPunctuation)
                {
                    marks = false;
                    break;
                }
            }
            if (marks)
            {
                return true;
            }
            // Слово в скобках: (empty), [hidden], (не задан).
            char first = value[0];
            char last = value[value.Length - 1];
            if (value.Length > 2 && ((first == '(' && last == ')') || (first == '[' && last == ']')))
            {
                foreach (char symbol in value.Substring(1, value.Length - 2))
                {
                    if (!char.IsLetter(symbol) && symbol != ' ')
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Значение после «ключ:» на деле начало фразы: «Пароль: не менее 8 символов», «Token: expired»,
        /// «Пароль: сбрасывается через портал», «PIN: 4 цифры». Проверяется только значение без кавычек сразу
        /// после двоеточия: в конфигурации с «=» и в кавычках так не пишут. Латинское слово в русской фразе
        /// («Пароль: qwerty для входа») остаётся паролем.
        /// </summary>
        internal static bool IsProseValue(string text, int start, int length)
        {
            int before = start - 1;
            while (before >= 0 && (text[before] == ' ' || text[before] == '\t'))
            {
                before--;
            }
            if (before < 0 || text[before] != ':' || length == 0)
            {
                return false;
            }
            string value = text.Substring(start, length);
            if (value.Length < 3)
            {
                return true; // «PIN: 4 цифры», «Пароль: у Иванова»
            }
            string core = value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            if (core.Length == 0)
            {
                return true;
            }
            foreach (char symbol in core)
            {
                if (!char.IsLetter(symbol) && symbol != '-' && symbol != '\'')
                {
                    return false; // цифры и знаки бывают в паролях, а не во фразах
                }
            }
            if (ProseWords.Contains(core))
            {
                return true;
            }
            return CyrillicWord.IsMatch(core) && NextWordIsCyrillic(text, start + length);
        }

        private static bool NextWordIsCyrillic(string text, int position)
        {
            if (position >= text.Length || (text[position] != ' ' && text[position] != '\t'))
            {
                return false;
            }
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t'))
            {
                position++;
            }
            int end = position;
            while (end < text.Length && char.IsLetter(text[end]))
            {
                end++;
            }
            return end > position && CyrillicWord.IsMatch(text.Substring(position, end - position));
        }

        // ---------------------------------------------------------------- логин:пароль

        private static void FindPairs(string text, List<Detection> found)
        {
            foreach (Match match in Detector.Collect(Pair, text))
            {
                string login = match.Groups["login"].Value;
                Group value = match.Groups["value"];
                if (!IsLogin(login))
                {
                    continue;
                }
                bool account = IsAccount(login);
                bool wholeLine = IsWholeLine(text, match.Index, match.Index + match.Length);
                // Запятая или точка после пары относится к фразе: «admin:Router_2024!, не забудь сменить».
                string password = value.Value.TrimEnd(',', ';', '.', ')', ']');
                if (!IsPairPassword(password, account, wholeLine))
                {
                    continue;
                }
                found.Add(Secret(value.Index, password, "пара «логин:пароль»"));
            }
        }

        private static bool IsLogin(string login)
        {
            if (NotLogins.Contains(login) || login.EndsWith(".", StringComparison.Ordinal) || login.EndsWith("\\", StringComparison.Ordinal))
            {
                return false;
            }
            foreach (char symbol in login)
            {
                if (char.IsLetter(symbol))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Учётная запись: admin, root, svc_backup, CORP\user, user@domain, root@pam.</summary>
        private static bool IsAccount(string login)
        {
            if (login.StartsWith("[USER_", StringComparison.Ordinal) || login.StartsWith("[EMAIL_", StringComparison.Ordinal))
            {
                return true;
            }
            if (AccountNames.Contains(login) || login.IndexOf('\\') > 0 || login.IndexOf('@') > 0)
            {
                return true;
            }
            string lower = login.ToLowerInvariant();
            return lower.StartsWith("svc_", StringComparison.Ordinal) || lower.StartsWith("svc-", StringComparison.Ordinal)
                || lower.StartsWith("sa_", StringComparison.Ordinal) || lower.StartsWith("adm_", StringComparison.Ordinal)
                || lower.StartsWith("admin", StringComparison.Ordinal);
        }

        private static bool IsPairPassword(string value, bool account, bool wholeLine)
        {
            if (value.Length < 4 || value.Length > 128 || IsReference(value) || Email.IsMatch(value))
            {
                return false;
            }
            char first = value[0];
            if (first == '/' || first == '\\' || first == '[' || first == '{' || first == '<' || first == '(' || first == '=')
            {
                return false;
            }
            Shape shape = new Shape(value);
            if (shape.Letters == 0)
            {
                return false; // порт, время, номер
            }
            if (shape.Strong > 0 && shape.Digits > 0)
            {
                return value.Length >= 6 || account;
            }
            if (shape.Upper > 0 && shape.Lower > 0 && shape.Digits > 0)
            {
                return value.Length >= 8 || account;
            }
            if (shape.Strong > 0 && shape.Upper > 0 && shape.Lower > 0)
            {
                return value.Length >= 8 && (wholeLine || account);
            }
            // Слева обычная учётная запись: admin:qwerty123, root:toor.
            return account && value.Length >= 4 && shape.Letters + shape.Digits >= 4;
        }

        // ---------------------------------------------------------------- Basic и base64

        private static void FindBasic(string text, List<Detection> found)
        {
            foreach (Match match in Detector.Collect(Basic, text))
            {
                Group value = match.Groups["value"];
                if (DecodesToCredentials(value.Value))
                {
                    found.Add(Secret(value.Index, value.Value, "base64 от «логин:пароль»"));
                }
            }
        }

        /// <summary>base64 раскрывается в печатное «логин:пароль».</summary>
        internal static bool DecodesToCredentials(string value)
        {
            string padded = value.TrimEnd('=');
            while (padded.Length % 4 != 0)
            {
                padded += "=";
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(padded);
            }
            catch (FormatException)
            {
                return false;
            }
            string decoded;
            try
            {
                decoded = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                return false;
            }
            int colon = decoded.IndexOf(':');
            if (colon < 1 || colon == decoded.Length - 1)
            {
                return false;
            }
            foreach (char symbol in decoded)
            {
                if (char.IsControl(symbol) || char.IsWhiteSpace(symbol))
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- номера карт

        private static void FindCards(string text, List<Detection> found)
        {
            foreach (Match match in Detector.Collect(GroupedCard, text))
            {
                if (IsCardNumber(Digits(match.Value)))
                {
                    found.Add(Secret(match.Index, match.Value, "номер карты"));
                }
            }
            foreach (Match match in Detector.Collect(PlainCard, text))
            {
                // Без пробелов номер легко спутать с другим числом, поэтому нужна подпись рядом.
                if (IsCardNumber(match.Value) && CardWords.IsMatch(Before(text, match.Index, 48)))
                {
                    found.Add(Secret(match.Index, match.Value, "номер карты"));
                }
            }
        }

        internal static bool IsCardNumber(string digits)
        {
            if (digits.Length < 13 || digits.Length > 19 || !KnownIssuer(digits))
            {
                return false;
            }
            int sum = 0;
            bool twice = false;
            for (int index = digits.Length - 1; index >= 0; index--)
            {
                int digit = digits[index] - '0';
                if (twice)
                {
                    digit *= 2;
                    if (digit > 9)
                    {
                        digit -= 9;
                    }
                }
                sum += digit;
                twice = !twice;
            }
            return sum % 10 == 0;
        }

        /// <summary>Visa, Mastercard, Мир, American Express, Discover, JCB, UnionPay, Maestro.</summary>
        private static bool KnownIssuer(string digits)
        {
            int two = int.Parse(digits.Substring(0, 2));
            int four = int.Parse(digits.Substring(0, 4));
            return digits[0] == '4' || (two >= 51 && two <= 55) || (four >= 2200 && four <= 2720) || two == 34 || two == 37
                || four == 6011 || two == 65 || (four >= 6440 && four <= 6499) || two == 62 || two == 35
                || two == 50 || (two >= 56 && two <= 69);
        }

        private static string Digits(string value)
        {
            StringBuilder digits = new StringBuilder(value.Length);
            foreach (char symbol in value)
            {
                if (char.IsDigit(symbol))
                {
                    digits.Append(symbol);
                }
            }
            return digits.ToString();
        }

        // ---------------------------------------------------------------- строки-пароли

        /// <summary>
        /// Строки из одного слова. Под заголовком про пароли (до пустой строки) секретом считается любое
        /// такое слово, кроме адресов и пар «ключ=значение». Без заголовка только слово, в котором есть
        /// заглавные и строчные буквы, цифры и спецсимвол: Password123!, P@ssw0rd.
        /// </summary>
        private static void FindLines(string text, List<Detection> found)
        {
            bool list = false;
            string heading = null;
            int position = 0;
            while (position < text.Length)
            {
                int end = text.IndexOf('\n', position);
                if (end < 0)
                {
                    end = text.Length;
                }
                int lineEnd = end > position && text[end - 1] == '\r' ? end - 1 : end;
                string line = text.Substring(position, lineEnd - position);
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    list = false;
                }
                else if (IsHeading(trimmed))
                {
                    list = HeadingWords.IsMatch(trimmed);
                    heading = trimmed;
                }
                else
                {
                    int start;
                    string token = SingleToken(line, out start);
                    if (token != null)
                    {
                        if (list && IsListedSecret(token))
                        {
                            found.Add(Secret(position + start, token, "в списке под заголовком «" + Short(heading) + "»"));
                        }
                        else if (IsStrongPassword(token))
                        {
                            found.Add(Secret(position + start, token, "строка похожа на пароль"));
                        }
                    }
                }
                position = end + 1;
            }
        }

        private static bool IsHeading(string trimmed)
        {
            if (trimmed.Length > 80)
            {
                return false;
            }
            return trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("--", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)
                || (trimmed.EndsWith(":", StringComparison.Ordinal) && trimmed.IndexOf(' ') >= 0)
                || (trimmed.EndsWith(":", StringComparison.Ordinal) && HeadingWords.IsMatch(trimmed));
        }

        /// <summary>Строка из одного слова, возможно с маркером списка или в кавычках. null: слов больше одного.</summary>
        private static string SingleToken(string line, out int start)
        {
            start = 0;
            while (start < line.Length && (line[start] == ' ' || line[start] == '\t'))
            {
                start++;
            }
            Match bullet = Bullet.Match(line.Substring(start));
            if (bullet.Success)
            {
                start += bullet.Length;
            }
            int end = line.Length;
            while (end > start && char.IsWhiteSpace(line[end - 1]))
            {
                end--;
            }
            if (end - start >= 2 && IsQuote(line[start]) && line[end - 1] == line[start])
            {
                start++;
                end--;
            }
            if (end <= start)
            {
                return null;
            }
            string token = line.Substring(start, end - start);
            foreach (char symbol in token)
            {
                if (char.IsWhiteSpace(symbol))
                {
                    return null;
                }
            }
            return token;
        }

        private static bool IsListedSecret(string token)
        {
            if (token.Length < 4 || token.Length > 128 || IsReference(token) || Email.IsMatch(token))
            {
                return false;
            }
            if (token.IndexOf('=') >= 0 || token.IndexOf(':') >= 0 || token.IndexOf("//", StringComparison.Ordinal) >= 0)
            {
                return false; // ключ=значение, логин:пароль и адреса разбирают свои правила
            }
            char first = token[0];
            return first != '/' && first != '\\' && first != '#' && first != '[' && first != '{' && first != '<';
        }

        private static bool IsStrongPassword(string token)
        {
            if (token.Length < 8 || token.Length > 64 || Email.IsMatch(token))
            {
                return false;
            }
            char first = token[0];
            if (first == '$' || first == '#' || first == '@' || first == '%' || first == '&')
            {
                return false; // переменная, директива, аннотация
            }
            if (VersionNumber.IsMatch(token))
            {
                return false; // версия: v1.2.3-Beta!, C#7.3+
            }
            int equals = token.IndexOf('=');
            if (equals >= 0 && token.TrimEnd('=').Length > equals)
            {
                return false; // ключ=значение; «=» бывает только в конце base64
            }
            int strong = 0;
            foreach (char symbol in token)
            {
                if (char.IsLetterOrDigit(symbol) || symbol == '.' || symbol == '-' || symbol == '_')
                {
                    continue;
                }
                if ("!@#$%^&*?~+=".IndexOf(symbol) < 0)
                {
                    return false; // скобки, кавычки, слэши и запятые бывают в коде и путях
                }
                strong++;
            }
            Shape shape = new Shape(token);
            return strong > 0 && shape.Upper > 0 && shape.Lower > 0 && shape.Digits > 0;
        }

        // ---------------------------------------------------------------- «учётка: логин / пароль»

        private static void FindCredentialPhrases(string text, List<Detection> found)
        {
            foreach (Match match in Detector.Collect(CredentialPhrase, text))
            {
                Group user = match.Groups["user"];
                Group value = match.Groups["value"];
                string secret = value.Value.TrimEnd('.', ',', ';', ')');
                if (secret.Length < 3 || IsReference(secret))
                {
                    continue;
                }
                bool spaced = match.Groups["sep"].Value.Trim().Length == 0;
                Shape shape = new Shape(secret);
                bool strong = shape.Digits > 0 && shape.Letters > 0 || shape.Strong > 0 || (shape.Upper > 0 && shape.Lower > 0);
                if (!strong || (spaced && !(shape.Digits > 0 && (shape.Strong > 0 || shape.Upper > 0))))
                {
                    continue; // «учётная запись admin заблокирована» не пара
                }
                found.Add(Secret(value.Index, secret, "после «" + Short(match.Value.Substring(0, user.Index - match.Index).Trim()) + "»"));
                Detection login = new Detection(user.Index, user.Length, user.Value, "USER", Confidence.High, 66);
                login.Source = Source;
                login.Reason = "логин перед паролем";
                found.Add(login);
            }
        }

        // ---------------------------------------------------------------- помощники

        private static Detection Secret(int start, string value, string reason)
        {
            Detection detection = new Detection(start, value.Length, value, "SECRET", Confidence.High, 100);
            detection.Locked = true;
            detection.Source = Source;
            detection.Reason = reason;
            return detection;
        }

        private static bool IsWholeLine(string text, int start, int end)
        {
            for (int index = start - 1; index >= 0 && text[index] != '\n'; index--)
            {
                if (text[index] != ' ' && text[index] != '\t')
                {
                    return false;
                }
            }
            for (int index = end; index < text.Length && text[index] != '\n'; index++)
            {
                if (!char.IsWhiteSpace(text[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string Before(string text, int index, int count)
        {
            int start = Math.Max(0, index - count);
            int newline = text.LastIndexOf('\n', Math.Max(0, index - 1), index - start);
            if (newline >= start)
            {
                start = newline + 1;
            }
            return text.Substring(start, index - start);
        }

        private static string Short(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            string trimmed = value.Trim().TrimStart('#', '/', '-', ';', '[', ' ').TrimEnd(':', ']', ' ');
            return trimmed.Length <= 40 ? trimmed : trimmed.Substring(0, 39) + "…";
        }

        private static bool IsQuote(char symbol)
        {
            return symbol == '"' || symbol == '\'' || symbol == '`';
        }

        private static bool IsIdentifier(string value, bool upper)
        {
            if (value.Length == 0 || char.IsDigit(value[0]))
            {
                return false;
            }
            foreach (char symbol in value)
            {
                bool allowed = symbol == '_' || (symbol >= '0' && symbol <= '9') || (symbol >= 'A' && symbol <= 'Z')
                    || (!upper && symbol >= 'a' && symbol <= 'z');
                if (!allowed)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Repeated(string value, char symbol)
        {
            foreach (char current in value)
            {
                if (current != symbol)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Из чего состоит значение. Точка, дефис и подчёркивание к спецсимволам не относятся: они есть в версиях и именах.</summary>
        private struct Shape
        {
            internal int Letters;
            internal int Upper;
            internal int Lower;
            internal int Digits;
            internal int Strong;

            internal Shape(string value)
            {
                Letters = 0;
                Upper = 0;
                Lower = 0;
                Digits = 0;
                Strong = 0;
                foreach (char symbol in value)
                {
                    if (char.IsLetter(symbol))
                    {
                        Letters++;
                        if (char.IsUpper(symbol)) Upper++;
                        if (char.IsLower(symbol)) Lower++;
                    }
                    else if (char.IsDigit(symbol))
                    {
                        Digits++;
                    }
                    else if (symbol != '.' && symbol != '-' && symbol != '_' && !char.IsWhiteSpace(symbol))
                    {
                        Strong++;
                    }
                }
            }
        }
    }
}
