using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using SafePaste.Detecting;
using SafePaste.Bridge;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    internal static class McpProgram
    {
        internal static int Run(string[] args)
        {
            try
            {
                string mode = null;
                string roots = null;
                string hook = null;
                bool full = false;
                string cwd = null;
                bool persist = true;
                bool customData = false;
                bool pageChars = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--mcp") mode = "mcp";
                    else if (args[i] == "--bridge-host") mode = "host";
                    else if (args[i] == "--filter") mode = "filter";
                    else if (args[i] == "--worker") mode = "worker";
                    else if (args[i] == "--hook" && i + 1 < args.Length) { mode = "hook"; hook = args[++i]; }
                    else if (args[i] == "--full") full = true;
                    else if (args[i] == "--cwd" && i + 1 < args.Length) cwd = args[++i];
                    else if (args[i] == "--data-dir" && i + 1 < args.Length) { Paths.DataDirectory = args[++i]; customData = true; }
                    else if (args[i] == "--roots" && i + 1 < args.Length) roots = args[++i];
                    else if (args[i] == "--no-persist") persist = false;
                    else if (args[i] == "--page-chars" && i + 1 < args.Length) { i++; pageChars = true; }
                    else throw new ArgumentException("Неизвестный параметр запуска.");
                }
                if (mode == "hook") return Hook(hook);
                if (mode == "filter") return Filter(persist);
                // Воркера запускает только мост и всегда передаёт папки, пусть и пустым списком.
                if (mode == "worker") return roots == null ? 2 : Worker(roots, cwd, full);
                if (mode == "host") return Host();
                return Relay(roots != null || pageChars || !persist, customData);
            }
            catch (Exception)
            {
                Console.Error.WriteLine("Мост SafePaste не запустился.");
                return 2;
            }
        }

        /// <summary>
        /// --mcp: разъём к мосту внутри SafePaste. Папки, страницы и сохранение меток настраиваются
        /// в окне моста; старые параметры отклоняются, а не игнорируются, чтобы агент не получил
        /// больше доступа, чем задумано в конфигурации.
        /// </summary>
        private static int Relay(bool legacyArguments, bool customData)
        {
            Stream input = Console.OpenStandardInput();
            Stream output = Console.OpenStandardOutput();
            string refusal = null;
            if (legacyArguments)
                refusal = "Параметры --roots, --page-chars и --no-persist больше не поддерживаются: настройки моста задаются в SafePaste, окно «Мост для агентов».";
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAFEPASTE_ROOTS")))
                refusal = "Переменная SAFEPASTE_ROOTS больше не используется: папки для агентов задаются в SafePaste, окно «Мост для агентов». Уберите её из настроек клиента.";
            if (refusal != null)
            {
                Console.Error.WriteLine(refusal);
                McpRelay.AnswerErrors(input, output, refusal);
                return 2;
            }
            return McpRelay.Run(input, output, BridgeHost.PipeName(), !customData, McpRelay.StartWaitMs);
        }

        /// <summary>--bridge-host: мост без трея для дымовой проверки. Работает, пока открыт stdin.</summary>
        private static int Host()
        {
            using (BridgeHost host = new BridgeHost(delegate { return new ApprovalGate(); }))
            {
                host.Start();
                // Проверка ждёт эту строку и только потом запускает разъём.
                Console.Out.WriteLine("ready");
                Console.Out.Flush();
                using (Stream input = Console.OpenStandardInput())
                {
                    byte[] buffer = new byte[256];
                    while (input.Read(buffer, 0, buffer.Length) > 0) { }
                }
            }
            return 0;
        }

        private static int Worker(string roots, string cwd, bool full)
        {
            Stream input = Console.OpenStandardInput();
            Stream output = Console.OpenStandardOutput();
            string command;
            using (StreamReader reader = new StreamReader(input, new UTF8Encoding(false))) command = reader.ReadToEnd();
            StdIo.Isolate();
            StdIo.UseConsoleCodePage();
            LabelStore labels = new LabelStore(true);
            FileTools files = new FileTools(roots, labels, new Anonymizer(labels));
            if (!string.IsNullOrEmpty(cwd)) files.CurrentDirectory = files.CheckPath(cwd);
            using (StreamWriter writer = new StreamWriter(output, new UTF8Encoding(false)))
            using (PowerShellWorker shell = new PowerShellWorker(files, !full))
            {
                writer.AutoFlush = true;
                RunTask task;
                try { task = shell.Start(command, 1); }
                catch (Exception error)
                {
                    // Мост видит только вывод и код выхода, поэтому причина отказа идёт в вывод.
                    // Перед агентом вывод обезличивается, как и всё остальное.
                    writer.WriteLine(error is ArgumentException || error is UnauthorizedAccessException
                        || error is InvalidOperationException || error is IOException
                        ? error.Message : "Команда не запустилась.");
                    return 3;
                }
                int sent = 0;
                do
                {
                    shell.Wait(task, 1);
                    string raw;
                    lock (task.Raw) raw = task.Raw.ToString();
                    int end = task.Finished ? raw.Length : raw.LastIndexOf('\n') + 1;
                    if (end > sent) { writer.Write(raw.Substring(sent, end - sent)); sent = end; }
                } while (!task.Finished);
                if (task.Error != null) { writer.WriteLine(task.Error); return 2; }
            }
            return 0;
        }

        private static int Filter(bool persist)
        {
            using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
            using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
            {
                char[] block = new char[8192]; int count; StringBuilder raw = new StringBuilder();
                while ((count = reader.Read(block, 0, block.Length)) > 0)
                {
                    raw.Append(block, 0, count);
                    if (raw.Length > 2000000) throw new IOException("Вход фильтра больше 2 млн символов.");
                }
                LabelStore labels = new LabelStore(persist);
                string safe = new Anonymizer(labels).Anonymize(raw.ToString());
                labels.Flush();
                writer.Write(safe);
                writer.Flush();
            }
            return 0;
        }

        private static int Hook(string mode)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            string input;
            using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                input = reader.ReadToEnd();
            Dictionary<string, object> request = json.Deserialize<Dictionary<string, object>>(input);
            object raw;
            if (mode == "prompt")
            {
                string prompt = request != null && request.TryGetValue("prompt", out raw) ? raw as string : null;
                if (prompt != null && prompt.StartsWith("!raw", StringComparison.Ordinal)) return 0;
                SafePasteDatabase database;
                try { database = SafePasteDatabase.Load(); }
                catch (DatabaseException)
                {
                    WriteHook(json.Serialize(new Dictionary<string, object> { { "decision", "block" },
                        { "reason", "База SafePaste не читается. Сообщение остановлено." } }));
                    return 0;
                }
                List<Detection> found = Detector.Scan(prompt ?? "", database, SafePasteSettings.Load().Mode, true);
                Dictionary<string, int> types = new Dictionary<string, int>();
                foreach (Detection item in found) if (item.Enabled)
                { if (!types.ContainsKey(item.Type)) types[item.Type] = 0; types[item.Type]++; }
                if (types.Count == 0) return 0;
                List<string> summary = new List<string>();
                foreach (KeyValuePair<string, int> item in types) summary.Add(item.Key + ": " + item.Value);
                WriteHook(json.Serialize(new Dictionary<string, object> { { "decision", "block" },
                    { "reason", "Обнаружены данные (" + string.Join(", ", summary.ToArray()) + "). Вставьте текст через SafePaste или начните сообщение с !raw." } }));
                return 0;
            }
            if (mode == "tool")
            {
                string tool = request != null && request.TryGetValue("tool_name", out raw) ? raw as string : null;
                // Codex 0.155 вызывает PreToolUse только для команд терминала, и отказ здесь закрывает
                // exec_command, который в этой версии флагами не выключается.
                if (tool != null && (Regex.IsMatch(tool, @"(^|__|[./:])safepaste(__|[./:])", RegexOptions.IgnoreCase)
                    || new string[] { "update_plan", "request_user_input", "TodoWrite", "AskUserQuestion", "Skill", "ToolSearch", "Agent", "Task", "EnterPlanMode", "ExitPlanMode" }.ContainsIgnoreCase(tool))) return 0;
                WriteHook(json.Serialize(new Dictionary<string, object> { { "hookSpecificOutput",
                    new Dictionary<string, object> { { "hookEventName", "PreToolUse" }, { "permissionDecision", "deny" },
                        { "permissionDecisionReason", "Данные доступны только через инструменты sp_* сервера safepaste." } } } }));
                return 0;
            }
            throw new ArgumentException("Неизвестный режим хука.");
        }

        private static void WriteHook(string text)
        {
            using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
            { writer.Write(text); writer.Flush(); }
        }
    }
}
