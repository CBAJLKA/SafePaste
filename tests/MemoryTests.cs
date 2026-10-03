using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SafePaste.Bridge;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Tests
{
    public static partial class TestProgram
    {
        /// <summary>Поиск многих значений, запоминание меток при вставке и настройки для этого.</summary>
        private static void MemoryTests()
        {
            Section("Запоминание меток");
            MatcherTests();
            LabelMemoryTests();
            MemoryExpiryTests();
            MemorySettingsTests();
            MemoryPerformanceTest();
            DecoderTests();
        }

        // ---------------------------------------------------------------- расшифровка

        private static void DecoderTests()
        {
            Section("Расшифровка меток");
            string file = Paths.LabelsFile;
            if (File.Exists(file)) File.Delete(file);
            LabelStore store = new LabelStore(true);
            string host = store.Hide("web-prod", "HOST");
            string fqdn = store.Hide("fs01.corp.example", "FQDN");
            SafePasteDatabase database = new SafePasteDatabase();
            database.Reserve("IP", "10.44.7.219", 7);
            string text = "Проверь " + host + " и [IP_7], потом [HOST_9] и [SECRET_1]. Буквально [~HOST_1]. "
                + "Части [SPLIT:" + fqdn.Trim('[', ']') + "].";
            DecodedText decoded = LabelDecoder.Decode(text, database, new LabelStore(true));
            Check("метки заменены реальными значениями", decoded.Text.StartsWith("Проверь web-prod и 10.44.7.219, потом [HOST_9]"),
                decoded.Text);
            Check("экранированная метка становится текстом", decoded.Text.Contains("Буквально [HOST_1]."), decoded.Text);
            Check("отметка сторожа расшифровывается", decoded.Text.Contains("Части fs01.corp.example."), decoded.Text);
            Check("счётчики расшифровки", decoded.Restored == 3 && decoded.Missing == 2,
                decoded.Restored.ToString() + "/" + decoded.Missing.ToString());
            DecodedSpan pinned = decoded.Spans.Find(delegate(DecodedSpan span) { return span.Label == "[IP_7]"; });
            Check("закреплённый номер помечен", pinned != null && pinned.Pinned && pinned.Kind == DecodedKind.Restored
                && decoded.Text.Substring(pinned.Start, pinned.Length) == "10.44.7.219", "");
            DecodedSpan unknown = decoded.Spans.Find(delegate(DecodedSpan span) { return span.Label == "[HOST_9]"; });
            Check("неизвестная метка остаётся и помечена", unknown != null && unknown.Kind == DecodedKind.Unknown
                && decoded.Text.Substring(unknown.Start, unknown.Length) == "[HOST_9]" && unknown.Type == "HOST", "");
            DecodedSpan secret = decoded.Spans.Find(delegate(DecodedSpan span) { return span.Label == "[SECRET_1]"; });
            Check("секрет не расшифровывается", secret != null && secret.Kind == DecodedKind.Secret, "");
            DecodedSpan split = decoded.Spans.Find(delegate(DecodedSpan span) { return span.Form == "SPLIT"; });
            Check("отметка сторожа знает свой вид", split != null && split.Type == "FQDN", "");
            Check("текст без меток не меняется", LabelDecoder.Decode("Просто [x] текст [host_1]", database, null).Text
                == "Просто [x] текст [host_1]", "");
            Check("метки распознаются", LabelDecoder.HasLabels("см. [HOST_1]") && !LabelDecoder.HasLabels("см. [host]"), "");
            ReservedOverLabel();
            File.Delete(file);
        }

        /// <summary>Закрепление важнее запомненной метки с тем же номером.</summary>
        private static void ReservedOverLabel()
        {
            LabelStore store = new LabelStore(true);
            string label = store.Hide("other-node", "HOST");
            SafePasteDatabase pinned = new SafePasteDatabase();
            pinned.Reserve("HOST", "pinned-node", int.Parse(label.Substring(6, label.Length - 7), CultureInfo.InvariantCulture));
            DecodedText decoded = LabelDecoder.Decode(label, pinned, store);
            Check("закреплённый номер важнее запомненной метки", decoded.Text == "pinned-node", decoded.Text);
        }

        // ---------------------------------------------------------------- поиск многих значений

        private static void MatcherTests()
        {
            string[] values = { "SRV-DB01", "srv-db01-old", @"\\domain\folder\it\", "fs01.corp.local", "admin", "Иван Петров",
                "10.0.0.1", @"\it\tratata" };
            string text = @"SRV-DB01-old и srv-db01, путь \\domain\folder\it\tratata, fs01.corp.local.; ADMIN; "
                + "иван петров; 10.0.0.12 10.0.0.1 xadmin admin_ admin.";
            Check("автомат находит то же, что шаблоны значений", SameMatches(values, text), Describe(values, text));

            ValueMatcher matcher = new ValueMatcher();
            matcher.Add("Иван Петров");
            Check("повтор без учёта регистра не добавляется", matcher.Add("ИВАН ПЕТРОВ") == -1 && matcher.Count == 1, "");
            List<ValueMatch> found = matcher.Find("Звонил ИВАН ПЕТРОВ.");
            Check("регистр не важен, найдено исходное место", found.Count == 1 && found[0].Start == 7 && found[0].Length == 11, "");

            // Перебор на маленьком алфавите: много пересечений, общих начал и границ слова.
            Random random = new Random(20260926);
            const string alphabet = "aAb-_. 1\\я";
            int failures = 0;
            string sample = null;
            for (int round = 0; round < 400; round++)
            {
                List<string> set = new List<string>();
                int count = 1 + random.Next(8);
                for (int i = 0; i < count; i++)
                {
                    set.Add(RandomText(random, alphabet, 1 + random.Next(4)));
                }
                string haystack = RandomText(random, alphabet, 10 + random.Next(60));
                if (!SameMatches(set.ToArray(), haystack))
                {
                    failures++;
                    if (sample == null)
                    {
                        sample = Describe(set.ToArray(), haystack);
                    }
                }
            }
            Check("автомат совпадает с шаблонами на случайных строках", failures == 0, sample ?? "");
        }

        private static string RandomText(Random random, string alphabet, int length)
        {
            StringBuilder builder = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }
            return builder.ToString();
        }

        /// <summary>Прежний способ: одна регулярка, длинные значения первыми, граница слова на краях из букв и цифр.</summary>
        private static List<string> RegexMatches(string[] values, string text)
        {
            List<string> ordered = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                if (value.Trim().Length > 0 && seen.Add(value)) ordered.Add(value);
            }
            List<string> result = new List<string>();
            if (ordered.Count == 0) return result;
            ordered.Sort(delegate(string left, string right) { return right.Length.CompareTo(left.Length); });
            List<string> parts = new List<string>();
            foreach (string value in ordered)
            {
                string part = Regex.Escape(value);
                if (IsEdge(value[0])) part = @"(?<![\p{L}\p{N}_-])" + part;
                if (IsEdge(value[value.Length - 1])) part += @"(?![\p{L}\p{N}_-])";
                parts.Add(part);
            }
            Regex regex = new Regex("(?:" + string.Join("|", parts.ToArray()) + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (Match match = regex.Match(text); match.Success; match = match.NextMatch())
            {
                result.Add(match.Index.ToString(CultureInfo.InvariantCulture) + ":" + match.Length.ToString(CultureInfo.InvariantCulture));
            }
            return result;
        }

        private static bool IsEdge(char value)
        {
            return char.IsLetter(value) || char.IsNumber(value) || value == '_' || value == '-';
        }

        private static List<string> MatcherMatches(string[] values, string text)
        {
            ValueMatcher matcher = new ValueMatcher();
            foreach (string value in values)
            {
                if (value.Trim().Length > 0) matcher.Add(value);
            }
            List<string> result = new List<string>();
            foreach (ValueMatch match in matcher.Find(text))
            {
                result.Add(match.Start.ToString(CultureInfo.InvariantCulture) + ":" + match.Length.ToString(CultureInfo.InvariantCulture));
            }
            return result;
        }

        private static bool SameMatches(string[] values, string text)
        {
            return string.Join(",", RegexMatches(values, text).ToArray()) == string.Join(",", MatcherMatches(values, text).ToArray());
        }

        private static string Describe(string[] values, string text)
        {
            return "значения [" + string.Join("|", values) + "] текст [" + text + "] шаблоны "
                + string.Join(",", RegexMatches(values, text).ToArray()) + " автомат "
                + string.Join(",", MatcherMatches(values, text).ToArray());
        }

        // ---------------------------------------------------------------- запоминание при вставке

        private static void LabelMemoryTests()
        {
            string file = Paths.LabelsFile;
            if (File.Exists(file)) File.Delete(file);
            SafePasteDatabase database = new SafePasteDatabase();
            string first = "hostname: web-prod\r\nuser: kolya\r\npassword: Secret123!";
            List<Detection> found = Detector.Scan(first, database, ControlMode.Balanced, false);
            Detection user = null;
            foreach (Detection detection in found)
            {
                if (detection.Value == "kolya") user = detection;
            }
            Check("в тексте найдена учётная запись", user != null, "");
            if (user != null) user.Transient = true;

            ReplacementResult preview = LabelMemory.Preview(first, found, database);
            Check("предпросмотр ничего не пишет", !File.Exists(file), "");
            ReplacementResult kept = LabelMemory.Apply(first, found, database, false);
            Check("вставка без запоминания ничего не пишет", !File.Exists(file) && kept.Text == preview.Text, kept.Text);
            ReplacementResult pasted = LabelMemory.Apply(first, found, database, true);
            Check("вставка с запоминанием создаёт labels.dat", File.Exists(file), "");
            Check("вставка и предпросмотр дают одно и то же", pasted.Text == preview.Text, pasted.Text);
            List<LabelEntry> stored = LabelMemory.Snapshot();
            string host = null;
            foreach (LabelEntry entry in stored)
            {
                if (entry.Value == "web-prod") host = entry.Placeholder;
            }
            Check("скрытое значение запомнено с номером", host != null && pasted.Text.Contains(host), pasted.Text);
            Check("отмеченное без сохранения не запоминается",
                !stored.Exists(delegate(LabelEntry entry) { return entry.Value == "kolya"; }), "");
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            Check("пароль не попадает в файл меток", !json.Contains("Secret123!") && json.Contains("web-prod"), "");

            string second = "Сервис web-prod снова упал";
            SafePasteDatabase next = new SafePasteDatabase();
            LabelMemory.Load(next, new SafePasteSettings(), ControlMode.Balanced);
            Check("запомненное загружено для поиска", next.Remembered.Exists(delegate(LearnedValue value) { return value.Value == "web-prod"; }), "");
            string again = LabelMemory.Apply(second, Detector.Scan(second, next, ControlMode.Balanced, false), next, true).Text;
            Check("запомненное скрывается без подсказок тем же номером", again == "Сервис " + host + " снова упал", again);
            List<Detection> quick = Detector.Scan(second, next, ControlMode.Balanced, true);
            Check("запомненное скрывается и быстрой вставкой",
                quick.Exists(delegate(Detection detection) { return detection.Value == "web-prod"; }), "");

            SafePasteDatabase light = new SafePasteDatabase();
            LabelMemory.Load(light, new SafePasteSettings(), ControlMode.Light);
            Check("в лёгком режиме запомненное без подсказок не ищется", light.Remembered.Count == 0, "");
            SafePasteDatabase allowed = new SafePasteDatabase();
            allowed.AddAllowed("WEB-PROD");
            LabelMemory.Load(allowed, new SafePasteSettings(), ControlMode.Strict);
            Check("исключение важнее запомненного", allowed.Remembered.Count == 0, "");

            SafePasteDatabase rules = new SafePasteDatabase();
            rules.AddLearned("web-prod", "SERIAL");
            LabelMemory.Load(rules, new SafePasteSettings(), ControlMode.Balanced);
            Detection ruled = null;
            foreach (Detection detection in Detector.Scan(second, rules, ControlMode.Balanced, false))
            {
                if (detection.Value == "web-prod") ruled = detection;
            }
            Check("правило пользователя важнее запомненной метки", ruled != null && ruled.Type == "SERIAL"
                && ruled.Source == Detection.LearnedSource, ruled == null ? "нет находки" : ruled.Type);

            DateTime before = File.GetLastWriteTimeUtc(file);
            LabelMemory.Apply("hostname: other-host", Detector.Scan("hostname: other-host", next, ControlMode.Balanced, false), next, false);
            Check("выключенное запоминание не меняет файл", File.GetLastWriteTimeUtc(file) == before
                && !LabelMemory.Snapshot().Exists(delegate(LabelEntry entry) { return entry.Value == "other-host"; }), "");

            Check("исключение забывает метку", LabelMemory.Forget("WEB-PROD") == 1
                && !LabelMemory.Snapshot().Exists(delegate(LabelEntry entry) { return entry.Value == "web-prod"; }), "");
            LabelMemory.Apply("hostname: fs-backup", Detector.Scan("hostname: fs-backup", next, ControlMode.Balanced, false), next, true);
            SafePasteDatabase reserved = new SafePasteDatabase();
            reserved.Reserve("HOST", "pinned-host", 5);
            reserved.Save();
            Check("очистка забывает все запомненные метки", LabelMemory.Clear() >= 1 && LabelMemory.Snapshot().Count == 0, "");
            Check("очистка не трогает правила", SafePasteDatabase.Load().GetReservedIndex("HOST", "pinned-host") == 5, "");

            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            string problem = null;
            try { LabelMemory.Load(new SafePasteDatabase(), 30); }
            catch (DatabaseException failure) { problem = failure.Message; }
            Check("испорченный файл меток отменяет вставку и подсказывает, что делать",
                problem != null && problem.Contains("Правила и исключения"), problem ?? "нет ошибки");
            LabelMemory.Clear();
            Check("очистка заменяет испорченный файл пустым", LabelMemory.Snapshot().Count == 0, "");

            Check("короткое значение не ищется без подсказок", !LabelMemory.IsDistinctive("ab"), "");
            Check("обычное слово не ищется без подсказок", !LabelMemory.IsDistinctive("test") && !LabelMemory.IsDistinctive("Admin")
                && !LabelMemory.IsDistinctive("сервер"), "");
            Check("короткое число не ищется без подсказок", !LabelMemory.IsDistinctive("12345") && LabelMemory.IsDistinctive("1234567"), "");
            Check("имена узлов ищутся без подсказок", LabelMemory.IsDistinctive("web-prod") && LabelMemory.IsDistinctive("SRV01")
                && LabelMemory.IsDistinctive("Orion") && LabelMemory.IsDistinctive("10.0.0.1"), "");

            File.Delete(file);
            File.Delete(Paths.DatabaseFile);
        }

        /// <summary>Срок, в который запомненное скрывается без подсказок, зависит от режима.</summary>
        private static void MemoryExpiryTests()
        {
            SafePasteSettings defaults = new SafePasteSettings();
            Check("срок по режиму: строгий 30, обычный 14, лёгкий не действует",
                LabelMemory.ActiveDays(defaults, ControlMode.Strict) == 30 && LabelMemory.ActiveDays(defaults, ControlMode.Balanced) == 14
                && LabelMemory.ActiveDays(defaults, ControlMode.Light) == 0, "");
            SafePasteSettings manual = new SafePasteSettings();
            manual.RememberDays = 7;
            Check("срок задаётся вручную, лёгкий режим всё равно без запомненного",
                LabelMemory.ActiveDays(manual, ControlMode.Strict) == 7 && LabelMemory.ActiveDays(manual, ControlMode.Balanced) == 7
                && LabelMemory.ActiveDays(manual, ControlMode.Light) == 0, "");
            SafePasteSettings off = new SafePasteSettings();
            off.HideRemembered = false;
            Check("выключатель отключает поиск запомненного", LabelMemory.ActiveDays(off, ControlMode.Strict) == 0, "");

            string day20 = DateTime.UtcNow.Date.AddDays(-20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string day3 = DateTime.UtcNow.Date.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string json = "{\"Labels\":[{\"Type\":\"HOST\",\"Value\":\"old-node\",\"Index\":1,\"Used\":\"" + day20 + "\"},"
                + "{\"Type\":\"HOST\",\"Value\":\"new-node\",\"Index\":2,\"Used\":\"" + day3 + "\"}]}";
            File.WriteAllBytes(Paths.LabelsFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
            SafePasteDatabase strict = new SafePasteDatabase();
            LabelMemory.Load(strict, defaults, ControlMode.Strict);
            SafePasteDatabase balanced = new SafePasteDatabase();
            LabelMemory.Load(balanced, defaults, ControlMode.Balanced);
            Check("строгий режим помнит 30 дней", strict.Remembered.Count == 2, strict.Remembered.Count.ToString());
            Check("обычный режим помнит 14 дней", balanced.Remembered.Count == 1 && balanced.Remembered[0].Value == "new-node",
                balanced.Remembered.Count.ToString());
            string text = "Узлы old-node и new-node";
            string numbered = LabelMemory.Preview(text, Detector.Scan(text, balanced, ControlMode.Balanced, false), balanced).Text;
            Check("у запомненного прежний номер", numbered.Contains("[HOST_2]") && !numbered.Contains("new-node"), numbered);
            File.Delete(Paths.LabelsFile);
        }

        // ---------------------------------------------------------------- настройки

        private static void MemorySettingsTests()
        {
            SafePasteSettings defaults = new SafePasteSettings();
            Check("по умолчанию метки запоминаются и скрываются", defaults.SaveLabels && defaults.HideRemembered && defaults.RememberDays == 0, "");
            File.WriteAllText(Paths.SettingsFile, "{\"SaveLabels\": false, \"HideRemembered\": false, \"RememberDays\": 99, "
                + "\"MarkTypes\": {\"host\": 3, \"bad type\": 5, \"SERIAL\": \"x\", \"USER\": -2}, \"ControlMode\": \"Strict\"}");
            SafePasteSettings loaded = SafePasteSettings.Load();
            Check("настройки меток читаются", !loaded.SaveLabels && !loaded.HideRemembered && loaded.RememberDays == 30, "");
            int host;
            Check("счётчики типов читаются, мусор пропускается", loaded.MarkTypeUsage.Count == 1
                && loaded.MarkTypeUsage.TryGetValue("HOST", out host) && host == 3, loaded.MarkTypeUsage.Count.ToString());
            loaded.Save();
            SafePasteSettings reloaded = SafePasteSettings.Load();
            Check("настройки меток переживают сохранение", !reloaded.SaveLabels && !reloaded.HideRemembered && reloaded.RememberDays == 30
                && reloaded.MarkTypeUsage["HOST"] == 3 && reloaded.Mode == ControlMode.Strict, "");
            SafePasteSettings.Update(delegate(SafePasteSettings latest) { latest.SaveLabels = true; });
            SafePasteSettings updated = SafePasteSettings.Load();
            Check("изменение одной настройки не затирает другие", updated.SaveLabels && !updated.HideRemembered
                && updated.MarkTypeUsage["HOST"] == 3 && updated.Mode == ControlMode.Strict, "");
            File.Delete(Paths.SettingsFile);
        }

        private static void MemoryPerformanceTest()
        {
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < 4000; index++)
            {
                builder.AppendLine("2026-09-22 10:00:00 srv" + index + ".corp.local 10.20." + (index % 250) + ".7 user=user" + index);
            }
            string text = builder.ToString();
            SafePasteDatabase database = new SafePasteDatabase();
            Random random = new Random(7);
            for (int index = 0; index < 5000; index++)
            {
                LearnedValue value = new LearnedValue();
                value.Value = RandomText(random, "abcdefghijklmnopqrstuvwxyz0123456789-", 6 + random.Next(10)) + index.ToString(CultureInfo.InvariantCulture);
                value.Type = "HOST";
                database.Remembered.Add(value);
            }
            database.Remembered[0].Value = "srv17.corp.local";
            Stopwatch watch = Stopwatch.StartNew();
            List<Detection> found = Detector.Scan(text, database, ControlMode.Balanced, false);
            watch.Stop();
            Console.WriteLine("  5000 запомненных значений, " + (text.Length / 1024) + " КБ: " + watch.ElapsedMilliseconds + " мс");
            Check("тысячи запомненных значений не замедляют проверку", watch.ElapsedMilliseconds < 5000, watch.ElapsedMilliseconds + " мс");
            Check("запомненное находится среди тысяч значений", found.Exists(delegate(Detection detection)
            {
                return detection.Value == "srv17.corp.local" && detection.Source == Detection.RememberedSource;
            }), "");
        }
    }
}
