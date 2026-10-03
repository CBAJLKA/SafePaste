using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    // A second process of the same SafePaste.exe executes PowerShell with a token
    // that has all optional Windows privileges disabled. The MCP process retains
    // the protocol and the anonymizer. Raw output stays on private inherited pipes.
    internal sealed class ExternalWorker : ICommandWorker
    {
        // Several agents share the tray process. A worker inherits every inheritable handle that
        // exists while it starts, so pipe ends of one session must not leak into another worker.
        private static readonly object SpawnGate = new object();
        private readonly FileTools files;
        private readonly bool restricted;
        private Process process;
        private IntPtr desktop;
        private AnonymousPipeServerStream input;
        private AnonymousPipeServerStream output;
        public RunTask Active { get; private set; }

        internal ExternalWorker(FileTools files, bool restricted)
        { this.files = files; this.restricted = restricted; }

        public RunTask Start(string command, int id)
        {
            if (Active != null && !Active.Finished) throw new InvalidOperationException("Уже выполняется задача " + Active.Id + ".");
            Cleanup();
            try
            {
                string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SafePaste.exe");
                string arguments = "--worker --data-dir " + Quote(Paths.DataDirectory) + " --roots " + Quote(files.RootsArgument)
                    + " --cwd " + Quote(files.CurrentDirectory)
                    + (restricted ? "" : " --full");
                lock (SpawnGate)
                {
                    input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                    output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                    try
                    {
                        process = RestrictedProcess.Start(executable, arguments,
                            input.GetClientHandleAsString(), output.GetClientHandleAsString(), out desktop);
                    }
                    finally
                    {
                        input.DisposeLocalCopyOfClientHandle();
                        output.DisposeLocalCopyOfClientHandle();
                    }
                }
                using (StreamWriter writer = new StreamWriter(input, new UTF8Encoding(false)))
                { writer.Write(command); writer.Flush(); }
                input = null;
                RunTask task = new RunTask(); task.Id = id;
                Active = task;
                Thread reader = new Thread(delegate()
                {
                    try
                    {
                        using (StreamReader stream = new StreamReader(output, new UTF8Encoding(false)))
                        {
                            string line;
                            while ((line = stream.ReadLine()) != null)
                            {
                                lock (task.Raw)
                                {
                                    if (task.Raw.Length + line.Length + 1 > 2000000) { task.Truncated = true; break; }
                                    task.Raw.Append(line).Append('\n');
                                }
                            }
                        }
                        try
                        {
                            process.WaitForExit();
                            if (process.ExitCode != 0) task.Error = "Воркер завершился с ошибкой.";
                            else if (restricted) UpdateLocation(command);
                        }
                        catch (InvalidOperationException)
                        {
                            lock (task.Raw) if (task.Raw.Length == 0) task.Error = "Воркер завершился без вывода.";
                        }
                    }
                    catch (Exception error) { task.Error = "Не удалось получить вывод воркера: " + error.GetType().Name + "."; }
                    finally { task.Finished = true; task.Done.Set(); }
                });
                reader.IsBackground = true;
                reader.Start();
                return task;
            }
            catch { Cleanup(); throw; }
        }

        private static string Quote(string value)
        { return "\"" + value + (value.EndsWith("\\", StringComparison.Ordinal) ? "\\" : "") + "\""; }

        private void UpdateLocation(string command)
        {
            try
            {
                using (PowerShell parsed = ScriptBlock.Create(command).GetPowerShell())
                {
                    foreach (Command item in parsed.Commands.Commands)
                    {
                        if (!item.CommandText.Equals("Set-Location", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (CommandParameter parameter in item.Parameters)
                        {
                            if (parameter.Value == null) continue;
                            if (!string.IsNullOrEmpty(parameter.Name)
                                && !parameter.Name.Equals("Path", StringComparison.OrdinalIgnoreCase)
                                && !parameter.Name.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase)) continue;
                            string next = files.CheckPath(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture));
                            if (Directory.Exists(next)) files.CurrentDirectory = next;
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        public void Wait(RunTask task, int seconds)
        { task.Done.WaitOne(Math.Max(0, Math.Min(seconds, 120)) * 1000); }

        public void Stop(RunTask task)
        {
            Process running = process;
            if (!task.Finished && task == Active && running != null)
            { try { running.Kill(); } catch (Exception) { } task.Done.WaitOne(5000); }
        }

        private void Cleanup()
        {
            if (process != null)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); process.WaitForExit(5000); }
                    catch (Exception) { }
                }
                process.Dispose(); process = null;
            }
            if (input != null) { input.Dispose(); input = null; }
            if (output != null) { output.Dispose(); output = null; }
            if (desktop != IntPtr.Zero) { RestrictedProcess.CloseDesktopHandle(desktop); desktop = IntPtr.Zero; }
            if (Active != null) { Active.Dispose(); Active = null; }
        }

        public void Dispose() { Cleanup(); }
    }

    internal static class RestrictedProcess
    {
        private const int TokenAllAccess = 0xF01FF;
        private const uint DisableMaxPrivilege = 0x1;
        private const int UseStdHandles = 0x100;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal int cb;
            internal IntPtr reserved;
            internal IntPtr desktop;
            internal IntPtr title;
            internal int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
            internal short showWindow, reserved2;
            internal IntPtr reservedBytes, stdInput, stdOutput, stdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            internal IntPtr process, thread;
            internal int processId, threadId;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, int disableSidCount, IntPtr disableSids,
            int deletePrivilegeCount, IntPtr deletePrivileges, int restrictSidCount, IntPtr restrictSids, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessAsUserW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder command,
            IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation information);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDesktopW")]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr attributes);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
            ref StartupInfo startup, out ProcessInformation information);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        internal static void CloseDesktopHandle(IntPtr desktop) { CloseDesktop(desktop); }

        // Starts the tray application for an agent connector. Agent clients may kill the whole job
        // of an MCP server and trim its environment, while the tray has to outlive the session
        // and give PowerShell workers the normal user environment. No handles are inherited, so
        // the client still sees the end of the connector's stdout when the connector exits.
        internal static void StartApplication(string executable)
        {
            IntPtr token = IntPtr.Zero, environment = IntPtr.Zero, desktopName = IntPtr.Zero;
            try
            {
                if (OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0008, out token)
                    && !CreateEnvironmentBlock(out environment, token, false)) environment = IntPtr.Zero;
                StartupInfo start = new StartupInfo();
                start.cb = Marshal.SizeOf(typeof(StartupInfo));
                desktopName = Marshal.StringToHGlobalUni("WinSta0\\Default");
                start.desktop = desktopName;
                uint flags = environment != IntPtr.Zero ? 0x00000400u : 0u;
                string folder = Path.GetDirectoryName(executable);
                ProcessInformation result;
                bool started = CreateProcess(executable, new StringBuilder("\"" + executable + "\""), IntPtr.Zero, IntPtr.Zero,
                    false, flags | 0x01000000u, environment, folder, ref start, out result);
                if (!started && Marshal.GetLastWin32Error() == 5)
                    started = CreateProcess(executable, new StringBuilder("\"" + executable + "\""), IntPtr.Zero, IntPtr.Zero,
                        false, flags, environment, folder, ref start, out result);
                if (!started) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(result.thread);
                CloseHandle(result.process);
            }
            finally
            {
                if (desktopName != IntPtr.Zero) Marshal.FreeHGlobal(desktopName);
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        internal static Process Start(string executable, string arguments, string inputHandle, string outputHandle,
            out IntPtr desktop)
        {
            desktop = IntPtr.Zero;
            IntPtr original = IntPtr.Zero, reduced = IntPtr.Zero;
            IntPtr desktopName = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenAllAccess, out original))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (!CreateRestrictedToken(original, DisableMaxPrivilege, 0, IntPtr.Zero, 0, IntPtr.Zero,
                    0, IntPtr.Zero, out reduced))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                StartupInfo start = new StartupInfo();
                start.cb = Marshal.SizeOf(typeof(StartupInfo));
                start.flags = UseStdHandles;
                start.stdInput = new IntPtr(long.Parse(inputHandle, CultureInfo.InvariantCulture));
                start.stdOutput = new IntPtr(long.Parse(outputHandle, CultureInfo.InvariantCulture));
                start.stdError = start.stdOutput;
                string name = "SafePasteWorker-" + Guid.NewGuid().ToString("N");
                desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
                if (desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                desktopName = Marshal.StringToHGlobalUni("WinSta0\\" + name);
                start.desktop = desktopName;
                ProcessInformation result;
                StringBuilder command = new StringBuilder("\"" + executable + "\" " + arguments);
                if (!CreateProcessAsUser(reduced, executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    0, IntPtr.Zero, Path.GetDirectoryName(executable), ref start, out result))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    Process managed = Process.GetProcessById(result.processId);
                    IntPtr opened = managed.Handle;
                    return managed;
                }
                finally { CloseHandle(result.thread); CloseHandle(result.process); }
            }
            finally
            {
                if (desktopName != IntPtr.Zero) Marshal.FreeHGlobal(desktopName);
                if (reduced != IntPtr.Zero) CloseHandle(reduced);
                if (original != IntPtr.Zero) CloseHandle(original);
            }
        }
    }
}
