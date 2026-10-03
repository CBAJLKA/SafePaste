using System;
using System.Collections.Generic;

namespace SafePaste.Detecting
{

    /// <summary>
    /// Словари для <see cref="ContextNames"/>: слова, после которых идёт имя («кластер», «базу», «ping»),
    /// и слова, которые именем не бывают: продукты, протоколы, общие слова. Ключи в нижнем регистре,
    /// «ё» заменена на «е».
    /// </summary>
    internal static class ContextLexicons
    {
        // Падежные окончания основ. Лишнее окончание ничего не ломает: после подсказки слово ещё
        // проверяется на похожесть на имя.
        private static readonly string[] Masculine = { "", "а", "у", "ом", "ем", "е", "ы", "и", "ов", "ей", "ам", "ами", "ах" };
        private static readonly string[] MasculineOblique = { "а", "у", "ом", "е", "ы", "ов", "ам", "ами", "ах" };
        private static readonly string[] Feminine = { "", "а", "ы", "и", "е", "у", "ой", "ою", "ам", "ами", "ах" };
        private static readonly string[] FeminineIya = { "я", "и", "ю", "ей", "ею", "й", "ям", "ями", "ях" };
        private static readonly string[] FeminineSoft = { "ь", "и", "ью", "ей", "ям", "ями", "ях" };
        private static readonly string[] Neuter = { "", "е", "а", "у", "ем", "ам", "ами", "ах" };
        private static readonly string[] MasculineIy = { "й", "я", "ю", "ем", "и", "ев", "ям", "ями", "ях" };

        /// <summary>Существительные, после которых обычно стоит имя, и тип этого имени: «сервер Phoenix», «базу crm_prod».</summary>
        internal static readonly Dictionary<string, string> Nouns = BuildNouns();

        /// <summary>Глаголы доступа: «перезагрузи web01», «зайди на buhgalter-pc».</summary>
        internal static readonly HashSet<string> AccessVerbs = Build(new string[] {
            "зайди", "зайдите", "зайти", "заходи", "заходите", "зашел", "зашла", "подключись", "подключитесь",
            "подключиться", "подключаюсь", "подключился", "подключилась", "подключение", "подключения",
            "залогинься", "залогиньтесь", "залогиниться", "войди", "войдите", "войти", "пингани", "пингануть",
            "пингуй", "перезагрузи", "перезагрузите", "перезагрузить", "перезапусти", "перезапустите",
            "перезапустить", "ребутни", "ребутнуть"
        });

        /// <summary>Протоколы доступа перед предлогом: «по RDP на X», «ssh к X».</summary>
        internal static readonly HashSet<string> AccessProtocols = Build(new string[] {
            "rdp", "ssh", "vnc", "winrm", "smb", "telnet", "sftp", "ftp"
        });

        internal static readonly HashSet<string> Prepositions = Build(new string[] {
            "на", "к", "ко", "в", "во", "с", "со", "до", "от"
        });

        /// <summary>Команды, первый аргумент которых имя узла: «ping web01», «Test-NetConnection phoenix».</summary>
        internal static readonly HashSet<string> HostCommands = Build(new string[] {
            "ping", "tracert", "traceroute", "pathping", "psping", "nslookup", "dig", "telnet", "ssh", "sftp",
            "rdesktop", "mstsc", "xfreerdp", "nmap", "test-connection", "test-netconnection", "tnc",
            "enter-pssession", "new-pssession", "resolve-dnsname", "test-wsman"
        });

        /// <summary>Параметры PowerShell, значение которых имя узла.</summary>
        internal static readonly HashSet<string> HostParameters = Build(new string[] {
            "computername", "cimsession", "server", "servername", "hostname", "dnshostname", "cluster", "node",
            "targetname"
        });

        /// <summary>Параметры PowerShell, значение которых учётная запись.</summary>
        internal static readonly HashSet<string> UserParameters = Build(new string[] {
            "username", "user", "samaccountname", "logonname"
        });

