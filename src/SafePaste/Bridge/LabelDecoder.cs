using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SafePaste.Storage;

namespace SafePaste.Bridge
{
    public enum DecodedKind
    {
        /// <summary>Метка заменена реальным значением.</summary>
        Restored,
        /// <summary>Такой метки нет ни в правилах, ни среди запомненных.</summary>
        Unknown,
        /// <summary>Метка пароля, токена или ключа: их значения не сохраняются.</summary>
        Secret
    }

    /// <summary>Одна метка в расшифрованном тексте: где она теперь стоит и что с ней стало.</summary>
    public sealed class DecodedSpan
    {
        /// <summary>Место в расшифрованном тексте.</summary>
        public int Start;
        public int Length;
        /// <summary>Как было в тексте: [HOST_1], [SPLIT:HOST_1].</summary>
        public string Label;
        /// <summary>Реальное значение или null, если расшифровать не удалось.</summary>
        public string Value;
        public DecodedKind Kind;
        /// <summary>Номер закреплён в правилах, а не просто запомнен.</summary>
        public bool Pinned;
        /// <summary>null, SPLIT или ENCODED: сторож моста так помечает значение по частям и в кодировке.</summary>
        public string Form;

        public string Type
        {
            get
            {
                string label = Label.Trim('[', ']');
                int colon = label.IndexOf(':');
                if (colon >= 0) label = label.Substring(colon + 1);
                int underscore = label.LastIndexOf('_');
                return underscore > 0 ? label.Substring(0, underscore) : label;
            }
        }
    }

    public sealed class DecodedText
    {
        public string Text = string.Empty;
        public readonly List<DecodedSpan> Spans = new List<DecodedSpan>();

        public int Restored
        {
            get { return Spans.FindAll(delegate(DecodedSpan span) { return span.Kind == DecodedKind.Restored; }).Count; }
        }

        public int Missing
        {
            get { return Spans.Count - Restored; }
        }
    }

    /// <summary>
    /// Расшифровка: метки SafePaste в тексте (например, в ответе ИИ) заменяются реальными значениями.
    /// Закреплённые номера важнее запомненных: за таким номером другое значение не бывает.
    /// Секреты не расшифровываются, SafePaste их не сохраняет. [~HOST_1] значит «так было написано»
    /// и становится текстом [HOST_1]. Реальные значения только показываются, в файлы не пишутся.
    /// </summary>
    public static class LabelDecoder
    {
        private static readonly Regex Token = new Regex(
            @"\[(?:(?<form>SPLIT|ENCODED):)?(?<tilde>~*)(?<label>[A-Z][A-Z0-9_]*?_\d{1,6})\]", RegexOptions.CultureInvariant);

        /// <summary>Есть ли в тексте хоть одна метка.</summary>
        public static bool HasLabels(string text)
        {
            return !string.IsNullOrEmpty(text) && Token.IsMatch(text);
        }

        /// <summary>Расшифровка по правилам и запомненным меткам на диске.</summary>
        public static DecodedText Decode(string text)
        {
            SafePasteDatabase database = SafePasteDatabase.Load();
            LabelStore store = LabelStore.OpenIfExists();
            DecodedText result = Decode(text, database, store);
            if (store != null)
            {
                // Расшифровка продлевает жизнь меткам, как и любое их использование.
                store.Flush();
            }
            return result;
        }

        public static DecodedText Decode(string text, SafePasteDatabase database, LabelStore store)
        {
            text = text ?? string.Empty;
            Dictionary<string, string> pinned = new Dictionary<string, string>(StringComparer.Ordinal);
            if (database != null)
            {
                foreach (ReservedPlaceholder reservation in database.Reservations)
                {
                    pinned[reservation.Placeholder] = reservation.Value;
                }
            }
            DecodedText result = new DecodedText();
            StringBuilder builder = new StringBuilder(text.Length);
            int position = 0;
            foreach (Match match in Token.Matches(text))
            {
                builder.Append(text, position, match.Index - position);
                position = match.Index + match.Length;
                string form = match.Groups["form"].Success ? match.Groups["form"].Value : null;
                string tilde = match.Groups["tilde"].Value;
                string label = "[" + match.Groups["label"].Value + "]";
                if (form == null && tilde.Length > 0)
                {
                    builder.Append("[" + tilde.Substring(1) + match.Groups["label"].Value + "]");
                    continue;
                }
                DecodedSpan span = new DecodedSpan();
                span.Label = match.Value;
                span.Form = form;
                string value;
                bool secret = false;
                if (pinned.TryGetValue(label, out value))
                {
                    span.Pinned = true;
                }
                else if (store == null || !store.TryResolve(label, out value, out secret) || secret)
                {
                    value = null;
                }
                span.Start = builder.Length;
                if (value != null)
                {
                    span.Kind = DecodedKind.Restored;
                    span.Value = value;
                    builder.Append(value);
                    span.Length = value.Length;
                }
                else
                {
                    string type = span.Type;
                    span.Kind = secret || type == "SECRET" || type == "TOKEN" || type == "PRIVATE_KEY"
                        ? DecodedKind.Secret : DecodedKind.Unknown;
                    builder.Append(match.Value);
                    span.Length = match.Length;
                }
                result.Spans.Add(span);
            }
            builder.Append(text, position, text.Length - position);
            result.Text = builder.ToString();
            return result;
        }
    }
}
