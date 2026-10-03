using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Пути к файлам и папкам: C:\..., \\сервер\папка\..., //сервер/папка/..., /home/..., ~/...,
    /// %APPDATA%\..., $HOME/..., .\... и ../..., а также пути с пробелами в кавычках.
    /// Путь целиком скрывает только строгий режим. В обычном папки видны, а узел, сетевая папка
    /// и имя пользователя из профиля скрываются отдельными находками.
    /// </summary>
    internal static class FilePaths
    {
        internal const int Priority = 85;
        private const int ProfilePriority = 67;

        private const RegexOptions Options = RegexOptions.CultureInvariant;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(750);

        // Windows: символ имени и он же без пробела (края папки и последняя часть пути).
        private const string WinChar = @"[^\\/:*?""<>|\r\n\t]";
        private const string WinEdge = @"[^\\/:*?""<>|\s]";
        private const string WinSep = @"(?:\\{1,2}|/)";
        private const string WinWord = @"[^\\/:*?""<>|\s]+";
        // Папка в середине пути: пробелы внутри можно («Program Files»), по краям нельзя.
        private const string WinDir = WinEdge + "(?:" + WinChar + "{0,253}" + WinEdge + ")?";
        // Последняя часть без пробелов. С пробелами только имя файла с расширением: «мой отчёт.docx».
        private const string WinLast = @"(?:(?:[^\\/:*?""<>|\s.]+ ){1,3}" + WinEdge + @"*\.[\p{L}\p{N}]{1,10}(?![\p{L}\p{N}])|"
            + WinEdge + "+)";
        private const string WinTail = "(?:" + WinDir + WinSep + ")*(?:" + WinLast + ")?";
        private const string WinStart = "(?=" + WinEdge + ")";

        // Unix: имя без пробелов, кавычек и скобок.
        private const string UnixName = @"[\p{L}\p{N}._~@%+#$-]+";
        private const string UnixTail = UnixName + "(?:/" + UnixName + ")*/?";

        // C:\..., C:/..., \\?\C:\...
        private static readonly Regex DriveRegex = new Regex(
            @"(?:(?<![\\\w])\\\\[?.]\\|(?<![\p{L}\p{N}_/\\.$%-]))[A-Za-z]:" + WinSep + WinStart + WinTail,
            Options, Timeout);

        // \\сервер\папка\... и \\?\UNC\сервер\папка\...
        private static readonly Regex UncRegex = new Regex(
            @"(?<![\\\w])\\\\(?:\?\\UNC\\)?[\p{L}\p{N}_](?:[\p{L}\p{N}_.-]{0,253}[\p{L}\p{N}_])?" + WinSep + WinStart + WinTail,
            Options, Timeout);

        // %APPDATA%\..., $env:USERPROFILE\...
        private static readonly Regex WinVariableRegex = new Regex(
            @"(?<![\p{L}\p{N}_\\/%])(?:%[A-Za-z_][A-Za-z0-9_()]*%|\$[Ee][Nn][Vv]:[A-Za-z_][A-Za-z0-9_()]*)" + WinSep + WinStart + WinTail,
            Options, Timeout);

        // //сервер/папка/...: так пишут сетевые папки в Linux (mount -t cifs, smbclient).
        private static readonly Regex SlashUncRegex = new Regex(
            @"(?<![\p{L}\p{N}_.:/\\-])//[\p{L}\p{N}_](?:[\p{L}\p{N}_.-]{0,253}[\p{L}\p{N}_])?/" + UnixTail,
            Options, Timeout);

        // $HOME/..., ${HOME}/...
        private static readonly Regex UnixVariableRegex = new Regex(
            @"(?<![\p{L}\p{N}_\\/$])\$(?:\{[A-Za-z_][A-Za-z0-9_]*\}|[A-Za-z_][A-Za-z0-9_]*)/" + UnixTail,
            Options, Timeout);

        // ~/..., ~user/...
        private static readonly Regex HomeRegex = new Regex(
            @"(?<![\p{L}\p{N}_.\\/~$%@#-])~[\p{L}\p{N}._-]{0,32}/" + UnixTail, Options, Timeout);

        // .\..., ..\..., ./..., ../...
        private static readonly Regex DotWinRegex = new Regex(
            @"(?<![\p{L}\p{N}_.\\/~$%-])\.{1,2}\\" + WinWord + @"(?:\\" + WinWord + @")*\\?", Options, Timeout);
        private static readonly Regex DotUnixRegex = new Regex(
            @"(?<![\p{L}\p{N}_.\\/~$%-])\.{1,2}/" + UnixTail, Options, Timeout);

        // /home/user/..., /etc/nginx/nginx.conf: не меньше двух частей, иначе это ключ вроде /s.
        private static readonly Regex UnixRegex = new Regex(
            @"(?<![\p{L}\p{N}_.\\/~$%@#-])/" + UnixName + "(?:/" + UnixName + ")+/?", Options, Timeout);

        // Относительные пути без точки в начале: bin\Debug\app.exe, src/main/App.java.
        private static readonly Regex RelativeWinRegex = new Regex(
            @"(?<![\p{L}\p{N}_.\\/:%$@-])[\p{L}\p{N}_$][^\\/:*?""<>|\s]*(?:\\" + WinWord + "){2,}", Options, Timeout);
        private static readonly Regex RelativeUnixRegex = new Regex(
            @"(?<![\p{L}\p{N}_.\\/:~$%@#-])" + UnixName + "(?:/" + UnixName + "){2,}", Options, Timeout);

        // Путь в кавычках может содержать пробелы где угодно: "C:\Program Files\My App\my file.txt".
        private static readonly Regex QuotedRegex = new Regex(
            @"(?<quote>[""'])(?<value>(?:[A-Za-z]:[\\/]|\\\\[\p{L}\p{N}_?]|%[A-Za-z_][A-Za-z0-9_()]*%[\\/]"
            + @"|~[\p{L}\p{N}._-]{0,32}/|\$\{?[A-Za-z_]|/[\p{L}\p{N}._~@%+#$-])(?:(?!\k<quote>)[^\r\n]){1,1024})\k<quote>",
            Options, Timeout);

        // Имя пользователя в профиле: C:\Users\ivanov, \\fs01\Users\ivanov, /Users/ivanov, /home/ivanov.
        // Без буквы диска папка должна быть с заглавной, иначе попадали бы адреса вроде /users/42.
        private const string ProfileDir = WinEdge + "(?:" + WinChar + "{0,62}" + WinEdge + ")?";
        private static readonly Regex WindowsProfileRegex = new Regex(
            @"(?:(?<![\p{L}\p{N}_])(?:[A-Za-z]:|%SystemDrive%)" + WinSep + "(?i:users|documents and settings)"
            + @"|(?<![\p{L}\p{N}_.\\/-])(?:\\\\[\p{L}\p{N}_.-]+)?" + WinSep + "(?:Users|Documents and Settings))"
            + WinSep + "(?:(?<user>" + ProfileDir + ")(?=" + WinSep + ")|(?<user>" + WinEdge + "{1,64}))",
            Options, Timeout);
        private static readonly Regex UnixProfileRegex = new Regex(
            @"(?<![\p{L}\p{N}_.~/\\-])/home/(?<user>[\p{L}\p{N}._-]{1,64})(?![\p{L}\p{N}._-])", Options, Timeout);

        /// <summary>Служебные профили и заглушки из инструкций: они ничего не говорят о человеке.</summary>
        private static readonly HashSet<string> NotProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Public", "Default", "Default User", "All Users", "desktop.ini", "Administrator", "Администратор",
            "Все пользователи", "Общие", "Guest", "Гость", "defaultuser0", "DefaultAppPool", "WDAGUtilityAccount",
            "Shared", "user", "username", "yourname", "youruser", "your_user", "имя_пользователя", "пользователь"
        };

        /// <summary>Разделы реестра и шины устройств: HKLM\SOFTWARE\..., SCSI\Disk&amp;Ven_... не файлы.</summary>
        private static readonly HashSet<string> NotFileRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HKLM", "HKCU", "HKCR", "HKU", "HKCC", "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKEY_CLASSES_ROOT",
            "HKEY_USERS", "HKEY_CURRENT_CONFIG", "SCSI", "IDE", "USB", "USBSTOR", "PCI", "NVME", "HID", "ACPI", "SWD",
            "STORAGE", "MMC", "BTH", "WPD", "DISPLAY", "UMB", "HDAUDIO", "HTREE", "FDC", "LPTENUM", "SW", "VMBUS",
            "COMPOSITE", "ROOT"
        };

        private enum Kind
        {
            /// <summary>Есть корень (диск, сервер, переменная): нужна хотя бы одна часть после него.</summary>
            Rooted,
            /// <summary>~/..., ./...: хватит одной части с буквой или цифрой.</summary>
            Relative,
            /// <summary>/a/b: не меньше двух частей, иначе это ключ команды.</summary>
            UnixAbsolute,
            WindowsRelative,
            UnixRelative
        }

        internal static List<Detection> Find(string text)
        {
            List<Detection> found = new List<Detection>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }
            AddPaths(found, text, DriveRegex, Kind.Rooted);
            AddPaths(found, text, UncRegex, Kind.Rooted);
            AddPaths(found, text, WinVariableRegex, Kind.Rooted);
            AddPaths(found, text, SlashUncRegex, Kind.Rooted);
            AddPaths(found, text, UnixVariableRegex, Kind.Rooted);
            AddPaths(found, text, HomeRegex, Kind.Relative);
            AddPaths(found, text, DotWinRegex, Kind.Relative);
            AddPaths(found, text, DotUnixRegex, Kind.Relative);
            AddPaths(found, text, UnixRegex, Kind.UnixAbsolute);
            AddPaths(found, text, RelativeWinRegex, Kind.WindowsRelative);
            AddPaths(found, text, RelativeUnixRegex, Kind.UnixRelative);
            AddQuoted(found, text);
            AddProfiles(found, text, WindowsProfileRegex);
            AddProfiles(found, text, UnixProfileRegex);
            return found;
        }

        private static void AddPaths(List<Detection> found, string text, Regex regex, Kind kind)
        {
            foreach (Match match in Detector.Collect(regex, text))
            {
                AddPath(found, text, match.Index, match.Index + match.Length, kind);
            }
        }

        private static void AddQuoted(List<Detection> found, string text)
        {
            foreach (Match match in Detector.Collect(QuotedRegex, text))
            {
                Group value = match.Groups["value"];
                string path = value.Value;
                if (path.IndexOf("://", StringComparison.Ordinal) >= 0 || HasSpaceAtEdge(path))
                {
                    continue;
                }
                char head = path[0];
                Kind kind = head == '/' ? Kind.UnixAbsolute : head == '~' ? Kind.Relative : Kind.Rooted;
                AddPath(found, text, value.Index, value.Index + value.Length, kind);
            }
        }

        private static void AddPath(List<Detection> found, string text, int start, int end, Kind kind)
        {
            end = TrimEnd(text, start, end);
            if (end <= start)
            {
                return;
            }
            string value = text.Substring(start, end - start);
            if (!IsValid(value, kind))
            {
                return;
            }
            Detection detection = new Detection(start, end - start, value, "PATH", Confidence.Low, Priority);
            detection.Enabled = false; // путь целиком скрывает только строгий режим
            found.Add(detection);
        }

        private static void AddProfiles(List<Detection> found, string text, Regex regex)
        {
            foreach (Match match in Detector.Collect(regex, text))
            {
                Group user = match.Groups["user"];
                int end = TrimEnd(text, user.Index, user.Index + user.Length);
                if (end <= user.Index)
                {
                    continue;
                }
                string value = text.Substring(user.Index, end - user.Index);
                char head = value[0];
                if (NotProfiles.Contains(value) || head == '%' || head == '$' || head == '{' || head == '[' || head == '.')
                {
                    continue;
                }
                found.Add(new Detection(user.Index, value.Length, value, "USER", Confidence.High, ProfilePriority));
            }
        }

        /// <summary>Знаки препинания в конце не входят в путь: «лежит в C:\Temp\file.txt.»</summary>
        private static int TrimEnd(string text, int start, int end)
        {
            while (end > start)
            {
                char last = text[end - 1];
                if (last == '.' || last == ',' || last == ';' || last == ':' || last == '!' || last == '?'
                    || last == '\'' || last == '`')
                {
                    end--;
                    continue;
                }
                char open = last == ')' ? '(' : last == ']' ? '[' : last == '}' ? '{' : '\0';
                if (open != '\0' && Count(text, start, end, open) < Count(text, start, end, last))
                {
                    end--;
                    continue;
                }
                break;
            }
            return end;
        }

        private static bool IsValid(string value, Kind kind)
        {
            string[] parts = value.Split('\\', '/');
            int named = 0;
            int lettered = 0;
            bool wide = false;
            foreach (string part in parts)
            {
                if (HasLetterOrDigit(part))
                {
                    named++;
                }
                if (HasLetter(part))
                {
                    lettered++;
                    wide |= part.Length >= 2;
                }
            }
            switch (kind)
            {
                case Kind.Rooted:
                    return named >= 2;
                case Kind.Relative:
                    return named >= 1;
                case Kind.UnixAbsolute:
                    return named >= 2 && wide;
                case Kind.WindowsRelative:
                    return named >= 3 && wide && !NotFileRoots.Contains(parts[0]) && !LooksEscaped(value);
                default:
                    return named >= 3 && lettered >= 2 && HasExtension(parts[parts.Length - 1]);
            }
        }

        /// <summary>Строка с экранированием вроде line1\nline2\nline3 похожа на путь, но им не является.</summary>
        private static bool LooksEscaped(string value)
        {
            int escapes = 0;
            for (int index = 0; index + 1 < value.Length; index++)
            {
                if (value[index] == '\\' && (value[index + 1] == 'n' || value[index + 1] == 'r' || value[index + 1] == 't'))
                {
                    escapes++;
                }
            }
            return escapes >= 2;
        }

        private static bool HasExtension(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot >= name.Length - 1 || name.Length - dot - 1 > 8 || !IsAsciiLetter(name[dot + 1]))
            {
                return false;
            }
            for (int index = dot + 1; index < name.Length; index++)
            {
                if (!char.IsLetterOrDigit(name[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasSpaceAtEdge(string path)
        {
            foreach (string part in path.Split('\\', '/'))
            {
                if (part.Length > 0 && (part[0] == ' ' || part[part.Length - 1] == ' '))
                {
                    return true;
                }
            }
            return false;
        }

        private static int Count(string text, int start, int end, char symbol)
        {
            int count = 0;
            for (int index = start; index < end; index++)
            {
                if (text[index] == symbol)
                {
                    count++;
                }
            }
            return count;
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

        private static bool HasLetter(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsLetter(symbol))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsAsciiLetter(char symbol)
        {
            return (symbol >= 'a' && symbol <= 'z') || (symbol >= 'A' && symbol <= 'Z');
        }
    }
}
