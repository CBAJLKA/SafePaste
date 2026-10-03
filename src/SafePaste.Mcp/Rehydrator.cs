using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Management.Automation.Language;
using SafePaste.Bridge;
namespace SafePaste.Mcp
{
    internal sealed class Rehydrator
    {
        private static readonly Regex Label = new Regex(@"\[([A-Z][A-Z0-9_]*?)_(\d{1,6})\]", RegexOptions.CultureInvariant);
        private static readonly Regex EscapedLabel = new Regex(@"\[~(~*)([A-Z][A-Z0-9_]*?_\d{1,6})\]", RegexOptions.CultureInvariant);
        private readonly LabelStore vault;

        internal Rehydrator(LabelStore vault)
        {
            this.vault = vault;
        }

        internal bool TryRehydrate(string command, out string result, out string problem)
        {
            result = null;
            problem = null;
            if (!Label.IsMatch(command))
            {
                result = EscapedLabel.Replace(command, "[$1$2]");
                return true;
            }
            Token[] tokens;
            ParseError[] errors;
            Parser.ParseInput(command, out tokens, out errors);

            List<Token> touched = new List<Token>();
            foreach (Match match in Label.Matches(command))
            {
                Token token = FindToken(tokens, match.Index, match.Length);
                if (token == null)
                {
                    problem = "Метка " + match.Value + " стоит вне аргумента команды.";
                    return false;
                }
                if (token.Kind != TokenKind.Generic && token.Kind != TokenKind.StringLiteral
                    && token.Kind != TokenKind.StringExpandable)
                {
                    problem = "Метка " + match.Value + " стоит в недопустимом месте (" + token.Kind + ").";
                    return false;
                }
                if (token.Kind == TokenKind.Generic && (token.Text.IndexOf('$') >= 0 || token.Text.IndexOf('`') >= 0))
                {
                    problem = "Слово с меткой содержит $ или `: возьмите его в кавычки.";
                    return false;
                }
                if (!touched.Contains(token))
                {
                    touched.Add(token);
                }
            }

            touched.Sort(delegate(Token left, Token right) { return right.Extent.StartOffset.CompareTo(left.Extent.StartOffset); });
            StringBuilder builder = new StringBuilder(command);
            foreach (Token token in touched)
            {
                string text = token.Extent.Text;
                string rewritten;
                if (!TrySubstitute(text, token.Kind, out rewritten, out problem))
                {
                    return false;
                }
                builder.Remove(token.Extent.StartOffset, text.Length);
                builder.Insert(token.Extent.StartOffset, rewritten);
            }
            result = EscapedLabel.Replace(builder.ToString(), "[$1$2]");
            return true;
        }

        private bool TrySubstitute(string text, TokenKind kind, out string rewritten, out string problem)
        {
            rewritten = null;
            problem = null;
            StringBuilder builder = new StringBuilder();
            int position = 0;
            foreach (Match match in Label.Matches(text))
            {
                string value;
                bool secret;
                if (!vault.TryResolve(match.Value, out value, out secret))
                {
                    problem = "Неизвестная метка " + match.Value + ". Используйте только метки из ответов этого сервера.";
                    return false;
                }
                if (secret)
                {
                    problem = "Метку " + match.Value + " нельзя подставить в команду: это секрет.";
                    return false;
                }
                builder.Append(text, position, match.Index - position);
                builder.Append(kind == TokenKind.StringExpandable ? EscapeDouble(value) : EscapeSingle(value));
                position = match.Index + match.Length;
            }
            builder.Append(text, position, text.Length - position);
            rewritten = kind == TokenKind.Generic ? "'" + builder.ToString() + "'" : builder.ToString();
            return true;
        }

        private static string EscapeSingle(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length + 4);
            foreach (char symbol in value)
            {
                builder.Append(symbol);
                if (symbol == '\'' || symbol == (char)0x2018 || symbol == (char)0x2019 || symbol == (char)0x201A
                    || symbol == (char)0x201B)
                {
                    builder.Append(symbol);
                }
            }
            return builder.ToString();
        }

        private static string EscapeDouble(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length + 4);
            foreach (char symbol in value)
            {
                if (symbol == '`' || symbol == '$' || symbol == '"' || symbol == (char)0x201C || symbol == (char)0x201D
                    || symbol == (char)0x201E)
                {
                    builder.Append('`');
                }
                builder.Append(symbol);
            }
            return builder.ToString();
        }

        private static Token FindToken(Token[] tokens, int start, int length)
        {
            foreach (Token token in tokens)
            {
                if (token.Extent.StartOffset <= start && start + length <= token.Extent.EndOffset)
                {
                    return token;
                }
            }
            return null;
        }
    }

}
