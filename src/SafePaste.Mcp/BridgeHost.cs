using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using SafePaste.Bridge;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    internal sealed class BridgeSessionInfo
    {
        internal int Id;
        internal string Client;
        internal string Folder;
        internal DateTime Connected;
        internal int Calls;
        internal string State;
        internal int Task;
        internal bool Closed;
        internal DateTime Ended;

        internal BridgeSessionInfo Copy()
        {
            return (BridgeSessionInfo)MemberwiseClone();
        }
    }

    internal sealed class BridgeCallInfo
    {
        internal int Id;
        internal int Session;
        internal string Client;
        internal DateTime Started;
        internal TimeSpan Duration;
        internal string Tool;
        internal string Request;
        internal string Outcome;
        internal string Response;
        internal bool Truncated;
    }

    internal sealed class BridgeStatus
    {
        internal bool Running;
        internal string Problem;
        /// <summary>Меняется вместе со всем, что показывает окно моста.</summary>
        internal string Signature = string.Empty;
        internal BridgeSettings Settings = new BridgeSettings();
        internal List<BridgeSessionInfo> Sessions = new List<BridgeSessionInfo>();
        /// <summary>Отключившиеся за последний час, новые первыми: короткое подключение иначе не заметить.</summary>
        internal List<BridgeSessionInfo> Recent = new List<BridgeSessionInfo>();
        internal List<BridgeCallInfo> Calls = new List<BridgeCallInfo>();
    }

    /// <summary>То, чем окно моста управляет. Проверки интерфейса подставляют свою реализацию.</summary>
    internal interface IBridgeControl
    {
        BridgeStatus Snapshot();
        void SetEnabled(bool enabled);
        void UpdateSettings(Action<BridgeSettings> change);
        void StopTask(int session);
        void Disconnect(int session);
        void ClearLog();
    }

    /// <summary>
    /// Мост внутри SafePaste. Агент запускает SafePaste.exe --mcp, а тот подключается сюда по
    /// именованному каналу, открытому только этой учётной записи. Метки, настройки и журнал общие
    /// для всех агентов; журнал живёт только в памяти.
    /// </summary>
    internal sealed class BridgeHost : IBridgeControl, IBridgeMonitor, IDisposable
    {
        internal const int MaxSessions = 16;
        private const int MaxCalls = 200;
        private const int MaxRecent = 10;
        private const int MaxResponse = 4000;
        private static readonly TimeSpan RecentFor = TimeSpan.FromHours(1);
        private readonly object gate = new object();
        private readonly object settingsGate = new object();
        private readonly Func<IApprovalGate> approvals;
        private readonly Dictionary<int, Session> sessions = new Dictionary<int, Session>();
        private readonly List<BridgeSessionInfo> recent = new List<BridgeSessionInfo>();
        private readonly List<BridgeCallInfo> calls = new List<BridgeCallInfo>();
        private LabelStore labels;
        private Thread listener;
        private string pipeName;
        private volatile bool stopping;
        private bool running;
        private string problem;
        private int version;
        private int nextSession;
        private int nextCall;
        private BridgeSettings settings;
        private DateTime settingsStamp;
        private long settingsSize = -2;

        private sealed class Session
        {
            internal BridgeSessionInfo Info;
            internal NamedPipeServerStream Pipe;
            internal McpServer Server;
        }

        internal BridgeHost(Func<IApprovalGate> approvals)
        {
            this.approvals = approvals;
        }

        internal bool Running
        {
            get { lock (gate) return running; }
        }

        internal string Problem
        {
            get { lock (gate) return problem; }
        }

        /// <summary>
        /// Имя канала своё у каждой учётной записи, сеанса Windows и папки данных. Имена каналов общие
        /// для всей машины: без номера сеанса агент из RDP попал бы в мост консольного сеанса, и окна
        /// подтверждения всплыли бы не на том экране. Папка данных разводит проверки и настоящий мост.
        /// </summary>
        internal static string PipeName()
        {
            string directory = Path.GetFullPath(Paths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            ulong hash = 14695981039346656037UL;
            foreach (char symbol in directory)
            {
                hash ^= symbol;
                hash *= 1099511628211UL;
            }
            int session;
            using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess()) session = self.SessionId;
            return "SafePaste-Bridge-" + WindowsIdentity.GetCurrent().User.Value + "-"
                + session.ToString(CultureInfo.InvariantCulture) + "-" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- запуск и остановка

        internal void Start()
        {
            lock (gate)
            {
                if (running) return;
                string name = PipeName();
                NamedPipeServerStream first;
                try { first = CreateInstance(name, true); }
                catch (Exception failure)
                {
                    problem = failure is IOException ? failure.Message : "Не удалось открыть канал моста.";
                    Interlocked.Increment(ref version);
                    throw;
                }
                pipeName = name;
                stopping = false;
                problem = null;
                running = true;
                // Новый запуск начинает с чистой памятью: метки секретов прошлого запуска недействительны.
                labels = new LabelStore(true);
                Interlocked.Increment(ref version);
                Thread thread = new Thread(delegate() { Listen(name, first); });
                thread.IsBackground = true;
                thread.Name = "SafePaste bridge";
                listener = thread;
                thread.Start();
            }
        }

        internal void Stop()
        {
            List<Session> active;
            string name;
            Thread thread;
            lock (gate)
            {
                if (!running && listener == null) return;
                stopping = true;
                running = false;
                name = pipeName;
                thread = listener;
                listener = null;
                active = new List<Session>(sessions.Values);
                Interlocked.Increment(ref version);
            }
            Wake(name);
            foreach (Session session in active) Close(session);
            if (thread != null && thread != Thread.CurrentThread) thread.Join(3000);
        }

        public void Dispose()
        {
            Stop();
        }

        private void Listen(string name, NamedPipeServerStream instance)
        {
            while (!stopping)
            {
                bool connected;
                try { instance.WaitForConnection(); connected = true; }
                catch (Exception) { connected = false; }
                if (stopping) break;
                if (connected)
                {
                    NamedPipeServerStream accepted = instance;
                    Thread thread = new Thread(delegate() { Serve(accepted); });
                    thread.IsBackground = true;
                    thread.Name = "SafePaste bridge session";
                    thread.Start();
                }
                else
                {
                    // Клиент ушёл, не дождавшись соединения: открываем канал заново.
                    instance.Dispose();
                    Thread.Sleep(200);
                }
                try { instance = CreateInstance(name, false); }
                catch (Exception failure)
                {
                    lock (gate)
                    {
                        if (!stopping)
                        {
                            problem = "Канал моста закрылся: " + failure.Message;
                            running = false;
                        }
                        Interlocked.Increment(ref version);
                    }
                    return;
                }
            }
            instance.Dispose();
        }

        /// <summary>WaitForConnection ждёт клиента, поэтому для остановки к каналу подключается пустой клиент.</summary>
        private static void Wake(string name)
        {
            if (name == null) return;
            try
            {
                using (NamedPipeClientStream wake = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                    wake.Connect(500);
            }
            catch (Exception) { }
        }

        private static void Close(Session session)
        {
            McpServer server = session.Server;
            if (server != null) server.StopActive();
            try { session.Pipe.Disconnect(); }
            catch (Exception) { }
            try { session.Pipe.Dispose(); }
            catch (Exception) { }
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            Session session = null;
            try
            {
                StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false);
                StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false));
                writer.AutoFlush = true;
                writer.NewLine = "\n";
                string folder;
                if (!ReadHello(pipe, reader, out folder)) return;
                string refusal = CheckElevation(pipe);
                if (refusal != null)
                {
                    McpRelay.AnswerErrors(reader, writer, refusal);
                    return;
                }
                LabelStore shared;
                lock (gate)
                {
                    if (stopping || !running || sessions.Count >= MaxSessions) return;
                    session = new Session();
                    session.Pipe = pipe;
                    session.Info = new BridgeSessionInfo();
                    session.Info.Id = ++nextSession;
                    session.Info.Client = "агент";
                    session.Info.Folder = folder;
                    session.Info.Connected = DateTime.Now;
                    sessions.Add(session.Info.Id, session);
                    shared = labels;
                    Interlocked.Increment(ref version);
                }
                McpServer server = new McpServer(reader, writer, shared, CurrentSettings, approvals(), folder, this, session.Info.Id);
                lock (gate) session.Server = server;
                if (stopping) return;
                server.Run();
            }
            catch (Exception) { }
            finally
            {
                if (session != null)
                {
                    lock (gate)
                    {
                        sessions.Remove(session.Info.Id);
                        BridgeSessionInfo gone = session.Info.Copy();
                        gone.Closed = true;
                        gone.Ended = DateTime.Now;
                        gone.Task = 0;
                        gone.State = "отключился в " + gone.Ended.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        recent.Insert(0, gone);
                        if (recent.Count > MaxRecent) recent.RemoveAt(recent.Count - 1);
                    }
                    Interlocked.Increment(ref version);
                    if (session.Server != null)
                    {
                        try { session.Server.Dispose(); }
                        catch (Exception) { }
                    }
                }
                try { pipe.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>Первая строка от разъёма: рабочая папка агента. Молчаливый клиент отключается через 10 с.</summary>
        private static bool ReadHello(NamedPipeServerStream pipe, StreamReader reader, out string folder)
        {
            folder = string.Empty;
            string line = null;
            object waitGate = new object();
            bool done = false;
            using (Timer timeout = new Timer(delegate
            {
                lock (waitGate)
                {
                    if (done) return;
                    try { pipe.Dispose(); }
                    catch (Exception) { }
                }
            }, null, 10000, Timeout.Infinite))
            {
                try { line = reader.ReadLine(); }
                catch (Exception) { line = null; }
                lock (waitGate) done = true;
            }
            if (line == null) return false;
            Dictionary<string, object> hello;
            try { hello = new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>; }
            catch (Exception) { return false; }
            object kind, cwd;
            if (hello == null || !hello.TryGetValue("safepaste", out kind) || !"hello".Equals(kind)) return false;
            if (hello.TryGetValue("cwd", out cwd) && cwd is string) folder = Clean((string)cwd, 260);
            return true;
        }

        // ---------------------------------------------------------------- настройки

        /// <summary>Настройки читаются из bridge.json при каждом вызове, если файл изменился.</summary>
        internal BridgeSettings CurrentSettings()
        {
            lock (settingsGate)
            {
                DateTime stamp = DateTime.MinValue;
                long size = -1;
                try
                {
                    FileInfo info = new FileInfo(Paths.BridgeSettingsFile);
                    if (info.Exists)
                    {
                        stamp = info.LastWriteTimeUtc;
                        size = info.Length;
                    }
                }
                catch (Exception) { }
                if (settings == null || stamp != settingsStamp || size != settingsSize)
                {
                    settings = BridgeSettings.Load();
                    settingsStamp = stamp;
                    settingsSize = size;
                    Interlocked.Increment(ref version);
                }
                return settings;
            }
        }

        public void UpdateSettings(Action<BridgeSettings> change)
        {
            lock (settingsGate)
            {
                BridgeSettings next = BridgeSettings.Load();
                if (next.Problem != null)
                {
                    // Нечитаемый файл не теряется: рядом остаётся копия с датой.
                    string broken = Paths.BridgeSettingsFile + ".broken-"
                        + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    File.Copy(Paths.BridgeSettingsFile, broken, true);
                }
                change(next);
                next.Save();
                settings = null;
            }
            Interlocked.Increment(ref version);
        }

        public void SetEnabled(bool enabled)
        {
            UpdateSettings(delegate(BridgeSettings next) { next.Enabled = enabled; });
            if (enabled) Start();
            else Stop();
        }

        // ---------------------------------------------------------------- окно моста

        public BridgeStatus Snapshot()
        {
            BridgeStatus status = new BridgeStatus();
            status.Settings = CurrentSettings().Clone();
            List<Session> active;
            int current;
            lock (gate)
            {
                status.Running = running;
                status.Problem = problem;
                active = new List<Session>(sessions.Values);
                foreach (BridgeCallInfo call in calls) status.Calls.Add(Copy(call));
                foreach (BridgeSessionInfo gone in recent)
                    if (DateTime.Now - gone.Ended < RecentFor) status.Recent.Add(gone.Copy());
                current = Thread.VolatileRead(ref version);
                foreach (Session session in active)
                {
                    BridgeSessionInfo info = new BridgeSessionInfo();
                    info.Id = session.Info.Id;
                    info.Client = session.Info.Client;
                    info.Folder = session.Info.Folder;
                    info.Connected = session.Info.Connected;
                    info.Calls = session.Info.Calls;
                    McpServer server = session.Server;
                    info.State = server == null ? "подключается" : server.State;
                    info.Task = server == null ? 0 : server.ActiveTask;
                    status.Sessions.Add(info);
                }
            }
            status.Sessions.Sort(delegate(BridgeSessionInfo left, BridgeSessionInfo right) { return left.Id.CompareTo(right.Id); });
            StringBuilder signature = new StringBuilder(current.ToString(CultureInfo.InvariantCulture));
            foreach (BridgeSessionInfo info in status.Sessions) signature.Append('|').Append(info.State);
            signature.Append('|').Append(status.Recent.Count.ToString(CultureInfo.InvariantCulture));
            status.Signature = signature.ToString();
            return status;
        }

        public void StopTask(int session)
        {
            Session found;
            lock (gate) sessions.TryGetValue(session, out found);
            if (found != null && found.Server != null) found.Server.StopActive();
        }

        public void Disconnect(int session)
        {
            Session found;
            lock (gate) sessions.TryGetValue(session, out found);
            if (found != null) Close(found);
        }

        public void ClearLog()
        {
            lock (gate) calls.Clear();
            Interlocked.Increment(ref version);
        }

        // ---------------------------------------------------------------- журнал

        public void ClientIdentified(int session, string name, string clientVersion)
        {
            lock (gate)
            {
                Session found;
                if (sessions.TryGetValue(session, out found)) found.Info.Client = FriendlyClient(name, clientVersion);
            }
            Interlocked.Increment(ref version);
        }

        public int CallStarted(int session, string tool, string request)
        {
            int id;
            lock (gate)
            {
                BridgeCallInfo call = new BridgeCallInfo();
                call.Id = id = ++nextCall;
                call.Session = session;
                Session found;
                if (sessions.TryGetValue(session, out found))
                {
                    call.Client = found.Info.Client;
                    found.Info.Calls++;
                }
                else call.Client = "агент";
                call.Started = DateTime.Now;
                call.Tool = tool;
                call.Request = request ?? string.Empty;
                call.Outcome = "идёт";
                call.Response = string.Empty;
                calls.Add(call);
                if (calls.Count > MaxCalls) calls.RemoveAt(0);
            }
            Interlocked.Increment(ref version);
            return id;
        }

        public void CallFinished(int id, string outcome, string response)
        {
            lock (gate)
            {
                foreach (BridgeCallInfo call in calls)
                {
                    if (call.Id != id) continue;
                    string text = response ?? string.Empty;
                    call.Duration = DateTime.Now - call.Started;
                    call.Outcome = outcome;
                    call.Truncated = text.Length > MaxResponse;
                    call.Response = call.Truncated ? text.Substring(0, MaxResponse) : text;
                    break;
                }
            }
            Interlocked.Increment(ref version);
        }

        public void StateChanged(int session)
        {
            Interlocked.Increment(ref version);
        }

        private static BridgeCallInfo Copy(BridgeCallInfo call)
        {
            BridgeCallInfo copy = new BridgeCallInfo();
            copy.Id = call.Id;
            copy.Session = call.Session;
            copy.Client = call.Client;
            copy.Started = call.Started;
            copy.Duration = call.Duration;
            copy.Tool = call.Tool;
            copy.Request = call.Request;
            copy.Outcome = call.Outcome;
            copy.Response = call.Response;
            copy.Truncated = call.Truncated;
            return copy;
        }

        /// <summary>Имя клиента из initialize: Codex, Claude Code или то, что прислал клиент.</summary>
        internal static string FriendlyClient(string name, string clientVersion)
        {
            string raw = Clean(name, 40);
            string title = raw.Length == 0 ? "агент"
                : raw.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0 ? "Codex"
                : raw.Equals("claude-code", StringComparison.OrdinalIgnoreCase) ? "Claude Code"
                : raw.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 ? "Claude"
                : raw;
            // «0.155.0-alpha.16.3» не помещается в столбец; сборка и метка выпуска для окна не важны.
            string number = Clean(clientVersion, 40);
            int suffix = number.IndexOfAny(new char[] { '-', '+', ' ' });
            if (suffix > 0) number = number.Substring(0, suffix);
            if (number.Length > 20) number = number.Substring(0, 20);
            return number.Length == 0 ? title : title + " " + number;
        }

        private static string Clean(string value, int limit)
        {
            StringBuilder text = new StringBuilder();
            foreach (char symbol in value ?? string.Empty)
                if (!char.IsControl(symbol)) text.Append(symbol);
            string result = text.ToString().Trim();
            return result.Length > limit ? result.Substring(0, limit) : result;
        }

        // ---------------------------------------------------------------- канал

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr Descriptor;
            internal int Inherit;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
        private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
            uint outBuffer, uint inBuffer, uint defaultTimeout, ref SecurityAttributes security);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int length, out int returned);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Повышены ли права процесса (TokenElevation). null: узнать не удалось.</summary>
        private static bool? IsElevated(IntPtr process)
        {
            IntPtr token;
            if (!OpenProcessToken(process, 0x0008, out token)) return null;
            try
            {
                int elevated, returned;
                if (!GetTokenInformation(token, 20, out elevated, 4, out returned)) return null;
                return elevated != 0;
            }
            finally { CloseHandle(token); }
        }

        /// <summary>
        /// Команды агента выполняются с правами SafePaste. Если трей запущен от имени администратора,
        /// а агент нет, агент получил бы права, которых у него не было, поэтому такая сессия отклоняется.
        /// </summary>
        private static string CheckElevation(NamedPipeServerStream pipe)
        {
            if (IsElevated(GetCurrentProcess()) != true) return null;
            bool? client = null;
            uint clientId;
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out clientId))
            {
                IntPtr process = OpenProcess(0x1000, false, clientId);
                if (process != IntPtr.Zero)
                {
                    try { client = IsElevated(process); }
                    finally { CloseHandle(process); }
                }
            }
            return client == true ? null
                : "SafePaste запущен от имени администратора, а агент нет. Мост не выполняет команды агента с правами администратора: перезапустите SafePaste без повышения прав.";
        }

        /// <summary>
        /// Экземпляр канала: доступ только у этой учётной записи, сетевые клиенты отклоняются.
        /// Первый экземпляр создаётся с FILE_FLAG_FIRST_PIPE_INSTANCE: если имя уже занято чужим
        /// процессом, мост не запускается, а не делит канал с ним.
        /// </summary>
        private static NamedPipeServerStream CreateInstance(string name, bool first)
        {
            RawSecurityDescriptor descriptor = new RawSecurityDescriptor(
                "D:P(A;;GA;;;" + WindowsIdentity.GetCurrent().User.Value + ")");
            byte[] binary = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(binary, 0);
            GCHandle pinned = GCHandle.Alloc(binary, GCHandleType.Pinned);
            try
            {
                SecurityAttributes security = new SecurityAttributes();
                security.Length = Marshal.SizeOf(typeof(SecurityAttributes));
                security.Descriptor = pinned.AddrOfPinnedObject();
                security.Inherit = 0;
                const uint Duplex = 0x3, Overlapped = 0x40000000, FirstInstance = 0x00080000, RejectRemote = 0x8;
                SafePipeHandle handle = CreateNamedPipe(@"\\.\pipe\" + name, Duplex | Overlapped | (first ? FirstInstance : 0u),
                    RejectRemote, 255, 65536, 65536, 0, ref security);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException(error == 5 || error == 231
                        ? "Канал моста уже занят. Возможно, мост запущен в другой копии SafePaste."
                        : "Не удалось открыть канал моста, код " + error.ToString(CultureInfo.InvariantCulture) + ".");
                }
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            finally { pinned.Free(); }
        }
    }
}
