using System;
using System.Collections.Generic;
using System.Globalization;
using SafePaste.Detecting;

namespace SafePaste.Ui
{
    /// <summary>Понятные названия типов находок для подсказок и меню.</summary>
    internal static class TypeNames
    {
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "HOST", "Имя компьютера" },
            { "TEXT", "Текст" },
            { "HIDE_TEXT", "Скрытый текст" },
            { "USER", "Учётная запись" },
            { "PERSON", "ФИО" },
            { "PATH", "Путь к файлу или папке" },
            { "IP", "IP-адрес" },
            { "IPV6", "IPv6-адрес" },
            { "FQDN", "Доменное имя" },
            { "EMAIL", "Почта" },
            { "URL", "Ссылка" },
            { "SHARE", "Сетевая папка" },
            { "MAC", "MAC-адрес" },
            { "SID", "SID" },
            { "GUID", "GUID" },
            { "SERIAL", "Серийный номер" },
            { "PHONE", "Телефон" },
            { "SECRET", "Пароль или секрет" },
            { "TOKEN", "Токен" },
            { "PRIVATE_KEY", "Закрытый ключ" },
            { "AWS_ACCESS_KEY", "Ключ AWS" },
            { "AZURE_TENANT", "Клиент Azure" },
            { "AZURE_SUBSCRIPTION", "Подписка Azure" },
            { "CERT_THUMBPRINT", "Отпечаток сертификата" },
            { "SSH_FINGERPRINT", "Отпечаток SSH" },
            { "SSH_PUBLIC_KEY", "Открытый ключ SSH" },
            { "AD_DN", "Путь в Active Directory" },
            { "DSN", "Строка подключения" },
            { "ISCSI_IQN", "Имя iSCSI" },
            { "WWN", "Адрес WWN" },
            { "VMWARE_MOREF", "Объект VMware" }
        };

        internal static string Describe(string type)
        {
            string name;
            return type != null && Names.TryGetValue(type, out name) ? name : type;
        }

        /// <summary>
        /// Почему находка останется в тексте или насколько она надёжна. Путь не догадка:
        /// он найден уверенно, но целиком его скрывает только строгий режим.
        /// </summary>
        internal static string DescribeFinding(string type, Confidence confidence)
        {
            if (confidence == Confidence.Low && string.Equals(type, "PATH", StringComparison.OrdinalIgnoreCase))
            {
                return "скрывается в строгом режиме";
            }
            return DescribeConfidence(confidence);
        }

        /// <summary>То же для находки: запомненная метка не правило пользователя, хотя находится так же.</summary>
        internal static string DescribeFinding(Detection detection)
        {
            if (detection.Source == Detection.RememberedSource)
            {
                return "метка запомнена раньше";
            }
            return DescribeFinding(detection.Type, detection.Confidence);
        }

        internal static string DescribeConfidence(Confidence confidence)
        {
            switch (confidence)
            {
                case Confidence.High: return "уверенно";
                case Confidence.Medium: return "по подсказке рядом";
                case Confidence.Low: return "догадка";
                case Confidence.Learned: return "из ваших правил";
                case Confidence.Manual: return "отмечено вручную";
                default: return confidence.ToString();
            }
        }

        /// <summary>«1 раз», «2 раза», «5 раз».</summary>
        internal static string Times(int count)
        {
            int tens = count % 100;
            int units = count % 10;
            string word = tens >= 11 && tens <= 14 ? "раз" : units >= 2 && units <= 4 ? "раза" : "раз";
            return count.ToString(CultureInfo.InvariantCulture) + " " + word;
        }
    }
}