        /// <summary>Правовая форма перед названием: ООО «Ромашка», ИП Петров.</summary>
        internal static readonly HashSet<string> LegalFormsBefore = Build(new string[] {
            "ооо", "оао", "зао", "пао", "ао", "ип", "нко", "ано", "гуп", "муп", "фгуп", "фгбу", "гбу", "мбу",
            "тоо", "чоп", "ук"
        });

        /// <summary>Правовая форма после названия: Contoso Ltd, Acme LLC.</summary>
        internal static readonly HashSet<string> LegalFormsAfter = Build(new string[] {
            "llc", "ltd", "inc", "gmbh", "corp", "corporation", "limited", "plc", "llp"
        });

        /// <summary>Слова, после которых название в кавычках: компания «Вектор», банк «Заря».</summary>
        internal static readonly HashSet<string> QuotedOrgNouns = BuildForms(new string[] {
            "компани", "организаци", "предприяти" }, FeminineIya,
            new string[] { "фирм" }, Feminine,
            new string[] { "завод", "банк", "холдинг", "заказчик", "клиент", "контрагент", "проект" }, Masculine);

        /// <summary>Части составного ключа рядом с подсказкой: cluster_name, projectId, db-name.</summary>
        internal static readonly HashSet<string> KeySuffixes = Build(new string[] {
            "name", "id", "host", "hostname", "alias", "label", "title", "code"
        });

        /// <summary>Части имени узла: buhgalter-pc, crm-prod, web-srv.</summary>
        internal static readonly HashSet<string> HostAffixes = Build(new string[] {
            "pc", "srv", "server", "db", "sql", "dc", "fs", "nas", "vm", "app", "web", "gw", "fw", "sw", "rtr",
            "prn", "ap", "host", "node", "prod", "stage", "stg", "dev", "test", "qa", "uat", "bak", "backup",
            "k8s", "ws", "nb", "mail", "proxy", "vpn", "ts", "rds", "sccm", "wsus", "print"
        });

        /// <summary>Единицы после числа: 10ms, 5GB, 64bit. Это не имена.</summary>
        internal static readonly HashSet<string> Units = Build(new string[] {
            "ms", "s", "sec", "m", "min", "h", "d", "kb", "mb", "gb", "tb", "pb", "k", "g", "t", "x", "px", "pt",
            "bit", "bits", "byte", "bytes", "kbps", "mbps", "gbps", "hz", "khz", "mhz", "ghz", "v", "w", "kw", "st",
            "nd", "rd", "th", "мс", "с", "сек", "мин", "ч", "дн", "кб", "мб", "гб", "тб", "шт", "й", "го", "я"
        });

