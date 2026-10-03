using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SafePaste.Storage;

namespace SafePaste.Detecting
{
    /// <summary>Детектор не уложился в лимит времени, поэтому вставка отменяется, а не выполняется вслепую.</summary>
    public sealed class DetectionTimeoutException : Exception
    {
        public DetectionTimeoutException()
            : base("Один из детекторов превысил лимит времени. Вставка отменена.")
        {
        }
    }

    /// <summary>Поиск идентификаторов и секретов в тексте буфера обмена.</summary>
    public static class Detector
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(750);
        // Уже выданные метки должны переживать повторную проверку без вложенных замен.
        private static readonly Regex ExistingLabel = new Regex(@"\[[A-Z][A-Z0-9_]*_\d+\]",
            RegexOptions.CultureInvariant, Timeout);

        private static readonly Regex IPv4Regex = new Regex(
            @"(?<![\p{L}\p{N}_]|\d\.|\b(?:version|ver)\.?[ \t]*[:=]?[ \t]*)" +
            @"(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)" +
            @"(?![\p{L}\p{N}_]|\.\d)", Options, Timeout);

        private static readonly Regex IPv6Regex = new Regex(
            @"(?<![\p{L}\p{N}:])(?=[0-9A-Fa-f:]*[0-9A-Fa-f])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\p{L}\p{N}:])",
            Options, Timeout);

        private static readonly Regex MacRegex = new Regex(
            @"^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", Options, Timeout);

        private static readonly Regex FqdnRegex = new Regex(
            @"(?<![\p{L}\p{N}_-])(?:[\p{L}\p{N}_](?:[\p{L}\p{N}_-]{0,61}[\p{L}\p{N}])?\.)+" +
            @"(?<tld>xn--[A-Za-z0-9-]{1,59}|\p{L}{2,63})(?![\p{L}\p{N}_-]|\.[\p{L}\p{N}])", Options, Timeout);

        private static readonly Regex SidRegex = new Regex(
            @"(?<!\w)S-\d+(?:-\d+){2,15}(?!\w)", Options, Timeout);

        private const string GuidCore = @"[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}";

        // Фигурные и круглые скобки берутся только парами: "(session GUID)" не должен потерять скобку.
        private static readonly Regex GuidRegex = new Regex(
            @"(?<![0-9A-Fa-f])(?:\{" + GuidCore + @"\}|\(" + GuidCore + @"\)|" + GuidCore + @")(?![0-9A-Fa-f])",
            Options, Timeout);

        private static readonly Regex DomainUserRegex = new Regex(
            @"(?<![\w\\/.$-])(?<domain>(?-i:[A-Z][A-Z0-9-]{1,14}))\\(?<user>[\p{L}\p{N}_][\p{L}\p{N}._$-]{0,63})(?![\w\\])",
            Options, Timeout);

        private static readonly Regex UserWordRegex = new Regex(
            @"\b(?:user(?:name)?|login|account|пользователь|логин)[ \t]+(?<value>[\p{L}\p{N}][\p{L}\p{N}._@$-]{1,127})\b",
            Options, Timeout);

        // Имя узла без контекста: DC01, BACKUP-SRV01, SW-CORE-01. Всегда в верхнем регистре и с цифрами на конце.
        private static readonly Regex BareHostRegex = new Regex(
            @"(?<![\w.\-@\\/])(?-i:[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*\d{1,4})(?![\w.\-@\\/])",
            RegexOptions.CultureInvariant, Timeout);

        // Серийный номер без контекста: FOC1234ABCD, JAB12345678.
        private static readonly Regex BareSerialRegex = new Regex(
            @"(?<![\w.\-@\\/])(?-i:[A-Z0-9][A-Z0-9-]{6,31})(?![\w.\-@\\/])",
            RegexOptions.CultureInvariant, Timeout);

