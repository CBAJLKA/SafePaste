using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SafePaste.Bridge;

namespace SafePaste.Mcp
{
    internal sealed class FileEditor
    {
        private readonly FileTools files;
        private readonly LabelStore labels;
        private readonly Anonymizer anonymizer;
        private readonly IApprovalGate approval;

        internal FileEditor(FileTools files, LabelStore labels, Anonymizer anonymizer, IApprovalGate approval)
        { this.files = files; this.labels = labels; this.anonymizer = anonymizer; this.approval = approval; }

        internal string Edit(string path, string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText)) throw new ArgumentException("old_text не может быть пустым.");
            string full = files.CheckPath(path);
            byte[] before = File.ReadAllBytes(full);
            if (before.Length > 2097152) throw new IOException("Файл больше 2 МБ.");
            Encoding encoding; byte[] bom;
            string raw = Decode(before, out encoding, out bom);
            string safe = anonymizer.Anonymize(raw);
            int start = safe.IndexOf(oldText, StringComparison.Ordinal);
            if (start < 0 || safe.IndexOf(oldText, start + 1, StringComparison.Ordinal) >= 0)
                throw new ArgumentException("old_text должен встречаться в обезличенном файле ровно один раз.");
            int[] boundaries = BuildBoundaries(safe, raw);
            if (boundaries[start] < 0 || boundaries[start + oldText.Length] < 0)
                throw new ArgumentException("Граница правки попадает внутрь метки.");
            string replacement = Expand(newText, oldText);
            replacement = MatchNewlines(replacement, raw);
            int rawStart = boundaries[start], rawEnd = boundaries[start + oldText.Length];
            string oldRaw = raw.Substring(rawStart, rawEnd - rawStart);
            string updated = raw.Substring(0, rawStart) + replacement + raw.Substring(rawEnd);
            string detail = "Файл: " + full + "\n\nБыло:\n" + oldRaw + "\n\nСтало:\n" + replacement;
            if (!approval.Approve("sp_edit", "Правка файла", detail)) return "Правка отклонена пользователем.";
            Commit(full, before, updated, encoding, bom);
            return anonymizer.Anonymize("Файл изменён: " + full);
        }

        internal string Write(string path, string text)
        {
            string full = files.CheckPath(path);
            byte[] before = File.Exists(full) ? File.ReadAllBytes(full) : null;
            if (before != null && before.Length > 2097152) throw new IOException("Файл больше 2 МБ.");
            Encoding encoding = new UTF8Encoding(false, true); byte[] bom = new byte[0];
            string original = before == null ? "" : Decode(before, out encoding, out bom);
            string updated = MatchNewlines(Expand(text, ""), original);
            string detail = "Файл: " + full + "\n\nПрежнее содержимое:\n" + original
                + "\n\nНовое содержимое:\n" + updated;
            if (!approval.Approve("sp_write", "Запись файла", detail)) return "Запись отклонена пользователем.";
            Commit(full, before, updated, encoding, bom);
            return anonymizer.Anonymize("Файл записан: " + full);
        }

        private string Expand(string input, string oldText)
        {
            if (input == null) throw new ArgumentException("Не указан текст правки.");
            Dictionary<string, int> oldSecrets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match match in Labels.Placeholder.Matches(oldText))
            {
                string value; bool secret;
                if (labels.TryResolve(match.Value, out value, out secret) && secret)
                { int count; oldSecrets.TryGetValue(match.Value, out count); oldSecrets[match.Value] = count + 1; }
            }
            string expanded = Labels.Placeholder.Replace(input, delegate(Match match)
            {
                string value; bool secret;
                if (!labels.TryResolve(match.Value, out value, out secret))
                    throw new ArgumentException("Неизвестная метка " + match.Value + ".");
                if (secret)
                {
                    int count;
                    if (!oldSecrets.TryGetValue(match.Value, out count) || count == 0)
                        throw new ArgumentException("Метка секрета может только сохраняться в правке.");
                    oldSecrets[match.Value] = count - 1;
                }
                return value;
            });
            labels.Flush();
            return Labels.Unescape(expanded);
        }

        private int[] BuildBoundaries(string safe, string raw)
        {
            int[] map = Enumerable.Repeat(-1, safe.Length + 1).ToArray();
            int si = 0, ri = 0;
            while (si < safe.Length)
            {
                map[si] = ri;
                Match label = Labels.Placeholder.Match(safe, si);
                if (label.Success && label.Index == si)
                {
                    string value; bool secret;
                    if (!labels.TryResolve(label.Value, out value, out secret)
                        || ri + value.Length > raw.Length || string.CompareOrdinal(raw, ri, value, 0, value.Length) != 0)
                        throw new ArgumentException("Не удалось сопоставить метки с исходным файлом.");
                    si += label.Length; ri += value.Length; map[si] = ri; continue;
                }
                Match escaped = Regex.Match(safe.Substring(si), @"^\[~([A-Z][A-Z0-9_]*?_\d{1,6})\]");
                if (escaped.Success)
                {
                    string literal = "[" + escaped.Groups[1].Value + "]";
                    if (ri + literal.Length > raw.Length || string.CompareOrdinal(raw, ri, literal, 0, literal.Length) != 0)
                        throw new ArgumentException("Не удалось сопоставить буквальную метку.");
                    si += escaped.Length; ri += literal.Length; map[si] = ri; continue;
                }
                if (ri >= raw.Length || safe[si] != raw[ri])
                    throw new ArgumentException("Правка этого файла требует ручной проверки: отображение отличается от исходника.");
                si++; ri++;
            }
            if (ri != raw.Length) throw new ArgumentException("Не удалось сопоставить конец файла.");
            map[si] = ri;
            return map;
        }

        private static string MatchNewlines(string text, string original)
        {
            if (original.IndexOf("\r\n", StringComparison.Ordinal) >= 0)
                return Regex.Replace(text, "\r?\n", "\r\n");
            if (original.IndexOf('\r') >= 0 && original.IndexOf('\n') < 0)
                return text.Replace("\r\n", "\n").Replace('\n', '\r');
            return text;
        }

        private static string Decode(byte[] bytes, out Encoding encoding, out byte[] bom)
        {
            int offset = 0; bom = new byte[0];
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            { encoding = new UTF8Encoding(false, true); offset = 3; bom = new byte[] { 0xEF, 0xBB, 0xBF }; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { encoding = new UnicodeEncoding(false, false, true); offset = 2; bom = new byte[] { 0xFF, 0xFE }; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { encoding = new UnicodeEncoding(true, false, true); offset = 2; bom = new byte[] { 0xFE, 0xFF }; }
            else
            {
                if (bytes.Take(Math.Min(bytes.Length, 8192)).Any(x => x == 0)) throw new IOException("Двоичный файл нельзя править.");
                try { new UTF8Encoding(false, true).GetString(bytes); encoding = new UTF8Encoding(false, true); }
                catch (DecoderFallbackException) { encoding = Encoding.Default; }
            }
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }

        private void Commit(string path, byte[] before, string updated, Encoding encoding, byte[] bom)
        {
            files.CheckPath(path);
            byte[] now = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if ((before == null) != (now == null) || (before != null && !before.SequenceEqual(now)))
                throw new IOException("Файл изменился после подтверждения. Правка отменена.");
            Encoding strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            byte[] body = strict.GetBytes(updated);
            if (body.Length > 2097152) throw new IOException("Новый файл больше 2 МБ.");
            byte[] bytes = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (before == null) File.Move(temporary, path);
                else File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