        /// <summary>
        /// Продукты, протоколы, компании и сокращения. После подсказки они не имя: «сервер Exchange»,
        /// «база PostgreSQL», «кластер Kubernetes», «сервер DNS».
        /// </summary>
        internal static readonly HashSet<string> Known = Build(new string[] {
            "windows", "linux", "ubuntu", "debian", "centos", "rhel", "redhat", "fedora", "alpine", "astra", "alt",
            "redos", "macos", "osx", "ios", "android", "unix", "freebsd", "exchange", "outlook", "office", "teams",
            "sharepoint", "onedrive", "skype", "lync", "sql", "mssql", "mysql", "mariadb", "postgres", "postgresql",
            "oracle", "sqlite", "mongodb", "mongo", "redis", "memcached", "cassandra", "clickhouse", "elasticsearch",
            "elastic", "opensearch", "kibana", "logstash", "kafka", "rabbitmq", "activemq", "zookeeper", "etcd",
            "consul", "vault", "nginx", "apache", "tomcat", "iis", "haproxy", "traefik", "envoy", "docker",
            "podman", "kubernetes", "k8s", "k3s", "openshift", "rancher", "helm", "vmware", "esxi", "vcenter",
            "vsphere", "hyper-v", "hyperv", "proxmox", "kvm", "qemu", "xen", "virtualbox", "citrix", "veeam",
            "acronis", "zabbix", "grafana", "prometheus", "nagios", "icinga", "jenkins", "teamcity", "gitlab",
            "github", "bitbucket", "jira", "confluence", "youtrack", "redmine", "1c", "bitrix", "active",
            "directory", "ad", "ldap", "kerberos", "dns", "dhcp", "ntp", "smtp", "imap", "pop3", "http", "https",
            "ftp", "sftp", "ssh", "rdp", "vnc", "tcp", "udp", "ip", "icmp", "snmp", "vpn", "ipsec", "openvpn",
            "wireguard", "wi-fi", "wifi", "ethernet", "lan", "wan", "vlan", "dmz", "nas", "san", "iscsi", "nfs",
            "smb", "cifs", "raid", "ssd", "hdd", "nvme", "api", "rest", "grpc", "json", "xml", "yaml", "csv",
            "powershell", "bash", "cmd", "python", "java", "javascript", "nodejs", "dotnet", "golang", "rust",
            "php", "ruby", "perl", "azure", "aws", "gcp", "google", "yandex", "microsoft", "cisco", "huawei",
            "juniper", "mikrotik", "fortinet", "fortigate", "checkpoint", "paloalto", "kaspersky", "dell", "hp",
            "hpe", "lenovo", "ibm", "intel", "amd", "nvidia", "apple", "samsung", "supermicro", "synology", "qnap",
            "netapp", "emc", "freeipa", "samba", "keycloak", "okta", "entra", "intune", "sccm", "wsus", "gpo",
            "chrome", "firefox", "edge", "safari", "opera", "telegram", "whatsapp", "slack", "zoom", "webex",
            "anydesk", "teamviewer", "rustdesk", "putty", "winscp", "filezilla", "excel", "word", "powerpoint",
            "visio", "access", "sap", "crm", "erp", "qlik", "tableau", "powerbi", "express", "standard",
            "enterprise", "datacenter", "professional", "pro", "home", "ultimate", "core", "cloud", "online",
            "hadoop", "spark", "airflow", "ceph", "glusterfs", "minio", "patroni", "pgbouncer", "influxdb",
            "victoriametrics", "loki", "tempo", "jaeger", "sentry", "graylog", "splunk", "wazuh", "suricata",
            "snort", "pfsense", "opnsense", "ubiquiti", "unifi", "tplink", "keenetic", "asterisk", "freepbx",
            "nextcloud", "owncloud", "wordpress", "moodle", "odoo", "vscode", "intellij", "rider", "eclipse",
            "tfs", "azuredevops", "argocd", "terraform", "ansible", "puppet", "chef", "saltstack", "vagrant",
            "localhost", "internet", "intranet", "extranet", "ip6-localhost", "ip6-loopback", "localdomain",
            "mssqlserver", "sqlexpress", "defaultapppool", "cloudflare", "quad9", "opendns", "fujitsu", "yadro",
            "aquarius", "depo", "kraftway", "gigabyte", "asus", "msi", "inspur", "eltex", "dlink", "d-link", "zyxel",
            "netgear", "aruba", "arista", "qtech", "tp-link", "tplink", "kyocera", "canon", "epson", "xerox",
            "brother", "ricoh", "konica", "minolta", "lexmark", "pantum", "sharp", "oki", "usergate", "ideco",
            "kerio", "sophos", "watchguard", "sonicwall", "vipnet", "keepass", "bitwarden", "lastpass", "1password",
            "vaultwarden", "passwork", "sshd", "httpd", "kernel", "systemd", "cron", "crond", "sudo", "named",
            "dhcpd", "ntpd", "rsyslogd", "postfix", "dovecot", "smbd", "nmbd", "winbind", "2fa", "mfa", "3d", "4k",
            "4g", "5g", "lte", "ipv4", "ipv6", "x64", "x86", "arm64", "win32", "win64", "utf8", "utf-8", "sha1",
            "sha256", "sha512", "md5", "aes128", "aes256", "rsa2048", "rsa4096", "tls12", "tls13", "http2", "h264",
            "h265", "mp3", "mp4", "p2p", "b2b", "b2c", "sp1", "sp2", "sp3", "e-mail", "email", "gmail", "mailru",
            "линукс", "виндовс", "убунту", "эксчейндж", "аутлук", "офис", "битрикс", "1с", "яндекс", "гугл",
            "майкрософт", "касперский", "сбер", "сбербанк", "мтс", "билайн", "мегафон", "ростелеком", "госуслуги",
            "интернет", "интранет", "астра", "ред", "альт", "субд", "бд", "ос", "по", "днс", "вм", "пк", "цод",
            "мфу", "схд", "ибп", "скуд", "атс", "эцп", "ит", "ии", "ад", "фиас", "эдо", "гис", "жкх", "егаис",
            "фнс", "пфр", "фсс", "мвд", "рф", "асу", "асутп", "вк", "вконтакте", "телеграм", "ватсап", "зум",
            "континент", "випнет", "эльтекс", "кейнетик", "микротик", "циско", "хуавей", "делл", "леново"
        });

