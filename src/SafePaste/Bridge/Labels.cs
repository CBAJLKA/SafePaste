using System;
using System.Text.RegularExpressions;

namespace SafePaste.Bridge
{
    public static class Labels
    {
        public static readonly Regex Placeholder = new Regex(@"\[([A-Z][A-Z0-9_]*?)_(\d{1,6})\]", RegexOptions.CultureInvariant);
        private static readonly Regex Literal = new Regex(@"\[(~*)([A-Z][A-Z0-9_]*?_\d{1,6})\]", RegexOptions.CultureInvariant);

        public static string EscapeRaw(string text) { return Literal.Replace(text ?? string.Empty, "[~$1$2]"); }
        public static string Unescape(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\[~(~*)([A-Z][A-Z0-9_]*?_\d{1,6})\]", "[$1$2]");
        }

        public static string ResolveSimple(string text, LabelStore store)
        {
            string result = Placeholder.Replace(text ?? string.Empty, delegate(Match match)
            {
                string value; bool secret;
                if (!store.TryResolve(match.Value, out value, out secret))
                    throw new ArgumentException("Неизвестная метка " + match.Value + ".");
                if (secret) throw new ArgumentException("Метка секрета не подставляется: " + match.Value + ".");
                return value;
            });
            store.Flush();
            return Unescape(result);
        }
    }
}
