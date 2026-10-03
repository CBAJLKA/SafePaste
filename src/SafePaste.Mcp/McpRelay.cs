using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    /// <summary>
    /// SafePaste.exe --mcp: разъём между агентом и мостом внутри SafePaste. Сам ничего не выполняет
    /// и реальных значений не видит: пересылает строки протокола в канал моста и обратно.
    /// </summary>
    internal static class McpRelay
    {
        internal const int StartWaitMs = 15000;

        /// <summary>
        /// appInstance: обычная папка данных, значит мостом владеет SafePaste в трее, и его можно
        /// запустить. С другой папкой данных (проверки) мост поднимает только --bridge-host.
        /// </summary>
        internal static int Run(Stream input, Stream output, string pipeName, bool appInstance, int waitMs)
        {
            string problem;
            NamedPipeClientStream pipe = Connect(pipeName, appInstance, waitMs, out problem);
            if (pipe == null)
            {
                AnswerErrors(input, output, problem);
                return 1;
            }
            using (pipe)
            {
                byte[] hello = new UTF8Encoding(false).GetBytes(Hello() + "\n");
                pipe.Write(hello, 0, hello.Length);
                pipe.Flush();
                ManualResetEvent ended = new ManualResetEvent(false);
                StartCopy(pipe, output, ended);
                StartCopy(input, pipe, ended);
                // Одна сторона закрылась: закрытый канал сообщает мосту, что агент ушёл,
                // а выход процесса сообщает клиенту, что мост отключил сессию.
                ended.WaitOne();
            }
            return 0;
        }

        private static string Hello()
        {
            string folder = string.Empty;
            try { folder = Environment.CurrentDirectory; }
            catch (Exception) { }
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "safepaste", "hello" }, { "version", 1 }, { "cwd", folder } });
        }

        private static void StartCopy(Stream from, Stream to, ManualResetEvent ended)
        {
            Thread thread = new Thread(delegate()
            {
                byte[] buffer = new byte[65536];
                try
                {
                    int count;
                    while ((count = from.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        to.Write(buffer, 0, count);
                        to.Flush();
                    }
                }
                catch (Exception) { }
                finally { ended.Set(); }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private static NamedPipeClientStream Connect(string name, bool appInstance, int waitMs, out string problem)
        {
            problem = null;
            NamedPipeClientStream pipe = TryConnect(name, 300);
            if (pipe == null)
            {
                BridgeSettings settings = BridgeSettings.Load();
                if (settings.Problem == null && !settings.Enabled)
                {
                    problem = "Мост выключен в SafePaste. Включите его: значок SafePaste в трее, пункт «Мост для агентов».";
                    return null;
                }
                if (!appInstance)
                {
                    problem = "Мост SafePaste для этой папки данных не запущен.";
                    return null;
                }
                bool running = IsAppRunning();
                if (!running)
                {
                    try { RestrictedProcess.StartApplication(Assembly.GetEntryAssembly().Location); }
                    catch (Exception)
                    {
                        problem = "Не удалось запустить SafePaste. Запустите его вручную, мост работает внутри приложения.";
                        return null;
                    }
                }
                pipe = TryConnect(name, running ? Math.Min(waitMs, 5000) : waitMs);
                if (pipe == null)
                {
                    problem = running
                        ? "SafePaste запущен, но мост не отвечает. Откройте окно моста в SafePaste или перезапустите приложение."
                        : "SafePaste не успел запустить мост. Повторите подключение.";
                    return null;
                }
            }
            if (!IsOwnServer(pipe))
            {
                pipe.Dispose();
                problem = "Канал моста открыт чужой программой. Подключение отменено.";
                return null;
            }
            return pipe;
        }

        private static NamedPipeClientStream TryConnect(string name, int waitMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (true)
            {
                NamedPipeClientStream pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    int left = (int)Math.Max(1, Math.Min(250, (until - DateTime.UtcNow).TotalMilliseconds));
                    pipe.Connect(left);
                    return pipe;
                }
                catch (Exception) { pipe.Dispose(); }
                if (DateTime.UtcNow >= until) return null;
                Thread.Sleep(100);
            }
        }

        private static bool IsAppRunning()
        {
            Mutex existing;
            if (!Mutex.TryOpenExisting(Program.InstanceName, out existing)) return false;
            existing.Dispose();
            return true;
        }

        /// <summary>Сообщение агенту, если моста нет: клиент покажет его вместо молчаливого сбоя.</summary>
        internal static void AnswerErrors(Stream input, Stream output, string message)
        {
            StreamWriter writer = new StreamWriter(output, new UTF8Encoding(false));
            writer.NewLine = "\n";
            AnswerErrors(new StreamReader(input, new UTF8Encoding(false)), writer, message);
        }

        /// <summary>Каждый запрос получает ошибку с объяснением, пока клиент не закроет поток.</summary>
        internal static void AnswerErrors(TextReader reader, TextWriter writer, string message)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Dictionary<string, object> request;
                    try { request = json.DeserializeObject(line) as Dictionary<string, object>; }
                    catch (Exception) { continue; }
                    object id;
                    if (request == null || !request.TryGetValue("id", out id)) continue;
                    writer.WriteLine(json.Serialize(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id },
                        { "error", new Dictionary<string, object> { { "code", -32000 }, { "message", message } } } }));
                    writer.Flush();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// Имена каналов общие для всех пользователей машины. Другой пользователь мог занять имя
        /// раньше SafePaste, поэтому разъём проверяет, что канал держит процесс этой учётной записи.
        /// </summary>
        internal static bool IsOwnServer(NamedPipeClientStream pipe)
        {
            uint processId;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out processId)) return false;
            IntPtr process = OpenProcess(0x1000, false, processId);
            if (process == IntPtr.Zero) return false;
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process, 0x0008, out token)) return false;
                using (WindowsIdentity owner = new WindowsIdentity(token))
                using (WindowsIdentity self = WindowsIdentity.GetCurrent())
                    return owner.User != null && owner.User.Equals(self.User);
            }
            catch (Exception) { return false; }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(process);
            }
        }
    }
}
