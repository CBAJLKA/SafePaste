using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SafePaste.Detecting;

namespace SafePaste.Tests
{
    /// <summary>
    /// Корпус секретов из tests\corpus\*.txt. Блок (строки до пустой строки) проверяется отдельно,
    /// затем все блоки файла одним текстом, как если бы файл вставили целиком.
    /// Разметка: ⟦секрет⟧ скрыт целиком даже в лёгком режиме быстрой вставки; ⟪идентификатор⟫ скрыт
    /// в обычном режиме окна проверки; ⟨слово⟩ остаётся в тексте. Строки с «##» в начале: пояснения
    /// к корпусу, в текст они не попадают. Секрет вне ⟦⟧ считается ошибкой: так ловятся ложные срабатывания.
    /// </summary>
    public static partial class TestProgram
    {
        private const char SecretOpen = '\u27E6';
        private const char SecretClose = '\u27E7';
        private const char IdOpen = '\u27EA';
        private const char IdClose = '\u27EB';
        private const char KeepOpen = '\u27E8';
        private const char KeepClose = '\u27E9';

        private sealed class CorpusSpan
        {
            internal int Start;
            internal int End;
            internal char Kind;
        }

        private sealed class CorpusBlock
        {
            internal string File;
            internal int Line;
            internal string Text;
            internal readonly List<CorpusSpan> Spans = new List<CorpusSpan>();
        }

        private static void CorpusTests()
        {
            Section("Корпус секретов");
            string folder = FindCorpus();
            Check("корпус найден", folder != null, "tests\\corpus");
            if (folder == null)
            {
                return;
            }
            string[] files = Directory.GetFiles(folder, "*.txt");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            int blocks = 0, secrets = 0, ids = 0, keeps = 0;
            int failuresBefore = Failures.Count;
            foreach (string file in files)
            {
                List<CorpusBlock> parsed = ParseCorpus(file);
                foreach (CorpusBlock block in parsed)
                {
                    blocks++;
                    foreach (CorpusSpan span in block.Spans)
                    {
                        if (span.Kind == 'S') secrets++;
                        else if (span.Kind == 'I') ids++;
                        else keeps++;
                    }
                    VerifyBlock(block, true);
                }
                VerifyBlock(Join(file, parsed), false);
            }
            int generated = VerifyGeneratedKeys();
            Console.WriteLine("  блоков: " + blocks + ", секретов: " + secrets + ", идентификаторов: " + ids
                + ", оставить: " + keeps + ", сгенерированных секретов: " + generated
                + ", ошибок: " + (Failures.Count - failuresBefore));
        }

        private static int VerifyGeneratedKeys()
        {
            // Комбинации контекстных имён, форматов конфигов и значений. Их легко расширять,
            // а каждый сгенерированный случай имеет точный ожидаемый диапазон секрета.
            string[] keys = {
                "password", "passwd", "passphrase", "pwd", "secret", "token", "api_key", "access_key",
                "account_key", "private_key", "client_secret", "auth_key", "credentials", "пароль", "токен",
                "psk", "pre_shared_key", "shared_access_key", "subscription_key", "functions_key",
                "encryption_key", "signing_key", "master_key", "app_key", "license_key", "hmac_key",
                "session_key", "storage_key", "webhook_key", "key_data", "community", "pin", "cvv",
                "otp", "recovery_code", "backup_code", "mfa_backup_code", "seed_phrase", "mnemonic",
                "recovery_phrase"
            };
            string[] values = { "Fake$Alpha2026!", "Fake#Beta98765", "FakeGamma_2026", "FakeDelta-112233" };
            int count = 0;
            foreach (string key in keys)
            {
                StringBuilder marked = new StringBuilder();
                foreach (string value in values)
                {
                    string secret = SecretOpen + value + SecretClose;
                    marked.Append(key).Append('=').Append(secret).Append('\n');
                    marked.Append(key).Append(": ").Append(secret).Append('\n');
                    marked.Append(key).Append(" = \"").Append(secret).Append("\"\n");
                    marked.Append(key).Append(": '").Append(secret).Append("'\n");
                    marked.Append('"').Append(key).Append("\": \"").Append(secret).Append("\"\n");
                    count += 5;
                }
                VerifyBlock(Strip("generated-key-matrix", 1, marked.ToString().TrimEnd()), true);
            }
            return count;
        }

        private static string FindCorpus()
        {
            DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, Path.Combine("tests", "corpus"));
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
            return null;
        }

