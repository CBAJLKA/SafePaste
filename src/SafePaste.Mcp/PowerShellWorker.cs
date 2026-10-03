using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SafePaste.Mcp
{
    internal sealed class RunTask : IDisposable
    {
        internal int Id;
        internal PowerShell Shell;
        internal IAsyncResult Pending;
        internal readonly StringBuilder Raw = new StringBuilder();
        internal int SentLines;
        internal bool Truncated;
        internal string Error;
        internal bool Finished;
        internal readonly ManualResetEvent Done = new ManualResetEvent(false);
        public void Dispose() { if (Shell != null) Shell.Dispose(); }
    }

    internal interface ICommandWorker : IDisposable
    {
        RunTask Active { get; }
        RunTask Start(string command, int id);
        void Wait(RunTask task, int seconds);
        void Stop(RunTask task);
    }

    internal sealed class PowerShellWorker : ICommandWorker
    {
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Get-ChildItem", "Get-Item", "Get-Content", "Test-Path", "Resolve-Path", "Split-Path", "Join-Path",
            "Get-Location", "Set-Location", "Get-Service", "Get-Process", "Get-HotFix", "Get-ComputerInfo",
            "Get-TimeZone", "Get-Date", "Get-EventLog", "Get-WinEvent", "Get-CimInstance", "Resolve-DnsName",
            "Get-DnsClientServerAddress", "Get-DnsClientCache", "Get-NetIPAddress", "Get-NetIPConfiguration",
            "Get-NetRoute", "Get-NetTCPConnection", "Get-NetAdapter", "Test-NetConnection", "Test-Connection",
            "Select-Object", "Sort-Object", "Where-Object", "Group-Object", "Measure-Object", "Select-String",
            "Format-List", "Format-Table", "Out-String", "ConvertTo-Json", "ConvertTo-Csv", "Get-FileHash",
            "Get-Unique", "Write-Output"
        };
        private static readonly HashSet<string> Programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ping", "tracert", "pathping", "nslookup", "getmac", "netstat", "whoami", "systeminfo", "tasklist", "driverquery", "ipconfig", "arp", "hostname" };
        private readonly FileTools files;
        private readonly bool restricted;
        private Runspace runspace;
        private readonly object gate = new object();
        public RunTask Active { get; private set; }
        internal int Visible;

        internal PowerShellWorker(FileTools files) : this(files, true) { }
        internal PowerShellWorker(FileTools files, bool restricted) { this.files = files; this.restricted = restricted; }

        private void Open()
        {
            if (runspace != null) return;
            InitialSessionState state = InitialSessionState.CreateDefault2();
            state.ImportPSModule(new string[] { "Microsoft.PowerShell.Management", "Microsoft.PowerShell.Utility",
                "Microsoft.PowerShell.Diagnostics", "CimCmdlets", "DnsClient", "NetTCPIP", "NetAdapter" });
            runspace = RunspaceFactory.CreateRunspace(state);
            runspace.Open();
            SessionStateProxy proxy = runspace.SessionStateProxy;
            proxy.Path.SetLocation(files.CurrentDirectory);
            if (!restricted) return;
            proxy.SetVariable("PSModuleAutoLoadingPreference", "None");
            foreach (CommandInfo command in proxy.InvokeCommand.GetCommands("*", CommandTypes.Alias | CommandTypes.Function
                | CommandTypes.Filter | CommandTypes.Cmdlet, true))
            {
                if (Allowed.Contains(command.Name)) Visible++;
                else command.Visibility = SessionStateEntryVisibility.Private;
            }
            proxy.Applications.Clear();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            foreach (string program in Programs) proxy.Applications.Add(Path.Combine(system, program + ".exe"));
            proxy.Scripts.Clear();
            foreach (string drive in new string[] { "Env", "Variable", "Function", "Alias", "HKLM", "HKCU", "Cert", "WSMan" })
            {
                try { proxy.Drive.Remove(drive, true, null); }
                catch (Exception) { }
            }
            proxy.LanguageMode = PSLanguageMode.NoLanguage;
        }

        internal static void CheckProgram(string program, IList<string> args)
        {
            string name = Path.GetFileNameWithoutExtension(program);
            if (!Programs.Contains(name)) throw new ArgumentException("Команда не разрешена: " + name);
            if (name.Equals("hostname", StringComparison.OrdinalIgnoreCase) && args.Count > 0)
                throw new ArgumentException("hostname запускается без аргументов.");
            if (name.Equals("ipconfig", StringComparison.OrdinalIgnoreCase))
                foreach (string arg in args) if (!new string[] { "/all", "/displaydns", "/allcompartments", "/?" }.ContainsIgnoreCase(arg))
                    throw new ArgumentException("Недопустимый аргумент ipconfig.");
            if (name.Equals("arp", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count == 0 || (args[0] != "-a" && args[0] != "-g")) throw new ArgumentException("Для arp разрешены только -a и -g.");
                for (int i = 1; i < args.Count; i++) if (args[i].StartsWith("-") && args[i] != "-N")
                    throw new ArgumentException("Недопустимый аргумент arp.");
            }
        }

        private static bool IsPath(string value)
        {
            return value == "." || value == ".." || value.StartsWith("~") || value.IndexOf('\\') >= 0
                || value.IndexOf('/') >= 0 || Regex.IsMatch(value, @"^[A-Za-z]:");
        }

        /// <summary>Какие команды и программы есть в ограниченном профиле: для sp_status и сообщений об отказе.</summary>
        internal static string DescribeRestricted()
        {
            List<string> commands = new List<string>(Allowed);
            commands.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> programs = new List<string>(Programs);
            programs.Sort(StringComparer.OrdinalIgnoreCase);
            return "Команды ограниченного профиля: " + string.Join(", ", commands.ToArray())
                + ".\nПрограммы: " + string.Join(", ", programs.ToArray())
                + ".\nФильтр в краткой форме: Where-Object IPAddress -notlike '127.*', несколько условий цепочкой Where-Object."
                + " Блоки { }, переменные ($_) и выражения не принимаются; для произвольного скрипта profile: full.";
        }

        /// <summary>
        /// Проверка команды ограниченного профиля без запуска. Мост вызывает её до старта воркера,
        /// чтобы агент получил причину отказа, а не только код выхода процесса.
        /// </summary>
        internal static void Check(string command, FileTools files)
        {
            using (PowerShell shell = new PowerShellWorker(files).Parse(command)) { }
        }

        private PowerShell Parse(string command)
        {
            Token[] tokens;
            ParseError[] errors;
            ScriptBlockAst parsed = Parser.ParseInput(command, out tokens, out errors);
            if (errors.Length != 0)
                throw new ArgumentException("Команда содержит синтаксическую ошибку PowerShell.");
            if (parsed.BeginBlock != null || parsed.ProcessBlock != null || parsed.EndBlock == null
                || parsed.EndBlock.Statements.Count != 1 || !(parsed.EndBlock.Statements[0] is PipelineAst))
                throw new ArgumentException("Разрешён только один конвейер команд: без ; и нескольких строк.");
            PipelineAst pipeline = (PipelineAst)parsed.EndBlock.Statements[0];
            foreach (CommandBaseAst element in pipeline.PipelineElements)
                if (element.Redirections.Count != 0) throw new ArgumentException("Перенаправление вывода запрещено.");
            PowerShell shell;
            try { shell = ScriptBlock.Create(command).GetPowerShell(); }
            catch (Exception)
            {
                throw new ArgumentException("Ограниченный профиль принимает только конвейер команд с постоянными аргументами:"
                    + " без блоков { }, переменных вроде $_ и выражений. Фильтр пишите в краткой форме:"
                    + " Where-Object IPAddress -notlike '127.*', несколько условий цепочкой Where-Object."
                    + " Для произвольного скрипта укажите profile: full.");
            }
            try { Validate(shell); }
            catch { shell.Dispose(); throw; }
            return shell;
        }

        internal void Validate(PowerShell shell)
        {
            foreach (Command command in shell.Commands.Commands)
            {
                string name = command.CommandText;
                string baseName = Path.GetFileNameWithoutExtension(name);
                bool external = Programs.Contains(baseName);
                if (external && (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf(':') >= 0))
                    throw new ArgumentException("Внешняя программа запускается только по имени из списка.");
                if (!external && !Allowed.Contains(name))
                    throw new ArgumentException("Команда не разрешена в ограниченном профиле: " + name
                        + ". Список команд показывает sp_status, для остального нужен profile: full.");
                List<string> args = new List<string>();
                string locationPath = null;
                foreach (CommandParameter parameter in command.Parameters)
                {
                    if (parameter.Value == null) continue;
                    string value = Convert.ToString(parameter.Value, CultureInfo.InvariantCulture);
                    args.Add(value);
                    if (name.Equals("Set-Location", StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrEmpty(parameter.Name) || parameter.Name.Equals("Path", StringComparison.OrdinalIgnoreCase)
                            || parameter.Name.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase))) locationPath = value;
                    if (Regex.IsMatch(value, @"(?i)(^|\W)(env|variable|function|alias|hklm|hkcu|cert|wsman):")
                        || value.Contains("::") || Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9]+:"))
                        throw new ArgumentException("Доступ к этому провайдеру или типу закрыт.");
                    if (value.StartsWith("~")) throw new ArgumentException("Путь с ~ не разрешён.");
                    if (IsPath(value) && !external) files.CheckPath(value);
                }
                if (external) CheckProgram(name, args);
                if (name.Equals("Set-Location", StringComparison.OrdinalIgnoreCase))
                {
                    if (locationPath == null) throw new ArgumentException("Для Set-Location нужен путь внутри разрешённых корней.");
                    files.CheckPath(locationPath);
                }
            }
        }

        public RunTask Start(string command, int id)
        {
            lock (gate)
            {
                if (Active != null && !Active.Finished) throw new InvalidOperationException("Уже выполняется задача " + Active.Id + ".");
                if (Active != null) { Active.Dispose(); Active = null; }
                Open();
                PowerShell shell;
                if (restricted) shell = Parse(command);
                else { shell = PowerShell.Create(); shell.AddScript(command); }
                shell.Runspace = runspace;
                foreach (Command item in shell.Commands.Commands)
                    item.MergeMyResults(PipelineResultTypes.Error | PipelineResultTypes.Warning, PipelineResultTypes.Output);
                shell.AddCommand("Out-String").AddParameter("Stream").AddParameter("Width", 4096);
                PSDataCollection<PSObject> output = new PSDataCollection<PSObject>();
                RunTask task = new RunTask(); task.Id = id; task.Shell = shell;
                output.DataAdded += delegate(object sender, DataAddedEventArgs args)
                {
                    lock (task.Raw)
                    {
                        if (task.Raw.Length >= 2000000) { task.Truncated = true; ThreadPool.QueueUserWorkItem(delegate { try { shell.Stop(); } catch (Exception) { } }); return; }
                        task.Raw.Append(output[args.Index]).Append('\n');
                    }
                };
                try { task.Pending = shell.BeginInvoke<PSObject, PSObject>(null, output); }
                catch { shell.Dispose(); throw; }
                Active = task;
                return task;
            }
        }

        public void Wait(RunTask task, int seconds)
        {
            if (task.Finished) return;
            if (!task.Pending.AsyncWaitHandle.WaitOne(Math.Max(0, Math.Min(seconds, 120)) * 1000)) return;
            lock (gate)
            {
                if (task.Finished) return;
                try { task.Shell.EndInvoke(task.Pending); }
                catch (Exception) { task.Error = "Команда завершилась с ошибкой."; }
                task.Finished = true;
                task.Done.Set();
                try { files.CurrentDirectory = runspace.SessionStateProxy.Path.CurrentLocation.Path; }
                catch (Exception) { }
            }
        }

        public void Stop(RunTask task)
        {
            if (!task.Finished) { try { task.Shell.Stop(); } catch (Exception) { } Wait(task, 1); }
        }

        public void Dispose() { if (Active != null) Active.Dispose(); if (runspace != null) runspace.Dispose(); }
    }

    internal static class StringSetExtensions
    {
        internal static bool ContainsIgnoreCase(this IEnumerable<string> source, string value)
        {
            foreach (string item in source) if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
