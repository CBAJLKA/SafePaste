using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Tests
{
    /// <summary>
    /// Проверки детектора, замен и хранилища. Запускается как отдельный exe: build.cmd test.
    /// Реальные буфер обмена и база пользователя не затрагиваются.
    /// </summary>
    public static partial class TestProgram
    {
        private static int passed;
        private static readonly List<string> Failures = new List<string>();

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 1 && string.Equals(args[0], "corpus", StringComparison.OrdinalIgnoreCase))
            {
                CorpusTests();
                Console.WriteLine("Пройдено: " + passed + ", провалено: " + Failures.Count);
                foreach (string failure in Failures) Console.WriteLine("  [FAIL] " + failure);
                return Failures.Count == 0 ? 0 : 1;
            }
            string sandbox = Path.Combine(Path.GetTempPath(), "SafePaste-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            Paths.DataDirectory = sandbox;
            try
            {
                SecretTests();
                SmartSecretTests();
                SecretFeedbackTests();
                IdentifierTests();
                FalsePositiveTests();
                PersonTests();
                PathTests();
                PhoneTests();
                ManualTestCases();
                QuickModeTests();
                ModeTests();
                LearnedSpanTests();
                ReplacementTests();
                ReservationTests();
                StorageTests();
                MemoryTests();
                ContextTests();
                BridgeTests();
                AutostartTests();
                CorpusTests();
                PerformanceTest();
            }
            finally
            {
                try { Directory.Delete(sandbox, true); }
                catch (Exception) { }
            }

            Console.WriteLine();
            Console.WriteLine("Пройдено: " + passed + ", провалено: " + Failures.Count);
            foreach (string failure in Failures)
            {
                Console.WriteLine("  [FAIL] " + failure);
            }
            return Failures.Count == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- секреты

        private static void SecretTests()
        {
            Section("Секреты");
            Hides("JSON-пароль", "{\"password\": \"hunter2\", \"user\": \"bob\"}", "hunter2");
            Hides("JSON остаётся валидным", "{\"password\": \"hunter2\"}", "hunter2", "\"password\": \"[SECRET_1]\"");
            Hides("env DB_PASSWORD", "DB_PASSWORD=Sup3rS3cret", "Sup3rS3cret");
            Hides("env client_secret", "client_secret=abc123XYZ", "abc123XYZ");
            Hides("env access_token", "ACCESS_TOKEN=ya29.a0AfH6SMBx", "ya29.a0AfH6SMBx");
            Hides("camelCase", "adminPassword: Qwerty123!", "Qwerty123!");
            Hides("PascalCase", "<add key=\"DbPassword\" value=\"P@ssw0rd\" />", "P@ssw0rd");
            Hides("aws secret", "aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCY");
            Hides("connection string", "Server=db01;User ID=sa;Password=Str0ng!;", "Str0ng!");
            Hides("русский пароль", "Пароль: Лето2024!", "Лето2024!");
            Hides("XML", "<userPassword>topsecret</userPassword>", "topsecret");
            Hides("unattend", "<AdministratorPassword><Value>Adm1n!</Value></AdministratorPassword>", "Adm1n!");
            Hides("CLI", "sqlcmd -S srv --password MyP@ss1", "MyP@ss1");
            Hides("Authorization", "Authorization: Bearer abc.def.ghi0123456789", "abc.def.ghi0123456789");
            Hides("Cookie", "Cookie: session=9f8b7a6c5d4e3f; Path=/", "9f8b7a6c5d4e3f");
            Hides("URL с паролем", "postgres://admin:P4ss@db01.corp.local:5432/app", "P4ss");
            Hides("JWT", "token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.dBjftJeZ4CVPmB92K27u", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.dBjftJeZ4CVPmB92K27u");
            Hides("GitHub token", "git remote: ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdefghijklmnopqrstuvwxyz0123456789");
            Hides("OpenAI ключ", "OPENAI_KEY sk-proj-abcdefghijklmnopqrstuvwxyz012345", "sk-proj-abcdefghijklmnopqrstuvwxyz012345");
            Hides("AWS key id", "AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE");
            Hides("приватный ключ", "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA\n-----END RSA PRIVATE KEY-----", "MIIEpAIBAAKCAQEA");
            Hides("обрезанный ключ", "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA1234567890", "MIIEpAIBAAKCAQEA1234567890");
            Hides("PGP ключ", "-----BEGIN PGP PRIVATE KEY BLOCK-----\nlQOYBF0abc\n-----END PGP PRIVATE KEY BLOCK-----", "lQOYBF0abc");
            Hides("хеш пароля", "root:$6$rounds=5000$saltsalt$hashhashhashhash:19000:0", "$6$rounds=5000$saltsalt$hashhashhashhash");
            Hides("Cisco enable", "enable secret 5 $1$mERr$hx5rVt7rPNoS4wqbXKX7m0", "$1$mERr$hx5rVt7rPNoS4wqbXKX7m0");
            Hides("Cisco username", "username admin privilege 15 password 7 0822455D0A16", "0822455D0A16");
            Hides("Cisco snmp", "snmp-server community S3cr3tRO RO", "S3cr3tRO");
            Hides("Cisco isakmp", "crypto isakmp key MySharedKey address 10.0.0.1", "MySharedKey");
            Hides("SecureString", "ConvertTo-SecureString 'P@ssw0rd!' -AsPlainText -Force", "P@ssw0rd!");
            Hides("net user", "net user admin Sup3r! /add", "Sup3r!");
            Hides("ключ продукта", "Key: ABCDE-12345-FGHIJ-67890-KLMNO", "ABCDE-12345-FGHIJ-67890-KLMNO");
            Hides("SAS-токен", "https://acc.blob.core.windows.net/c?sv=2021&sig=abc%2Fdef123", "abc%2Fdef123");
        }

        // ---------------------------------------------------------------- идентификаторы

        private static void IdentifierTests()
        {
            Section("Идентификаторы");
            Hides("IPv4", "Шлюз 192.168.10.5 недоступен", "192.168.10.5");
            Hides("IPv6", "fe80::1c2d:3e4f:5a6b:7c8d недоступен", "fe80::1c2d:3e4f:5a6b:7c8d");
            Hides("MAC", "MAC 00:1A:2B:3C:4D:5E", "00:1A:2B:3C:4D:5E");
            Hides("MAC Cisco", "0011.2233.4455", "0011.2233.4455");
            Hides("домен SID", "S-1-5-21-1004336348-1177238915-682003330-512", "S-1-5-21-1004336348-1177238915-682003330-512");
            Hides("GUID", "CorrelationId 3f2504e0-4f89-11d3-9a0c-0305e82c3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301");
            Hides("почта", "Пишите на john.smith@example.com.", "john.smith@example.com");
            Hides("FQDN", "Сервер dc01.corp.local не отвечает", "dc01.corp.local");
            Hides("URL", "Смотри https://portal.contoso.com/page.", "portal.contoso.com");
            Hides("UNC", "Путь \\\\fs01\\Share\\docs", "fs01");
            Hides("AD DN", "CN=John Smith,OU=Users,DC=corp,DC=local", "OU=Users");
            Hides("AD DN с экранированием", "CN=Smith\\, John,OU=Users,DC=corp,DC=local", "Smith\\, John");
            Hides("DOMAIN\\user", "Вход CORP\\jsmith выполнен", "CORP\\jsmith");
            Hides("user@host", "ssh admin@web01", "admin@web01");
            Hides("iSCSI", "iqn.1998-01.com.vmware:esx01-1a2b3c", "iqn.1998-01.com.vmware:esx01-1a2b3c");
            Hides("MoRef кластер", "cluster domain-c7", "domain-c7");
            Hides("MoRef группа", "folder group-v3", "group-v3");
            Hides("Azure tenant", "tenant id: 72f988bf-86f1-41af-91ab-2d7cd011db47", "72f988bf-86f1-41af-91ab-2d7cd011db47");
            Hides("серийный номер", "Service Tag: 7XK2L93", "7XK2L93");
            Hides("контекстный хост", "hostname: SRV-DB01", "SRV-DB01");
            Hides("контекстный логин", "username: jsmith", "jsmith");
            Hides("SSH fingerprint", "SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG", "SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG");
            AssertType("тип URL", "https://portal.contoso.com/x", "https://portal.contoso.com/x", "URL");
            AssertType("тип пароля в URL", "postgres://admin:P4ss@db01.corp.local/app", "P4ss", "SECRET");
            AssertType("Azure tenant в JSON", "\"TenantId\": \"72f988bf-86f1-41af-91ab-2d7cd011db47\"",
                "72f988bf-86f1-41af-91ab-2d7cd011db47", "AZURE_TENANT");
            Keeps("скобка после GUID", "(session 3f2504e0-4f89-11d3-9a0c-0305e82c3301) готово", ") готово");
            Keeps("скобка после токена", "(Authorization: Bearer abcdefgh12345678) готово", ") готово");
            Hides("GUID в фигурных скобках", "CLSID {3f2504e0-4f89-11d3-9a0c-0305e82c3301} найден", "3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        }

        // ---------------------------------------------------------------- ложные срабатывания

        private static void FalsePositiveTests()
        {
            Section("Ложные срабатывания");
            Keeps("имена файлов", "Get-Content .\\config.json и readme.md", "config.json", "readme.md");
            Keeps("код .NET", "[System.Windows.Forms.Clipboard]::GetText()", "System.Windows.Forms.Clipboard");
            Keeps("код JS", "console.log(document.getElementById('x'))", "document.getElementById");
            Keeps("модуль PowerShell", "Import-Module Microsoft.PowerShell.Commands.Utility", "Microsoft.PowerShell.Commands.Utility");
            Keeps("версия сборки", "System.Net.Http, Version=4.0.0.0", "4.0.0.0");
            Keeps("OID", "OID 1.3.6.1.4.1.311", "1.3.6.1.4.1.311");
            Keeps("маска подсети", "ip route 10.0.0.0 255.255.255.0", "255.255.255.0");
            Keeps("localhost", "Listening on 127.0.0.1", "127.0.0.1");
            Keeps("встроенный SID", "Группа S-1-5-32-544", "S-1-5-32-544");
            Keeps("слова после user", "The user is logged in. Login failed for account locked.", "is", "failed", "locked");
            Keeps("Asset management", "Asset management system upgrade", "system", "upgrade");
            Keeps("token_type", "token_type=Bearer, expires_in=3600", "3600");
            Keeps("password_policy", "password_policy = strict", "strict");
            Keeps("bypass", "bypass=true", "true");
            Keeps("Windows Server", "Windows Server 2019 Standard", "2019");
            Keeps("YAML-блок", "password:\n  - item", "- item");
            Keeps("свойство объекта", "var x = user.id; var y = os.path;", "user.id", "os.path");
            Keeps("путь к устройству", "SCSI\\Disk&Ven_TEST&Prod_VIRTUAL_DISK\\123456789", "SCSI\\Disk");
            Keeps("имя узла не MoRef", "ESXI-HOST-01", "ESXI-HOST-01");
            KeepsQuick("имя узла без контекста остаётся в быстром режиме", "перезагрузка DC01 завершена", "DC01");
        }

        // ---------------------------------------------------------------- ФИО

        private static void PersonTests()
        {
            Section("ФИО");
            Hides("ФИО полностью", "Заявку создал Иванов Иван Иванович вчера", "Иванов", "Иван Иванович");
            AssertType("тип ФИО", "Иванов Иван Иванович", "Иванов Иван Иванович", "PERSON");
            Hides("имя, отчество, фамилия", "Звонил Сергей Петрович Сидоров из бухгалтерии", "Сергей", "Петрович", "Сидоров");
            Hides("женское ФИО", "Ответственная: Сидорова Анна Петровна.", "Сидорова", "Анна", "Петровна");
            Hides("заглавными буквами", "ИВАНОВ ИВАН ИВАНОВИЧ", "ИВАНОВ", "ИВАНОВИЧ");
            Hides("фамилия заглавными", "ИВАНОВ Иван Иванович", "ИВАНОВ", "Иванович");
            Hides("падежи", "Передать Иванову Ивану Ивановичу, копия Петровой Анне Сергеевне",
                "Иванову", "Ивановичу", "Петровой", "Сергеевне");
            Hides("родительный падеж", "Ноутбук Сергея Ивановича не включается", "Сергея", "Ивановича");
            Keeps("слово перед именем в падеже не входит в имя", "Ноутбук Сергея Ивановича не включается", "Ноутбук");
            Hides("буква ё", "Семёнов Пётр Фёдорович", "Семёнов", "Фёдорович");
            Hides("фамилия и инициалы", "Исполнитель: Петров П.П.", "Петров");
            Hides("инициалы через пробел", "Иванов И. И. согласовал", "Иванов");
            Hides("инициалы перед фамилией", "Утвердил И.И. Иванов", "Иванов");
            Hides("инициалы вплотную", "Подпись: И.И.Иванов", "Иванов");
            Hides("один инициал", "Передал В. Петрова", "Петрова");
            Hides("инициалы и фамилия без окончания", "Бухгалтер И.И. Кац", "Кац");
            Hides("имя и фамилия", "Иван Петров сообщил о сбое", "Иван Петров");
            Hides("фамилия и имя", "Петрова Мария", "Петрова Мария");
            Hides("имя и фамилия в падеже", "письмо от Марии Ивановой", "Марии Ивановой");
            Hides("двойная фамилия", "Римский-Корсаков Николай Андреевич", "Римский-Корсаков");
            Hides("имя и отчество", "Иван Петрович, добрый день", "Иван Петрович");
            Hides("латиницей с отчеством", "Ivanov Ivan Ivanovich", "Ivanov", "Ivanovich");
            Hides("латиницей имя и фамилия", "Contact: Ivan Petrov", "Ivan Petrov");
            Hides("латиницей заглавными", "PETROV IVAN", "PETROV IVAN");
            Hides("латиницей инициалы", "Signed by I.I. Ivanov", "Ivanov");
            Hides("английское имя", "Assigned to John Smith yesterday", "John Smith");
            Hides("подпись ФИО", "ФИО: Цой Виктор", "Цой Виктор");
            Hides("подпись Фамилия", "Фамилия: Шойгу", "Шойгу");
            Hides("displayName", "\"displayName\": \"Кац Анна\"", "Кац Анна");
            Hides("обращение", "Прошу г-на Кацмана перезвонить", "Кацмана");
            Hides("должность перед фамилией", "сотрудник Сидоров не может войти", "Сидоров");
            Hides("поле заявки", "Исполнитель: Петров, срок: пятница", "Петров");
            Hides("поле Jira", "Assignee: Kevin Hart", "Kevin Hart");
            Hides("имя в экранированном тексте", "{\"msg\": \"Заявка от Иванов Иван Иванович\\nтел.\"}", "Иванов", "Иванович");
            Hides("подпись в экранированном тексте", "С уважением,\\nИванов И.И.\\nинженер", "Иванов");
            HidesQuick("ФИО скрывается и в быстрой вставке", "Отчёт подготовил Иванов Иван Иванович", "Иванов");
            HidesQuick("фамилия с инициалами в быстрой вставке", "Отв. Петров П.П.", "Петров");
            HidesQuick("имя и фамилия в быстрой вставке", "Иван Петров сообщил о сбое", "Петров");
            Keeps("обращение не входит в имя", "Уважаемый Иван Петрович, добрый день", "Уважаемый");
            Keeps("должность не входит в имя", "Директор Иванов И.И.", "Директор");
            Keeps("глагол не входит в имя", "Обновил Иван Петрович сервер", "Обновил");
            Keeps("приложение с буквой", "Приложение А. Схема сети", "Приложение", "Схема сети");
            Keeps("И.о. не инициалы", "И.о. директора Кузнецова", "И.о.");
            Keeps("города и улицы", "Москва, ул. Ленина, Санкт-Петербург, Нижний Новгород",
                "Москва", "Ленина", "Санкт-Петербург", "Нижний Новгород");
            // Название после ООО скрывает ContextNames как TEXT, но ФИО оно не становится.
            NoType("сокращения организаций не ФИО", "ООО Ромашка, ИНН 7701234567", "PERSON");
            Keeps("продукты", "Microsoft Teams и Visual Studio Code", "Microsoft Teams", "Visual Studio Code");
            Keeps("U.S. не инициалы", "U.S. Army and P.S. Note", "U.S. Army", "P.S. Note");
            Keeps("CamelCase", "DevOps Engineer и iPhone Pro", "DevOps Engineer");
            Keeps("имена, которые чаще слова", "Max Connections и Adam Optimizer", "Max Connections", "Adam Optimizer");
            Keeps("прилагательное на -ична", "Проверка Системы Критична", "Проверка Системы Критична");
            Keeps("слово на -ов перед именем", "Слова Ивана подтвердились", "Слова");
            AssertType("имя в учётной записи", "CORP\\Ivanov", "CORP\\Ivanov", "USER");
            FoundButKept("имя и слово без окончания фамилии", "Сергей Шойгу", "Сергей Шойгу");
            FoundButKept("фамилия впереди и имя в падеже", "Вызвать Смирнова Ивана", "Смирнова Ивана");
            KeepsQuick("догадка не скрывается в быстрой вставке", "Сергей Шойгу", "Сергей Шойгу");
            HidesMode("строгий режим скрывает догадку", "Сергей Шойгу", ControlMode.Strict, "Шойгу");
        }

        // ---------------------------------------------------------------- пути

        private static void PathTests()
        {
            Section("Пути");
            const string local = @"Лог лежит в D:\Projects\Alpha\build.log.";
            FoundButKept("путь найден, но в обычном режиме остаётся", local, @"D:\Projects\Alpha\build.log");
            AssertType("тип пути", local, @"D:\Projects\Alpha\build.log", "PATH");
            KeepsQuick("быстрая вставка в обычном режиме путь оставляет", local, @"D:\Projects\Alpha\build.log");
            Strict("путь Windows", local, "Лог лежит в [PATH_1].");
            Strict("путь с пробелами", @"Файл C:\Program Files\My App\app.exe не найден", "Файл [PATH_1] не найден");
            Strict("путь в кавычках", "\"C:\\Program Files\\My App\\my file.txt\"", "\"[PATH_1]\"");
            Strict("файл с пробелом в имени", @"Открыл C:\Temp\мой отчёт.docx и закрыл", "Открыл [PATH_1] и закрыл");
            Strict("прямые слеши", "C:/Users/Public/report.txt", "[PATH_1]");
            Strict("экранированный путь в JSON", "{\"path\": \"C:\\\\Users\\\\Public\\\\x.txt\"}", "{\"path\": \"[PATH_1]\"}");
            Strict("стек вызовов", @"at Program.Main() in C:\src\App\Program.cs:line 42",
                "at Program.Main() in [PATH_1]:line 42");
            Strict("переменная окружения", @"%APPDATA%\Microsoft\Teams\logs.txt", "[PATH_1]");
            Strict("PowerShell", @"Get-Content $env:USERPROFILE\Desktop\notes.txt", "Get-Content [PATH_1]");
            Strict("длинный путь", @"\\?\C:\very\long\path.txt", "[PATH_1]");

            const string unc = @"Скопируйте \\fs01\Share\docs\plan.xlsx себе";
            Strict("сетевая папка", unc, "Скопируйте [PATH_1] себе");
            Balanced("сетевая папка в обычном режиме", unc, @"Скопируйте \\[HOST_1]\[SHARE_1]\docs\plan.xlsx себе");
            Balanced("сетевая папка с пробелом", @"\\fs01\Общая папка\Отчёты\x.xlsx", @"\\[HOST_1]\[SHARE_1]\Отчёты\x.xlsx");
            Strict("сетевая папка с пробелом в строгом режиме", @"Файл \\fs01\Общая папка\Отчёты 2024\x.xlsx готов", "Файл [PATH_1] готов");
            Strict("сетевая папка по адресу", @"\\10.0.0.5\c$\Temp", "[PATH_1]");
            const string samba = "mount -t cifs //fs01/share /mnt/share";
            Strict("сетевая папка Linux", samba, "mount -t cifs [PATH_1] [PATH_2]");
            Balanced("сетевая папка Linux в обычном режиме", samba, "mount -t cifs //[HOST_1]/[SHARE_1] /mnt/share");
            Keeps("ссылка без схемы не сетевая папка", "<script src=\"//cdn.example.com/lib/app.js\">", "lib");

            Strict("путь Linux", "cat /etc/nginx/nginx.conf", "cat [PATH_1]");
            Strict("домашняя папка", "ssh-keygen -f ~/.ssh/id_ed25519", "ssh-keygen -f [PATH_1]");
            Strict("переменная в пути Linux", "source $HOME/.config/app.yml", "source [PATH_1]");
            Strict("относительные пути", @".\scripts\deploy.ps1 и ../lib/utils.js", "[PATH_1] и [PATH_2]");
            Strict("относительный путь Windows", @"bin\Debug\app.exe", "[PATH_1]");
            Strict("относительный путь с расширением", "src/main/java/App.java", "[PATH_1]");

            const string profile = @"C:\Users\ivanov\Documents\report.docx";
            Balanced("имя пользователя в профиле Windows", profile, @"C:\Users\[USER_1]\Documents\report.docx");
            Strict("профиль Windows в строгом режиме", profile, "[PATH_1]");
            Balanced("профиль с пробелом", @"C:\Users\Иван Иванов\Desktop\x.txt", @"C:\Users\[USER_1]\Desktop\x.txt");
            Balanced("сетевая папка профилей", @"\\fs01\Users\ivanov\docs", @"\\[HOST_1]\[SHARE_1]\[USER_1]\docs");
            Balanced("профиль macOS", "/Users/ivanov/Library/Logs/app.log", "/Users/[USER_1]/Library/Logs/app.log");
            Balanced("профиль Linux", "/home/ivanov/.bashrc", "/home/[USER_1]/.bashrc");
            HidesQuick("профиль скрывается в быстрой вставке", profile, "ivanov");
            Check("лёгкий режим оставляет путь и профиль",
                SanitizeMode(profile, null, ControlMode.Light, true) == profile, SanitizeMode(profile, null, ControlMode.Light, true));
            Balanced("служебный профиль не скрывается", @"C:\Users\Public\Desktop\x.lnk", @"C:\Users\Public\Desktop\x.lnk");
            Balanced("адрес в журнале веб-сервера не профиль", "GET /users/42/profile", "GET /users/42/profile");

            KeepsStrict("не пути", "dir /s /b, and/or, TCP/IP, 1/2, 12/31/2024, application/json, text/html",
                "/s /b", "and/or", "TCP/IP", "1/2", "12/31/2024", "application/json", "text/html");
            KeepsStrict("реестр и устройства", @"HKLM\SOFTWARE\Microsoft\Windows и SCSI\Disk&Ven_TEST\123",
                @"HKLM\SOFTWARE\Microsoft\Windows", @"SCSI\Disk&Ven_TEST\123");
            KeepsStrict("экранированные переводы строк", "\"message\": \"line1\\nline2\\nline3\"", "line1\\nline2\\nline3");
            KeepsStrict("диск без пути", @"Диск C:\ заполнен", @"C:\ заполнен");
            NoType("ссылка не путь", "https://example.com/docs/guide/setup.md", "PATH");
            NoType("время и дробь не путь", "в 10:30/11:00 и 3/4", "PATH");
        }

        private static void Strict(string name, string text, string expected)
        {
            string review = SanitizeMode(text, null, ControlMode.Strict, false);
            Check(name, review == expected, review);
            string quick = SanitizeMode(text, null, ControlMode.Strict, true);
            Check(name + " (быстрая вставка)", quick == expected, quick);
        }

        private static void Balanced(string name, string text, string expected)
        {
            string review = SanitizeMode(text, null, ControlMode.Balanced, false);
            Check(name, review == expected, review);
        }

        private static void KeepsStrict(string name, string text, params string[] values)
        {
            string result = SanitizeMode(text, null, ControlMode.Strict, false);
            foreach (string value in values)
            {
                Check(name + " (" + value + ")", result.Contains(value), result);
            }
        }

        private static void HidesMode(string name, string text, ControlMode mode, params string[] values)
        {
            string result = SanitizeMode(text, null, mode, false);
            foreach (string value in values)
            {
                Check(name, !result.Contains(value), result);
            }
        }

        private static void NoType(string name, string text, string type)
        {
            foreach (Detection detection in Detector.Scan(text, null, ControlMode.Strict, false))
            {
                if (detection.Type == type)
                {
                    Check(name, false, detection.Value);
                    return;
                }
            }
            Check(name, true, "");
        }

        // ---------------------------------------------------------------- случаи из ручного теста

        private static void ManualTestCases()
        {
            Section("Случаи из ручного теста");
            HidesQuick("серийник с разделителем", "S/N: ABCD12345678", "ABCD12345678");
            HidesQuick("серийник без разделителя", "SN1234567890", "1234567890");
            HidesQuick("Service Tag", "Dell Service Tag: ABCD123", "ABCD123");
            HidesQuick("FQDN с незнакомым доменом", "backup01.corp.example", "backup01.corp.example");
            HidesQuick("хост по контексту", "Host=FS01", "FS01");
            HidesQuick("отпечаток SHA256", "SHA256:abcdefghijklmnopqrstuvwx1234567890ABCDEFG", "SHA256:abcdefg");
            HidesQuick("отпечаток MD5", "MD5:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff", "00:11:22:33:44:55");
            HidesQuick("UUID из BIOS", "VMware UUID: 56 4d 12 34 ab cd ef 00-11 22 33 44 55 66 77 88", "56 4d 12 34");
            Hides("отпечаток сертификата", "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", "A1B2C3D4E5F6");
            Hides("телефон", "Контакт: +1-202-555-0147", "+1-202-555-0147");
            // Без подсказок вокруг уверенности мало: находка показывается, но не скрывается сама.
            FoundButKept("имя узла без контекста", "перезагрузка DC01 завершена", "DC01");
            FoundButKept("серийник без контекста", "плата FOC1234ABCD заменена", "FOC1234ABCD");
            Keeps("аббревиатуры не узлы", "алгоритм SHA256 и AES256, порт COM1", "SHA256", "AES256", "COM1");
        }

        // ---------------------------------------------------------------- быстрый режим

        private static void QuickModeTests()
        {
            Section("Быстрый режим");
            HidesQuick("секрет в быстром режиме", "password=hunter2", "hunter2");
            HidesQuick("IP в быстром режиме", "ping 10.20.30.40", "10.20.30.40");
            HidesQuick("явный контекст скрывается", "hostname: SRV-DB01", "SRV-DB01");
            KeepsQuick("догадка без контекста остаётся", "оборудование FOC1234ABCD", "FOC1234ABCD");

            SafePasteDatabase database = new SafePasteDatabase();
            database.AddLearned("SRV-DB01", "HOST");
            string quick = Sanitize("Проверьте SRV-DB01 сегодня", database, true);
            Check("заученное значение в быстром режиме", !quick.Contains("SRV-DB01"), quick);

            string sentence = Sanitize("Проблема на SRV-DB01.", database, true);
            Check("заученное значение перед точкой", !sentence.Contains("SRV-DB01"), sentence);

            SafePasteDatabase allowed = new SafePasteDatabase();
            allowed.AddAllowed("admin");
            string mixed = Sanitize("user=admin password=admin", allowed, false);
            Check("allowlist не раскрывает пароль", !mixed.Contains("password=admin"), mixed);
            Check("allowlist оставляет логин", mixed.Contains("user=admin"), mixed);
        }

        // ---------------------------------------------------------------- режимы

        private static void ModeTests()
        {
            Section("Режимы");
            const string text = "hostname: SRV-DB01\r\nping 10.20.30.40\r\nоборудование FOC1234ABCD\r\npassword=hunter2";

            string strictQuick = SanitizeMode(text, null, ControlMode.Strict, true);
            Check("строгий режим скрывает догадку", !strictQuick.Contains("FOC1234ABCD"), strictQuick);
            Check("строгий режим скрывает IP и имя", !strictQuick.Contains("10.20.30.40") && !strictQuick.Contains("SRV-DB01"), strictQuick);
            string strictReview = SanitizeMode(text, null, ControlMode.Strict, false);
            Check("строгий режим в окне проверки отмечает догадку", !strictReview.Contains("FOC1234ABCD"), strictReview);

            Check("обычный режим в быстрой вставке как раньше",
                SanitizeMode(text, null, ControlMode.Balanced, true) == Sanitize(text, null, true), "");
            Check("обычный режим в окне проверки как раньше",
                SanitizeMode(text, null, ControlMode.Balanced, false) == Sanitize(text, null, false), "");
            Check("обычный режим оставляет догадку",
                SanitizeMode(text, null, ControlMode.Balanced, false).Contains("FOC1234ABCD"), "");

            string light = SanitizeMode(text, null, ControlMode.Light, true);
            Check("лёгкий режим скрывает пароль", !light.Contains("hunter2"), light);
            Check("лёгкий режим оставляет IP", light.Contains("10.20.30.40"), light);
            Check("лёгкий режим оставляет имя из подсказки", light.Contains("SRV-DB01"), light);
            string lightReview = SanitizeMode(text, null, ControlMode.Light, false);
            Check("лёгкий режим в окне проверки", lightReview.Contains("10.20.30.40") && !lightReview.Contains("hunter2"), lightReview);

            SafePasteDatabase learned = new SafePasteDatabase();
            learned.AddLearned("SRV-DB01", "HOST");
            string lightLearned = SanitizeMode(text, learned, ControlMode.Light, true);
            Check("лёгкий режим скрывает значения из правил", !lightLearned.Contains("SRV-DB01"), lightLearned);

            List<Detection> strict = Detector.Scan(text, null, ControlMode.Strict, false);
            List<Detection> balanced = Detector.Scan(text, null, ControlMode.Balanced, false);
            Check("режим меняет галочки, а не набор находок", strict.Count == balanced.Count,
                strict.Count + " и " + balanced.Count);

            // Путь накрывает узел и папку: какой из них останется, решают галочки режима.
            const string share = @"Отчёт в \\fs01\Share\2024\report.docx";
            List<Detection> strictShare = Detector.Scan(share, null, ControlMode.Strict, false);
            List<Detection> balancedShare = Detector.Scan(share, null, ControlMode.Balanced, false);
            Check("строгий режим берёт путь целиком", strictShare.Count == 1 && strictShare[0].Type == "PATH"
                && strictShare[0].Enabled, strictShare.Count.ToString());
            Check("обычный режим скрывает узел и папку внутри пути",
                balancedShare.Exists(delegate(Detection item) { return item.Type == "HOST" && item.Enabled; })
                && balancedShare.Exists(delegate(Detection item) { return item.Type == "SHARE" && item.Enabled; })
                && !balancedShare.Exists(delegate(Detection item) { return item.Type == "PATH"; }), "");

            SafePasteDatabase allowed = new SafePasteDatabase();
            allowed.AddAllowed("10.20.30.40");
            Check("исключения действуют и в строгом режиме",
                SanitizeMode(text, allowed, ControlMode.Strict, true).Contains("10.20.30.40"), "");

            SafePasteSettings settings = new SafePasteSettings();
            settings.Mode = ControlMode.Light;
            settings.Save();
            Check("режим сохраняется в settings.json", SafePasteSettings.Load().Mode == ControlMode.Light, "");
            File.Delete(Paths.SettingsFile);
            Check("без настроек режим обычный", SafePasteSettings.Load().Mode == ControlMode.Balanced, "");
            Check("непонятный режим читается как обычный", ControlModes.Parse("fast", ControlMode.Balanced) == ControlMode.Balanced, "");
        }

        private static string SanitizeMode(string text, SafePasteDatabase database, ControlMode mode, bool quick)
        {
            return Replacer.Apply(text, Detector.Scan(text, database, mode, quick)).Text;
        }

        // ---------------------------------------------------------------- заученное значение целиком

        private static void LearnedSpanTests()
        {
            Section("Заученное значение целиком");
            const string path = @"\\domain\folder\it\tratata";
            string text = @"Отчёт лежит в " + path + @"\report.docx, копия в \\domain\folder\old";
            string before = Sanitize(text, null, false);
            Check("без правила UNC скрывается по частям", !before.Contains("domain") && before.Contains("tratata"), before);

            SafePasteDatabase database = new SafePasteDatabase();
            database.AddLearned(path, "HOST");
            string review = Sanitize(text, database, false);
            Check("заученный путь скрывается целиком", !review.Contains("tratata") && !review.Contains(@"\it\"), review);
            Check("заученный путь получает один заполнитель", review.Contains("[HOST_1]\\report.docx"), review);
            Check("другой путь на том же сервере скрывается как раньше",
                !review.Contains(@"\\domain\folder\old") && review.Contains("old"), review);
            string quick = Sanitize(text, database, true);
            Check("заученный путь скрывается целиком и в быстром режиме", !quick.Contains("tratata"), quick);
            string light = SanitizeMode(text, database, ControlMode.Light, true);
            Check("заученный путь скрывается целиком в лёгком режиме", !light.Contains("tratata"), light);

            SafePasteDatabase shortRule = new SafePasteDatabase();
            shortRule.AddLearned("admin", "USER");
            string url = Sanitize("Вход: https://admin.example.com/login?next=/", shortRule, false);
            Check("короткое правило не дробит ссылку", !url.Contains("example.com") && !url.Contains("login"), url);

            SafePasteDatabase withSecret = new SafePasteDatabase();
            withSecret.AddLearned("backup01", "HOST");
            string secret = Sanitize("host=backup01 password=backup01pass", withSecret, false);
            Check("правило не забирает пароль у секрета", !secret.Contains("backup01pass"), secret);

            SafePasteDatabase fqdn = new SafePasteDatabase();
            fqdn.AddLearned("srv01.corp.local", "HOST");
            AssertLearnedType("тип из правила важнее автоматического", "ping srv01.corp.local", fqdn, "srv01.corp.local", "HOST");

            SafePasteDatabase edge = new SafePasteDatabase();
            edge.AddLearned(@"\\domain\folder\it\", "HOST");
            string trailing = Sanitize(@"см. \\domain\folder\it\tratata", edge, false);
            Check("правило с разделителем в конце находится", !trailing.Contains(@"\it\"), trailing);
            SafePasteDatabase inner = new SafePasteDatabase();
            inner.AddLearned(@"\it\tratata", "HOST");
            string leading = Sanitize(@"см. \\domain\folder\it\tratata", inner, false);
            Check("правило с разделителем в начале находится", !leading.Contains("tratata"), leading);
            Check("разделитель на краю не мешает поиску вхождений",
                Detector.FindOccurrences(@"a \\domain\folder\it\x и \\domain\folder\it\y", @"\\domain\folder\it\").Count == 2, "");
            SafePasteDatabase word = new SafePasteDatabase();
            word.AddLearned("SRV-DB01", "HOST");
            Check("граница слова для обычных имён остаётся", Sanitize("SRV-DB01-old", word, false).Contains("SRV-DB01-old"), "");
        }

        private static void AssertLearnedType(string name, string text, SafePasteDatabase database, string value, string expected)
        {
            string actual = "нет находки";
            foreach (Detection detection in Detector.Scan(text, database, false))
            {
                if (detection.Value == value)
                {
                    actual = detection.Type;
                    break;
                }
            }
            Check(name, actual == expected, "получено: " + actual);
        }

        // ---------------------------------------------------------------- замены

        private static void ReplacementTests()
        {
            Section("Замены");
            string text = "srv01.corp.local и SRV01.CORP.LOCAL и dc02.corp.local";
            string result = Sanitize(text, null, false);
            Check("одинаковые значения получают один заполнитель",
                CountOccurrences(result, "[FQDN_1]") == 2 && result.Contains("[FQDN_2]"), result);

            List<Detection> detections = Detector.Scan("password=hunter2 и ip 10.0.0.1", null, false);
            string masked = Replacer.MaskSecrets("password=hunter2 и ip 10.0.0.1", detections);
            Check("маскировка сохраняет длину", masked.Length == "password=hunter2 и ip 10.0.0.1".Length, masked);
            Check("маскировка прячет секрет", !masked.Contains("hunter2"), masked);
            Check("маскировка не трогает IP", masked.Contains("10.0.0.1"), masked);

            List<Detection> disabled = Detector.Scan("ping 10.20.30.40", null, false);
            foreach (Detection detection in disabled)
            {
                detection.Enabled = false;
            }
            ReplacementResult untouched = Replacer.Apply("ping 10.20.30.40", disabled);
            Check("снятая галочка оставляет значение", untouched.Text == "ping 10.20.30.40", untouched.Text);
        }

        // ---------------------------------------------------------------- закреплённые номера

        private static void ReservationTests()
        {
            Section("Закреплённые номера");
            SafePasteDatabase database = new SafePasteDatabase();
            database.Reserve("IP", "10.44.7.219", 7);

            string first = Replacer.Apply("ping 10.44.7.219", Detector.Scan("ping 10.44.7.219", database, true), database).Text;
            Check("закреплённый номер используется", first == "ping [IP_7]", first);

            string other = "connect 10.0.0.1 then 10.44.7.219";
            string second = Replacer.Apply(other, Detector.Scan(other, database, true), database).Text;
            Check("чужой номер не занимает закреплённый", second == "connect [IP_1] then [IP_7]", second);

            database.Reserve("IP", "10.0.0.1", 1);
            string third = "10.44.7.219 и 10.0.0.1 и 10.9.9.9";
            string result = Replacer.Apply(third, Detector.Scan(third, database, true), database).Text;
            Check("номера стабильны между вставками", result == "[IP_7] и [IP_1] и [IP_2]", result);

            // Закрепление подразумевает, что значение вообще находится.
            database.AddLearned("BACKUP-SRV01", "HOST");
            database.Reserve("HOST", "BACKUP-SRV01", 3);
            string host = Replacer.Apply("узел BACKUP-SRV01 упал",
                Detector.Scan("узел BACKUP-SRV01 упал", database, true), database).Text;
            Check("закреплённый узел скрывается в быстром режиме", host == "узел [HOST_3] упал", host);

            database.Unreserve("HOST", "BACKUP-SRV01");
            Check("закрепление снимается", database.GetReservedIndex("HOST", "BACKUP-SRV01") == 0, "");

            database.Reserve("IP", "10.44.7.219", 7);
            database.Save();
            SafePasteDatabase loaded = SafePasteDatabase.Load();
            Check("закрепления переживают перезапуск", loaded.GetReservedIndex("ip", "10.44.7.219") == 7, "");

            loaded.AddAllowed("10.44.7.219");
            Check("allowlist снимает закрепление", loaded.GetReservedIndex("IP", "10.44.7.219") == 0, "");

            File.Delete(Paths.DatabaseFile);
        }

        // ---------------------------------------------------------------- хранилище

        private static void StorageTests()
        {
            Section("База и настройки");
            SafePasteDatabase database = new SafePasteDatabase();
            database.AddLearned("SRV-DB01", "host");
            database.AddAllowed("example.com");
            database.Save();

            SafePasteDatabase loaded = SafePasteDatabase.Load();
            Check("база сохраняется и читается",
                loaded.Learned.Count == 1 && loaded.Learned[0].Type == "HOST" && loaded.IsAllowed("EXAMPLE.COM"), "");

            loaded.AddAllowed("SRV-DB01");
            Check("allowlist вытесняет заученное", loaded.Learned.Count == 0 && loaded.IsAllowed("srv-db01"), "");
            loaded.AddLearned("SRV-DB01", "HOST");
            Check("заученное вытесняет allowlist", loaded.Learned.Count == 1 && !loaded.IsAllowed("SRV-DB01"), "");
            loaded.RemoveLearned("srv-db01", null);
            Check("удаление заученного", loaded.Learned.Count == 0, "");

            File.WriteAllBytes(Paths.DatabaseFile, new byte[] { 1, 2, 3, 4, 5 });
            bool threw = false;
            try { SafePasteDatabase.Load(); }
            catch (DatabaseException) { threw = true; }
            Check("повреждённая база не молчит", threw, "");
            string backup = SafePasteDatabase.Reset();
            Check("сброс базы сохраняет копию", backup != null && File.Exists(backup), backup ?? "null");
            Check("после сброса база пустая", SafePasteDatabase.Load().Learned.Count == 0, "");

            File.WriteAllText(Paths.SettingsFile, "{\"ClipboardClearDelayMs\": 2500}");
            SafePasteSettings settings = SafePasteSettings.Load();
            Check("частичные настройки применяются", settings.ClipboardClearDelayMs == 2500, settings.ClipboardClearDelayMs.ToString());
            Check("остальные значения по умолчанию", settings.MaxClipboardChars == 1000000 && settings.ShowClipboardWarning, "");

            File.WriteAllText(Paths.SettingsFile, "{\"ClipboardClearDelayMs\": 5}");
            Check("слишком малая задержка поднимается до минимума", SafePasteSettings.Load().ClipboardClearDelayMs == 200, "");

            File.WriteAllText(Paths.SettingsFile, "не json");
            Check("битые настройки не ломают запуск", SafePasteSettings.Load().ClipboardClearDelayMs == 1500, "");
            File.Delete(Paths.SettingsFile);
        }

        // ---------------------------------------------------------------- скорость

        private static void PerformanceTest()
        {
            Section("Скорость");
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < 4000; index++)
            {
                builder.AppendLine("2026-09-22 10:00:00 srv" + index + ".corp.local 10.20." + (index % 250) + ".7 user=user" + index + " GET /api/items?id=" + index);
            }
            string text = builder.ToString();
            Stopwatch watch = Stopwatch.StartNew();
            List<Detection> detections = Detector.Scan(text, null, false);
            ReplacementResult result = Replacer.Apply(text, detections);
            watch.Stop();
            Console.WriteLine("  " + (text.Length / 1024) + " КБ, находок: " + detections.Count +
                ", время: " + watch.ElapsedMilliseconds + " мс");
            Check("мегабайтный лог обрабатывается быстрее 5 с", watch.ElapsedMilliseconds < 5000,
                watch.ElapsedMilliseconds + " мс");
            Check("замены применились", result.Text.Contains("[FQDN_1]") || result.Text.Contains("[IP_1]"), "");
        }

        // ---------------------------------------------------------------- инфраструктура

        private static string Sanitize(string text, SafePasteDatabase database, bool quick)
        {
            List<Detection> detections = Detector.Scan(text, database, quick);
            return Replacer.Apply(text, detections).Text;
        }

        private static void Hides(string name, string text, params string[] secrets)
        {
            string result = Sanitize(text, null, false);
            foreach (string secret in secrets)
            {
                if (secret.StartsWith("\"password\""))
                {
                    Check(name, result.Contains(secret), result);
                }
                else
                {
                    Check(name, !result.Contains(secret), result);
                }
            }
        }

        private static void HidesQuick(string name, string text, string secret)
        {
            string result = Sanitize(text, null, true);
            Check(name, !result.Contains(secret), result);
        }

        /// <summary>Находка есть, но по умолчанию выключена: значение остаётся в тексте.</summary>
        private static void FoundButKept(string name, string text, string value)
        {
            List<Detection> detections = Detector.Scan(text, null, false);
            bool found = false;
            foreach (Detection detection in detections)
            {
                if (detection.Value == value)
                {
                    found = !detection.Enabled;
                    break;
                }
            }
            Check(name + " (найдено, но выключено)", found, "");
            Check(name + " (осталось в тексте)", Sanitize(text, null, false).Contains(value), "");
        }

        private static void KeepsQuick(string name, string text, string value)
        {
            string result = Sanitize(text, null, true);
            Check(name, result.Contains(value), result);
        }

        private static void Keeps(string name, string text, params string[] values)
        {
            string result = Sanitize(text, null, false);
            foreach (string value in values)
            {
                Check(name + " (" + value + ")", result.Contains(value), result);
            }
        }

        private static void AssertType(string name, string text, string value, string expectedType)
        {
            List<Detection> detections = Detector.Scan(text, null, false);
            string actual = "нет находки";
            foreach (Detection detection in detections)
            {
                if (detection.Value == value)
                {
                    actual = detection.Type;
                    break;
                }
            }
            Check(name, actual == expectedType, "получено: " + actual);
        }

        private static void Check(string name, bool condition, string details)
        {
            if (condition)
            {
                passed++;
                return;
            }
            Failures.Add(name + (string.IsNullOrEmpty(details) ? "" : " -> " + Shorten(details)));
        }

        private static string Shorten(string value)
        {
            value = value.Replace("\r", "\\r").Replace("\n", "\\n");
            return value.Length <= 160 ? value : value.Substring(0, 157) + "...";
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = text.IndexOf(value, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
            }
            return count;
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name);
        }
    }
}
