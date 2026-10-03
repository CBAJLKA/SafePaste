using System;
using System.Collections.Generic;
using SafePaste.Storage;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Скрытое значение скрывается во всём тексте. Без этого место подсказки решало бы, где значение
    /// спрятано: «hostname: [HOST_1]», а строкой ниже то же имя открытым, и соседство прямо выдаёт,
    /// что стоит за меткой. То же с паролем, который встретился второй раз уже без «password=».
    /// Заодно скрываются производные значения: короткое имя из FQDN и ссылки, учётная запись из
    /// DOMAIN\user, фамилия из ФИО в других падежах.
    /// </summary>
    internal static class Repeats
    {
        private const int MaxValue = 512;
        private const string RepeatReason = "то же значение скрыто в другом месте текста";

        // Эти типы детектор находит по виду значения в любом месте текста: их повторы искать незачем.
        // Телефона тут нет: короткий номер узнаётся только после «тел.», а повтор ниже идёт без подсказки.
        private static readonly HashSet<string> SelfEvident = new HashSet<string>(StringComparer.Ordinal)
        {
            "IP", "IPV6", "MAC", "EMAIL", "URL", "GUID", "SID", "FQDN", "PATH", "AD_DN", "ISCSI_IQN", "WWN",
            "SSH_FINGERPRINT", "SSH_PUBLIC_KEY", "AWS_ACCESS_KEY"
        };

        // Первые части имён, которые ничего не выдают: www.contoso.com, mail.example.org.
        private static readonly HashSet<string> GenericLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "www", "www1", "www2", "mail", "webmail", "smtp", "imap", "pop", "pop3", "mx", "ns", "ns1", "ns2", "ns3",
            "dns", "portal", "api", "app", "apps", "web", "login", "auth", "sso", "id", "my", "vpn", "remote", "owa",
            "autodiscover", "intranet", "git", "cdn", "static", "assets", "img", "images", "media", "files",
            "download", "downloads", "ftp", "sftp", "proxy", "gateway", "gw", "ntp", "time", "admin", "test", "dev",
            "stage", "staging", "prod", "beta", "demo", "cloud", "office", "lync", "sip", "meet", "shop", "store",
            "blog", "news", "forum", "wiki", "docs", "help", "support", "status", "local", "localhost", "corp",
            "internal", "secure", "online", "account", "accounts", "search", "maps", "drive", "calendar",
            // Имена служб: registry.example.com, db.corp.local. Короткое имя тут ничего не выдаёт, а в тексте это слово.
            "registry", "db", "database", "sql", "mongo", "redis", "rabbit", "rabbitmq", "mq", "es", "elastic", "kafka",
            "ldap", "backup", "nas", "gitlab", "github", "jenkins", "ci", "build", "grafana", "prometheus", "monitoring",
            "metrics", "logs", "s3", "minio", "vault", "k8s", "kube", "docker", "hub", "npm", "pypi", "nuget", "repo",
            "packages", "artifactory", "nexus", "sentry", "queue", "cache", "idp", "adfs", "sts", "radius", "syslog",
            "relay", "exchange", "jira", "confluence", "chat", "zabbix", "vcenter", "proxmox", "firewall", "router",
            "mongodb", "postgres", "mysql", "smtp2", "server", "host", "node", "master", "worker", "primary", "replica"
        };

        /// <summary>Значение, которое ищется по тексту, и находка, от которой оно пошло.</summary>
        private sealed class Seed
        {
            internal Detection Origin;
            internal string Type;
            internal int Priority;
            internal bool Capitalized;
            internal string Reason;
        }

        /// <summary>
        /// Добавляет к разобранным находкам повторы включённых значений и производные значения.
        /// Секреты повторяются с четырёх символов, остальное с трёх; обычные слова («test», «Public»)
        /// и значения из исключений не повторяются.
        /// </summary>
        internal static List<Detection> Spread(string text, List<Detection> resolved, SafePasteDatabase database)
        {
            if (string.IsNullOrEmpty(text) || resolved == null || resolved.Count == 0)
            {
                return resolved;
            }
            ValueMatcher matcher = new ValueMatcher();
            List<Seed> seeds = new List<Seed>();
            foreach (Detection detection in resolved)
            {
                if (!detection.Enabled)
                {
                    continue;
                }
                if (!SelfEvident.Contains(detection.Type) && Worth(detection.Value, detection.Locked))
                {
                    Add(matcher, seeds, database, detection.Value, detection, detection.Type, detection.Priority,
                        false, RepeatReason);
                }
                Derive(matcher, seeds, database, detection);
            }
            if (seeds.Count == 0)
            {
                return resolved;
            }

            List<Detection> copies = new List<Detection>();
            foreach (ValueMatch match in matcher.Find(text))
            {
                Seed seed = seeds[match.Index];
                if (seed.Capitalized && !char.IsUpper(text[match.Start]))
                {
                    continue; // «волков» с маленькой буквы не фамилия Волков
                }
                if (Covered(resolved, match.Start, match.Start + match.Length))
                {
                    continue;
                }
                Detection origin = seed.Origin;
                Detection copy = new Detection(match.Start, match.Length, text.Substring(match.Start, match.Length),
                    seed.Type, origin.Confidence, seed.Priority);
                copy.Locked = origin.Locked && seed.Type == origin.Type;
                copy.Source = origin.Source;
                copy.DefaultEnabled = origin.DefaultEnabled;
                copy.Enabled = true;
                copy.Reason = seed.Reason;
                copies.Add(copy);
            }
            if (copies.Count == 0)
            {
                return resolved;
            }
            List<Detection> all = new List<Detection>(resolved.Count + copies.Count);
            all.AddRange(resolved);
            all.AddRange(copies);
            return OverlapResolver.Resolve(all);
        }

        private static void Derive(ValueMatcher matcher, List<Seed> seeds, SafePasteDatabase database, Detection detection)
        {
            string value = detection.Value;
            switch (detection.Type)
            {
                case "FQDN":
                    AddLabel(matcher, seeds, database, value, detection);
                    break;
                case "URL":
                    string host = UrlHost(value);
                    if (host != null)
                    {
                        Add(matcher, seeds, database, host, detection, "FQDN", 75, false, "узел из ссылки");
                        AddLabel(matcher, seeds, database, host, detection);
                    }
                    break;
                case "USER":
                    int slash = value.LastIndexOf('\\');
                    if (slash > 0 && slash < value.Length - 1)
                    {
                        string account = value.Substring(slash + 1);
                        if (Worth(account, false))
                        {
                            Add(matcher, seeds, database, account, detection, "USER", detection.Priority, false,
                                "учётная запись из «" + value + "»");
                        }
                    }
                    break;
                case "PERSON":
                    foreach (string form in PersonNames.SurnameForms(value))
                    {
                        if (form.Length >= 3 && !string.Equals(form, value, StringComparison.OrdinalIgnoreCase))
                        {
                            Add(matcher, seeds, database, form, detection, "PERSON", PersonNames.Priority, true,
                                "фамилия из «" + value + "»");
                        }
                    }
                    break;
            }
        }

        /// <summary>Короткое имя из FQDN: phoenix из phoenix.corp.local.</summary>
        private static void AddLabel(ValueMatcher matcher, List<Seed> seeds, SafePasteDatabase database, string host,
            Detection origin)
        {
            int dot = host.IndexOf('.');
            if (dot < 3)
            {
                return;
            }
            string label = host.Substring(0, dot);
            bool letter = false;
            foreach (char symbol in label)
            {
                letter |= char.IsLetter(symbol);
            }
            if (!letter || GenericLabels.Contains(label) || ContextNames.IsPlainWord(label))
            {
                return;
            }
            Add(matcher, seeds, database, label, origin, "HOST", ContextNames.Priority, false,
                "имя компьютера из «" + host + "»");
        }

        /// <summary>Узел из ссылки без учётной записи и порта; адрес IP не нужен, его находит сканер IP.</summary>
        private static string UrlHost(string url)
        {
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0)
            {
                return null;
            }
            int start = scheme + 3;
            int end = start;
            while (end < url.Length && url[end] != '/' && url[end] != '?' && url[end] != '#')
            {
                end++;
            }
            string authority = url.Substring(start, end - start);
            int at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                authority = authority.Substring(at + 1);
            }
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                return null;
            }
            int colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                authority = authority.Substring(0, colon);
            }
            bool letter = false;
            foreach (char symbol in authority)
            {
                letter |= char.IsLetter(symbol);
            }
            return letter && authority.IndexOf('.') > 0 ? authority : null;
        }

        private static void Add(ValueMatcher matcher, List<Seed> seeds, SafePasteDatabase database, string value,
            Detection origin, string type, int priority, bool capitalized, string reason)
        {
            if (database != null && database.IsAllowed(value))
            {
                return;
            }
            // Номер значения в автомате совпадает с номером в списке: повтор не добавляется.
            if (matcher.Add(value) < 0)
            {
                return;
            }
            Seed seed = new Seed();
            seed.Origin = origin;
            seed.Type = type;
            seed.Priority = priority;
            seed.Capitalized = capitalized;
            seed.Reason = origin.IsSensitiveValue ? RepeatReason : reason;
            seeds.Add(seed);
        }

        /// <summary>Стоит ли искать значение по тексту: короткие числа и обычные слова дали бы ложные совпадения.</summary>
        private static bool Worth(string value, bool secret)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxValue)
            {
                return false;
            }
            bool letter = false;
            bool digitsOnly = true;
            foreach (char symbol in value)
            {
                letter |= char.IsLetter(symbol);
                digitsOnly &= char.IsDigit(symbol);
            }
            if (digitsOnly)
            {
                return value.Length >= 6;
            }
            if (secret)
            {
                return value.Length >= 4;
            }
            if (value.Length < 3 || (!letter && value.Length < 6))
            {
                return false;
            }
            return !ContextNames.IsPlainWord(value);
        }

        /// <summary>Вхождение уже внутри включённой находки: например, имя внутри своего FQDN или ссылки.</summary>
        private static bool Covered(List<Detection> resolved, int start, int end)
        {
            int low = 0;
            int high = resolved.Count - 1;
            int found = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (resolved[middle].Start <= start)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            if (found < 0)
            {
                return false;
            }
            Detection candidate = resolved[found];
            return candidate.Enabled && candidate.End >= end;
        }
    }
}
