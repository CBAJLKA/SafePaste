using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using SafePaste.Bridge;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    /// <summary>Что видит окно моста. Реализация не бросает исключений: сбой окна не должен ломать ответ агенту.</summary>
    internal interface IBridgeMonitor
    {
        void ClientIdentified(int session, string name, string version);
        int CallStarted(int session, string tool, string request);
        void CallFinished(int call, string outcome, string response);
        void StateChanged(int session);
    }

    internal sealed class McpServer : IDisposable
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly TextReader reader;
        private readonly TextWriter writer;
        private readonly object writeGate = new object();
        private readonly object processGate = new object();
        private readonly object drainGate = new object();
        private readonly ManualResetEvent drained = new ManualResetEvent(true);
        private int pendingRequests;
        private bool closed;
        private readonly LabelStore labels;
        private readonly Anonymizer anonymizer;
        private readonly FileTools files;
        private readonly FileEditor editor;
        private readonly IApprovalGate approval;
        private readonly Func<BridgeSettings> settings;
        private readonly IBridgeMonitor monitor;
        private readonly int sessionId;
        private ICommandWorker worker;
        private ICommandWorker fullWorker;
        private readonly Dictionary<int, string> pages = new Dictionary<int, string>();
        private readonly Dictionary<string, List<DateTime>> labelQueries = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        private int nextId;
        private volatile string activity;
        private volatile bool waitingApproval;
        private bool denied;
        private static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
        private static readonly string[] ToolNames = { "sp_status", "sp_list", "sp_read", "sp_find", "sp_run", "sp_next", "sp_hide", "sp_edit", "sp_write", "sp_ask_local" };

        private struct ToolReply
        {
            internal string Text;
            internal bool Error;
        }

        internal McpServer(TextReader input, TextWriter output, string roots, bool persist, int pageChars)
            : this(input, output, roots, persist, pageChars, new ApprovalGate()) { }

        internal McpServer(TextReader input, TextWriter output, string roots, bool persist, int pageChars, IApprovalGate approval)
            : this(input, output, new LabelStore(persist), Fixed(roots, pageChars), approval, null, null, 0) { }

        /// <summary>
        /// Сессия одного агента. Метки общие для всех сессий, настройки читаются на каждый вызов,
        /// поэтому изменения в окне моста действуют и для уже подключённых агентов.
        /// </summary>
        internal McpServer(TextReader input, TextWriter output, LabelStore labels, Func<BridgeSettings> settings,
            IApprovalGate approval, string startDirectory, IBridgeMonitor monitor, int sessionId)
        {
            reader = input; writer = output;
            json.MaxJsonLength = int.MaxValue;
            this.labels = labels;
            this.settings = settings;
            this.monitor = monitor;
            this.sessionId = sessionId;
            this.approval = new WatchedApproval(this, approval);
            anonymizer = new Anonymizer(labels);
            files = new FileTools(settings().RootsText, labels, anonymizer, startDirectory);
            editor = new FileEditor(files, labels, anonymizer, this.approval);
        }

        private static Func<BridgeSettings> Fixed(string roots, int pageChars)
        {
            BridgeSettings fixedSettings = new BridgeSettings();
            if (!string.IsNullOrEmpty(roots))
                foreach (string root in roots.Split(';'))
                    if (!string.IsNullOrWhiteSpace(root)) fixedSettings.Roots.Add(root.Trim());
            fixedSettings.PageChars = pageChars;
            return delegate { return fixedSettings; };
        }

        /// <summary>Строка состояния для окна моста.</summary>
        internal string State
        {
            get
            {
                if (waitingApproval) return "ждёт вашего подтверждения";
                string tool = activity;
                if (tool != null) return "выполняет " + tool;
                int task = ActiveTask;
                if (task > 0) return "команда " + task.ToString(CultureInfo.InvariantCulture) + " идёт";
                return "ждёт запроса";
            }
        }

        internal int ActiveTask
        {
            get
            {
                foreach (ICommandWorker item in new ICommandWorker[] { worker, fullWorker })
                {
                    RunTask task = item == null ? null : item.Active;
                    if (task != null && !task.Finished) return task.Id;
                }
                return 0;
            }
        }

        /// <summary>Остановить выполняющуюся команду: кнопка в окне моста, отмена клиентом или уход агента.</summary>
        internal void StopActive()
        {
            foreach (ICommandWorker item in new ICommandWorker[] { worker, fullWorker })
            {
                RunTask task = item == null ? null : item.Active;
                if (task == null || task.Finished) continue;
                try { item.Stop(task); }
                catch (Exception) { }
            }
        }

        internal void Run()
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string message = line;
                    lock (drainGate) { pendingRequests++; drained.Reset(); }
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { HandleLine(message); }
                        catch (Exception) { }
                        finally { lock (drainGate) { if (--pendingRequests == 0) drained.Set(); } }
                    });
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            // Агент ушёл: его команды и окна подтверждения больше не нужны.
            StopActive();
            approval.Cancel();
            drained.WaitOne(15000);
        }

        internal void HandleLine(string line)
        {
            Dictionary<string, object> request;
            try { request = json.DeserializeObject(line) as Dictionary<string, object>; }
            catch { Send(Error(null, -32700, "Ошибка JSON.")); return; }
            if (request == null) { Send(Error(null, -32700, "Ошибка JSON.")); return; }
            object methodValue, id;
            if (!request.TryGetValue("method", out methodValue) || !(methodValue is string)) return;
            string method = (string)methodValue;
            bool response = request.TryGetValue("id", out id);
            if (method == "notifications/cancelled")
            {
                StopActive();
                return;
            }
            if (!response) return;
            object parametersValue;
            Dictionary<string, object> parameters = request.TryGetValue("params", out parametersValue)
                ? parametersValue as Dictionary<string, object> : null;
            if (parameters == null) parameters = new Dictionary<string, object>();
            try
            {
                object result;
                if (method == "initialize") result = Initialize(parameters);
                else if (method == "ping") result = new Dictionary<string, object>();
                else if (method == "tools/list") result = ListTools();
                else if (method == "tools/call")
                {
                    lock (processGate) result = Call(parameters);
                }
                else { Send(Error(id, -32601, "Неизвестный метод.")); return; }
                Send(Result(id, result));
            }
            catch (Exception)
            {
                Send(Error(id, -32603, "Внутренняя ошибка моста. Ответ не отправлен."));
            }
        }

        private object Initialize(Dictionary<string, object> args)
        {
            string requested = GetString(args, "protocolVersion", "");
            string version = Array.IndexOf(Versions, requested) >= 0 ? requested : "2025-06-18";
            object clientValue;
            Dictionary<string, object> client = args.TryGetValue("clientInfo", out clientValue) ? clientValue as Dictionary<string, object> : null;
            if (client != null && monitor != null)
                monitor.ClientIdentified(sessionId, GetString(client, "name", ""), GetString(client, "version", ""));
            return new Dictionary<string, object> {
                { "protocolVersion", version },
                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object> { { "listChanged", false } } } } },
                { "serverInfo", new Dictionary<string, object> { { "name", "safepaste" }, { "version", "1.0" } } },
                { "instructions", "Все ответы обезличены. Передавайте метки [HOST_1] в команды в одинарных кавычках. Секреты не подставляются. Для файлов и команд используйте только инструменты sp_*. sp_run по умолчанию ограничен: один конвейер разрешённых команд без { }, $переменных и выражений, фильтр Where-Object Имя -оператор Значение; список команд в sp_status." }
            };
        }

        private static Dictionary<string, object> Schema(string[] required, params string[] properties)
        {
            Dictionary<string, object> fields = new Dictionary<string, object>();
            foreach (string property in properties)
            {
                string[] parts = property.Split(':');
                fields[parts[0]] = new Dictionary<string, object> { { "type", parts[1] } };
            }
            return new Dictionary<string, object> { { "type", "object" }, { "properties", fields }, { "required", required } };
        }

        private static object Tool(string name, string description, Dictionary<string, object> schema, bool open, bool readOnly)
        {
            Dictionary<string, object> annotations = new Dictionary<string, object> { { "readOnlyHint", readOnly } };
            if (open) annotations["openWorldHint"] = true;
            return new Dictionary<string, object> { { "name", name }, { "description", description },
                { "inputSchema", schema }, { "annotations", annotations } };
        }

        private object ListTools()
        {
            return new Dictionary<string, object> { { "tools", new object[] {
                Tool("sp_status", "Настройки и метки моста без значений.", Schema(new string[0]), false, true),
                Tool("sp_list", "Список папок и файлов.", Schema(new string[0], "path:string", "depth:integer", "pattern:string"), false, true),
                Tool("sp_read", "Обезличенное содержимое файла по строкам.", Schema(new string[] { "path" }, "path:string", "offset:integer", "limit:integer"), false, true),
                Tool("sp_find", "Поиск по файлам.", Schema(new string[] { "pattern" }, "pattern:string", "path:string", "glob:string", "regex:boolean", "case_sensitive:boolean", "max_results:integer"), false, true),
                Tool("sp_run", "PowerShell. restricted (по умолчанию, без подтверждения): один конвейер разрешённых команд с постоянными аргументами, без блоков { }, $переменных и выражений; фильтр в краткой форме Where-Object IPAddress -notlike '127.*', несколько условий цепочкой Where-Object; список команд в sp_status. full: любой скрипт после подтверждения пользователя, если разрешён в SafePaste.", Schema(new string[] { "command" }, "command:string", "profile:string", "wait_sec:integer"), true, false),
                Tool("sp_next", "Продолжение страницы или задачи.", Schema(new string[] { "id" }, "id:integer", "stop:boolean", "wait_sec:integer"), false, true),
                Tool("sp_hide", "Скрыть значение в следующих ответах.", Schema(new string[] { "value" }, "value:string", "type:string"), false, true),
                Tool("sp_edit", "Заменить один обезличенный фрагмент файла после подтверждения.", Schema(new string[] { "path", "old_text", "new_text" }, "path:string", "old_text:string", "new_text:string"), false, false),
                Tool("sp_write", "Записать файл после подтверждения.", Schema(new string[] { "path", "text" }, "path:string", "text:string"), false, false),
                Tool("sp_ask_local", "Задать вопрос локальной модели на этом компьютере и обезличить ответ.", Schema(new string[] { "task" }, "task:string", "files:array"), false, true)
            } } };
        }

        private object Call(Dictionary<string, object> parameters)
        {
            string name = GetString(parameters, "name", "");
            object argValue;
            Dictionary<string, object> args = parameters.TryGetValue("arguments", out argValue) ? argValue as Dictionary<string, object> : null;
            if (args == null) args = new Dictionary<string, object>();
            if (!ToolNames.ContainsIgnoreCase(name))
                return ToolText("Неизвестный инструмент.", true);
            name = name.ToLowerInvariant();
            int call = monitor == null ? 0 : monitor.CallStarted(sessionId, name, Describe(name, args));
            denied = false;
            activity = name;
            Changed();
            ToolReply reply;
            try { reply = Dispatch(name, args); }
            catch (Exception)
            {
                if (monitor != null) monitor.CallFinished(call, "ошибка", "Внутренняя ошибка моста. Ответ не отправлен.");
                throw;
            }
            finally { activity = null; Changed(); }
            if (monitor != null)
                monitor.CallFinished(call, denied ? "отклонено" : reply.Error ? "ошибка" : "готово", reply.Text);
            return ToolText(reply.Text, reply.Error);
        }

        private ToolReply Dispatch(string name, Dictionary<string, object> args)
        {
            BridgeSettings current = settings();
            if (current.Problem != null)
                return Failure(current.Problem + " Откройте окно моста в SafePaste и сохраните настройки заново.");
            try
            {
                files.UpdateRoots(current.RootsText);
                string safe;
                switch (name)
                {
                    case "sp_status":
                        safe = anonymizer.Anonymize(files.RootsDescription)
                            + "\nПрофиль по умолчанию: ограниченный PowerShell. Полный: "
                            + (current.AllowFull ? "после подтверждения" : "выключен в настройках SafePaste")
                            + ".\nПравка файлов: " + (current.AllowEdits ? "после подтверждения" : "выключена в настройках SafePaste")
                            + ".\nЛокальная модель: " + (current.AllowLocalModel ? "доступна" : "выключена в настройках SafePaste")
                            + ".\nСтраница: " + PageChars(current).ToString(CultureInfo.InvariantCulture)
                            + " символов. Чтение файла: до 2 МБ.\n" + PowerShellWorker.DescribeRestricted()
                            + "\n" + labels.Describe(); break;
                    case "sp_list":
                        safe = files.List(GetString(args, "path", null), GetInt(args, "depth", 1), GetString(args, "pattern", "*")); break;
                    case "sp_read":
                        safe = files.Read(GetString(args, "path", null), GetInt(args, "offset", 1), GetInt(args, "limit", 400)); break;
                    case "sp_find":
                        if (!CheckQuerySeries(GetString(args, "pattern", ""))) return Refused("Серия запросов к метке отклонена пользователем.");
                        safe = files.Find(GetString(args, "pattern", ""), GetString(args, "path", null), GetString(args, "glob", "*"),
                            GetBool(args, "regex", false), GetBool(args, "case_sensitive", false), GetInt(args, "max_results", 100)); break;
                    case "sp_hide":
                        safe = labels.Hide(GetString(args, "value", ""), GetString(args, "type", "TEXT")); break;
                    case "sp_edit":
                        if (!current.AllowEdits) return Refused("Правка файлов выключена в настройках SafePaste.");
                        safe = editor.Edit(GetString(args, "path", null), GetString(args, "old_text", null), GetString(args, "new_text", null)); break;
                    case "sp_write":
                        if (!current.AllowEdits) return Refused("Правка файлов выключена в настройках SafePaste.");
                        safe = editor.Write(GetString(args, "path", null), GetString(args, "text", null)); break;
                    case "sp_ask_local":
                        if (!current.AllowLocalModel) return Refused("Локальная модель выключена в настройках SafePaste.");
                        object pathsValue;
                        List<string> paths = new List<string>();
                        if (args.TryGetValue("files", out pathsValue) && pathsValue != null)
                        {
                            object[] array = pathsValue as object[];
                            if (array == null) throw new ArgumentException("files должен быть массивом путей.");
                            foreach (object item in array)
                            {
                                if (!(item is string)) throw new ArgumentException("files должен содержать пути.");
                                paths.Add((string)item);
                            }
                        }
                        safe = new LocalModel(files, labels, anonymizer, current.LocalModel, current.LocalModelId)
                            .Ask(GetString(args, "task", null), paths); break;
                    case "sp_run": return RunCommand(args, current);
                    default: return Next(args, current);
                }
                return Page(safe, 0, current);
            }
            catch (Exception error)
            {
                try { return Failure(anonymizer.Anonymize(error.Message)); }
                catch { return Failure("Операция отменена: ответ не удалось обезличить."); }
            }
            finally { labels.Flush(); }
        }

        private ToolReply RunCommand(Dictionary<string, object> args, BridgeSettings current)
        {
            string command = GetString(args, "command", "");
            if (command.Length == 0) throw new ArgumentException("Не указана команда.");
            string profile = GetString(args, "profile", "restricted");
            if (profile != "restricted" && profile != "full") throw new ArgumentException("Профиль: restricted или full.");
            bool full = profile == "full";
            if (full && !current.AllowFull) return Refused("Полный PowerShell выключен в настройках SafePaste.");
            if (!CheckQuerySeries(command)) return Refused("Серия запросов к метке отклонена пользователем.");
            if (worker != null && worker.Active != null && !worker.Active.Finished
                || fullWorker != null && fullWorker.Active != null && !fullWorker.Active.Finished)
                throw new InvalidOperationException("Уже выполняется другая команда.");
            string rewritten, problem;
            if (!new Rehydrator(labels).TryRehydrate(command, out rewritten, out problem))
                throw new ArgumentException(problem);
            // Проверка здесь, а не только в воркере: из его кода выхода агент не узнал бы причину отказа.
            if (!full) PowerShellWorker.Check(rewritten, files);
            if (full && !approval.Approve("sp_run_full", "Полный PowerShell", rewritten))
                return Refused("Команда отклонена пользователем.");
            ICommandWorker selected = full ? fullWorker : worker;
            if (selected == null)
            {
                selected = new ExternalWorker(files, !full);
                if (full) fullWorker = selected; else worker = selected;
            }
            RunTask task = selected.Start(rewritten, ++nextId);
            Changed();
            selected.Wait(task, GetInt(args, "wait_sec", 30));
            return TaskOutput(task, current);
        }

        private bool CheckQuerySeries(string query)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Labels.Placeholder.Matches(query ?? "")) seen.Add(match.Value);
            foreach (string label in seen)
            {
                List<DateTime> events;
                if (!labelQueries.TryGetValue(label, out events))
                { events = new List<DateTime>(); labelQueries[label] = events; }
                events.RemoveAll(delegate(DateTime stamp) { return stamp < DateTime.UtcNow.AddMinutes(-2); });
                events.Add(DateTime.UtcNow);
                if (events.Count >= 5)
                {
                    if (!approval.Approve("series:" + label, "Много запросов к одной метке",
                        "За 2 минуты агент обратился к " + label + " " + events.Count
                        + " раз. Последний запрос:\n\n" + query)) return false;
                    events.Clear();
                }
            }
            return true;
        }

        private ToolReply Next(Dictionary<string, object> args, BridgeSettings current)
        {
            int id = GetInt(args, "id", 0);
            string rest;
            if (pages.TryGetValue(id, out rest)) { pages.Remove(id); return Page(rest, id, current); }
            ICommandWorker selected = worker != null && worker.Active != null && worker.Active.Id == id ? worker
                : fullWorker != null && fullWorker.Active != null && fullWorker.Active.Id == id ? fullWorker : null;
            if (selected == null)
                throw new ArgumentException("Неизвестный номер задачи.");
            RunTask task = selected.Active;
            if (GetBool(args, "stop", false)) selected.Stop(task);
            else selected.Wait(task, GetInt(args, "wait_sec", 30));
            return TaskOutput(task, current);
        }

        private ToolReply TaskOutput(RunTask task, BridgeSettings current)
        {
            string raw;
            lock (task.Raw) raw = task.Raw.ToString();
            string safe = anonymizer.Anonymize(raw);
            string[] lines = safe.Split('\n');
            int available = task.Finished ? lines.Length : lines.Length - 1;
            StringBuilder result = new StringBuilder();
            for (int i = task.SentLines; i < available; i++) result.Append(lines[i]).Append('\n');
            task.SentLines = available;
            if (task.Truncated) result.Append("Вывод обрезан на 2 000 000 символов.\n");
            if (!task.Finished) result.Append("Задача ").Append(task.Id).Append(" выполняется. Используйте sp_next.\n");
            else if (task.Error != null) result.Append(task.Error).Append('\n');
            if (task.Error != null) return Failure(result.ToString());
            return Page(result.ToString(), task.Id, current);
        }

        private static int PageChars(BridgeSettings current)
        {
            return Math.Max(BridgeSettings.MinPageChars, current.PageChars);
        }

        private ToolReply Page(string text, int id, BridgeSettings current)
        {
            int pageChars = PageChars(current);
            if (text.Length <= pageChars) return Success(text);
            if (id == 0) id = ++nextId;
            int end = text.LastIndexOf('\n', Math.Min(pageChars, text.Length - 1));
            if (end < pageChars / 2) end = pageChars;
            int open = text.LastIndexOf('[', end - 1);
            int close = text.LastIndexOf(']', end - 1);
            if (open > close) end = open;
            if (end <= 0) end = pageChars;
            pages[id] = text.Substring(end);
            return Success(text.Substring(0, end) + "\nПродолжение: sp_next id=" + id.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Короткая запись запроса для журнала окна моста: только то, что прислал агент.</summary>
        private static string Describe(string name, Dictionary<string, object> args)
        {
            string text;
            try
            {
                switch (name)
                {
                    case "sp_run":
                        text = (GetString(args, "profile", "restricted") == "full" ? "full: " : "") + GetString(args, "command", ""); break;
                    case "sp_find":
                        text = GetString(args, "pattern", "") + " | " + GetString(args, "path", "."); break;
                    case "sp_next":
                        text = "id " + GetInt(args, "id", 0).ToString(CultureInfo.InvariantCulture)
                            + (GetBool(args, "stop", false) ? ", остановить" : ""); break;
                    case "sp_hide":
                        text = "тип " + GetString(args, "type", "TEXT"); break;
                    case "sp_ask_local":
                        text = GetString(args, "task", ""); break;
                    case "sp_status":
                        text = ""; break;
                    default:
                        text = GetString(args, "path", "."); break;
                }
            }
            catch (Exception) { text = ""; }
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }

        private ToolReply Refused(string text)
        {
            denied = true;
            return Failure(text);
        }

        private static ToolReply Success(string text) { ToolReply reply = new ToolReply(); reply.Text = text; return reply; }
        private static ToolReply Failure(string text) { ToolReply reply = new ToolReply(); reply.Text = text; reply.Error = true; return reply; }

        private void Changed()
        {
            if (monitor != null) monitor.StateChanged(sessionId);
        }

        private static string GetString(Dictionary<string, object> args, string key, string fallback)
        { object value; return args.TryGetValue(key, out value) && value is string ? (string)value : fallback; }
        private static int GetInt(Dictionary<string, object> args, string key, int fallback)
        { object value; return args.TryGetValue(key, out value) && value != null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : fallback; }
        private static bool GetBool(Dictionary<string, object> args, string key, bool fallback)
        { object value; return args.TryGetValue(key, out value) && value is bool ? (bool)value : fallback; }

        private static object ToolText(string text, bool error)
        { return new Dictionary<string, object> { { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } }, { "isError", error } }; }
        private static object Result(object id, object value)
        { return new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", value } }; }
        private static object Error(object id, int code, string message)
        { return new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } } }; }

        /// <summary>Канал к агенту может закрыться в любой момент: ответ тогда просто не нужен.</summary>
        private void Send(object message)
        {
            lock (writeGate)
            {
                if (closed) return;
                try { writer.WriteLine(json.Serialize(message)); writer.Flush(); }
                catch (IOException) { closed = true; }
                catch (ObjectDisposedException) { closed = true; }
            }
        }

        public void Dispose()
        {
            approval.Cancel();
            if (worker != null) worker.Dispose();
            if (fullWorker != null) fullWorker.Dispose();
        }

        /// <summary>Отмечает ожидание окна и отказ пользователя для журнала.</summary>
        private sealed class WatchedApproval : IApprovalGate
        {
            private readonly McpServer owner;
            private readonly IApprovalGate inner;

            internal WatchedApproval(McpServer owner, IApprovalGate inner) { this.owner = owner; this.inner = inner; }

            public bool Approve(string kind, string title, string details)
            {
                bool allowed = false;
                owner.waitingApproval = true;
                owner.Changed();
                try { allowed = inner.Approve(kind, title, details); }
                finally { owner.waitingApproval = false; owner.Changed(); }
                if (!allowed) owner.denied = true;
                return allowed;
            }

            public void Cancel() { inner.Cancel(); }
        }
    }
}