        private static List<CorpusBlock> ParseCorpus(string file)
        {
            List<CorpusBlock> blocks = new List<CorpusBlock>();
            string[] lines = File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
            List<string> current = new List<string>();
            int first = 0;
            for (int index = 0; index <= lines.Length; index++)
            {
                string line = index < lines.Length ? lines[index] : string.Empty;
                if (line.StartsWith("##", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.Trim().Length == 0)
                {
                    if (current.Count > 0)
                    {
                        blocks.Add(Strip(Path.GetFileName(file), first + 1, string.Join("\n", current.ToArray())));
                        current.Clear();
                    }
                    continue;
                }
                if (current.Count == 0)
                {
                    first = index;
                }
                current.Add(line);
            }
            return blocks;
        }

        /// <summary>Убирает разметку и запоминает, где стояли размеченные значения.</summary>
        private static CorpusBlock Strip(string file, int line, string marked)
        {
            CorpusBlock block = new CorpusBlock();
            block.File = file;
            block.Line = line;
            StringBuilder text = new StringBuilder(marked.Length);
            Stack<CorpusSpan> open = new Stack<CorpusSpan>();
            foreach (char symbol in marked)
            {
                char kind = symbol == SecretOpen ? 'S' : symbol == IdOpen ? 'I' : symbol == KeepOpen ? 'K' : '\0';
                if (kind != '\0')
                {
                    CorpusSpan span = new CorpusSpan();
                    span.Start = text.Length;
                    span.Kind = kind;
                    open.Push(span);
                    continue;
                }
                if (symbol == SecretClose || symbol == IdClose || symbol == KeepClose)
                {
                    if (open.Count == 0)
                    {
                        throw new InvalidDataException(file + ":" + line + ": лишняя закрывающая скобка разметки");
                    }
                    CorpusSpan span = open.Pop();
                    span.End = text.Length;
                    block.Spans.Add(span);
                    continue;
                }
                text.Append(symbol);
            }
            if (open.Count > 0)
            {
                throw new InvalidDataException(file + ":" + line + ": разметка не закрыта");
            }
            block.Text = text.ToString();
            return block;
        }

        private static CorpusBlock Join(string file, List<CorpusBlock> blocks)
        {
            CorpusBlock all = new CorpusBlock();
            all.File = Path.GetFileName(file);
            all.Line = 0;
            StringBuilder text = new StringBuilder();
            foreach (CorpusBlock block in blocks)
            {
                if (text.Length > 0)
                {
                    text.Append("\n\n");
                }
                int offset = text.Length;
                text.Append(block.Text);
                foreach (CorpusSpan span in block.Spans)
                {
                    CorpusSpan moved = new CorpusSpan();
                    moved.Start = span.Start + offset;
                    moved.End = span.End + offset;
                    moved.Kind = span.Kind;
                    all.Spans.Add(moved);
                }
            }
            all.Text = text.ToString();
            return all;
        }

        /// <summary>
        /// Секреты проверяются в лёгком режиме быстрой вставки: там скрывается только обязательное.
        /// Идентификаторы и «оставить» проверяются в обычном режиме окна проверки. Целиком файл
        /// проверяется на секреты и на «оставить»: повторы из соседних блоков не должны их задеть.
        /// </summary>
        private static void VerifyBlock(CorpusBlock block, bool single)
        {
            string where = block.File + (single ? ":" + block.Line : " целиком");
            List<Detection> light = Detector.Scan(block.Text, null, ControlMode.Light, true, true);
            List<Detection> review = Detector.Scan(block.Text, null, ControlMode.Balanced, false, true);
            bool[] hidden = Covered(block.Text.Length, light);
            bool[] shown = Covered(block.Text.Length, review);
            foreach (CorpusSpan span in block.Spans)
            {
                string value = block.Text.Substring(span.Start, span.End - span.Start);
                if (span.Kind == 'S')
                {
                    string open = Uncovered(block.Text, hidden, span);
                    Check(where + ": секрет «" + value + "» скрыт", open == null, open == null ? "" : "открыто: " + open);
                }
                else if (span.Kind == 'I' && single)
                {
                    string open = Uncovered(block.Text, shown, span);
                    Check(where + ": идентификатор «" + value + "» скрыт", open == null, open == null ? "" : "открыто: " + open);
                }
                else if (span.Kind == 'K')
                {
                    bool touched = false;
                    for (int index = span.Start; index < span.End; index++)
                    {
                        touched |= shown[index];
                    }
                    Check(where + ": «" + value + "» остался в тексте", !touched, Describe(review, span));
                }
            }
            if (!single)
            {
                return;
            }
            foreach (Detection detection in review)
            {
                if (!detection.Enabled || !detection.Locked)
                {
                    continue;
                }
                bool expected = false;
                foreach (CorpusSpan span in block.Spans)
                {
                    expected |= span.Kind == 'S' && detection.Start < span.End && span.Start < detection.End;
                }
                Check(where + ": лишний секрет", expected,
                    "«" + detection.Value + "» (" + (detection.Reason ?? detection.Source) + ")");
            }
        }

        private static bool[] Covered(int length, List<Detection> detections)
        {
            bool[] covered = new bool[length];
            foreach (Detection detection in detections)
            {
                if (!detection.Enabled)
                {
                    continue;
                }
                for (int index = detection.Start; index < detection.End && index < length; index++)
                {
                    covered[index] = true;
                }
            }
            return covered;
        }

        /// <summary>Открытая часть размеченного значения или null, если оно скрыто целиком.</summary>
        private static string Uncovered(string text, bool[] covered, CorpusSpan span)
        {
            StringBuilder open = new StringBuilder();
            for (int index = span.Start; index < span.End; index++)
            {
                if (!covered[index] && !char.IsWhiteSpace(text[index]))
                {
                    open.Append(text[index]);
                }
            }
            return open.Length == 0 ? null : open.ToString();
        }

        private static string Describe(List<Detection> detections, CorpusSpan span)
        {
            foreach (Detection detection in detections)
            {
                if (detection.Enabled && detection.Start < span.End && span.Start < detection.End)
                {
                    return detection.Type + " «" + detection.Value + "» (" + (detection.Reason ?? detection.Source) + ")";
                }
            }
            return "";
        }
    }
}