        /// <summary>
        /// Общие слова, которые встают после подсказки, но имени не несут: «Server Error», «Domain Admins»,
        /// «сеть Guest», «база Default».
        /// </summary>
        internal static readonly HashSet<string> Generic = Build(new string[] {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being", "to", "of", "for", "from", "with",
            "without", "by", "on", "in", "at", "as", "and", "or", "but", "if", "then", "else", "not", "no", "yes",
            "all", "any", "each", "every", "some", "this", "that", "these", "those", "it", "its", "my", "your",
            "our", "their", "his", "her", "who", "which", "what", "when", "where", "how", "why", "can", "could",
            "will", "would", "should", "must", "may", "might", "has", "have", "had", "do", "does", "did", "done",
            "new", "old", "main", "primary", "secondary", "backup", "backups", "test", "tests", "testing", "prod",
            "production", "dev", "development", "staging", "stage", "local", "remote", "default", "public",
            "private", "shared", "internal", "external", "guest", "guests", "admin", "admins", "administrator",
            "administrators", "root", "user", "users", "data", "database", "databases", "server", "servers",
            "host", "hosts", "node", "nodes", "cluster", "clusters", "name", "names", "side", "room", "farm",
            "error", "errors", "status", "list", "time", "down", "up", "online", "offline", "running", "stopped",
            "failed", "failure", "ok", "config", "configuration", "settings", "setting", "version", "update",
            "updates", "application", "applications", "app", "apps", "service", "services", "web", "mail",
            "file", "files", "share", "shares", "group", "groups", "account", "accounts", "domain", "domains",
            "controller", "controllers", "network", "networks", "pool", "pools", "project", "projects",
            "customer", "customers", "client", "clients", "company", "companies", "instance", "instances",
            "storage", "volume", "volumes", "disk", "disks", "memory", "cpu", "load", "health", "pod", "pods",
            "container", "containers", "image", "images", "key", "keys", "certificate", "certificates", "log",
            "logs", "event", "events", "message", "messages", "request", "requests", "response", "session",
            "sessions", "connection", "connections", "port", "ports", "address", "addresses", "gateway",
            "gateways", "router", "routers", "switch", "switches", "firewall", "firewalls", "printer", "printers",
            "computer", "computers", "workstation", "workstations", "laptop", "laptops", "desktop", "machine",
            "machines", "vm", "vms", "site", "sites", "team", "owner", "owners", "manager", "managers", "core",
            "engine", "adapter", "adapters", "access", "hello", "side", "farm", "role", "roles", "roll", "admin",
            "console", "portal", "dashboard", "agent", "agents", "tools", "tool", "module", "modules", "component",
            "components", "instance", "cache", "queue", "queues", "topic", "topics", "bucket", "buckets",
            "datastore", "datastores", "repo", "repos", "repository", "repositories", "namespace", "namespaces",
            "tenant", "tenants", "mailbox", "mailboxes", "bridge", "none", "null", "true", "false", "auto",
            "manual", "current", "master", "slave", "replica", "replicas", "standby", "active", "passive",
            "primary", "leader", "follower", "worker", "workers", "edge", "center", "centre", "zone", "zones",
            "region", "regions", "cloud", "local", "global", "common", "temp", "tmp", "home", "office", "lab",
            "demo", "sandbox", "legacy", "unknown", "other", "info", "debug", "trace", "warning", "critical",
            "here", "there", "now", "again", "only", "also", "just", "still", "yet", "via", "per", "into", "onto",
            "over", "under", "after", "before", "between", "during", "within", "about", "above", "below",
            "first", "last", "next", "previous", "same", "different", "another", "such", "own", "both", "either",
            "neither", "more", "most", "less", "least", "many", "much", "few", "several", "restart", "reboot",
            "start", "stop", "check", "fix", "install", "uninstall", "upgrade", "migrate", "migration", "deploy",
            "deployment", "build", "release", "rollback", "monitoring", "alert", "alerts", "incident", "ticket",
            "issue", "task", "job", "jobs", "script", "scripts", "command", "commands", "output", "input",
            "result", "results", "state", "type", "types", "mode", "level", "value", "values", "field", "fields",
            "table", "tables", "column", "columns", "row", "rows", "record", "records", "entry", "entries",
            "object", "objects", "item", "items", "folder", "folders", "path", "paths", "drive", "drives",
            "partition", "partitions", "snapshot", "snapshots", "template", "templates", "policy", "policies",
            "rule", "rules", "profile", "profiles", "license", "licenses", "role", "permissions", "permission",
            "process", "processes", "package", "packages", "studio", "system", "systems", "guard", "protocol",
            "edition", "feature", "features", "hosting", "runtime", "framework", "sdk", "bus", "broker", "index",
            "compute", "recovery", "monitor", "security", "administration", "explorer", "browser", "viewer",
            "editor", "designer", "profiler", "reporting", "analytics", "integration", "integrations", "connector",
            "connectors", "driver", "drivers", "plugin", "plugins", "extension", "extensions", "library",
            "libraries", "timeout", "unreachable", "reachable", "success", "open", "closed", "filtered", "alive",
            "dead", "idle", "busy", "ready", "pending", "unknown", "farm", "mirroring", "replication", "cluster",
            "failover", "witness", "quorum", "share", "sharing", "mail", "name", "names", "key", "keys",
            "главный", "главная", "основной", "основная", "резервный", "резервная", "тестовый", "тестовая",
            "новый", "новая", "старый", "старая", "общий", "общая", "локальный", "локальная", "удаленный",
            "удаленная", "внутренний", "внутренняя", "внешний", "внешняя", "гостевой", "гостевая", "рабочий",
            "рабочая", "продуктивный", "продуктивная", "боевой", "боевая"
        });

