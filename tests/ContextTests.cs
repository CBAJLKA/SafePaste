using System;
using System.Collections.Generic;
using System.IO;
using SafePaste.Bridge;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Tests
{
    /// <summary>Повторы значений, имена по контексту и сторож утечек для коротких значений.</summary>
    public static partial class TestProgram
    {
        private static void ContextTests()
        {
            RepeatTests();
            ContextNameTests();
            ContextFalsePositiveTests();
            LeakGuardRepeatTests();
        }

        // ---------------------------------------------------------------- повторы

        private static void RepeatTests()
        {
            Section("Повторы значений");
            Balanced("повтор имени узла", "hostname: srv-web\nВчера srv-web упал.", "hostname: [HOST_1]\nВчера [HOST_1] упал.");
            HidesQuick("повтор имени узла в быстрой вставке", "hostname: srv-web\nВчера srv-web упал.", "srv-web");
            HidesQuick("повтор пароля", "Пароль: Лето2024!\nЯ ввёл Лето2024! и меня не пустило.", "Лето2024!");
            Balanced("повтор пароля одной меткой", "password=Qwerty123 потом Qwerty123 ещё раз",
                "password=[SECRET_1] потом [SECRET_1] ещё раз");
            Check("лёгкий режим прячет повтор пароля",
                !SanitizeMode("password=Qwerty123 потом Qwerty123", null, ControlMode.Light, true).Contains("Qwerty123"), "");
            Balanced("повтор серийного номера", "Serial Number: FOC1234ABCD\nЗамена платы для FOC1234ABCD.",
                "Serial Number: [SERIAL_1]\nЗамена платы для [SERIAL_1].");
            Balanced("повтор в другом регистре", "Сервер: PHOENIX\nPhoenix не отвечает.", "Сервер: [HOST_1]\n[HOST_1] не отвечает.");
            Balanced("короткое имя из FQDN", "Сервер phoenix.corp.local перезагружен, phoenix снова в строю.",
                "Сервер [FQDN_1] перезагружен, [HOST_1] снова в строю.");
            Balanced("узел из ссылки", "Открой https://jira.corp.example/browse/OPS-1, jira.corp.example тормозит",
                "Открой [URL_1], [FQDN_1] тормозит");
            Balanced("учётная запись из DOMAIN\\user", "Вход CORP\\svc_backup выполнен, но svc_backup не видит папку.",
                "Вход [USER_1] выполнен, но [USER_2] не видит папку.");
            Hides("фамилия отдельно", "Иванов Иван Иванович перезапустил службу. Иванов не помог.", "Иванов не");
            Hides("фамилия в другом падеже", "Иванов Иван Иванович перезапустил службу. Логи передали Иванову.", "Иванову");
            Hides("фамилия с ё и без", "Семёнов Пётр Фёдорович написал. Семенову ответили.", "Семенову");
            Keeps("слово с маленькой буквы не фамилия", "Волков Пётр Ильич пишет, что видел волков.", "видел волков");
            Keeps("обычное слово не повторяется", "hostname: test\nПрогони test ещё раз", "Прогони test ещё раз");
            Keeps("короткое значение не повторяется", "user: sa\nsa и прочие", "sa и прочие");
            Keeps("общее слово из пути не повторяется", "mount -t cifs //fs01/share /mnt/share", "/mnt/share");

            SafePasteDatabase allowed = new SafePasteDatabase();
            allowed.AddAllowed("phoenix");
            string kept = SanitizeMode("Узел phoenix.corp.local, он же phoenix", allowed, ControlMode.Balanced, false);
            Check("исключение не выводится из FQDN", kept.Contains("он же phoenix") && !kept.Contains("phoenix.corp"), kept);

            List<Detection> strict = Detector.Scan("плата FOC1234ABCD и снова FOC1234ABCD", null, ControlMode.Strict, false);
            List<Detection> balanced = Detector.Scan("плата FOC1234ABCD и снова FOC1234ABCD", null, ControlMode.Balanced, false);
            Check("догадка повторяется только там, где её скрывают", strict.Count == 2 && balanced.Count == 2
                && strict.TrueForAll(delegate(Detection item) { return item.Enabled; })
                && balanced.TrueForAll(delegate(Detection item) { return !item.Enabled; }),
                strict.Count + " и " + balanced.Count);
        }

        // ---------------------------------------------------------------- имена по контексту

        private static void ContextNameTests()
        {
            Section("Имена по контексту");
            Hides("слово-имя после подсказки", "Перезагрузи кластер Orion.", "Orion");
            KeepsQuick("слово-имя после подсказки остаётся в быстрой вставке", "Перезагрузи кластер Orion.", "Orion");
            Check("причина в находке", ReasonOf("Перезагрузи кластер Orion.", "Orion").Contains("после слова «кластер»"),
                ReasonOf("Перезагрузи кластер Orion.", "Orion"));
            HidesQuick("идентификатор после подсказки", "Проверь базу crm_prod.", "crm_prod");
            AssertType("тип имени базы", "Проверь базу crm_prod.", "crm_prod", "TEXT");
            HidesQuick("подсказка в падеже", "Логи на сервере srv-web01 пустые", "srv-web01");
            Hides("латиница после подсказки", "Упал сервер Phoenix, чиним.", "Phoenix");
            HidesQuick("ООО и кавычки", "Заказчик ООО «Ромашка» просит доступ.", "Ромашка");
            HidesQuick("ООО без кавычек", "ООО Ромашка, ИНН 7701234567", "Ромашка");
            HidesQuick("название в кавычках после подсказки", "Доступ к проекту «Альтаир» закрыт.", "Альтаир");
            Hides("проект без кавычек", "Доступ к проекту Альтаир закрыт.", "Альтаир");
            HidesQuick("аргумент ping", "ping phoenix -n 4", "phoenix");
            HidesQuick("Test-NetConnection", "Test-NetConnection phoenix -Port 1433", "phoenix");
            HidesQuick("параметр ComputerName", "Invoke-Command -ComputerName buhgalter-pc -ScriptBlock { hostname }", "buhgalter-pc");
            HidesQuick("список значений параметра", "Restart-Computer -ComputerName web-01, web-02", "web-02");
            AssertType("Identity пользователя", "Get-ADUser -Identity ivanov_a -Properties *", "ivanov_a", "USER");
            AssertType("Identity компьютера", "Get-ADComputer -Identity PC-0042", "PC-0042", "HOST");
            HidesQuick("вывод ping", "Обмен пакетами с phoenix [10.0.0.5] с 32 байтами данных:", "phoenix");
            string hosts = SanitizeMode("10.0.0.5    phoenix phoenix.corp.local", null, ControlMode.Balanced, true);
            Check("строка hosts", !hosts.Contains("phoenix"), hosts);
            HidesQuick("mstsc", "mstsc /v:buhgalter-pc", "buhgalter-pc");
            string json = SanitizeMode("{\"cluster\": \"orion\", \"database\": \"billing\"}", null, ControlMode.Balanced, true);
            Check("ключи в JSON", !json.Contains("orion") && !json.Contains("billing")
                && json.Contains("\"cluster\": \"[HOST_1]\""), json);
            HidesQuick("составной ключ YAML", "cluster_name: orion-prod", "orion-prod");
            HidesQuick("список после подсказки", "Серверы web01, web02 и web03 обновлены.", "web03");
            HidesQuick("RDP на узел", "Зайди по RDP на buhgalter-pc и проверь.", "buhgalter-pc");
            HidesQuick("перезагрузи узел", "Перезагрузи web01, пожалуйста", "web01");
            HidesQuick("контроллер домена", "Контроллер домена DC01 недоступен", "DC01");
            AssertType("контроллер домена это узел", "Контроллер домена DC01 недоступен", "DC01", "HOST");
            AssertType("тип после «шару»", "Открой шару Finance_2024", "Finance_2024", "SHARE");
            HidesQuick("форма после названия", "Contract with Contoso Ltd signed", "Contoso");
            FoundButKept("слово после глагола только догадка", "Зайди на Kaiten и посмотри", "Kaiten");
            HidesMode("строгий режим скрывает догадку после глагола", "Зайди на Kaiten и посмотри", ControlMode.Strict, "Kaiten");
            Check("лёгкий режим не скрывает имена по контексту",
                SanitizeMode("Проверь базу crm_prod", null, ControlMode.Light, false) == "Проверь базу crm_prod", "");
            string bridge = Replacer.Apply("Перезагрузи кластер Orion.",
                Detector.Scan("Перезагрузи кластер Orion.", null, ControlMode.Bridge, false)).Text;
            Check("мост скрывает слово-имя после подсказки", !bridge.Contains("Orion"), bridge);
        }

        private static void ContextFalsePositiveTests()
        {
            Section("Имена по контексту: ложные срабатывания");
            Keeps("обычные слова после подсказок", "Сервер упал, база недоступна, проект закрыт, сеть работает.",
                "упал", "недоступна", "закрыт", "работает");
            Keeps("продукты после подсказок",
                "На сервере Windows Server 2019 стоит Exchange, база PostgreSQL на кластере Kubernetes.",
                "Windows", "Exchange", "PostgreSQL", "Kubernetes");
            Keeps("сокращения после подсказок", "сервер DNS, сервер 1С, база СУБД, сервер БД", "DNS", "1С", "СУБД", "БД");
            Keeps("заголовки на английском",
                "Server Error in '/' Application. Domain Admins and Database Engine. Host Process for Windows Services.",
                "Error", "Admins", "Engine", "Process");
            Keeps("сеть Интернет", "Доступ в сеть Интернет пропал", "Интернет");
            Keeps("служебные имена", "ping localhost и Test-NetConnection -ComputerName localhost", "localhost");
            Keeps("ключи команд не имена", "ping -n 4 -w 1000 10.0.0.1", "-n 4 -w 1000");
            Keeps("hosts со служебным именем", "127.0.0.1 localhost", "localhost");
            Keeps("время в журнале не hosts", "12:30:45 phoenix started", "phoenix");
            Keeps("код не имя", "var db = new Database(); db.Connect(server);", "Database", "Connect", "server");
            Keeps("английская фраза", "Restart the server and check the database status.", "server and", "database status");
            Keeps("клиент почты", "Клиент Outlook не видит ящик", "Outlook");
            Keeps("продукты с заглавной внутри", "база ClickHouse и кластер OpenSearch", "ClickHouse", "OpenSearch");
            Keeps("строчное русское слово", "проект альфа закрыт", "альфа");
            Keeps("числа после подсказок", "VLAN 120, сервер 2, база 3", "120");
            Keeps("размеры после подсказок", "база 50GB, кластер 64bit", "50GB", "64bit");
            Keeps("имя файла после подсказки", "на сервере лежит config.json", "config.json");
            Keeps("подсказка в конце фразы", "Проверь сервер. Orion потом.", "Orion");
            NoType("организация не ФИО", "ООО Ромашка, ИНН 7701234567", "PERSON");
        }

        // ---------------------------------------------------------------- сторож утечек

        private static void LeakGuardRepeatTests()
        {
            Section("Сторож утечек: короткие значения");
            List<LabelEntry> entries = new List<LabelEntry>();
            entries.Add(MakeLabel("HOST", "srv-web", 1));
            entries.Add(MakeLabel("HOST", "test", 2));
            string warning;
            string guarded = LeakGuard.Check("restart srv-web now", entries, out warning);
            Check("сторож скрывает короткое значение", guarded == "restart [HOST_1] now", guarded);
            string bounded = LeakGuard.Check("srv-web01 и xsrv-web", entries, out warning);
            Check("сторож соблюдает границы слова", bounded == "srv-web01 и xsrv-web", bounded);
            string labelled = LeakGuard.Check("[HOST_1] и srv-web", entries, out warning);
            Check("сторож не трогает метки", labelled == "[HOST_1] и [HOST_1]", labelled);
            string plain = LeakGuard.Check("run the test again", entries, out warning);
            Check("сторож не ищет обычные слова", plain == "run the test again", plain);

            LabelStore store = new LabelStore(Path.Combine(Paths.DataDirectory, "context-labels.dat"), false);
            Anonymizer anonymizer = new Anonymizer(store);
            string answer = anonymizer.Anonymize("hostname: srv-web\nВчера srv-web перезагрузился.");
            Check("мост скрывает повтор в том же ответе", !answer.Contains("srv-web"), answer);
        }

        private static LabelEntry MakeLabel(string type, string value, int index)
        {
            LabelEntry entry = new LabelEntry();
            entry.Type = type;
            entry.Value = value;
            entry.Index = index;
            entry.Used = DateTime.UtcNow.ToString("yyyy-MM-dd");
            return entry;
        }

        private static string ReasonOf(string text, string value)
        {
            foreach (Detection detection in Detector.Scan(text, null, ControlMode.Balanced, false))
            {
                if (detection.Value == value)
                {
                    return detection.Reason ?? string.Empty;
                }
            }
            return "нет находки";
        }
    }
}
