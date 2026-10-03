using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SafePaste.Detecting
{
    /// <summary>Что делать с одной группой совпадения.</summary>
    internal sealed class GroupTarget
    {
        internal readonly string Group;
        internal readonly string Type;
        internal readonly Confidence Confidence;
        internal readonly int Priority;
        internal readonly bool Locked;

        internal GroupTarget(string group, string type, Confidence confidence, int priority, bool locked)
        {
            Group = group;
            Type = type;
            Confidence = confidence;
            Priority = priority;
            Locked = locked;
        }
    }

    /// <summary>Правило детекции: регулярное выражение и разметка его групп.</summary>
    internal sealed class Rule
    {
        internal readonly Regex Regex;
        internal readonly GroupTarget[] Targets;

        internal Rule(string pattern, string type, Confidence confidence, int priority)
            : this(pattern, new GroupTarget[] { new GroupTarget(null, type, confidence, priority, false) })
        {
        }

        internal Rule(string pattern, string type, Confidence confidence, int priority, bool locked)
            : this(pattern, new GroupTarget[] { new GroupTarget(null, type, confidence, priority, locked) })
        {
        }

        internal Rule(string pattern, string type, Confidence confidence, int priority, string group, bool locked)
            : this(pattern, new GroupTarget[] { new GroupTarget(group, type, confidence, priority, locked) })
        {
        }

        internal Rule(string pattern, params GroupTarget[] targets)
        {
            Regex = new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(750));
            Targets = targets;
        }
    }

    /// <summary>Таблица правил. Порядок не важен: пересечения разбирает OverlapResolver по приоритету.</summary>
    internal static class Rules
    {
        // Слова, после которых идёт секрет. "pass" отдельно: голое "pass: 15" слишком часто встречается в отчётах.
        // "key" и "seed" отдельно тоже не секреты: partition_key, random_seed.
        private const string SecretWords =
            "password|passwd|passphrase|pwd|secret|token|api[_-]?key|access[_-]?key|account[_-]?key|" +
            "private[_-]?key|client[_-]?secret|auth[_-]?key|credentials?|пароль|токен|" +
            // Общие ключи: PSK, PresharedKey, SharedAccessKey, Ocp-Apim-Subscription-Key, APP_KEY, ENCRYPTION_KEY.
            "psk|pre[_-]?shared[_-]?key|shared[_-]?access[_-]?(?:key|signature)|subscription[_-]?key|functions?[_-]?key|" +
            "(?:encryption|signing|master|app|license|hmac|crypt|cipher|session|storage|webhook)[_-]?key|" +
            "secret[_-]?key[_-]?base|(?:devops|github|gitlab)[_-]?pat|" +
            // Закрытый ключ в kubeconfig, SNMP, PIN, CVV, одноразовые коды и коды восстановления, сид-фраза.
            "key[_-]?data|community|pin(?:[_-]?code)?|cvv2?|cvc2?|passcode|t?otp|hotp|" +
            "(?:recovery|backup|mfa|2fa|otp|verification|security)[_-]?codes?|seed[_-]?phrase|mnemonic|recovery[_-]?phrase";

        // Имя ключа целиком: db_password, MYSQL_ROOT_PASSWORD, adminPassword, x-api-key, SecretAccessKey,
        // _authToken из .npmrc, secret_key_base из Rails.
        private const string SecretKeyName =
            @"_?(?:[A-Za-z0-9]+[_.-])*" +
            @"(?:(?-i:[A-Za-z][a-z0-9]*(?:[A-Z][a-z0-9]*)*?)(?-i:(?=[A-Z])))?" +
            @"(?:" + SecretWords + @")" +
            @"(?:[_.-]?(?:key|value|hash|string|base|data))?";

        // Значение ключа командной строки: в кавычках или до пробела.
        private const string ArgValue =
            "(?:\"(?<value>[^\"\\r\\n]+)\"|'(?<value>[^'\\r\\n]+)'|(?<value>[^\\s\"']+))";

        // Значение: в кавычках (кавычки остаются в тексте) или до ближайшего разделителя.
        private const string SecretValue =
            "(?:\"(?<value>(?:[^\"\\\\\r\n]|\\\\.)*)\"" +
            @"|'(?<value>[^'\r\n]*)'" +
            @"|(?<value>\[[^\]\r\n]+\])" +
            "|(?<value>(?![=>])[^\\s;,&\"'<>]+))";

        // Скобки учитываются парами, иначе заполнитель съест закрывающую скобку из текста.
        private const string GuidCore =
            @"[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}";
        private const string Guid =
            @"(?:\{" + GuidCore + @"\}|\(" + GuidCore + @"\)|" + GuidCore + ")";

        // Компонент DN: значения DC не содержат пробелов, остальные могут ("CN=John Smith").
        private const string DnComponent =
            @"(?:(?:(?i:CN|OU|UID|STREET)|(?-i:O|L|ST|S|C|E))=(?:\\.|[^,;+\r\n\\=""<>])+|(?i:DC)=[\w-]+)";

        // Слово имени с заглавной буквы («Иванов», «ИВАНОВ», «Петров-Водкин») или инициал «И.».
        // Сразу после него не может идти то, что делает слово частью адреса, пути или имени файла.
        private const string NameEnd = @"(?![\p{L}\p{N}_@\\/-]|\.[\p{L}\p{N}])";
        private const string NameWord = @"(?-i:\p{Lu}\p{Ll}+(?:-\p{Lu}\p{Ll}+)?|\p{Lu}{2,}(?:-\p{Lu}{2,})?)";
        private const string NamePart = "(?:" + NameWord + @"|(?-i:\p{Lu})\.)";
        private const string NameWords = NamePart + @"(?:(?:[ \t]{1,2}|(?<=\.))" + NamePart + "){0,3}" + NameEnd;
        private const string TitleWords = @"(?-i:\p{Lu}\p{Ll}+(?:-\p{Lu}\p{Ll}+)?)(?:[ \t]+(?-i:\p{Lu}\p{Ll}+(?:-\p{Lu}\p{Ll}+)?)){0,2}" + NameEnd;
        // Фамилия с привычным окончанием: после должности без неё легко принять за имя обычное слово.
        private const string RoleSurname = @"(?-i:\p{Lu}\p{Ll}+(?:ов|ев|ёв|ин|ын)(?:а|у|ым|е|ой)?|\p{Lu}\p{Ll}+(?:ск|цк)(?:ий|ая|ого|ому|им|ом|ую|ой)|\p{Lu}\p{Ll}+(?:енко|чук|юк))" + NameEnd;

        internal static readonly Rule[] All = new Rule[]
        {
            // ---------- Обязательные секреты: их нельзя снять галочкой ----------

            // Приватный ключ: конец блока может отсутствовать в обрезанной вставке.
            new Rule(@"(?s)-----BEGIN (?:[A-Za-z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----.*?(?:-----END (?:[A-Za-z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----|\z)",
                "PRIVATE_KEY", Confidence.High, 100, true),
            new Rule(@"(?s)PuTTY-User-Key-File-\d+:.*?(?:Private-MAC:[ \t]*[0-9A-Fa-f]+|\z)",
                "PRIVATE_KEY", Confidence.High, 100, true),

            // JWT: заголовок настоящего токена всегда начинается с eyJ (это '{"' в base64url).
            new Rule(@"(?<![A-Za-z0-9_-])(?-i:eyJ)[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]*(?:\.[A-Za-z0-9_-]+){0,2}(?![A-Za-z0-9_-])",
                "TOKEN", Confidence.High, 98, true),

            // password=..., "password": "...", DB_PASSWORD=..., clientSecret: '...', x-api-key: ...
            new Rule(@"(?<!\w)(?:" + SecretKeyName + @"|(?:[A-Za-z0-9]+[_.-])+pass)[""']?[ \t]*(?::=|=>|:|=)[ \t]*" + SecretValue,
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\b(?:пароль|password)[ \t]+(?:от|для|к|for)[ \t]+[^\r\n:]{1,60}:[ \t]*" + SecretValue,
                "SECRET", Confidence.High, 100, "value", true),

            // Ключи командной строки: --password x, /pass:x, -Token 'x'.
            new Rule(@"(?<![\w-])(?:--?|/)(?:password|passwd|pwd|pass|secret|token|api[_-]?key|access[_-]?key|secret[_-]?key|client[_-]?secret|auth[_-]?token)(?:[ \t]*[:=][ \t]*|[ \t]+)(?:""(?<value>[^""\r\n]*)""|'(?<value>[^'\r\n]*)'|(?<value>(?![-/])[^\s""'`|;&]+))",
                "SECRET", Confidence.High, 100, "value", true),

            // XML/HTML: <password>x</password>, <unicodePwd>x</unicodePwd>.
            new Rule(@"<(?<tag>(?:[\w.-]+:)?[\w.-]*?(?:password|passwd|pwd|passphrase|secret|token|api[_-]?key|apikey|client[_-]?secret|credential)(?:[_.-]?(?:key|value|hash))?)(?:[ \t][^<>]*)?>(?<value>[^<]+)</\k<tag>>",
                "SECRET", Confidence.High, 100, "value", true),
            // unattend.xml: <AdministratorPassword><Value>x</Value>.
            new Rule(@"<(?<tag>(?:[\w.-]+:)?[\w.-]*?(?:password|passwd|pwd|secret))(?:[ \t][^<>]*)?>\s*<(?:[\w.-]+:)?Value>(?<value>[^<]+)</",
                "SECRET", Confidence.High, 100, "value", true),
            // app.config: <add key="DbPassword" value="x" />.
            new Rule(@"\b(?:key|name)[ \t]*=[ \t]*""[^""\r\n]*(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|credential)[^""\r\n]*""[ \t]+value[ \t]*=[ \t]*""(?<value>[^""\r\n]*)""",
                "SECRET", Confidence.High, 100, "value", true),

            // HTTP-заголовки.
            new Rule(@"\b(?:Proxy-)?Authorization[ \t]*[:=][ \t]*(?:(?:Bearer|Basic|Digest|NTLM|Negotiate|Token|ApiKey|SSWS)[ \t]+)?(?<value>(?!(?:Bearer|Basic|Digest|NTLM|Negotiate|Token|ApiKey|SSWS)\b)[A-Za-z0-9._~+/=:-]{4,})",
                "TOKEN", Confidence.High, 100, "value", true),
            new Rule(@"\bBearer[ \t]+(?<value>[A-Za-z0-9._~+/-]{16,}=*)",
                "TOKEN", Confidence.High, 100, "value", true),
            new Rule(@"(?m)^[ \t]*(?:Set-)?Cookie[ \t]*:[ \t]*(?<value>[^\r\n]+)",
                "SECRET", Confidence.High, 100, "value", true),

            // Логин и пароль внутри URL: postgres://admin:P4ss@db01/..., redis://:P4ss@cache01.
            // Пароль берётся до последней «@» перед узлом: в нём бывают «#», «?» и даже «@».
            new Rule(@"\b[A-Za-z][A-Za-z0-9+.-]*://(?<user>[^\s:/?#@\[\]]*):(?<value>[^\s/]+)@(?=[\p{L}\p{N}\[])",
                new GroupTarget("user", "USER", Confidence.High, 67, false),
                new GroupTarget("value", "SECRET", Confidence.High, 100, true)),

            // Токены известных сервисов. Длина у подделок и старых форматов бывает другой, поэтому диапазоны шире,
            // чем у текущих токенов.
            new Rule(@"(?-i)(?<![A-Za-z0-9_-])(?:gh[pousr]_[A-Za-z0-9]{30,255}|github_pat_[A-Za-z0-9_]{22,255}|gl(?:pat|ptt|dt|rt|cbt|imt|oas|ft|soat|agent)-[A-Za-z0-9_-]{20,}|xox[abposre]-[A-Za-z0-9-]{10,}|xapp-\d-[A-Za-z0-9-]{20,}|AIza[0-9A-Za-z_-]{35}|sk-(?:proj-|ant-(?:api\d+-)?|live-|test-|svcacct-|admin-)?[A-Za-z0-9_-]{20,}|(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{16,}|whsec_[A-Za-z0-9]{24,}|npm_[A-Za-z0-9]{36}|pypi-[A-Za-z0-9_-]{32,}|dop_v1_[a-f0-9]{64}|hf_[A-Za-z0-9]{30,}|SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}|\d{8,10}:AA[A-Za-z0-9_-]{30,40}|dckr_pat_[A-Za-z0-9_-]{20,}|hv[sb]\.[A-Za-z0-9_-]{24,}|gl(?:sa|c)_[A-Za-z0-9_=+/-]{32,}|shp(?:at|ca|pa|ss)_[a-fA-F0-9]{32}|key-[0-9a-f]{32}|sq0(?:atp|csp)-[A-Za-z0-9_-]{22,}|EAAA[A-Za-z0-9_-]{60,}|NRAK-[A-Z0-9]{27}|dp\.(?:st|ct|sa|scim|audit)\.[A-Za-z0-9_-]{40,}|lin_api_[A-Za-z0-9]{40}|ntn_[A-Za-z0-9]{40,}|figd_[A-Za-z0-9_-]{40,}|sbp_[a-f0-9]{40}|pul-[a-f0-9]{40}|PMAK-[a-f0-9]{24}-[a-f0-9]{34}|AKCp[A-Za-z0-9]{60,}|ATATT[A-Za-z0-9_=-]{50,}|AGE-SECRET-KEY-1[0-9A-Z]{58}|ya29\.[A-Za-z0-9_-]{20,}|1//0[A-Za-z0-9_-]{30,}|gsk_[A-Za-z0-9]{40,}|xai-[A-Za-z0-9]{60,}|r8_[A-Za-z0-9]{37}|pplx-[A-Za-z0-9]{40,}|re_[A-Za-z0-9]{8,}_[A-Za-z0-9]{20,}|xkeysib-[a-f0-9]{64}-[A-Za-z0-9]{16}|sk\.eyJ[A-Za-z0-9._-]{20,}|SK[0-9a-f]{32}|access_token\$production\$[a-z0-9]{16}\$[a-f0-9]{32}|[MN][A-Za-z0-9]{23,25}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,38})(?![A-Za-z0-9_-])",
                "TOKEN", Confidence.High, 99, true),
            // Токен бота в адресе Telegram API стоит сразу после «bot».
            new Rule(@"\bapi\.telegram\.org/bot(?<value>\d{8,10}:[A-Za-z0-9_-]{30,40})",
                "TOKEN", Confidence.High, 99, "value", true),
            // Вебхуки Slack, Discord и Teams: адрес сам даёт право писать в канал.
            new Rule(@"\bhooks\.slack\.com/(?:services|workflows|triggers)/(?<value>[A-Za-z0-9_/-]{20,})",
                "TOKEN", Confidence.High, 99, "value", true),
            new Rule(@"\bdiscord(?:app)?\.com/api/webhooks/\d+/(?<value>[A-Za-z0-9_-]{30,})",
                "TOKEN", Confidence.High, 99, "value", true),
            new Rule(@"\.webhook\.office\.com/webhookb2/(?<value>[^\s""'<>]+)",
                "TOKEN", Confidence.High, 99, "value", true),
            // Sentry DSN: ключ стоит на месте логина.
            new Rule(@"\bhttps?://(?<value>[0-9a-f]{32})(?::[0-9a-f]{32})?@[\w.-]*sentry\.io\b",
                "TOKEN", Confidence.High, 99, "value", true),
            new Rule(@"(?-i)(?<![A-Z0-9])(?:AKIA|ASIA|AGPA|AIDA|AROA|AIPA|ANPA|ANVA|APKA|ABIA|ACCA)[A-Z0-9]{16}(?![A-Z0-9])",
                "AWS_ACCESS_KEY", Confidence.High, 96, true),
            new Rule(@"[?&](?:sig|signature|sas_token)=(?<value>[^&\s""'<>]+)",
                "SECRET", Confidence.High, 100, "value", true),

            // Хеши паролей: $6$..., $2y$..., Cisco type 8/9, /etc/shadow, pwdump.
            new Rule(@"(?<![\w$])\$(?:1|2[abxy]?|5|6|7|8|9|y|gy|md5|sha1|apr1|argon2(?:id|i|d)|pbkdf2(?:-sha\d+)?|scrypt)\$[./A-Za-z0-9$=,+-]{8,}",
                "SECRET", Confidence.High, 100, true),
            new Rule(@"(?<![0-9A-Fa-f])(?<value>[0-9A-Fa-f]{32}:[0-9A-Fa-f]{32})(?=:::)",
                "SECRET", Confidence.High, 100, "value", true),

            // Конфигурации сетевого оборудования.
            new Rule(@"(?m)^[ \t]*enable[ \t]+(?:secret|password)(?:[ \t]+level[ \t]+\d+)?(?:[ \t]+[0-9])?[ \t]+(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"(?m)^[ \t]*username[ \t]+\S+(?:[ \t]+privilege[ \t]+\d+)?[ \t]+(?:secret|password)(?:[ \t]+[0-9])?[ \t]+(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            // Голая строка "password ..." только если это похоже на конфиг, а не на фразу из текста.
            new Rule(@"(?m)^[ \t]*(?:password|secret|key-string|pre-shared-key|authentication-key|md5-key)(?:[ \t]+[0-9][ \t]+(?<value>\S+)|[ \t]+(?<value>(?=\S*[^\p{L}\s])\S+))[ \t]*$",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bsnmp-server[ \t]+community[ \t]+(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bsnmp[ \t]+community(?:[ \t]+string)?[ \t]*[:=]?[ \t]*(?:""(?<value>[^""\r\n]*)""|(?<value>[^\s,;]+))",
                "SECRET", Confidence.High, 100, "value", true),
            // net-snmp: rocommunity public default.
            new Rule(@"(?m)^[ \t]*(?:ro|rw)community6?[ \t]+(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            // FortiGate: set password ENC ..., set psksecret ...; Huawei: local-user admin password cipher ...
            new Rule(@"(?m)^[ \t]*set[ \t]+(?:password|passwd|psksecret|secret|auth-pwd|passphrase|sae-password|private-key|key)[ \t]+(?:ENC[ \t]+)?" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\blocal-user[ \t]+\S+[ \t]+password[ \t]+(?:irreversible-cipher|cipher|simple)[ \t]+(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bppp[ \t]+(?:chap|pap)[ \t]+(?:sent-username[ \t]+\S+[ \t]+)?password[ \t]+(?:[0-9][ \t]+)?(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bcrypto[ \t]+isakmp[ \t]+key[ \t]+(?:[0-9][ \t]+)?(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\b(?:tacacs|radius)-server\b[^\r\n]*?[ \t]key[ \t]+(?:[0-9][ \t]+)?(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\b(?:encrypted-password|authentication-key|ascii-text|pre-shared-key|secret)[ \t]+""(?<value>[^""\r\n]+)""",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"(?m)^[ \t]*wpa-psk[ \t]+ascii[ \t]+(?:[0-9][ \t]+)?(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),

            // Windows и утилиты администратора.
            new Rule(@"\bConvertTo-SecureString[ \t]+(?:-String[ \t]+)?(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')[ \t]*\|[ \t]*ConvertTo-SecureString\b",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bnet(?:\.exe)?[ \t]+user[ \t]+(?:""[^""\r\n]+""|[^\s/""]+)[ \t]+(?:""(?<value>[^""\r\n]+)""|(?<value>(?![/*])[^\s""]+))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\b(?:psexec(?:64)?|paexec)(?:\.exe)?\b[^\r\n]*?[ \t]-p[ \t]+(?:""(?<value>[^""\r\n]+)""|(?<value>[^\s""]+))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bsshpass[ \t]+-p[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)'|(?<value>[^\s""']+))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bmysql(?:dump|admin|import|sh)?(?:\.exe)?\b[^\r\n]*?[ \t](?-i:-p)(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)'|(?<value>[^\s""'-][^\s""']*))",
                "SECRET", Confidence.High, 100, "value", true),
            // docker login -p, podman login -p, helm registry login -p.
            new Rule(@"\b(?:docker|podman|nerdctl|buildah|skopeo|helm|oras|crane|regctl)(?:\.exe)?\b[^\r\n]*?\blogin\b[^\r\n]*?[ \t](?-i:-p)[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            // sqlcmd -P, bcp -P, osql -P, ipmitool -P; у racadm и az login ключ строчный.
            new Rule(@"\b(?:sqlcmd|bcp|osql|isql|ipmitool)(?:\.exe)?\b[^\r\n]*?[ \t](?-i:-P)[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)'|(?<value>[^\s""'-][^\s""']*))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\b(?:racadm|az[ \t]+login|vcsa-cli|esxcli)\b[^\r\n]*?[ \t](?-i:-p)[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bvmrun(?:\.exe)?\b[^\r\n]*?[ \t]-(?:gp|vp)[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bmongo(?:sh|dump|restore|export|import)?(?:\.exe)?\b[^\r\n]*?[ \t](?-i:-p)[ \t]+(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)'|(?<value>[^\s""'-][^\s""']*))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bredis-cli(?:\.exe)?\b[^\r\n]*?[ \t](?:-a|--pass)[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bldap(?:search|add|modify|delete|passwd|whoami|compare|modrdn)\b[^\r\n]*?[ \t]-w[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            // smbclient -U user%pass, crackmapexec и другие утилиты Samba.
            new Rule(@"\b(?:smbclient|rpcclient|smbmap|smbcacls|net[ \t]+rpc|crackmapexec|nxc|netexec)\b[^\r\n]*?[ \t](?:-U|--user(?:name)?)(?:[ \t]+|=)[""']?[^\s%""']+%(?<value>[^\s""']+)",
                "SECRET", Confidence.High, 100, "value", true),
            // curl -u user:pass, --proxy-user user:pass.
            new Rule(@"\bcurl(?:\.exe)?\b[^\r\n]*?[ \t](?:-u|--user|-U|--proxy-user)(?:[ \t]+|=)[""']?[^\s:""']*:(?<value>[^\s""']+)",
                "SECRET", Confidence.High, 100, "value", true),
            // wget --http-password, --proxy-password и подобные с приставкой.
            new Rule(@"(?<![\w-])--(?:http|ftp|proxy|db|admin|root|user|ssh|smtp|mail|bind|store|key|trust|remote|vault)[_-]?(?:password|passwd|pass|pwd|secret)(?:[ \t]*=[ \t]*|[ \t]+)(?:""(?<value>[^""\r\n]*)""|'(?<value>[^'\r\n]*)'|(?<value>(?![-/])[^\s""'`|;&]+))",
                "SECRET", Confidence.High, 100, "value", true),
            // keytool -storepass, -keypass, -srcstorepass; openssl -passin pass:..., -k.
            new Rule(@"(?<![\w-])-(?:src|dest)?(?:store|key)pass(?:wd)?[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"(?<![\w-])-(?:passin|passout|pass|password)[ \t]+pass:(?:""(?<value>[^""\r\n]+)""|(?<value>[^\s""']+))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bopenssl\b[^\r\n]*?[ \t]-k[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            // 7-Zip и RAR: пароль слитно с -p; zip и unzip: -P пароль.
            new Rule(@"\b(?:7z|7za|7zr|7zz|rar|unrar|winrar)(?:\.exe)?\b[^\r\n]*?[ \t]-p(?:""(?<value>[^""\r\n]+)""|(?<value>[^\s""-][^\s""]*))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\b(?:zip|unzip|zipcloak)(?:\.exe)?\b[^\r\n]*?[ \t](?-i:-P)[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            // htpasswd -b файл пользователь пароль.
            new Rule(@"\bhtpasswd\b[ \t]+-[A-Za-z]*b[A-Za-z]*[ \t]+(?:-[A-Za-z]+[ \t]+)*\S+[ \t]+\S+[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            // schtasks /RP пароль; net use \\srv\share пароль /user:...
            new Rule(@"\bschtasks(?:\.exe)?\b[^\r\n]*?[ \t]/(?:RP|P)[ \t]+(?:""(?<value>[^""\r\n]+)""|(?<value>(?![/*])[^\s""]+))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bnet(?:\.exe)?[ \t]+use\b[^\r\n]*?\\\\[^\s""]+[ \t]+(?:""(?<value>[^""\r\n]+)""|(?<value>(?![/*])[^\s""]+))",
                "SECRET", Confidence.High, 100, "value", true),
            // sqlplus user/password@db, expdp, rman.
            new Rule(@"\b(?:sqlplus|rman|expdp|impdp|exp|imp|sqlldr|dgmgrl)(?:\.exe)?[ \t]+(?:-[A-Za-z]+[ \t]+)*[""']?(?<user>[A-Za-z_][\w$#]*)/(?<value>[^@\s""'/]+)(?=@|[ \t""']|$)",
                new GroupTarget("user", "USER", Confidence.High, 66, false),
                new GroupTarget("value", "SECRET", Confidence.High, 100, true)),
            // nmcli: wifi-sec.psk пароль.
            new Rule(@"\bwifi-sec\.psk[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),
            // netsh wlan show profile key=clear: «Key Content : ...», «Содержимое ключа : ...».
            new Rule(@"\b(?:Key[ \t]+Content|Содержимое[ \t]+ключа)[ \t]*:[ \t]*(?<value>[^\r\n]*[^\s])",
                "SECRET", Confidence.High, 100, "value", true),

            // Пароль в коде: NetworkCredential("user", "pass"), auth=("user", "pass"), smtp.login("user", "pass").
            new Rule(@"\bNetworkCredential[ \t]*\([ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[\w.]+)[ \t]*,[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\b(?:HTTPBasicAuth|HTTPDigestAuth|HTTPProxyAuth|BasicAuth|auth)[ \t]*(?:=[ \t]*\(|\()[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*')[ \t]*,[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\.(?:login|simple_bind_s|bind_s|setCredentials|withCredentials|basicAuth|authenticate)[ \t]*\([ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*')[ \t]*,[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bgetConnection[ \t]*\([^,\r\n]+,[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|\w+)[ \t]*,[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')",
                "SECRET", Confidence.High, 100, "value", true),
            // PHP: define('DB_PASSWORD', '...'), ключи и соли WordPress.
            new Rule(@"\bdefine[ \t]*\([ \t]*['""]\w*(?:PASSWORD|PASSWD|PASS|PWD|SECRET|TOKEN|_KEY|_SALT|APIKEY)['""][ \t]*,[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')",
                "SECRET", Confidence.High, 100, "value", true),
            // SQL: IDENTIFIED BY 'x', WITH PASSWORD 'x', PASSWORD('x').
            new Rule(@"\bIDENTIFIED[ \t]+(?:WITH[ \t]+\w+[ \t]+)?BY[ \t]+(?:PASSWORD[ \t]+)?(?:'(?<value>[^'\r\n]+)'|""(?<value>[^""\r\n]+)""|(?<value>[^\s;'""]+))",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bPASSWORD[ \t]+E?'(?<value>[^'\r\n]+)'",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bPASSWORD[ \t]*\([ \t]*'(?<value>[^'\r\n]+)'[ \t]*\)",
                "SECRET", Confidence.High, 100, "value", true),
            // Redis: requirepass, masterauth.
            new Rule(@"(?m)^[ \t]*(?:requirepass|masterauth|masterpassword)[ \t]+" + ArgValue,
                "SECRET", Confidence.High, 100, "value", true),

            // Файлы учётных данных: .pgpass (узел:порт:база:пользователь:пароль), .netrc, хеши LDAP.
            new Rule(@"(?m)^[ \t]*[^\s:#][^\s:]*:(?:\d{1,5}|\*):[^\s:]+:[^\s:]+:(?<value>\S+)[ \t]*$",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\bmachine[ \t]+\S+(?:[ \t]+login[ \t]+\S+)?[ \t]+password[ \t]+(?<value>\S+)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"\{(?:SSHA|SHA|SSHA256|SSHA512|SHA256|SHA512|MD5|SMD5|CRYPT|PBKDF2(?:-SHA\d+)?|ARGON2)\}[A-Za-z0-9+/$.=]{8,}",
                "SECRET", Confidence.High, 100, true),

            // CVV и сид-фраза кошелька: 12 или 24 слова после подписи.
            new Rule(@"(?<![\p{L}\p{N}_])(?:cvv2?|cvc2?|cvn|csc|security[ \t_-]?code|код[ \t]+безопасности)(?![\p{L}\p{N}_])[""']?[ \t]*[:=]?[ \t]*[""']?(?<value>\d{3,4})(?!\d)",
                "SECRET", Confidence.High, 100, "value", true),
            new Rule(@"(?<![\p{L}\p{N}_])(?:(?:seed|mnemonic|recovery|backup|secret[ \t_-]?recovery)[ \t_-]?(?:phrase|words)|mnemonic|seed(?=[""']?[ \t]*[:=])|сид[ -]?фраз\p{L}*|мнемоническ\p{L}*[ \t]+фраз\p{L}*|фраз\p{L}*[ \t]+(?:восстановления|для[ \t]+восстановления))[""']?[ \t]*[:=\-]?[ \t]*[""']?(?<value>(?-i:[a-z]{3,8})(?:[ \t]+(?-i:[a-z]{3,8})){11,23})(?![\p{L}])",
                "SECRET", Confidence.High, 100, "value", true),
            // Ключ продукта: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.
            new Rule(@"(?<![A-Za-z0-9-])[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){4}(?![A-Za-z0-9-])",
                "SECRET", Confidence.High, 100, true),

            // ---------- Идентификаторы ----------

            new Rule(@"(?<![0-9A-Fa-f:-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f]|[:-][0-9A-Fa-f])",
                "MAC", Confidence.High, 72),
            new Rule(@"(?<![0-9A-Fa-f.])[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}(?![0-9A-Fa-f.])",
                "MAC", Confidence.High, 72),
            new Rule(@"\b(?:WWN|WWPN|WWNN|World[ \t]+Wide[ \t]+Name)[ \t]*[:=]?[ \t]*(?<value>(?:0[xX])?[0-9A-Fa-f]{16})\b",
                "WWN", Confidence.High, 76, "value", false),
            new Rule(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{2}:){7}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:])",
                "WWN", Confidence.High, 76),

            // Почта: точка в конце предложения не должна мешать.
            new Rule(@"(?<![\w.+-])[\w.+-]+@[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+(?![\w-]|\.[\p{L}\p{N}])",
                "EMAIL", Confidence.High, 82),

            // URL: хвостовая пунктуация не входит в адрес.
            new Rule(@"\b(?:https?|ftps?|sftp|ssh|smb|nfs|file|ldaps?|wss?|rdp|vnc|git|svn|mysql|postgres(?:ql)?|mongodb(?:\+srv)?|rediss?|amqps?|mqtt|kafka|sqlserver|oracle|jdbc:[a-z0-9]+)://[^\s<>""'`]+(?<![.,;:!?)\]}])",
                "URL", Confidence.High, 80),

            // После «bind_dn=», «base=», «memberOf=» DN тоже начинается сразу за «=».
            new Rule(@"(?:(?<![\w=])|(?<=(?:dn|base|path|of|by)[""']?[ \t]*[:=][ \t]*[""']?))" + DnComponent + @"(?:[ \t]*[,;+][ \t]*" + DnComponent + @")+",
                "AD_DN", Confidence.High, 86),

            new Rule(@"\biqn\.\d{4}-\d{2}\.[A-Za-z0-9.-]+(?::[A-Za-z0-9._-]+)?\b",
                "ISCSI_IQN", Confidence.High, 76),
            new Rule(@"\b(?:certificate[ \t]+)?thumbprint[ \t]*[:=][ \t]*(?<value>[0-9A-Fa-f]{40}(?:[0-9A-Fa-f]{24})?)\b",
                "CERT_THUMBPRINT", Confidence.High, 76, "value", false),
            // MoRef всегда в нижнем регистре, иначе "ESXI-HOST-01" распознаётся как "host-01".
            new Rule(@"(?<![A-Za-z0-9-])(?-i:(?:vm|host|datastore|network|datacenter|resgroup|dvportgroup|dvs|vapp|storagepod|snapshot)-|domain-[cs]|group-[dhnvps])\d+(?![A-Za-z0-9-])",
                "VMWARE_MOREF", Confidence.Medium, 64),
            new Rule(@"\btenant\)?[ \t_-]*(?:id)?[""']?[ \t]*[:=][ \t]*[""']?(?<value>" + Guid + @")",
                "AZURE_TENANT", Confidence.High, 78, "value", false),
            new Rule(@"\bsubscription\)?[ \t_-]*(?:id)?[""']?[ \t]*[:=][ \t]*[""']?(?<value>" + Guid + @")",
                "AZURE_SUBSCRIPTION", Confidence.High, 78, "value", false),
            new Rule(@"\b(?:ssh-(?:rsa|dss|ed25519)|ecdsa-sha2-nistp\d+|sk-ssh-ed25519@openssh\.com)[ \t]+(?<value>[A-Za-z0-9+/]{32,}={0,3})",
                "SSH_PUBLIC_KEY", Confidence.High, 78, "value", false),
            new Rule(@"\b(?:ssh[ \t]+)?fingerprint[ \t]*[:=][ \t]*(?<value>(?:SHA256:[A-Za-z0-9+/=]{16,}|(?:[0-9A-Fa-f]{2}:){15,31}[0-9A-Fa-f]{2}))",
                "SSH_FINGERPRINT", Confidence.High, 76, "value", false),
            new Rule(@"(?<![A-Za-z0-9])SHA256:[A-Za-z0-9+/]{32,64}={0,2}(?![A-Za-z0-9+/=])",
                "SSH_FINGERPRINT", Confidence.High, 76),
            // Отпечаток MD5: шестнадцать байт через двоеточие (длиннее MAC-адреса).
            new Rule(@"(?<![0-9A-Fa-f:])(?:MD5:)?(?:[0-9A-Fa-f]{2}:){15}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:])",
                "SSH_FINGERPRINT", Confidence.High, 77),
            // Отпечаток сертификата без подписи "thumbprint": 40 или 64 шестнадцатеричных символа в верхнем регистре.
            new Rule(@"(?<![A-Za-z0-9])(?-i:[0-9A-F]{40}|[0-9A-F]{64})(?![A-Za-z0-9])",
                "CERT_THUMBPRINT", Confidence.Medium, 74),
            // UUID из BIOS виртуальной машины: 56 4d 12 34 ab cd ef 00-11 22 33 44 55 66 77 88
            new Rule(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2} ){7}[0-9A-Fa-f]{2}-(?:[0-9A-Fa-f]{2} ){7}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])",
                "GUID", Confidence.High, 71),
            new Rule(@"\bDSN[ \t]*[:=][ \t]*(?<value>[A-Za-z0-9][A-Za-z0-9._-]{1,127})\b",
                "DSN", Confidence.Medium, 65, "value", false),
            new Rule(@"\b(?:RDP[ \t]+gateway|VPN[ \t]+(?:remote|gateway|server)|gateway)[ \t]*[:=][ \t]*(?<value>[A-Za-z0-9][A-Za-z0-9.-]{1,253})\b",
                "HOST", Confidence.Medium, 65, "value", false),

            // UNC: узел и ресурс скрываются отдельно: \\[HOST_1]\[SHARE_1]. Путь целиком скрывает строгий режим.
            // Пробел внутри имени ресурса («Общая папка») допустим, если дальше идёт следующая папка.
            new Rule(@"(?<![\\\w])\\\\(?<host>[\p{L}\p{N}_](?:[\p{L}\p{N}_.-]{0,253}[\p{L}\p{N}_])?)\\(?<share>[^\\/:*?""<>|\s](?:[^\\/:*?""<>|\r\n\t]{0,78}[^\\/:*?""<>|\s])?(?=\\)|[^\\/:*?""<>|\s]+)",
                new GroupTarget("host", "HOST", Confidence.High, 74, false),
                new GroupTarget("share", "SHARE", Confidence.High, 72, false)),
            // Та же сетевая папка в записи Linux: //fs01/share. Узел берётся, только если похож на имя компьютера,
            // иначе под правило попали бы ссылки без схемы вроде //cdn.example.com/lib.
            new Rule(@"(?<![\p{L}\p{N}_.:/\\-])//(?<host>(?=[\p{L}\p{N}_-]*[0-9-])[\p{L}\p{N}_][\p{L}\p{N}_-]{0,62}|(?:\d{1,3}\.){3}\d{1,3})/(?<share>[^/\s""'<>|:*?\\]*[^/\s""'<>|:*?\\.,;!?)\]])",
                new GroupTarget("host", "HOST", Confidence.Medium, 74, false),
                new GroupTarget("share", "SHARE", Confidence.Medium, 72, false)),

            // user@host без домена: ssh admin@web01, приглашение оболочки.
            new Rule(@"(?<![\w.+@-])(?<user>[A-Za-z_][A-Za-z0-9_.-]{0,31})@(?<host>(?=[A-Za-z0-9-]*[A-Za-z])[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?![\w@-]|\.[\w-])",
                new GroupTarget("user", "USER", Confidence.Medium, 63, false),
                new GroupTarget("host", "HOST", Confidence.Medium, 65, false)),

            // ---------- Контекстные подсказки ----------

            // ФИО по подписи рядом: "ФИО: Цой Виктор", "Фамилия: Шойгу", "Исполнитель: Петров", "displayName": "Иванов Иван".
            // Просто "name" и "author" не берутся: так называют что угодно, от задачи до компании.
            new Rule(@"(?<![\p{L}\p{N}_])(?:ФИО|Ф\.[ \t]?И\.[ \t]?О\.|фамилия(?:,[ \t]*имя(?:,[ \t]*отчество)?)?|имя|отчество|(?:full|display|first|last|given|middle|sur)[ \t_-]?name|contact[ \t_-]?person|контактное[ \t]+лицо|исполнитель|ответственн(?:ый|ая|ое[ \t]+лицо)|заявитель|инициатор|assignee|reporter|requester)[""']?[ \t]*[:=][ \t]*[""']?(?<value>" + NameWords + ")",
                "PERSON", Confidence.High, PersonNames.Priority, "value", false),
            // Обращение перед фамилией: "г-н Иванов", "госпоже Петровой", "Mr. Smith".
            new Rule(@"(?<![\p{L}\p{N}_])(?-i:[Гг]-(?:н|на|ну|ном|не|жа|жи|же|жу|жой)|[Гг]осподин(?:а|у|ом|е)?|[Гг]оспож(?:а|и|е|у|ой))\.?[ \t]+(?<value>" + TitleWords + ")",
                "PERSON", Confidence.High, PersonNames.Priority, "value", false),
            new Rule(@"(?<![\p{L}\p{N}_])(?-i:Mr|Mrs|Ms|Mister|Dr\.)\.?[ \t]+(?<value>" + TitleWords + ")",
                "PERSON", Confidence.Medium, PersonNames.Priority, "value", false),
            // Должность перед фамилией: "сотрудник Иванов", "инженера Петровой".
            new Rule(@"(?<![\p{L}\p{N}_])(?:сотрудни(?:к|ка|ку|ком|ке|ца|цы|це|цу|цей)|коллег(?:а|и|е|у|ой)|инженер(?:а|у|ом|е)?|директор(?:а|у|ом|е)?|руководител(?:ь|я|ю|ем|е)|начальни(?:к|ка|ку|ком|ке|ца|цы|це|цу)|администратор(?:а|у|ом|е)?|менеджер(?:а|у|ом|е)?|специалист(?:а|у|ом|е)?|бухгалтер(?:а|у|ом|е)?|исполнител(?:ь|я|ю|ем|е)|заявител(?:ь|я|ю|ем|е)|товарищ(?:а|у|ем|е)?|граждани(?:н|на|ну|ном|не)|гражданк(?:а|и|е|у|ой)|автор(?:а|у|ом|е)?)[ \t]+(?<value>" + RoleSurname + ")",
                "PERSON", Confidence.Medium, PersonNames.Priority, "value", false),

            // Явный контекст "host=" надёжен, поэтому High: такие значения скрывает и быстрый режим.
            new Rule(@"(?<!Windows[ \t])\b(?:hostname|host|server|computer|machine|node|data[ \t]+source|сервер|узел|компьютер|хост)[ \t]*[:=][ \t]*(?!(?:tcp|udp|np|lpc):)(?<value>(?=[\w-]*[A-Za-z])[A-Za-z0-9][A-Za-z0-9_-]{1,62})\b",
                "HOST", Confidence.High, 65, "value", false),
            new Rule(@"(?<!Windows[ \t])\b(?:hostname|host|server|computer|machine|node|сервер|узел|компьютер|хост)[ \t]+(?<value>(?=[\w-]*[A-Za-z])(?=[\w-]*[0-9-])[A-Za-z0-9][A-Za-z0-9_-]{1,62})\b",
                "HOST", Confidence.Medium, 65, "value", false),
            // В том числе с приставкой: VCENTER_USERNAME, ansible_user, snmpv3_user, adminUser.
            new Rule(@"(?<![\w])(?:[A-Za-z0-9]+[_.-]|(?-i:[a-z][a-z0-9]*(?=[A-Z])))*(?:user(?:[ \t_-]?name)?|login|logon|account|пользователь|логин)[""']?[ \t]*[:=][ \t]*[""']?(?<value>[\p{L}\p{N}][\p{L}\p{N}._@$-]{1,127})",
                "USER", Confidence.High, 63, "value", false),
            new Rule(@"\b(?:user[ \t_-]?id|uid)[ \t]*[:=][ \t]*(?<value>[\p{L}\p{N}][\p{L}\p{N}._-]{1,127})",
                "USER", Confidence.High, 66, "value", false),
            new Rule(@"<(?<tag>(?:[\w.-]+:)?(?:user(?:name|id)?|login|account))>(?<value>[\p{L}\p{N}][\p{L}\p{N}._@$-]{1,127})</\k<tag>>",
                "USER", Confidence.High, 66, "value", false),
            // MikroTik: /user add name=..., /ppp secret add name=...
            new Rule(@"(?m)^[ \t]*/(?:user|ppp[ \t]+secret|ip[ \t]+hotspot[ \t]+user)[ \t]+(?:add|set)\b[^\r\n]*?\bname=(?:""(?<value>[^""\r\n]+)""|(?<value>[^\s""]+))",
                "USER", Confidence.High, 66, "value", false),
            new Rule(@"\b(?:S/?N|Serial(?:[ \t]+(?:Number|No\.?|#))?|Service[ \t]+Tag|Asset(?:[ \t]+Tag)?|IMEI|серийный(?:[ \t]+номер)?|инвентарный(?:[ \t]+номер)?)\b(?:[ \t]+\p{L}+){0,2}[ \t]*[:=#№][ \t]*(?<value>(?=[A-Za-z-]*[0-9])[A-Za-z0-9][A-Za-z0-9-]{4,31})\b",
                "SERIAL", Confidence.High, 60, "value", false),
            // "SN1234567890": приставка входит в значение, иначе в тексте осталось бы "SN[SERIAL_1]".
            new Rule(@"\b(?-i:S/?N)[-_]?(?=\d)[A-Za-z0-9-]{5,31}\b",
                "SERIAL", Confidence.High, 61),
            // Тот же серийный номер без разделителя: "Serial CZC1234ABC", "SN 1234567890".
            new Rule(@"\b(?:S/?N|Serial(?:[ \t]+(?:Number|No\.?|#))?|Service[ \t]+Tag|Asset(?:[ \t]+Tag)?|IMEI|серийный(?:[ \t]+номер)?|инвентарный(?:[ \t]+номер)?)(?:[ \t]+|(?=\d))(?<value>(?=[A-Za-z-]*[0-9])[A-Za-z0-9][A-Za-z0-9-]{4,31})\b",
                "SERIAL", Confidence.High, 60, "value", false)
        };
    }
}