        /// <summary>Команды AD и Exchange: какой тип у значения -Identity.</summary>
        internal static string IdentityType(string command)
        {
            if (command.IndexOf("aduser", StringComparison.Ordinal) >= 0
                || command.IndexOf("adserviceaccount", StringComparison.Ordinal) >= 0
                || command.IndexOf("localuser", StringComparison.Ordinal) >= 0
                || command.IndexOf("mailbox", StringComparison.Ordinal) >= 0)
            {
                return "USER";
            }
            if (command.IndexOf("adcomputer", StringComparison.Ordinal) >= 0)
            {
                return "HOST";
            }
            if (command.IndexOf("adgroup", StringComparison.Ordinal) >= 0
                || command.IndexOf("localgroup", StringComparison.Ordinal) >= 0
                || command.IndexOf("distributiongroup", StringComparison.Ordinal) >= 0)
            {
                return "TEXT";
            }
            return null;
        }

        private static Dictionary<string, string> BuildNouns()
        {
            Dictionary<string, string> nouns = new Dictionary<string, string>(StringComparer.Ordinal);
            // Имена компьютеров и устройств.
            AddForms(nouns, "HOST", Masculine, "сервер", "сервак", "хост", "компьютер", "кластер", "роутер",
                "маршрутизатор", "коммутатор", "свитч", "свич", "фаервол", "файрвол", "шлюз", "принтер");
            AddForms(nouns, "HOST", MasculineOblique, "узл");
            AddForms(nouns, "HOST", Feminine, "нод", "виртуалк");
            AddExact(nouns, "HOST", "узел", "вм", "пк", "мфу", "server", "servers", "host", "hosts", "hostname",
                "computername", "node", "nodes", "cluster", "clusters", "vm", "vms", "computer", "workstation",
                "router", "switch", "firewall", "gateway", "printer");
            // Хранилища и сетевые папки.
            AddForms(nouns, "SHARE", Feminine, "шар");
            AddForms(nouns, "SHARE", Masculine, "бакет", "датастор");
            AddForms(nouns, "SHARE", Neuter, "хранилищ");
            AddExact(nouns, "SHARE", "схд", "share", "bucket", "datastore");
            // Учётные записи.
            AddForms(nouns, "USER", Feminine, "учетк");
            AddForms(nouns, "USER", Masculine, "аккаунт", "ящик");
            AddExact(nouns, "USER", "account", "mailbox");
            // Всё остальное без своего типа: базы, проекты, заказчики, сети, домены.
            AddForms(nouns, "TEXT", Feminine, "баз", "площадк");
            AddForms(nouns, "TEXT", Masculine, "инстанс", "проект", "заказчик", "клиент", "контрагент", "филиал",
                "домен", "тенант", "пул");
            AddForms(nouns, "TEXT", FeminineIya, "компани", "организаци");
            AddForms(nouns, "TEXT", FeminineSoft, "сет");
            AddForms(nouns, "TEXT", MasculineIy, "репозитори");
            AddExact(nouns, "TEXT", "бд", "цод", "database", "db", "instance", "project", "customer", "client",
                "company", "tenant", "domain", "network", "ssid", "pool", "repo", "repository", "namespace");
            return nouns;
        }

