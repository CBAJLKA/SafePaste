using System;
using System.Collections.Generic;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Словари, которые отделяют настоящие имена узлов от кода: "dc01.corp.local" это узел,
    /// "config.json" и "Console.WriteLine" нет. Здесь же имена и стоп-слова для поиска ФИО.
    /// </summary>
    internal static class Lexicons
    {
        /// <summary>Домены, после которых значение почти наверняка настоящий узел.</summary>
        internal static readonly HashSet<string> StrongTlds = Build(new string[] {
            // Общие домены верхнего уровня.
            "com", "net", "org", "edu", "gov", "biz", "arpa",
            // Типовые внутренние суффиксы корпоративных сетей.
            "local", "localdomain", "lan", "corp", "internal", "intranet", "intra", "private", "ad", "office", "domain",
            // Страны, чьи домены редко совпадают со словами из кода.
            "ru", "su", "рф", "рус", "xn--p1ai", "xn--p1acf", "ua", "by", "kz", "uz", "kg", "tj", "tm", "ge", "am", "az",
            "lt", "lv", "ee", "pl", "de", "uk", "eu", "fr", "nl", "cz", "sk", "ch", "se", "fi", "dk", "es", "gr", "hu",
            "ro", "bg", "hr", "si", "ie", "lu", "li", "ca", "au", "nz", "jp", "cn", "kr", "hk", "tw", "sg", "br", "ar",
            "mx", "cl", "za", "tr", "il", "ae", "sa", "ir"
        });

        /// <summary>
        /// Настоящие домены, которые часто совпадают с именами полей в коде ("user.id", "logging.info").
        /// Такие находки показываются в окне проверки, но по умолчанию не скрываются.
        /// </summary>
        internal static readonly HashSet<string> WeakTlds = Build(new string[] {
            "io", "ai", "me", "co", "cc", "tv", "fm", "ly", "app", "dev", "cloud", "tech", "online", "site", "info",
            "int", "mil", "home", "name", "email", "page", "link", "one", "top", "pro", "xyz", "club", "shop", "store",
            "live", "news", "blog", "team", "group", "space", "world", "work", "network", "systems", "solutions",
            "services", "digital", "media", "id", "is", "in", "it", "at", "to", "do", "no", "my", "be", "as", "so",
            "us", "ms", "tk", "cf", "ga", "gg", "sc", "st"
        });

        /// <summary>Расширения файлов: "readme.md" и "setup.exe" не узлы.</summary>
        internal static readonly HashSet<string> FileExtensions = Build(new string[] {
            "txt", "log", "json", "jsonc", "json5", "xml", "yml", "yaml", "md", "markdown", "rst", "csv", "tsv", "ini",
            "cfg", "conf", "config", "toml", "properties", "env", "lock", "tf", "tfstate", "tfvars", "hcl", "gitignore",
            "exe", "dll", "sys", "drv", "msi", "msp", "msu", "cab", "bat", "cmd", "ps", "psm", "psd", "vbs", "vbe",
            "wsf", "hta", "js", "mjs", "cjs", "ts", "mts", "cts", "jsx", "tsx", "vue", "svelte", "py", "pyc", "pyw",
            "pyi", "ipynb", "rb", "go", "rs", "cs", "csx", "csproj", "vb", "vbproj", "fs", "fsx", "cpp", "hpp", "cxx",
            "hxx", "cc", "hh", "c", "h", "m", "mm", "swift", "kt", "kts", "java", "class", "jar", "war", "scala",
            "groovy", "gradle", "php", "phtml", "pm", "lua", "r", "jl", "dart", "ex", "exs", "erl", "hrl", "hs", "ml",
            "clj", "sh", "bash", "zsh", "fish", "ksh", "sql", "psql", "db", "sqlite", "sqlite3", "mdb", "accdb", "bak",
            "tmp", "temp", "old", "orig", "swp", "dat", "bin", "iso", "img", "vhd", "vhdx", "vmdk", "vmx", "vmsd",
            "nvram", "ova", "ovf", "qcow2", "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "lz4", "pdf",
            "doc", "docx", "docm", "dot", "dotx", "xls", "xlsx", "xlsm", "xlsb", "ppt", "pptx", "rtf", "odt", "ods",
            "odp", "png", "jpg", "jpeg", "gif", "bmp", "svg", "ico", "icns", "webp", "tif", "tiff", "heic", "mp3",
            "mp4", "m4a", "avi", "mkv", "mov", "wmv", "flv", "wav", "flac", "ogg", "html", "htm", "xhtml", "css",
            "scss", "sass", "less", "map", "pem", "crt", "cer", "der", "key", "pfx", "p12", "p7b", "p7c", "csr", "pub",
            "ppk", "jks", "keystore", "reg", "inf", "evtx", "evt", "etl", "dmp", "mdmp", "hdmp", "lnk", "xaml", "resx",
            "resw", "sln", "props", "targets", "nupkg", "nuspec", "pdb", "ilk", "obj", "lib", "so", "dylib", "o", "ko",
            "wasm", "woff", "woff2", "ttf", "otf", "eot", "apk", "aab", "ipa", "deb", "rpm", "pkg", "dmg", "appx",
            "msix", "vsix", "whl", "egg", "gem", "crx", "xpi", "rdp", "ovpn", "kdbx", "pt", "pth", "onnx", "ckpt",
            "safetensors", "h5", "pkl", "pickle", "npy", "npz", "parquet", "avro", "orc", "feather"
        });

        /// <summary>
        /// Частые слова после "user"/"login"/"account" без двоеточия: в "the user is logged in"
        /// это не имя учётной записи.
        /// </summary>
        internal static readonly HashSet<string> UserStopWords = Build(new string[] {
            "a", "an", "the", "is", "are", "was", "were", "be", "been", "being", "has", "have", "had", "do", "does",
            "did", "not", "no", "to", "of", "for", "from", "with", "without", "by", "on", "in", "at", "as", "and",
            "or", "but", "if", "then", "than", "that", "this", "these", "those", "which", "who", "whom", "whose",
            "what", "when", "where", "why", "how", "it", "its", "he", "she", "they", "we", "you", "i", "me", "my",
            "your", "our", "their", "his", "her", "them", "us", "all", "any", "each", "every", "some", "one", "two",
            "first", "last", "new", "old", "other", "same", "such", "own", "only", "just", "very", "can", "cannot",
            "could", "will", "would", "shall", "should", "may", "might", "must", "id", "ids", "name", "names",
            "account", "accounts", "profile", "profiles", "data", "info", "information", "details", "settings",
            "setting", "interface", "interfaces", "input", "output", "agent", "agents", "group", "groups", "mode",
            "type", "types", "page", "pages", "list", "lists", "session", "sessions", "management", "manager",
            "rights", "right", "permission", "permissions", "access", "policy", "policies", "role", "roles", "level",
            "levels", "object", "objects", "principal", "principals", "context", "space", "logged", "logging",
            "logon", "logoff", "login", "logins", "log", "logs", "locked", "unlocked", "lockout", "failed", "failure",
            "fails", "succeeded", "success", "successful", "successfully", "created", "deleted", "removed", "added",
            "updated", "changed", "modified", "disabled", "enabled", "exists", "exist", "already", "provided",
            "specified", "requested", "authenticated", "authorized", "unauthorized", "denied", "expired", "required",
            "found", "invalid", "valid", "unknown", "record", "records", "entry", "entries", "mapping", "mappings",
            "via", "using", "through", "error", "errors", "attempt", "attempts", "request", "requests", "count",
            "не", "был", "была", "было", "были", "это", "этот", "эта", "для", "при", "или", "и", "в", "на", "с", "по",
            "из", "к", "от", "нет", "есть", "вход", "выход", "ошибка", "доступ", "профиль", "группа", "имя",
            // Команды после «/user», «user»: «/user add name=...», «net user set».
            "add", "set", "remove", "rm", "print", "edit", "enable", "disable", "export", "import", "find", "show",
            "get", "del", "delete", "mod", "modify", "create", "unlock", "lock", "reset", "move", "rename", "comment"
        });

        // ---------------------------------------------------------------- ФИО

        /// <summary>
        /// Русские имена в именительном падеже, в нижнем регистре и с «е» вместо «ё».
        /// Слова, которые чаще значат что-то другое («Слава», «Роза», «Лада», «Август»), сюда не входят.
        /// </summary>
        internal static readonly HashSet<string> FirstNames = Build(new string[] {
            "александр", "алексей", "анатолий", "андрей", "антон", "аркадий", "арсений", "артем", "артур", "афанасий",
            "богдан", "борис", "вадим", "валентин", "валерий", "василий", "вениамин", "виктор", "виталий", "владимир",
            "владислав", "всеволод", "вячеслав", "гавриил", "геннадий", "георгий", "герман", "глеб", "гордей",
            "григорий", "давид", "даниил", "данила", "демьян", "денис", "дмитрий", "евгений", "егор", "елисей",
            "емельян", "ефим", "захар", "иван", "игнат", "игорь", "илларион", "илья", "иннокентий", "иосиф",
            "ипполит", "кирилл", "климент", "кондрат", "константин", "кузьма", "лаврентий", "лев", "леонид", "лука",
            "макар", "максим", "марк", "мартын", "матвей", "мирон", "мирослав", "михаил", "мстислав", "назар",
            "никита", "никифор", "николай", "олег", "остап", "павел", "петр", "платон", "прохор", "родион", "роман",
            "ростислав", "руслан", "савва", "святослав", "семен", "сергей", "станислав", "степан", "тарас",
            "тимофей", "тимур", "тихон", "трофим", "федор", "филипп", "фома", "фрол", "харитон", "эдуард", "юлиан",
            "юрий", "яков", "ярослав",
            "адам", "айдар", "айрат", "азат", "альберт", "амир", "анвар", "арам", "арман", "армен", "арсен", "асхат",
            "ахмед", "ахмат", "ашот", "али", "алишер", "бахтиер", "булат", "вартан", "гиви", "гурген", "гусейн",
            "дамир", "данияр", "джамшид", "динар", "ерлан", "зураб", "ильдар", "ильназ", "ильнур", "ильшат", "ильяс",
            "ислам", "камиль", "карен", "карим", "магомед", "марат", "мурат", "мухаммед", "нодар", "нурлан", "отар",
            "радик", "рамзан", "рамиль", "рафаэль", "рашид", "реваз", "ренат", "ринат", "рустам", "салават", "самир",
            "серик", "тагир", "тигран", "фарид", "фаррух", "хасан", "шамиль", "эльдар", "эмиль", "эрик", "юсуф",
            "саша", "шура", "алеша", "леша", "толя", "андрюша", "боря", "вадик", "валера", "вася", "витя", "вова",
            "володя", "гена", "гоша", "жора", "гриша", "даня", "дима", "митя", "женя", "ваня", "илюша", "кирюша",
            "костя", "лева", "леня", "макс", "миша", "коля", "паша", "петя", "рома", "сеня", "сережа", "стас",
            "степа", "тема", "тима", "федя", "юра", "яша", "эдик", "славик", "толик",
            "агата", "агния", "аделина", "аида", "айгуль", "александра", "алена", "алина", "алиса", "алла", "алсу",
            "альбина", "альфия", "амина", "анастасия", "ангелина", "анжела", "анжелика", "анна", "антонина", "арина",
            "асель", "валентина", "валерия", "варвара", "василиса", "вера", "вероника", "виктория", "виолетта",
            "влада", "владислава", "галина", "гаяне", "гузель", "гульнара", "гульшат", "дарина", "дарья", "диана",
            "дина", "динара", "диляра", "доминика", "ева", "евгения", "екатерина", "елена", "елизавета", "есения",
            "жанна", "залина", "зарема", "зарина", "злата", "зинаида", "зоя", "изабелла", "инга", "инесса", "инна",
            "ирина", "камила", "камилла", "карина", "каролина", "кира", "клавдия", "кристина", "ксения", "лариса",
            "лейла", "лиана", "лидия", "лилия", "лиля", "луиза", "любовь", "людмила", "ляйсан", "мадина", "майя",
            "малика", "маргарита", "марина", "мария", "марьям", "милана", "милена", "мирослава", "мирра", "надежда",
            "наталия", "наталья", "нелли", "ника", "нина", "нино", "нонна", "оксана", "олеся", "ольга", "полина",
            "прасковья", "раиса", "регина", "резеда", "римма", "руфина", "сабина", "светлана", "серафима", "снежана",
            "софия", "софья", "стелла", "стефания", "сусанна", "таисия", "тамара", "тамила", "татьяна", "ульяна",
            "фаина", "фатима", "эвелина", "элеонора", "элина", "эльвира", "эльза", "эльмира", "эмилия", "юлиана",
            "юлия", "яна", "ярослава",
            "аня", "маша", "лена", "оля", "наташа", "таня", "ира", "света", "катя", "настя", "юля", "даша", "ксюша",
            "галя", "люда", "надя", "люба", "валя", "лиза", "соня", "вика", "рита", "уля", "варя", "лера", "тоня",
            "зина", "мила",
            "олександр", "олексій", "андрій", "сергій", "юрій", "дмитро", "микола", "василь", "петро", "євген",
            "олена", "наталія", "тетяна", "ірина", "світлана", "катерина", "юлія", "надія", "віра", "любов",
            "софія", "ганна", "марія"
        });

        /// <summary>Основы имён с беглой гласной: «Павла» от «Павел», «Льва» от «Лев».</summary>
        internal static readonly HashSet<string> FleetingNameStems = Build(new string[] { "павл", "льв" });

        /// <summary>Русские имена латиницей: Ivan, Sergey, Tatiana.</summary>
        internal static readonly HashSet<string> TranslitFirstNames = Build(new string[] {
            "aleksandr", "alexander", "alexandr", "aleksey", "alexey", "alexei", "aleksei", "anatoly", "anatoliy",
            "andrey", "andrei", "anton", "arkady", "arkadiy", "arseny", "arseniy", "artem", "artyom", "artur", "bogdan",
            "boris", "vadim", "valentin", "valery", "valeriy", "vasily", "vasiliy", "vassily", "viktor", "vitaly",
            "vitaliy", "vladimir", "vladislav", "vsevolod", "vyacheslav", "gennady", "gennadiy", "georgy", "georgiy",
            "gleb", "grigory", "grigoriy", "denis", "dmitry", "dmitriy", "dmitrii", "dmitri", "evgeny", "evgeniy",
            "evgenii", "yevgeny", "egor", "yegor", "ivan", "igor", "ilya", "ilia", "kirill", "konstantin", "leonid",
            "maksim", "maxim", "matvey", "mikhail", "nikita", "nikolay", "nikolai", "oleg", "pavel", "petr", "pyotr",
            "ruslan", "rustam", "semyon", "semen", "sergey", "sergei", "sergej", "stanislav", "stepan", "timofey",
            "timur", "fedor", "fyodor", "yuri", "yury", "yuriy", "yaroslav", "zakhar", "ildar", "ilnur", "marat",
            "rinat", "renat", "ramil",
            "anna", "anastasia", "anastasiya", "alena", "alyona", "alina", "alla", "valentina", "valeriya", "varvara",
            "vera", "veronika", "viktoriya", "galina", "darya", "daria", "dariya", "ekaterina", "yekaterina", "elena",
            "yelena", "elizaveta", "zhanna", "zoya", "inna", "irina", "karina", "kristina", "ksenia", "kseniya",
            "larisa", "larissa", "lyudmila", "ludmila", "lyubov", "margarita", "marina", "mariya", "nadezhda",
            "natalia", "natalya", "nataliya", "oksana", "olga", "polina", "svetlana", "sofia", "sofya", "tamara",
            "tatiana", "tatyana", "ulyana", "yulia", "yuliya", "yana",
            "dasha", "masha", "sasha", "pasha", "misha", "dima", "kolya", "vanya", "sveta", "natasha", "katya",
            "nastya", "olya", "tanya"
        });

        /// <summary>
        /// Частые английские имена. Имена, которые чаще значат обычное слово или продукт
        /// («Will», «Mark», «Grace», «Ruby», «Julia», «Adam»), сюда не входят.
        /// </summary>
        internal static readonly HashSet<string> EnglishFirstNames = Build(new string[] {
            "aaron", "abigail", "alan", "albert", "alex", "alexis", "alice", "alison", "allison", "amanda", "amelia",
            "amy", "andrea", "andrew", "angela", "ann", "anne", "anthony", "antonio", "arthur", "ashley", "barbara",
            "ben", "benjamin", "beth", "betty", "beverly", "bob", "bobby", "brandon", "brenda", "brian", "brittany",
            "bruce", "bryan", "carl", "carlos", "carolyn", "catherine", "charles", "charlie", "charlotte", "cheryl",
            "chloe", "chris", "christina", "christine", "christopher", "cynthia", "dan", "daniel", "danielle", "dave",
            "david", "deborah", "debra", "denise", "dennis", "diana", "diane", "donald", "donna", "dorothy",
            "douglas", "dylan", "edward", "elijah", "elizabeth", "ella", "emily", "emma", "eric", "erik", "ethan",
            "eugene", "evelyn", "fernando", "frances", "francesco", "francisco", "gabriel", "gary", "george",
            "gerald", "giovanni", "giuseppe", "gloria", "greg", "gregory", "hannah", "hans", "harold", "harper",
            "harry", "heather", "helen", "henry", "isaac", "isabella", "jacob", "jacqueline", "jacques", "jake",
            "james", "janet", "janice", "jason", "javier", "jeff", "jeffrey", "jen", "jennifer", "jeremy", "jerry",
            "jesse", "jessica", "jim", "joan", "joe", "john", "johnny", "jon", "jonathan", "jorge", "jose", "joseph",
            "josh", "joshua", "joyce", "juan", "judith", "judy", "julie", "jurgen", "justin", "karen", "kate",
            "katherine", "kathleen", "kathryn", "kayla", "keith", "kelly", "kenneth", "kevin", "kimberly", "klaus",
            "krzysztof", "kyle", "lars", "larry", "laura", "lauren", "lawrence", "liam", "linda", "linus", "lisa",
            "liz", "lori", "louis", "luca", "lucas", "luis", "luke", "madison", "manuel", "marco", "margaret", "marie",
            "marilyn", "markus", "martha", "martin", "mary", "mason", "matt", "matteo", "matthew", "megan", "melissa",
            "mia", "michael", "michelle", "miguel", "mike", "nancy", "natalie", "nathan", "nicholas", "nick",
            "nicole", "noah", "oliver", "olivia", "owen", "pablo", "pamela", "patricia", "patrick", "paul", "pedro",
            "peter", "philip", "piotr", "pierre", "rachel", "rafael", "ralph", "randy", "raymond", "rebecca",
            "ricardo", "richard", "robert", "roger", "ronald", "russell", "ruth", "ryan", "sam", "samantha", "samuel",
            "sandra", "sara", "sarah", "scott", "sean", "sebastian", "sergio", "sharon", "shirley", "sophia",
            "stefan", "stephanie", "stephen", "steve", "steven", "susan", "teresa", "terry", "theresa", "thomas",
            "tim", "timothy", "tom", "tomasz", "tyler", "vincent", "virginia", "walter", "wayne", "william", "zachary",
            "zoe"
        });

        /// <summary>
        /// Слова с заглавной буквы, которые не бывают частью ФИО: обращения, должности, глаголы из подписи,
        /// месяцы, служебные слова, сокращения организаций.
        /// </summary>
        internal static readonly HashSet<string> NameStopWords = Build(new string[] {
            "привет", "приветствую", "здравствуйте", "здравствуй", "добрый", "доброе", "доброго", "доброй",
            "уважаемый", "уважаемая", "уважаемые", "уважаемого", "уважаемому", "уважаемой", "дорогой", "дорогая",
            "дорогие", "коллега", "коллеги", "коллеге", "коллегу", "господин", "господина", "господину", "госпожа",
            "госпожи", "госпоже", "госпожу", "товарищ", "гражданин", "гражданка", "спасибо", "благодарю",
            "пожалуйста", "извините", "прошу", "просим", "всем", "всех", "также", "тоже", "еще", "уже", "да", "нет",
            "это", "этот", "эта", "эти", "вот", "как", "где", "когда", "кто", "что", "почему", "зачем", "если", "или",
            "но", "он", "она", "они", "оно", "мы", "вы", "ты", "его", "ее", "их", "ему", "ей", "им", "меня", "мне",
            "тебе", "вам", "нам", "сегодня", "завтра", "вчера", "январь", "февраль", "март", "апрель", "май", "июнь",
            "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь", "января", "февраля", "марта", "апреля",
            "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря", "понедельник", "вторник",
            "среда", "четверг", "пятница", "суббота", "воскресенье", "директор", "директора", "начальник",
            "начальника", "руководитель", "руководителя", "инженер", "инженера", "менеджер", "менеджера",
            "специалист", "специалиста", "сотрудник", "сотрудника", "сотрудники", "администратор", "администратора",
            "пользователь", "пользователя", "исполнитель", "исполнителя", "ответственный", "ответственного",
            "автор", "автора", "заявитель", "заявителя", "инициатор", "утвердил", "утвердила", "утверждаю",
            "согласовал", "согласовала", "согласовано", "подписал", "подписала", "проверил", "проверила", "выполнил",
            "выполнила", "подготовил", "подготовила", "составил", "составила", "исполнил", "исполнила", "принял",
            "приняла", "передал", "передала", "сообщил", "сообщила", "написал", "написала", "звонил", "звонила",
            "позвонил", "позвонила", "ответил", "ответила", "приложение", "раздел", "таблица", "рисунок", "схема",
            "пункт", "глава", "статья", "вариант", "приказ", "распоряжение", "отчет", "заявка", "задача", "проект",
            "система", "сервер", "ошибка", "внимание", "важно", "примечание", "итого", "всего", "россия", "фио", "инн",
            "ооо", "оао", "зао", "пао", "ао", "ип",
            "бухгалтер", "юрист", "программист", "разработчик", "аналитик", "тестировщик", "архитектор", "консультант",
            "эксперт", "заместитель", "председатель", "секретарь", "помощник", "ассистент", "оператор", "техник",
            "мастер", "водитель", "врач", "преподаватель", "студент", "клиент", "абонент", "заказчик", "подрядчик",
            "потом", "затем", "теперь", "сейчас", "здесь", "там", "тут", "туда", "сюда", "опять", "снова", "вообще",
            "конечно", "наверное", "возможно", "может", "можно", "нужно", "надо", "только", "лишь", "даже", "кроме",
            "после", "перед", "между", "около", "возле", "вместе", "вместо", "ранее", "позже", "итак", "поэтому",
            "однако", "хотя", "пока", "чтобы", "будет", "есть", "пишет", "просит", "говорит", "считает", "думает",
            "знает", "машина", "машины", "причина", "картина", "половина", "вершина", "кабина", "глубина", "ширина",
            "длина", "мужчина", "логин", "логина", "магазин", "магазина", "админ", "админа", "бензин", "слова",
            "основа", "голова", "готов", "готова", "здоров", "здорова",
            "hi", "hello", "dear", "hey", "thanks", "thank", "regards", "best", "cheers", "sincerely", "please",
            "sorry", "yes", "no", "ok", "okay", "the", "a", "an", "and", "or", "but", "if", "then", "this", "that",
            "these", "those", "it", "its", "he", "she", "they", "we", "you", "i", "me", "my", "our", "your", "his",
            "her", "their", "to", "from", "for", "with", "by", "on", "in", "at", "of", "as", "is", "are", "was", "were",
            "be", "been", "mr", "mrs", "ms", "dr", "sir", "madam", "team", "all", "everyone", "monday", "tuesday",
            "wednesday", "thursday", "friday", "saturday", "sunday", "january", "february", "march", "april", "may",
            "june", "july", "august", "september", "october", "november", "december", "error", "warning", "info",
            "debug", "trace", "fatal", "server", "client", "user", "users", "admin", "administrator", "login", "logon",
            "domain", "service", "system", "windows", "linux", "microsoft", "google", "apple", "amazon", "azure",
            "office", "teams", "outlook", "update", "new", "old", "test", "prod", "production", "staging", "dev",
            "release", "version", "build", "deploy", "note", "see", "also", "via", "re", "fw", "fwd", "subject", "cc",
            "bcc", "sent", "date", "main", "master", "begin", "origin", "within", "plugin", "bitcoin", "street",
            "avenue", "road", "city", "county", "university", "college", "school", "hospital", "bank", "group", "inc",
            "ltd", "llc", "corp", "company", "center", "centre"
        });

        /// <summary>Сокращения с точками, которые похожи на инициалы: U.S. Army, P.S., A.M.</summary>
        internal static readonly HashSet<string> InitialAbbreviations = Build(new string[] {
            "us", "usa", "uk", "eu", "un", "ny", "la", "dc", "ps", "pps", "am", "pm", "ie", "eg", "qa", "bc", "ad",
            "nb", "rip", "ok", "tv", "uae", "ussr", "ca", "nj", "tx", "pp", "vs", "etc"
        });

        private static HashSet<string> Build(string[] values)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                set.Add(value);
            }
            return set;
        }
    }
}
