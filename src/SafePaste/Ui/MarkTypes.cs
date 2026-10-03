using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SafePaste.Ui
{
    /// <summary>
    /// Типы для «Пометить как». Показываются только те, что подходят к выделенному тексту: IP-адресом
    /// нельзя пометить ФИО, а путём строку без разделителей. Сначала типы, чей строгий вид совпал
    /// с текстом (адрес, почта, GUID), потом остальные. Внутри каждой группы частые выше: сколько раз
    /// выбирали тип, хранится в настройках, пока выбора не было, действует обычный порядок.
    /// </summary>
    internal static class MarkTypes
    {
        /// <summary>Отметка без выбора типа: «Отметить» в меню выделенного.</summary>
        internal const string Generic = "HIDE_TEXT";

        /// <summary>Все типы от частых к редким.</summary>
        internal static readonly string[] All =
        {
            "HOST", "USER", "PERSON", "IP", "PATH", "SERIAL", "FQDN", "EMAIL", "URL", "SHARE", "PHONE", "MAC",
            "IPV6", "GUID", "SID", "AD_DN", "DSN", "CERT_THUMBPRINT", "SSH_FINGERPRINT", "SSH_PUBLIC_KEY",
            "AZURE_TENANT", "AZURE_SUBSCRIPTION", "AWS_ACCESS_KEY", "VMWARE_MOREF", "ISCSI_IQN", "WWN",
            "TOKEN", "PRIVATE_KEY", "SECRET"
        };

        private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);
        private const string GuidShape = @"^[{(]?[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}[})]?$";

        /// <summary>Строгий вид значения: если текст на него похож, тип почти наверняка этот.</summary>
        private static readonly Dictionary<string, Regex> Shapes = new Dictionary<string, Regex>(StringComparer.Ordinal)
        {
            { "IP", new Regex(@"^(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?(?::\d{1,5})?$", Options, Limit) },
            { "IPV6", new Regex(@"^(?=.*:.*:)[0-9a-f:.]+(?:%\w+)?(?:/\d{1,3})?$", Options, Limit) },
            { "EMAIL", new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", Options, Limit) },
            { "URL", new Regex(@"^(?:[a-z][a-z0-9+.-]*://|www\.)\S+$", Options, Limit) },
            { "MAC", new Regex(@"^(?:(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}|(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}|[0-9a-f]{12})$", Options, Limit) },
            { "GUID", new Regex(GuidShape, Options, Limit) },
            { "AZURE_TENANT", new Regex(GuidShape, Options, Limit) },
            { "AZURE_SUBSCRIPTION", new Regex(GuidShape, Options, Limit) },
            { "SID", new Regex(@"^S-\d+(?:-\d+){2,15}$", Options, Limit) },
            { "PHONE", new Regex(@"^\+?(?=(?:\D*\d){7,15}\D*$)[\d\s().-]+$", Options, Limit) },
            { "CERT_THUMBPRINT", new Regex(@"^(?:[0-9a-f]{2}[\s:]?){19}[0-9a-f]{2}$|^(?:[0-9a-f]{2}[\s:]?){31}[0-9a-f]{2}$", Options, Limit) },
            { "SSH_FINGERPRINT", new Regex(@"^(?:SHA256:[A-Za-z0-9+/=]{20,}|MD5:\S+|(?:[0-9a-f]{2}:){15}[0-9a-f]{2})$", Options, Limit) },
            { "SSH_PUBLIC_KEY", new Regex(@"^(?:ssh-|ecdsa-|sk-)\S+\s+\S+", Options, Limit) },
            { "AD_DN", new Regex(@"(?:^|,)\s*(?:CN|OU|DC|O)=[^,]+", Options, Limit) },
            { "DSN", new Regex(@"=.*;|;.*=|^[a-z][a-z0-9+.-]*://[^/\s]*@", Options, Limit) },
            { "AWS_ACCESS_KEY", new Regex(@"^(?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA|ANVA|APKA)[A-Z0-9]{16}$", Options, Limit) },
            { "VMWARE_MOREF", new Regex(@"^(?:vm|host|datastore|network|resgroup|domain|group|dvportgroup|dvs|datacenter|folder|vapp)-\d+$", Options, Limit) },
            { "ISCSI_IQN", new Regex(@"^(?:iqn\.\d{4}-\d{2}\.|eui\.|naa\.)\S+$", Options, Limit) },
            { "WWN", new Regex(@"^(?:[0-9a-f]{2}:){7}[0-9a-f]{2}$|^[0-9a-f]{16}$", Options, Limit) },
            { "FQDN", new Regex(@"^(?=.*\p{L})[\p{L}\p{N}_-]+(?:\.[\p{L}\p{N}_-]+)+\.?$", Options, Limit) },
            { "PATH", new Regex(@"^(?:[a-z]:[\\/]|\\\\|//|~[\\/]|%\w+%|\$\w+|\.{1,2}[\\/])|[\\/].*[\\/]|\w[\\/]\w", Options, Limit) }
        };

        /// <summary>Типы, которые подходят к значению, от вероятных и частых к редким.</summary>
        internal static List<string> For(string value, IDictionary<string, int> usage)
        {
            List<string> strict = new List<string>();
            List<string> loose = new List<string>();
            foreach (string type in All)
            {
                bool shaped;
                if (Fits(type, value, out shaped))
                {
                    (shaped ? strict : loose).Add(type);
                }
            }
            SortByUse(strict, usage);
            SortByUse(loose, usage);
            strict.AddRange(loose);
            return strict;
        }

        internal static bool Fits(string type, string value)
        {
            bool shaped;
            return Fits(type, value, out shaped);
        }

        /// <summary>shaped: текст похож на строгий вид этого типа.</summary>
        private static bool Fits(string type, string value, out bool shaped)
        {
            shaped = false;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            // Пароль, токен и ключ бывают любыми, в том числе в несколько строк.
            if (type == "SECRET" || type == "TOKEN" || type == "PRIVATE_KEY")
            {
                return true;
            }
            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
            {
                return false;
            }
            Regex shape;
            if (Shapes.TryGetValue(type, out shape))
            {
                shaped = Matches(shape, value);
                return shaped;
            }
            bool spaces = HasSpace(value);
            bool slashes = value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0;
            switch (type)
            {
                case "HOST":
                    return !spaces && !slashes && value.Length <= 253 && HasLetterOrDigit(value);
                case "USER":
                    // Имя с пробелами почти всегда ФИО, а учётная запись бывает и с доменом: CORP\ivanov.
                    return !spaces && value.Length <= 256 && value.IndexOf('/') < 0 && Count(value, '\\') <= 1
                        && HasLetterOrDigit(value);
                case "PERSON":
                    return !slashes && value.IndexOf('@') < 0 && HasLetter(value) && !HasDigit(value)
                        && value.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length <= 6;
                case "SERIAL":
                    return !spaces && !slashes && value.Length >= 4 && value.Length <= 64 && HasDigit(value);
                case "SHARE":
                    return !slashes && value.Length <= 256 && HasLetterOrDigit(value);
                default:
                    return true;
            }
        }

        private static bool Matches(Regex shape, string value)
        {
            try
            {
                return shape.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static void SortByUse(List<string> types, IDictionary<string, int> usage)
        {
            List<string> order = new List<string>(All);
            types.Sort(delegate(string left, string right)
            {
                int result = Uses(usage, right).CompareTo(Uses(usage, left));
                return result != 0 ? result : order.IndexOf(left).CompareTo(order.IndexOf(right));
            });
        }

        private static int Uses(IDictionary<string, int> usage, string type)
        {
            int count;
            return usage != null && usage.TryGetValue(type, out count) ? count : 0;
        }

        private static bool HasSpace(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsWhiteSpace(symbol)) return true;
            }
            return false;
        }

        private static bool HasLetter(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsLetter(symbol)) return true;
            }
            return false;
        }

        private static bool HasDigit(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsDigit(symbol)) return true;
            }
            return false;
        }

        private static bool HasLetterOrDigit(string value)
        {
            foreach (char symbol in value)
            {
                if (char.IsLetterOrDigit(symbol)) return true;
            }
            return false;
        }

        private static int Count(string value, char symbol)
        {
            int count = 0;
            foreach (char item in value)
            {
                if (item == symbol) count++;
            }
            return count;
        }
    }
}