        private static void AddForms(Dictionary<string, string> nouns, string type, string[] endings, params string[] stems)
        {
            foreach (string stem in stems)
            {
                foreach (string ending in endings)
                {
                    string form = stem + ending;
                    if (!nouns.ContainsKey(form))
                    {
                        nouns.Add(form, type);
                    }
                }
            }
        }

        private static void AddExact(Dictionary<string, string> nouns, string type, params string[] words)
        {
            foreach (string word in words)
            {
                if (!nouns.ContainsKey(word))
                {
                    nouns.Add(word, type);
                }
            }
        }

        private static HashSet<string> BuildForms(string[] firstStems, string[] firstEndings,
            string[] secondStems, string[] secondEndings, string[] thirdStems, string[] thirdEndings)
        {
            HashSet<string> forms = new HashSet<string>(StringComparer.Ordinal);
            AddAll(forms, firstStems, firstEndings);
            AddAll(forms, secondStems, secondEndings);
            AddAll(forms, thirdStems, thirdEndings);
            return forms;
        }

        private static void AddAll(HashSet<string> forms, string[] stems, string[] endings)
        {
            foreach (string stem in stems)
            {
                foreach (string ending in endings)
                {
                    forms.Add(stem + ending);
                }
            }
        }

        private static HashSet<string> Build(string[] values)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in values)
            {
                set.Add(value);
            }
            return set;
        }
    }
}