        /// <summary>Сокращения, которые выглядят как имя узла, но им не являются.</summary>
        private static readonly HashSet<string> NotHostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SHA1", "SHA256", "SHA384", "SHA512", "MD5", "CRC32", "AES128", "AES192", "AES256", "RSA2048", "RSA4096",
            "UTF8", "UTF16", "UTF32", "ISO8601", "RFC1918", "RFC2616", "X64", "X86", "WIN32", "WIN64", "ARM64",
            "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "USB2", "USB3", "USB4", "IPV4", "IPV6", "HTTP2", "HTTP3",
            "TLS10", "TLS11", "TLS12", "TLS13", "SSL2", "SSL3", "RAID0", "RAID1", "RAID5", "RAID6", "RAID10",
            "VLAN1", "IEEE802", "P2P", "H264", "H265", "MP3", "MP4", "GB18030", "CP1251", "CP866", "UTC0", "GMT0",
            "SQL2019", "SQL2022", "WINDOWS10", "WINDOWS11", "OFFICE365", "TCP4", "TCP6", "UDP4", "UDP6", "PS1"
        };

        /// <summary>Каталоги, шины устройств и служебные имена, которые не являются доменом учётной записи.</summary>
        private static readonly HashSet<string> NotDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Пути к устройствам: SCSI\Disk&Ven_..., USBSTOR\Disk..., PCI\VEN_...
            "SCSI", "IDE", "USB", "USBSTOR", "PCI", "NVME", "HID", "ACPI", "SWD", "STORAGE", "MMC", "BTH", "WPD",
            "DISPLAY", "UMB", "HDAUDIO", "VOLUME", "DISK", "HTREE", "FDC", "LPTENUM", "SW", "VMBUS", "COMPOSITE",
            "HKLM", "HKCU", "HKCR", "HKU", "HKCC", "HKEY", "NT", "BUILTIN", "AUTHORITY", "SERVICE", "APPPOOL", "IIS",
            "SYSVOL", "NETLOGON", "SYSTEM32", "WINDOWS", "USERS", "PUBLIC", "PROGRAMDATA", "PROGRAM", "TEMP", "TMP",
            "LOG", "LOGS", "SRC", "BIN", "OBJ", "LIB", "DIST", "BUILD", "DEBUG", "RELEASE", "DATA", "DOCS", "TEST",
            "TESTS", "ASSETS", "CONFIG", "SCRIPTS", "TOOLS", "BACKUP", "ARCHIVE", "IMAGES", "MEDIA", "STATIC", "ETC",
            "VAR", "OPT", "HOME", "ROOT", "MNT", "DEV", "PROC", "SYS", "APP", "APPS", "WEB", "WWW", "FILES"
        };

        /// <summary>
        /// Накопитель находок. На один и тот же диапазон и тип остаётся более надёжная:
        /// иначе порядок сканеров решал бы, победит ли значение из базы или слабая догадка.
        /// </summary>
        private sealed class ScanContext
        {
            internal readonly List<Detection> Items = new List<Detection>();
            private readonly Dictionary<string, int> positions = new Dictionary<string, int>(StringComparer.Ordinal);

            internal void Add(Detection detection)
            {
                if (detection == null || detection.Length <= 0)
                {
                    return;
                }
                string key = detection.Start.ToString(CultureInfo.InvariantCulture) + ":"
                    + detection.Length.ToString(CultureInfo.InvariantCulture) + ":" + detection.Type;
                int index;
                if (!positions.TryGetValue(key, out index))
                {
                    positions.Add(key, Items.Count);
                    Items.Add(detection);
                    return;
                }
                if (IsBetter(detection, Items[index]))
                {
                    Items[index] = detection;
                }
            }

            private static bool IsBetter(Detection candidate, Detection current)
            {
                if (candidate.Enabled != current.Enabled)
                {
                    return candidate.Enabled;
                }
                if (candidate.Locked != current.Locked)
                {
                    return candidate.Locked;
                }
                return candidate.Priority > current.Priority;
            }
        }

        /// <summary>
        /// Находит всё, что стоит скрыть, в обычном режиме. В быстром режиме остаются только
        /// обязательные секреты, высоконадёжные находки и значения, подтверждённые пользователем ранее.
        /// </summary>
        public static List<Detection> Scan(string text, SafePasteDatabase database, bool quick)
        {
            return Scan(text, database, ControlMode.Balanced, quick);
        }

        /// <summary>
        /// Находит всё, что стоит скрыть. Режим решает, какие находки включены: для быстрой вставки
        /// лишние отбрасываются до разбора пересечений, для окна проверки остаются выключенными.
        /// </summary>
        public static List<Detection> Scan(string text, SafePasteDatabase database, ControlMode mode, bool quick)
        {
            return Scan(text, database, mode, quick, true);
        }

        /// <summary>Вариант с переключателем контекстного поиска секретов в обычной речи.</summary>
        public static List<Detection> Scan(string text, SafePasteDatabase database, ControlMode mode, bool quick,
            bool smartSecrets)
        {
            List<Detection> empty = new List<Detection>();
            if (string.IsNullOrEmpty(text))
            {
                return empty;
            }

            ScanContext context = new ScanContext();
            ScanRules(context, text);
            if (smartSecrets)
            {
                ScanFound(context, SmartSecrets.Find(text));
            }
            ScanFound(context, ContextSecrets.Find(text, smartSecrets));
            ScanIPv4(context, text);
            ScanIPv6(context, text);
            ScanFqdn(context, text);
            ScanSid(context, text);
            ScanGuid(context, text);
            ScanDomainUser(context, text);
            ScanUserWord(context, text);
            ScanBareNames(context, text);
            ScanFound(context, PhoneNumbers.Find(text));
            ScanFound(context, PersonNames.Find(text));
            ScanFound(context, FilePaths.Find(text));
            ScanFound(context, ContextNames.Find(text));
            ScanLearned(context, text, database);

            List<Match> existingLabels = Collect(ExistingLabel, text);
            List<Detection> filtered = new List<Detection>(context.Items.Count);
            foreach (Detection detection in context.Items)
            {
                if (OverlapsExistingLabel(existingLabels, detection.Start, detection.End)) continue;
                detection.DefaultEnabled = detection.Enabled;
                // Пользователь отметил в окне проверки, что это не пароль: значение остаётся в тексте.
                if (detection.Locked && database != null && database.IsNotSecret(detection.Value))
                {
                    continue;
                }
                // Остальные секреты нельзя отключить ни allowlist'ом, ни режимом.
                if (!detection.Locked)
                {
                    if (quick && !ControlModes.HiddenInQuick(detection, mode))
                    {
                        continue;
                    }
                    if (database != null && database.IsAllowed(detection.Value))
                    {
                        continue;
                    }
                }
                // Пересечения разбираются по галочкам выбранного режима. Так в строгом режиме путь
                // \\srv\share\dir скрывается целиком, а в обычном только узел и папка внутри него.
                // В быстрой вставке скрывается всё, что до неё дошло; в строгом режиме это и догадки.
                detection.Enabled = quick || ControlModes.HiddenInReview(detection, mode);
                filtered.Add(detection);
            }
            // Скрытое значение скрывается во всём тексте, иначе открытый повтор рядом выдаст метку.
            return Repeats.Spread(text, OverlapResolver.Resolve(filtered), database);
        }

        private static bool OverlapsExistingLabel(List<Match> labels, int start, int end)
        {
            int low = 0;
            int high = labels.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                Match label = labels[middle];
                if (label.Index + label.Length <= start) low = middle + 1;
                else high = middle;
            }
            return low < labels.Count && labels[low].Index < end;
        }

        private static void ScanFound(ScanContext context, List<Detection> found)
        {
            foreach (Detection detection in found)
            {
                context.Add(detection);
            }
        }

        private static void ScanRules(ScanContext context, string text)
        {
            foreach (Rule rule in Rules.All)
            {
                foreach (Match match in Collect(rule.Regex, text))
                {
                    foreach (GroupTarget target in rule.Targets)
                    {
                        Group part = target.Group == null ? (Group)match : match.Groups[target.Group];
                        if (!part.Success || part.Length <= 0)
                        {
                            continue;
                        }
                        // «password: ${DB_PASSWORD}» ссылается на секрет, но сам его не содержит.
                        if (target.Type == "SECRET" && target.Group != null && ContextSecrets.IsReference(part.Value))
                        {
                            continue;
                        }
                        // «Пароль: не менее 8 символов», «Token: expired»: после двоеточия фраза, а не значение.
                        if (target.Type == "SECRET" && target.Group != null && ContextSecrets.IsProseValue(text, part.Index, part.Length))
                        {
                            continue;
                        }
                        // «Credentials: admin / pass» и «login/password: user / pass»:
                        // первое слово обозначает логин, сам секрет стоит после разделителя.
                        if (target.Type == "SECRET" && target.Group != null
                            && Regex.IsMatch(text.Substring(part.Index + part.Length), @"^[ \t]+[/|][ \t]+\S", Options, Timeout))
                        {
                            continue;
                        }
                        Detection detection = new Detection(part.Index, part.Length, part.Value,
                            target.Type, target.Confidence, target.Priority);
                        detection.Locked = target.Locked;
                        context.Add(detection);
                    }
                }
            }
        }

        private static void ScanIPv4(ScanContext context, string text)
        {
            foreach (Match match in Collect(IPv4Regex, text))
            {
                string[] parts = match.Value.Split('.');
                uint address = 0;
                int first = 0;
                bool valid = true;
                for (int index = 0; index < parts.Length; index++)
                {
                    int octet;
                    if (!int.TryParse(parts[index], out octet) || octet < 0 || octet > 255)
                    {
                        valid = false;
                        break;
                    }
                    if (index == 0)
                    {
                        first = octet;
                    }
                    address = (address << 8) | (uint)octet;
                }
                if (!valid)
                {
                    continue;
                }
                Detection detection = new Detection(match.Index, match.Length, match.Value, "IP", Confidence.High, 70);
                if (IsNonIdentifyingIPv4(address, first))
                {
                    // Маски, 0.0.0.0 и localhost ничего не выдают: показываем, но не скрываем.
                    detection.Confidence = Confidence.Low;
                    detection.Enabled = false;
                }
                context.Add(detection);
            }
        }

        private static bool IsNonIdentifyingIPv4(uint address, int firstOctet)
        {
            if (firstOctet == 127 || address == 0 || address == uint.MaxValue)
            {
                return true;
            }
            uint inverted = ~address;
            if (((inverted + 1) & inverted) == 0)
            {
                return true; // маска подсети: 255.255.255.0
            }
            if (((address + 1) & address) == 0)
            {
                return true; // обратная маска: 0.0.0.255
            }
            return false;
        }

        private static void ScanIPv6(ScanContext context, string text)
        {
            foreach (Match match in Collect(IPv6Regex, text))
            {
                string value = match.Value;
                if (MacRegex.IsMatch(value))
                {
                    continue; // MAC-адрес определяется отдельной категорией
                }
                IPAddress address;
                if (!IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    continue;
                }
                Detection detection = new Detection(match.Index, match.Length, value, "IPV6", Confidence.High, 70);
                if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address))
                {
                    detection.Confidence = Confidence.Low;
                    detection.Enabled = false;
                }
                context.Add(detection);
            }
        }

        private static void ScanFqdn(ScanContext context, string text)
        {
            foreach (Match match in Collect(FqdnRegex, text))
            {
                Confidence confidence;
                bool enabled;
                if (!TryClassifyFqdn(match.Value, match.Groups["tld"].Value, out confidence, out enabled))
                {
                    continue;
                }
                Detection detection = new Detection(match.Index, match.Length, match.Value, "FQDN", confidence, 75);
                detection.Enabled = enabled;
                context.Add(detection);
            }
        }

        /// <summary>
        /// Отличает имя узла от точечной конструкции из кода: "config.json", "Console.WriteLine",
        /// "System.IO" узлами не считаются.
        /// </summary>
        private static bool TryClassifyFqdn(string value, string tld, out Confidence confidence, out bool enabled)
        {
            confidence = Confidence.High;
            enabled = true;

            bool strong = Lexicons.StrongTlds.Contains(tld);
            if (!strong && Lexicons.FileExtensions.Contains(tld))
            {
                return false; // имя файла
            }
            if (IsCapitalizedWord(tld) || HasCamelHump(value))
            {
                return false; // идентификатор из кода
            }
            if (strong)
            {
                return true;
            }
            if (HasUpper(value) && HasLower(value))
            {
                return false; // смешанный регистр почти всегда означает код
            }
            int labels = CountLabels(value);
            if (HasDigitOrHyphen(value))
            {
                // "backup01.corp.example" почти наверняка узел, даже если домен верхнего уровня незнакомый.
                confidence = labels >= 3 ? Confidence.High : Confidence.Medium;
                return true;
            }
            if (labels >= 3 || Lexicons.WeakTlds.Contains(tld) || IsTwoLetterAscii(tld))
            {
                confidence = Confidence.Low;
                enabled = false;
                return true;
            }
            return false;
        }

        private static void ScanSid(ScanContext context, string text)
        {
            foreach (Match match in Collect(SidRegex, text))
            {
                string value = match.Value;
                Detection detection = new Detection(match.Index, match.Length, value, "SID", Confidence.High, 70);
                bool domainSid = value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);
                if (!domainSid)
                {
                    // S-1-5-18, S-1-5-32-544 и подобные одинаковы на всех компьютерах.
                    detection.Confidence = Confidence.Low;
                    detection.Enabled = false;
                }
                context.Add(detection);
            }
        }

        private static void ScanGuid(ScanContext context, string text)
        {
            foreach (Match match in Collect(GuidRegex, text))
            {
                string value = match.Value;
                Detection detection = new Detection(match.Index, match.Length, value, "GUID", Confidence.High, 70);
                if (value.Trim('{', '(', ')', '}').Replace("-", string.Empty).Trim('0').Length == 0)
                {
                    detection.Confidence = Confidence.Low;
                    detection.Enabled = false; // нулевой GUID
                }
                context.Add(detection);
            }
        }

        private static void ScanDomainUser(ScanContext context, string text)
        {
            foreach (Match match in Collect(DomainUserRegex, text))
            {
                string domain = match.Groups["domain"].Value;
                string user = match.Groups["user"].Value;
                if (NotDomains.Contains(domain))
                {
                    continue; // это путь или реестр, а не DOMAIN\user
                }
                int dot = user.LastIndexOf('.');
                if (dot > 0 && Lexicons.FileExtensions.Contains(user.Substring(dot + 1)))
                {
                    continue; // LOGS\app.log
                }
                context.Add(new Detection(match.Index, match.Length, match.Value, "USER", Confidence.High, 68));
            }
        }

        private static void ScanUserWord(ScanContext context, string text)
        {
            foreach (Match match in Collect(UserWordRegex, text))
            {
                Group value = match.Groups["value"];
                if (Lexicons.UserStopWords.Contains(value.Value))
                {
                    continue; // "the user is logged in"
                }
                if (value.Value.IndexOf('.') != value.Value.LastIndexOf('.'))
                {
                    continue; // «docker login registry.example.com»: адрес, а не учётная запись
                }
                context.Add(new Detection(value.Index, value.Length, value.Value, "USER", Confidence.Medium, 63));
            }
        }

        /// <summary>
        /// Имена узлов и серийные номера, написанные без подсказок вокруг. Уверенности мало,
        /// поэтому такие находки показываются в окне проверки, но по умолчанию не скрываются:
        /// достаточно один раз закрепить значение, чтобы дальше оно узнавалось само.
        /// </summary>
        private static void ScanBareNames(ScanContext context, string text)
        {
            foreach (Match match in Collect(BareHostRegex, text))
            {
                string value = match.Value;
                if (value.Length < 4 || value.Length > 32 || NotHostNames.Contains(value)
                    || CountLetters(value) < 2)
                {
                    continue;
                }
                Detection detection = new Detection(match.Index, match.Length, value, "HOST", Confidence.Low, 66);
                detection.Enabled = false;
                context.Add(detection);
            }

            foreach (Match match in Collect(BareSerialRegex, text))
            {
                string value = match.Value;
                if (value.Length < 8 || value.Length > 32 || NotHostNames.Contains(value))
                {
                    continue;
                }
                if (CountLetters(value) < 2 || CountDigits(value) < 2)
                {
                    continue;
                }
                Detection detection = new Detection(match.Index, match.Length, value, "SERIAL", Confidence.Low, 59);
                detection.Enabled = false;
                context.Add(detection);
            }
        }

        /// <summary>
        /// Правила пользователя и запомненные метки. Правило важнее метки с тем же значением:
        /// остаются его тип и происхождение.
        /// </summary>
        private static void ScanLearned(ScanContext context, string text, SafePasteDatabase database)
        {
            if (database == null || (database.Learned.Count == 0 && database.Remembered.Count == 0))
            {
                return;
            }
            ValueMatcher matcher = new ValueMatcher();
            List<string> types = new List<string>();
            List<string> sources = new List<string>();
            AddLearned(matcher, types, sources, database.Learned, Detection.LearnedSource);
            AddLearned(matcher, types, sources, database.Remembered, Detection.RememberedSource);
            foreach (ValueMatch match in matcher.Find(text))
            {
                Detection detection = new Detection(match.Start, match.Length, text.Substring(match.Start, match.Length),
                    types[match.Index], Confidence.Learned, 73);
                detection.Source = sources[match.Index];
                context.Add(detection);
            }
        }

        private static void AddLearned(ValueMatcher matcher, List<string> types, List<string> sources,
            List<LearnedValue> entries, string source)
        {
            foreach (LearnedValue entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Value) || string.IsNullOrEmpty(entry.Type))
                {
                    continue;
                }
                // Номер значения в автомате совпадает с номером в списках типов: повтор не добавляется.
                if (matcher.Add(entry.Value) >= 0)
                {
                    types.Add(entry.Type.ToUpperInvariant());
                    sources.Add(source);
                }
            }
        }

        /// <summary>
        /// Шаблон значения с границами: SRV-DB01 не должно находиться внутри SRV-DB01-old.
        /// Граница нужна только там, где само значение начинается или кончается буквой, цифрой,
        /// «_» или «-». Если на краю разделитель (\, /, точка), он и есть граница: иначе путь,
        /// выделенный вместе с последним «\», не находился бы перед следующей папкой.
        /// </summary>
        private static string BoundedPattern(string value)
        {
            StringBuilder pattern = new StringBuilder(value.Length + 48);
            if (ValueMatcher.IsWordEdge(value[0]))
            {
                pattern.Append(@"(?<![\p{L}\p{N}_-])");
            }
            pattern.Append(Regex.Escape(value));
            if (ValueMatcher.IsWordEdge(value[value.Length - 1]))
            {
                pattern.Append(@"(?![\p{L}\p{N}_-])");
            }
            return pattern.ToString();
        }

        /// <summary>
        /// Все вхождения значения с теми же границами, что и у заученных значений:
        /// пометка одного вхождения должна скрыть и остальные.
        /// </summary>
        public static List<int> FindOccurrences(string text, string value)
        {
            List<int> result = new List<int>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            {
                return result;
            }
            Regex regex = new Regex(BoundedPattern(value), Options, Timeout);
            try
            {
                for (Match match = regex.Match(text); match.Success; match = match.NextMatch())
                {
                    result.Add(match.Index);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Пометка применится хотя бы к выделенному вхождению.
            }
            return result;
        }

        internal static List<Match> Collect(Regex regex, string text)
        {
            List<Match> matches = new List<Match>();
            try
            {
                for (Match match = regex.Match(text); match.Success; match = match.NextMatch())
                {
                    matches.Add(match);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new DetectionTimeoutException();
            }
            return matches;
        }

        private static bool IsCapitalizedWord(string value)
        {
            if (value.Length < 2 || !char.IsUpper(value[0]))
            {
                return false;
            }
            for (int index = 1; index < value.Length; index++)
            {
                if (char.IsLower(value[index]))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasCamelHump(string value)
        {
            for (int index = 1; index < value.Length; index++)
            {
                if (char.IsUpper(value[index]) && (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasUpper(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsUpper(symbol))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasLower(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsLower(symbol))
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountLetters(string value)
        {
            int count = 0;
            foreach (char symbol in value)
            {
                if (char.IsLetter(symbol))
                {
                    count++;
                }
            }
            return count;
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

        private static bool HasDigitOrHyphen(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsDigit(symbol) || symbol == '-')
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountLabels(string value)
        {
            int labels = 1;
            foreach (char symbol in value)
            {
                if (symbol == '.')
                {
                    labels++;
                }
            }
            return labels;
        }

        private static bool IsTwoLetterAscii(string value)
        {
            if (value.Length != 2)
            {
                return false;
            }
            return IsAsciiLetter(value[0]) && IsAsciiLetter(value[1]);
        }

        private static bool IsAsciiLetter(char symbol)
        {
            return (symbol >= 'a' && symbol <= 'z') || (symbol >= 'A' && symbol <= 'Z');
        }
    }
}
