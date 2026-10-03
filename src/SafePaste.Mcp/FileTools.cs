using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SafePaste.Bridge;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    internal sealed class FileTools
    {
        private string[] roots = new string[0];
        private string rootsText;
        private readonly LabelStore labels;
        private readonly Anonymizer anonymizer;
        private readonly List<ReadCache> cache = new List<ReadCache>();
        private sealed class ReadCache
        {
            internal string Path;
            internal long Length;
            internal DateTime Stamp;
            internal DateTime DatabaseStamp;
            internal int Version;
            internal string Text;
        }
        internal string CurrentDirectory;
        internal string RootsDescription { get { return roots.Length == 0 ? "Корни не заданы: доступны все файлы пользователя." : string.Join("; ", roots); } }
        internal string RootsArgument { get { return string.Join(";", roots); } }

        internal FileTools(string rootText, LabelStore labels, Anonymizer anonymizer) : this(rootText, labels, anonymizer, null) { }

        /// <summary>startDirectory: рабочая папка агента, если она внутри разрешённых папок.</summary>
        internal FileTools(string rootText, LabelStore labels, Anonymizer anonymizer, string startDirectory)
        {
            this.labels = labels; this.anonymizer = anonymizer;
            SetRoots(rootText);
            CurrentDirectory = DefaultDirectory;
            if (!string.IsNullOrEmpty(startDirectory))
            {
                try
                {
                    string start = Path.GetFullPath(startDirectory);
                    if (Directory.Exists(start) && FindRoot(start) != null) CurrentDirectory = start;
                }
                catch (Exception) { }
            }
        }

        private string DefaultDirectory
        {
            get { return roots.Length == 0 ? Environment.CurrentDirectory : roots[0]; }
        }

        /// <summary>Папки меняются в окне моста и действуют сразу, в том числе для уже подключённых агентов.</summary>
        internal void UpdateRoots(string rootText)
        {
            if (string.Equals(rootText ?? "", rootsText, StringComparison.Ordinal)) return;
            SetRoots(rootText);
            if (FindRoot(CurrentDirectory) == null) CurrentDirectory = DefaultDirectory;
        }

        private void SetRoots(string rootText)
        {
            List<string> allowed = new List<string>();
            if (!string.IsNullOrEmpty(rootText)) foreach (string root in rootText.Split(';'))
                if (!string.IsNullOrWhiteSpace(root))
                {
                    string full = Path.GetFullPath(root.Trim());
                    allowed.Add(full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
                        ? full : full.TrimEnd(Path.DirectorySeparatorChar));
                }
            roots = allowed.ToArray();
            rootsText = rootText ?? "";
        }

        /// <summary>Разрешённая папка, внутри которой лежит путь; при пустом списке подходит любой путь.</summary>
        private string FindRoot(string full)
        {
            if (roots.Length == 0) return "";
            foreach (string root in roots)
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                        ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return root;
            return null;
        }

        internal string CheckPath(string supplied)
        {
            string resolved = Labels.ResolveSimple(string.IsNullOrEmpty(supplied) ? CurrentDirectory : supplied, labels);
            string full = Path.GetFullPath(Path.IsPathRooted(resolved) ? resolved : Path.Combine(CurrentDirectory, resolved));
            if (roots.Length == 0) return full;
            foreach (string root in roots)
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                        ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    string current = root;
                    string relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                    foreach (string part in relative.Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        current = Path.Combine(current, part);
                        if ((File.Exists(current) || Directory.Exists(current))
                            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                            throw new UnauthorizedAccessException("Переход через ссылку вне корней запрещён.");
                    }
                    return full;
                }
            throw new UnauthorizedAccessException("Путь вне разрешённых корней.");
        }

        internal string List(string path, int depth, string pattern)
        {
            if (depth < 1 || depth > 3) throw new ArgumentException("Глубина от 1 до 3.");
            string root = CheckPath(path);
            StringBuilder output = new StringBuilder();
            Queue<KeyValuePair<string, int>> pending = new Queue<KeyValuePair<string, int>>();
            pending.Enqueue(new KeyValuePair<string, int>(root, 1));
            int count = 0;
            while (pending.Count > 0 && count < 500)
            {
                KeyValuePair<string, int> next = pending.Dequeue();
                foreach (string dir in Directory.GetDirectories(next.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (count++ >= 500) break;
                    output.Append("папка ").Append(dir).Append("\\\n");
                    if (next.Value < depth && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
                        pending.Enqueue(new KeyValuePair<string, int>(dir, next.Value + 1));
                }
                foreach (string file in Directory.GetFiles(next.Key, string.IsNullOrEmpty(pattern) ? "*" : pattern).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (count++ >= 500) break;
                    FileInfo info = new FileInfo(file);
                    output.Append("файл ").Append(file).Append(' ').Append(info.Length.ToString(CultureInfo.InvariantCulture))
                        .Append(' ').Append(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
                }
            }
            if (count >= 500) output.Append("Список ограничен 500 строками.\n");
            return anonymizer.Anonymize(output.ToString());
        }

        internal string Read(string path, int offset, int limit)
        {
            if (offset < 1 || limit < 1 || limit > 2000) throw new ArgumentException("Неверный диапазон строк.");
            string full = CheckPath(path);
            FileInfo info = new FileInfo(full);
            if (info.Length > 2097152) throw new IOException("Файл больше 2 МБ. Используйте sp_find или Select-String.");
            labels.Snapshot();
            DateTime dbStamp = File.Exists(Paths.DatabaseFile) ? File.GetLastWriteTimeUtc(Paths.DatabaseFile) : DateTime.MinValue;
            ReadCache cached = null;
            foreach (ReadCache entry in cache)
                if (entry.Path.Equals(full, StringComparison.OrdinalIgnoreCase) && entry.Length == info.Length
                    && entry.Stamp == info.LastWriteTimeUtc && entry.DatabaseStamp == dbStamp && entry.Version == labels.Version)
                { cached = entry; break; }
            string safe;
            if (cached != null) safe = cached.Text;
            else
            {
                byte[] bytes = File.ReadAllBytes(full);
                safe = anonymizer.Anonymize(Decode(bytes));
                cached = new ReadCache { Path = full, Length = info.Length, Stamp = info.LastWriteTimeUtc,
                    DatabaseStamp = dbStamp, Version = labels.Version, Text = safe };
                cache.Insert(0, cached);
                if (cache.Count > 4) cache.RemoveAt(cache.Count - 1);
            }
            string[] lines = Regex.Split(safe, "\r\n|\n|\r");
            StringBuilder result = new StringBuilder();
            result.Append(anonymizer.Anonymize(full)).Append(": строк ").Append(lines.Length.ToString(CultureInfo.InvariantCulture))
                .Append(", диапазон ").Append(offset.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(Math.Min(lines.Length, offset + limit - 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = offset - 1; i < lines.Length && i < offset + limit - 1; i++)
            {
                string line = lines[i];
                if (line.Length > 2000) line = CutLine(line, 2000) + " [строка обрезана]";
                result.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("| ").Append(line).Append('\n');
            }
            if (offset + limit - 1 < lines.Length) result.Append("Продолжение: offset=").Append((offset + limit).ToString(CultureInfo.InvariantCulture));
            return result.ToString();
        }

        private static string CutLine(string line, int limit)
        {
            int end = limit;
            int open = line.LastIndexOf('[', end - 1);
            int close = line.LastIndexOf(']', end - 1);
            if (open > close) end = open;
            return line.Substring(0, end);
        }

        private static string Decode(byte[] bytes)
        {
            int offset = 0; Encoding encoding;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = new UTF8Encoding(false, true); offset = 3; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = Encoding.Unicode; offset = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = Encoding.BigEndianUnicode; offset = 2; }
            else
            {
                for (int i = 0; i < Math.Min(bytes.Length, 8192); i++) if (bytes[i] == 0) throw new IOException("Двоичный файл не читается как текст.");
                try { return new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
            }
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }

        internal string Find(string pattern, string path, string glob, bool regex, bool caseSensitive, int max)
        {
            if (max < 1 || max > 1000) throw new ArgumentException("max_results от 1 до 1000.");
            string needle = Labels.ResolveSimple(pattern, labels);
            Regex expression = regex ? new Regex(needle, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) : null;
            string root = CheckPath(path);
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            int files = 0, matches = 0;
            StringBuilder raw = new StringBuilder();
            Queue<string> folders = new Queue<string>();
            folders.Enqueue(root);
            while (folders.Count > 0 && files < 20000 && DateTime.UtcNow <= until && matches < max)
            {
                string folder = folders.Dequeue();
                foreach (string dir in Directory.GetDirectories(folder))
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) folders.Enqueue(dir);
                foreach (string file in Directory.GetFiles(folder, string.IsNullOrEmpty(glob) ? "*" : glob))
                {
                if (++files > 20000 || DateTime.UtcNow > until || matches >= max) break;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                if (new FileInfo(file).Length > 20971520) continue;
                string content;
                try { content = Decode(File.ReadAllBytes(file)); }
                catch (IOException) { continue; }
                string[] lines = Regex.Split(content, "\r\n|\n|\r");
                for (int i = 0; i < lines.Length && matches < max; i++)
                {
                    bool found = regex ? expression.IsMatch(lines[i]) : lines[i].IndexOf(needle,
                        caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;
                    if (found) { raw.Append(file).Append(':').Append(i + 1).Append(": ").Append(lines[i]).Append('\n'); matches++; }
                }
                }
            }
            return anonymizer.Anonymize(raw.Length == 0 ? "Совпадений нет." : raw.ToString());
        }
    }
}
