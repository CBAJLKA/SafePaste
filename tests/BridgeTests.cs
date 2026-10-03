using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Management.Automation;
using System.Management.Automation.Language;
using SafePaste.Bridge;
using SafePaste.Detecting;
using SafePaste.Mcp;
using SafePaste.Storage;

namespace SafePaste.Tests
{
    internal sealed class TestApproval : IApprovalGate
    {
        internal bool Allowed;
        internal string LastDetails;
        public bool Approve(string kind, string title, string details)
        { LastDetails = details; return Allowed; }
        public void Cancel() { }
    }

    public static partial class TestProgram
    {
        private static void BridgeTests()
        {
            Section("Мост MCP");
            string sample = "DC01 FOC1234ABCD 255.255.255.0 127.0.0.1 S-1-5-18 00000000-0000-0000-0000-000000000000 \\\\fs01\\Share\\x C:\\Users\\ivanov\\a.txt password=Secret123!";
            string hidden = Replacer.Apply(sample, Detector.Scan(sample, new SafePasteDatabase(), ControlMode.Bridge, false)).Text;
            Check("мост скрывает догадки", !hidden.Contains("DC01") && !hidden.Contains("FOC1234ABCD"), hidden);
            Check("мост сохраняет служебные значения", hidden.Contains("255.255.255.0") && hidden.Contains("127.0.0.1")
                && hidden.Contains("S-1-5-18") && hidden.Contains("00000000-0000-0000-0000-000000000000"), hidden);
            Check("мост скрывает части UNC и пользователя", hidden.Contains("[HOST_") && hidden.Contains("[SHARE_")
                && hidden.Contains("[USER_") && !hidden.Contains("ivanov") && !hidden.Contains("[PATH_"), hidden);
            Check("мост скрывает пароль", !hidden.Contains("Secret123!"), hidden);
            Check("режим моста не выбирается из настроек", ControlModes.Parse("Bridge", ControlMode.Balanced) == ControlMode.Balanced, "");
            string pem = "PRIVATE KEY=abc\ndef";
            Detection multi = new Detection(12, 7, "abc\ndef", "PRIVATE_KEY", Confidence.High, 100);
            ReplacementResult replaced = Replacer.Apply(pem, new List<Detection> { multi }, null, true);
            Check("замена сохраняет переводы строк", replaced.Text.Split('\n').Length == pem.Split('\n').Length
                && replaced.Applied.Count == 1 && replaced.Applied[0].Placeholder == "[PRIVATE_KEY_1]", replaced.Text);

            string labelPath = Path.Combine(Paths.DataDirectory, "bridge-test-labels.dat");
            LabelStore store = new LabelStore(labelPath, true);
            string host = store.Hide("fs01.corp.example", "FQDN");
            string secret = store.Hide("DoNotPersist123!", "SECRET");
            LabelStore second = new LabelStore(labelPath, true);
            string value; bool isSecret;
            Check("метки общие между процессами", second.TryResolve(host, out value, out isSecret) && value == "fs01.corp.example", "");
            Check("секрет не переживает перезапуск", !second.TryResolve(secret, out value, out isSecret)
                && store.TryResolve(secret, out value, out isSecret) && isSecret, "");
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(labelPath), null, DataProtectionScope.CurrentUser);
            Check("секрет отсутствует в файле", !Encoding.UTF8.GetString(plain).Contains("DoNotPersist123!"), "");
            Array.Clear(plain, 0, plain.Length);
            string other = second.Hide("db02.corp.example", "FQDN");
            Check("последовательные записи не теряются", store.TryResolve(other, out value, out isSecret)
                && store.TryResolve(host, out value, out isSecret), "");
            string conflictFile = Path.Combine(Paths.DataDirectory, "bridge-conflict.dat");
            LabelStore conflict = new LabelStore(conflictFile, true);
            conflict.Hide("alpha", "HOST");
            SafePasteDatabase user = new SafePasteDatabase();
            user.Reserve("HOST", "beta", 1);
            Detection beta = new Detection(0, 4, "beta", "HOST", Confidence.Learned, 73);
            ReplacementResult reserved = conflict.Apply("beta", new List<Detection> { beta }, user, false);
            Check("закрепление пользователя важнее старой метки", reserved.Text == "[HOST_1]"
                && conflict.TryResolve("[HOST_1]", out value, out isSecret) && value == "beta", reserved.Text);
            string ephemeralFile = Path.Combine(Paths.DataDirectory, "bridge-no-persist.dat");
            LabelStore ephemeral = new LabelStore(ephemeralFile, false);
            ephemeral.Hide("temporary", "TEXT");
            Check("режим без файла не пишет метки", !File.Exists(ephemeralFile), "");
            string removable = store.Hide("temporary-bridge-host", "HOST");
            Check("метку можно удалить", store.Delete(removable) && !store.TryResolve(removable, out value, out isSecret), "");
            Check("номер можно закрепить", store.Reserve(host) && SafePasteDatabase.Load().GetReservedIndex("FQDN", "fs01.corp.example") > 0, "");
            string expiredFile = Path.Combine(Paths.DataDirectory, "bridge-expired.dat");
            string oldJson = "{\"Labels\":[{\"Type\":\"HOST\",\"Value\":\"old-host\",\"Index\":1,\"Used\":\"2020-01-01\"}]}";
            File.WriteAllBytes(expiredFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(oldJson), null, DataProtectionScope.CurrentUser));
            Check("старые метки удаляются", new LabelStore(expiredFile, true).Snapshot().Count == 0, "");
            Check("нет файла - нет общей базы", LabelStore.OpenIfExists() == null, "");

            Anonymizer anonymizer = new Anonymizer(store);
            string safe = anonymizer.Anonymize("[HOST_1] fs01.corp.example");
            Check("чужая метка экранируется, известное скрывается", safe.Contains("[~HOST_1]")
                && safe.Contains(host) && !safe.Contains("fs01.corp.example"), safe);
            List<LabelEntry> entries = store.Snapshot();
            string warning;
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("fs01.corp.example"));
            Check("сторож скрывает base64", LeakGuard.Check(encoded, entries, out warning).Contains("[ENCODED:FQDN_"), "");
            Check("сторож скрывает значение внутри слова", LeakGuard.Check("prefixfs01.corp.examplesuffix", entries, out warning).Contains(host), "");
            Check("сторож скрывает разбивку", LeakGuard.Check("f s 0 1 . c o r p . e x a m p l e", entries, out warning).Contains("[SPLIT:FQDN_"), "");
            Check("сторож сохраняет метку", LeakGuard.Check(host, entries, out warning) == host, "");

            Rehydrator hydration = new Rehydrator(store);
            string actual, problem;
            Check("метка подставляется в одинарной строке", hydration.TryRehydrate("Write-Output '" + host + "'", out actual, out problem)
                && actual.Contains("fs01.corp.example"), actual);
            Check("неизвестная метка отклоняется", !hydration.TryRehydrate("Write-Output '[FQDN_999999]'", out actual, out problem), problem);
            Check("секретная метка отклоняется", !hydration.TryRehydrate("Write-Output '" + secret + "'", out actual, out problem), problem);
            Check("буквальная метка остаётся текстом", hydration.TryRehydrate("Write-Output '[~HOST_1]'", out actual, out problem)
                && actual.Contains("'[HOST_1]'"), actual);
            string hostile = store.Hide("x'; Remove-Item C:\\ -Recurse; '", "TEXT");
            Check("значение не становится кодом", hydration.TryRehydrate("Write-Output '" + hostile + "'", out actual, out problem)
                && actual.Contains("x''; Remove-Item") && actual.Contains("-Recurse; ''"), actual);
            using (PowerShell parsedHostile = ScriptBlock.Create(actual).GetPowerShell())
                Check("подставленный текст остаётся одним аргументом", parsedHostile.Commands.Commands.Count == 1
                    && parsedHostile.Commands.Commands[0].Parameters.Count == 1
                    && Convert.ToString(parsedHostile.Commands.Commands[0].Parameters[0].Value) == "x'; Remove-Item C:\\ -Recurse; '", actual);
            Check("метка в переменной отклоняется", !hydration.TryRehydrate("Write-Output $" + host, out actual, out problem), problem);
            string apostrophe = store.Hide("O\u2019Neil", "PERSON");
            Check("типографская кавычка экранируется", hydration.TryRehydrate("Write-Output '" + apostrophe + "'", out actual, out problem)
                && actual.Contains("O\u2019\u2019Neil"), actual);
            string expandable = store.Hide("x$`\"z", "TEXT");
            Check("двойная строка экранирует специальные знаки", hydration.TryRehydrate("Write-Output \"" + expandable + "\"", out actual, out problem)
                && actual.Contains("`$") && actual.Contains("``") && actual.Contains("`\""), actual);

            string folder = Path.Combine(Paths.DataDirectory, "bridge-files");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "demo.txt");
            File.WriteAllText(file, "server=fs01.corp.example\n", new UTF8Encoding(false));
            FileTools files = new FileTools(folder, store, anonymizer);
            Check("чтение файла обезличено", files.Read(file, 1, 10).Contains(host), "");
            Check("поиск разрешает метки", files.Find(host, folder, "*", false, false, 10).Contains("demo.txt"), "");
            bool denied = false;
            try { files.CheckPath(Paths.DataDirectory); }
            catch (UnauthorizedAccessException) { denied = true; }
            Check("выход за корни отклоняется", denied, "");
            File.WriteAllBytes(Path.Combine(folder, "binary.dat"), new byte[] { 65, 0, 66 });
            denied = false;
            try { files.Read(Path.Combine(folder, "binary.dat"), 1, 10); }
            catch (IOException) { denied = true; }
            Check("двоичный файл отклоняется", denied, "");
            string large = Path.Combine(folder, "large.txt");
            File.WriteAllBytes(large, new byte[2097153]);
            denied = false;
            try { files.Read(large, 1, 10); }
            catch (IOException) { denied = true; }
            Check("файл больше 2 МБ отклоняется", denied, "");
            Check("список файлов обезличен", files.List(folder, 1, "*").Contains("demo.txt"), "");
            // Файл не в UTF-8 читается в кодировке ANSI системы; проверка имеет смысл, где это cp1251.
            bool ansi1251 = Encoding.Default.CodePage == 1251;
            string cp = Path.Combine(folder, "cp1251.txt");
            File.WriteAllBytes(cp, Encoding.GetEncoding(1251).GetBytes("Привет мир"));
            if (ansi1251) Check("файл cp1251 читается", files.Read(cp, 1, 10).Contains("Привет мир"), "");

            TestApproval approval = new TestApproval();
            FileEditor editor = new FileEditor(files, store, anonymizer, approval);
            string editText = "server=" + host;
            Check("правка без разрешения не пишет файл", editor.Edit(file, editText, "server=localhost")
                .Contains("отклонена") && File.ReadAllText(file).Contains("fs01.corp.example"), "");
            Check("подтверждение показывает реальные значения", approval.LastDetails.Contains("fs01.corp.example"), "");
            approval.Allowed = true;
            editor.Edit(file, editText, "server=localhost");
            Check("правка меняет только выбранный фрагмент", File.ReadAllText(file) == "server=localhost\n", "");
            editor.Write(file, "server=" + host + "\n");
            Check("запись подставляет метку", File.ReadAllText(file) == "server=fs01.corp.example\n", "");
            denied = false;
            try { editor.Edit(file, "[FQDN_1", "x"); }
            catch (ArgumentException) { denied = true; }
            Check("граница внутри метки отклонена", denied, "");
            denied = false;
            try { editor.Write(file, "password=" + secret); }
            catch (ArgumentException) { denied = true; }
            Check("секрет нельзя добавлять при записи", denied, "");
            string formatted = Path.Combine(folder, "formatted.txt");
            byte[] formattedBody = Encoding.UTF8.GetBytes("server=fs01.corp.example\r\nnext=ok\r\n");
            byte[] formattedBytes = new byte[formattedBody.Length + 3];
            formattedBytes[0] = 0xEF; formattedBytes[1] = 0xBB; formattedBytes[2] = 0xBF;
            Buffer.BlockCopy(formattedBody, 0, formattedBytes, 3, formattedBody.Length);
            File.WriteAllBytes(formatted, formattedBytes);
            editor.Edit(formatted, "server=" + host, "server=localhost");
            byte[] afterFormat = File.ReadAllBytes(formatted);
            Check("правка сохраняет BOM и CRLF", afterFormat[0] == 0xEF && afterFormat[1] == 0xBB
                && afterFormat[2] == 0xBF && Encoding.UTF8.GetString(afterFormat).Contains("localhost\r\nnext=ok\r\n"), "");
            if (ansi1251)
            {
                editor.Write(cp, "Привет мир!");
                Check("запись сохраняет cp1251", Encoding.GetEncoding(1251).GetString(File.ReadAllBytes(cp)) == "Привет мир!", "");
                denied = false;
                try { editor.Write(cp, "emoji \u263a"); }
                catch (EncoderFallbackException) { denied = true; }
                Check("непредставимый символ не портит cp1251", denied
                    && Encoding.GetEncoding(1251).GetString(File.ReadAllBytes(cp)) == "Привет мир!", "");
            }
            string secretFile = Path.Combine(folder, "secret.txt");
            File.WriteAllText(secretFile, "password=DoNotPersist123!", new UTF8Encoding(false));
            editor.Edit(secretFile, "password=" + secret, "password=" + secret);
            Check("секрет можно оставить без изменений", File.ReadAllText(secretFile) == "password=DoNotPersist123!", "");
            denied = false;
            try { editor.Edit(secretFile, "password=" + secret, "password=" + secret + secret); }
            catch (ArgumentException) { denied = true; }
            Check("секрет нельзя размножить", denied, "");
            using (PowerShellWorker full = new PowerShellWorker(files, false))
            {
                RunTask fullTask = full.Start("$x = 7; Write-Output $x", 20);
                full.Wait(fullTask, 10);
                Check("полный PowerShell выполняет скрипт", fullTask.Finished && fullTask.Raw.ToString().Contains("7"), fullTask.Error);
            }
            // Консольные программы пишут в кодировке консоли; проверка имеет смысл, где она умеет кириллицу.
            Encoding oem = Encoding.GetEncoding(StdIo.OemCodePage);
            if (oem.GetString(oem.GetBytes("Привет")) == "Привет")
            {
                using (ExternalWorker console = new ExternalWorker(files, false))
                {
                    RunTask echo = console.Start("cmd.exe /c echo Привет", 30);
                    console.Wait(echo, 20);
                    string said;
                    lock (echo.Raw) said = echo.Raw.ToString();
                    Check("кириллица из консольной программы в воркере не искажается", echo.Finished && said.Contains("Привет"), said);
                }
            }

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string received = null;
            Thread serverThread = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        MemoryStream headerBytes = new MemoryStream();
                        while (true)
                        {
                            int next = stream.ReadByte();
                            if (next < 0) throw new IOException("Нет HTTP-заголовка.");
                            headerBytes.WriteByte((byte)next);
                            byte[] h = headerBytes.GetBuffer(); int n = (int)headerBytes.Length;
                            if (n >= 4 && h[n - 4] == 13 && h[n - 3] == 10 && h[n - 2] == 13 && h[n - 1] == 10) break;
                        }
                        int length = 0;
                        foreach (string line in Encoding.ASCII.GetString(headerBytes.ToArray()).Split('\n'))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                length = int.Parse(line.Substring(15).Trim());
                        byte[] body = new byte[length]; int read = 0;
                        while (read < length) read += stream.Read(body, read, length - read);
                        received = Encoding.UTF8.GetString(body);
                        byte[] response = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"server fs01.corp.example\"}}]}");
                        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + response.Length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(header, 0, header.Length); stream.Write(response, 0, response.Length);
                    }
                }
                catch (Exception) { }
            });
            serverThread.IsBackground = true; serverThread.Start();
            try
            {
                string localAnswer = new LocalModel(files, store, anonymizer,
                    "http://127.0.0.1:" + port + "/v1/chat/completions", "demo-llm").Ask("Проверь файл", new List<string> { file });
                Check("локальная модель получает сырой файл", received != null && received.Contains("fs01.corp.example"), "");
                Check("запрос использует выбранное имя модели", received != null && received.Contains("\"model\":\"demo-llm\""), received);
                Check("ответ локальной модели обезличен", localAnswer.Contains(host) && !localAnswer.Contains("fs01.corp.example"), localAnswer);
            }
            finally { listener.Stop(); serverThread.Join(1000); }

            using (PowerShellWorker worker = new PowerShellWorker(files))
            {
                RunTask date = worker.Start("Get-Date", 1);
                worker.Wait(date, 10);
                Check("ограниченный PowerShell выполняет чтение", date.Finished && date.Raw.Length > 0, date.Error);
                denied = false;
                try { worker.Start("Remove-Item '" + file + "'", 2); }
                catch (ArgumentException) { denied = true; }
                Check("команда изменения отклоняется", denied && File.Exists(file), "");
                denied = false;
                try { worker.Start("Get-ChildItem Env:", 3); }
                catch (ArgumentException) { denied = true; }
                Check("Env: отклоняется", denied, "");
                denied = false;
                try { worker.Start("Get-Content '" + Paths.DatabaseFile + "'", 4); }
                catch (UnauthorizedAccessException) { denied = true; }
                Check("PowerShell не читает вне корней", denied, "");
                denied = false;
                try { worker.Start("Get-ChildItem ..", 7); }
                catch (UnauthorizedAccessException) { denied = true; }
                Check("относительный выход за корни отклоняется", denied, "");
                denied = false;
                try { worker.Start("Set-Location", 8); }
                catch (ArgumentException) { denied = true; }
                Check("Set-Location без пути отклоняется", denied, "");
                denied = false;
                try { worker.Start("$env:USERNAME", 5); }
                catch (ArgumentException) { denied = true; }
                Check("выражение отклоняется", denied, "");
                denied = false;
                try { worker.Start("Get-Date; Get-Date", 9); }
                catch (ArgumentException) { denied = true; }
                Check("несколько команд отклоняются", denied, "");
                string redirected = Path.Combine(folder, "redirected.txt");
                denied = false;
                try { worker.Start("Get-Date > '" + redirected + "'", 10); }
                catch (ArgumentException) { denied = true; }
                Check("перенаправление вывода отклоняется", denied && !File.Exists(redirected), "");
                denied = false;
                try { PowerShellWorker.CheckProgram("ipconfig", new List<string> { "/release" }); }
                catch (ArgumentException) { denied = true; }
                Check("ipconfig release отклоняется", denied, "");
                PowerShellWorker.CheckProgram("ipconfig", new List<string> { "/all" });
                Check("ipconfig all разрешён", true, "");
                RunTask ping = worker.Start("ping.exe -n 3 127.0.0.1", 6);
                worker.Wait(ping, 1);
                Check("долгая команда получает номер задачи", !ping.Finished && ping.Id == 6, "");
                worker.Wait(ping, 10);
                Check("долгая команда завершается через следующий опрос", ping.Finished && ping.Raw.Length > 0, ping.Error);
            }

            using (StringWriter output = new StringWriter())
            using (McpServer server = new McpServer(new StringReader(""), output, folder, false, 12000))
            {
                server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
                server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
                server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
                server.HandleLine("not json");
                string rpc = output.ToString();
                Check("протокол согласует версию", rpc.Contains("2025-06-18"), rpc);
                Check("сервер объявляет десять инструментов", rpc.Contains("sp_hide") && rpc.Contains("sp_next")
                    && rpc.Contains("sp_edit") && rpc.Contains("sp_write") && rpc.Contains("sp_ask_local"), rpc);
                Check("битый JSON получает -32700", rpc.Contains("-32700"), rpc);
            }
            TestApproval commandApproval = new TestApproval();
            using (StringWriter output = new StringWriter())
            using (McpServer server = new McpServer(new StringReader(""), output, folder, false, 12000, commandApproval))
            {
                string fullRequest = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"sp_run\",\"arguments\":{\"profile\":\"full\",\"command\":\"$x = 7; Write-Output $x\",\"wait_sec\":10}}}";
                server.HandleLine(fullRequest);
                Check("полный профиль требует подтверждения", output.ToString().Contains("отклонена")
                    && commandApproval.LastDetails.Contains("$x = 7"), output.ToString());
                output.GetStringBuilder().Clear();
                commandApproval.Allowed = true;
                server.HandleLine(fullRequest);
                Check("полный профиль работает в отдельном воркере", output.ToString().Contains("7")
                    && output.ToString().Contains("\"isError\":false"), output.ToString());
            }
            byte[] previous = File.Exists(Paths.DatabaseFile) ? File.ReadAllBytes(Paths.DatabaseFile) : null;
            try
            {
                File.WriteAllText(Paths.DatabaseFile, "corrupt");
                bool blocked = false;
                try { new Anonymizer(store).Anonymize("fs01.corp.example"); }
                catch (InvalidOperationException error) { blocked = !error.Message.Contains("fs01.corp.example"); }
                Check("повреждённая база закрывает ответ", blocked, "");
            }
            finally
            {
                if (previous == null) File.Delete(Paths.DatabaseFile);
                else File.WriteAllBytes(Paths.DatabaseFile, previous);
            }
            string requests = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"unknown\"}}\n"
                + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"
                + "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"missing\"}}\n";
            using (StringWriter output = new StringWriter())
            using (McpServer server = new McpServer(new StringReader(requests), output, folder, false, 12000))
            {
                server.Run();
                string rpc = output.ToString();
                Check("потоковый сервер выбирает запасную версию", rpc.Contains("2025-06-18"), rpc);
                Check("уведомление не получает ответа", rpc.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length == 2, rpc);
                Check("неизвестный инструмент получает ошибку", rpc.Contains("isError") && rpc.Contains("true"), rpc);
            }
            BridgeSettingsTests();
            LocalModelDiscoveryTests();
            BridgePolicyTests();
            BridgeHostTests();
        }

        private static void BridgeSettingsTests()
        {
            Section("Настройки моста");
            if (File.Exists(Paths.BridgeSettingsFile)) File.Delete(Paths.BridgeSettingsFile);
            BridgeSettings defaults = BridgeSettings.Load();
            Check("без файла мост включён и папок нет", defaults.Enabled && defaults.Roots.Count == 0 && defaults.Problem == null
                && defaults.AllowFull && defaults.AllowEdits && defaults.PageChars == 12000, "");
            string folder = Path.Combine(Paths.DataDirectory, "bridge-root");
            Directory.CreateDirectory(folder);
            BridgeSettings saved = new BridgeSettings();
            saved.Roots.Add(folder + "\\");
            saved.PageChars = 5000;
            saved.AllowFull = false;
            saved.LocalModelId = "demo-llm";
            saved.Save();
            BridgeSettings loaded = BridgeSettings.Load();
            Check("настройки моста сохраняются", loaded.Problem == null && loaded.Roots.Count == 1 && loaded.Roots[0] == folder
                && loaded.PageChars == 5000 && !loaded.AllowFull && loaded.AllowEdits
                && loaded.LocalModelId == "demo-llm", loaded.Problem ?? string.Join(";", loaded.Roots.ToArray()));
            File.WriteAllText(Paths.BridgeSettingsFile, "{\"Roots\": [\"relative\\\\path\"]}");
            Check("относительная папка делает настройки нечитаемыми", BridgeSettings.Load().Problem != null, "");
            File.WriteAllText(Paths.BridgeSettingsFile, "{\"LocalModel\": \"http://example.com/v1/chat/completions\"}");
            Check("внешний адрес модели не принимается", BridgeSettings.Load().Problem != null, "");
            File.WriteAllText(Paths.BridgeSettingsFile, "{\"AllowFull\": \"yes\"}");
            Check("непонятный флаг не считается разрешением", BridgeSettings.Load().Problem != null, "");
            File.WriteAllText(Paths.BridgeSettingsFile, "не json");
            BridgeSettings broken = BridgeSettings.Load();
            Check("битый bridge.json отмечается, а не заменяется значениями по умолчанию", broken.Problem != null
                && broken.Problem.Contains("bridge.json"), broken.Problem);
            File.WriteAllText(Paths.BridgeSettingsFile, "{\"PageChars\": 5, \"Roots\": \"" + folder.Replace("\\", "\\\\") + "\"}");
            BridgeSettings single = BridgeSettings.Load();
            Check("одна папка строкой, страница не меньше предела", single.Problem == null && single.Roots.Count == 1
                && single.PageChars == BridgeSettings.MinPageChars, single.Problem);
            Check("адрес модели только на этом компьютере", BridgeSettings.IsLocalEndpoint("http://127.0.0.1:8081/v1/chat/completions")
                && BridgeSettings.IsLocalEndpoint("http://localhost:1234/v1") && BridgeSettings.IsLocalEndpoint("http://[::1]:8081/")
                && !BridgeSettings.IsLocalEndpoint("http://127.0.0.1.example.com/") && !BridgeSettings.IsLocalEndpoint("http://user@127.0.0.1/")
                && !BridgeSettings.IsLocalEndpoint("file:///C:/model") && !BridgeSettings.IsLocalEndpoint("http://10.0.0.5:8081/"), "");
            string problem;
            Check("папка с точкой с запятой отклоняется", BridgeSettings.NormalizeRoot("C:\\a;b", out problem) == null, problem);
            Check("относительная папка отклоняется", BridgeSettings.NormalizeRoot("docs", out problem) == null, problem);
            Check("корень диска остаётся с чертой", BridgeSettings.NormalizeRoot("C:\\", out problem) == "C:\\", problem);
            bool refused = false;
            BridgeSettings remote = new BridgeSettings();
            remote.LocalModel = "http://10.0.0.5:8081/v1/chat/completions";
            try { remote.Save(); }
            catch (ArgumentException) { refused = true; }
            Check("внешний адрес модели не записывается", refused && BridgeSettings.Load().LocalModel == BridgeSettings.DefaultLocalModel, "");
            File.Delete(Paths.BridgeSettingsFile);
            Check("версия клиента без метки выпуска", BridgeHost.FriendlyClient("codex-mcp-client", "0.155.0-alpha.16.3") == "Codex 0.155.0",
                BridgeHost.FriendlyClient("codex-mcp-client", "0.155.0-alpha.16.3"));
            Check("имена клиентов", BridgeHost.FriendlyClient("codex-mcp-client", "0.144.4") == "Codex 0.144.4"
                && BridgeHost.FriendlyClient("claude-code", "") == "Claude Code" && BridgeHost.FriendlyClient("", null) == "агент"
                && BridgeHost.FriendlyClient("my\u0007tool", "1") == "mytool 1", "");
        }

        private static void LocalModelDiscoveryTests()
        {
            Section("Поиск локальных моделей");
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread server = new Thread(delegate()
            {
                try
                {
                    // Сервер отвечает, пока тест не остановит listener: Accept тогда бросит исключение.
                    while (true)
                    {
                        using (TcpClient client = listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        {
                            MemoryStream request = new MemoryStream();
                            while (true)
                            {
                                int next = stream.ReadByte();
                                if (next < 0) throw new IOException("Нет HTTP-заголовка.");
                                request.WriteByte((byte)next);
                                byte[] bytes = request.GetBuffer(); int n = (int)request.Length;
                                if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10
                                    && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                            }
                            string line = Encoding.ASCII.GetString(request.ToArray()).Split('\r')[0];
                            string json = line.Contains("/api/ps")
                                ? "{\"models\":[{\"name\":\"ollama-live\"},{\"name\":\"nomic-embed-text\"}]}"
                                : line.Contains("/api/tags")
                                ? "{\"models\":[{\"name\":\"ollama-live\"},{\"name\":\"ollama-disk\",\"details\":{\"family\":\"llama\"}},"
                                    + "{\"name\":\"bge-m3\",\"details\":{\"family\":\"bert\"}}]}"
                                : line.Contains("/api/v1/models")
                                ? "{\"models\":[{\"type\":\"llm\",\"key\":\"lm-live\",\"display_name\":\"LM Live\",\"loaded_instances\":[{}]},{\"type\":\"llm\",\"key\":\"lm-disk\",\"loaded_instances\":[]},{\"type\":\"embedding\",\"key\":\"embed\",\"loaded_instances\":[{}]}]}"
                                : "{\"data\":[{\"id\":\"llama-live\"}]}";
                            byte[] body = Encoding.UTF8.GetBytes(json);
                            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                                + body.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(header, 0, header.Length);
                            stream.Write(body, 0, body.Length);
                        }
                    }
                }
                catch (Exception) { }
            });
            server.IsBackground = true;
            server.Start();
            try
            {
                string origin = "http://127.0.0.1:" + port;
                List<LocalModelChoice> found = new List<LocalModelChoice>();
                LocalModelDiscovery.ProbeOllama(origin, found);
                LocalModelDiscovery.ProbeLmStudio(origin, found);
                LocalModelDiscovery.ProbeOpenAi(origin, "OpenAI API", found);
                Check("поиск берёт работающие модели и скачанные модели Ollama", found.Count == 4
                    && found[0].ModelId == "ollama-live" && found[1].ModelId == "ollama-disk"
                    && found[2].ModelId == "lm-live" && found[3].ModelId == "llama-live", "найдено: " + found.Count);
                Check("модель Ollama в памяти отмечена, скачанная нет", found[0].Loaded && !found[1].Loaded
                    && found[2].Loaded && found[3].Loaded, "");
                Check("модели для векторов не предлагаются", found.TrueForAll(delegate(LocalModelChoice choice)
                    { return choice.ModelId != "nomic-embed-text" && choice.ModelId != "bge-m3"; }), "");
                Check("для найденных моделей выбран локальный API", found[0].Endpoint == origin + "/v1/chat/completions"
                    && found[1].Endpoint == origin + "/v1/chat/completions" && found[2].Name == "LM Live", "");
            }
            finally { listener.Stop(); server.Join(1000); }
            Check("OLLAMA_HOST: берётся только порт, адрес всегда этот компьютер",
                LocalModelDiscovery.OllamaOrigin("0.0.0.0:11434") == "http://127.0.0.1:11434"
                && LocalModelDiscovery.OllamaOrigin(":11500") == "http://127.0.0.1:11500"
                && LocalModelDiscovery.OllamaOrigin("http://localhost:11501/") == "http://127.0.0.1:11501"
                && LocalModelDiscovery.OllamaOrigin("[::1]:11502") == "http://127.0.0.1:11502"
                && LocalModelDiscovery.OllamaOrigin("192.168.1.5:11503") == "http://127.0.0.1:11503", "");
            Check("OLLAMA_HOST без порта даёт порт по умолчанию, мусор пропускается",
                LocalModelDiscovery.OllamaOrigin("0.0.0.0") == LocalModelDiscovery.OllamaDefault
                && LocalModelDiscovery.OllamaOrigin("[::1]") == LocalModelDiscovery.OllamaDefault
                && LocalModelDiscovery.OllamaOrigin("host:abc") == null && LocalModelDiscovery.OllamaOrigin(" ") == null
                && LocalModelDiscovery.OllamaOrigin("host:70000") == null, "");
            Check("порт Ollama по умолчанию ищется всегда", LocalModelDiscovery.OllamaOrigins()[0] == LocalModelDiscovery.OllamaDefault, "");
        }

        private static void BridgePolicyTests()
        {
            Section("Разрешения моста");
            string folder = Path.Combine(Paths.DataDirectory, "policy-files");
            string outside = Path.Combine(Paths.DataDirectory, "policy-outside");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(outside);
            string inside = Path.Combine(folder, "a.txt");
            string other = Path.Combine(outside, "b.txt");
            File.WriteAllText(inside, "server=fs02.corp.example\n", new UTF8Encoding(false));
            File.WriteAllText(other, "server=fs03.corp.example\n", new UTF8Encoding(false));
            BridgeSettings policy = new BridgeSettings();
            policy.Roots.Add(folder);
            policy.AllowFull = false;
            policy.AllowEdits = false;
            policy.AllowLocalModel = false;
            TestApproval asked = new TestApproval();
            asked.Allowed = true;
            using (StringWriter output = new StringWriter())
            using (McpServer server = new McpServer(new StringReader(""), output, new LabelStore(false),
                delegate { return policy; }, asked, null, null, 0))
            {
                server.HandleLine(ToolCall(1, "sp_run", "\"profile\":\"full\",\"command\":\"Write-Output 1\""));
                Check("запрещённый полный PowerShell не спрашивает пользователя", output.ToString().Contains("выключен в настройках")
                    && asked.LastDetails == null, output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(2, "sp_write", "\"path\":\"" + Json(inside) + "\",\"text\":\"x\""));
                Check("запрещённая правка не трогает файл", output.ToString().Contains("выключена в настройках")
                    && File.ReadAllText(inside).Contains("fs02"), output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(3, "sp_ask_local", "\"task\":\"проверка\""));
                Check("запрещённая локальная модель", output.ToString().Contains("Локальная модель выключена"), output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(4, "sp_status", ""));
                Check("статус показывает запреты", output.ToString().Contains("выключен в настройках SafePaste"), output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(5, "sp_read", "\"path\":\"" + Json(other) + "\""));
                Check("папка вне списка закрыта", output.ToString().Contains("вне разрешённых"), output.ToString());
                output.GetStringBuilder().Clear();
                policy.Roots.Clear();
                policy.Roots.Add(outside);
                server.HandleLine(ToolCall(6, "sp_read", "\"path\":\"" + Json(other) + "\""));
                Check("новая папка действует без переподключения", output.ToString().Contains("[FQDN_")
                    && !output.ToString().Contains("fs03.corp.example"), output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(7, "sp_read", "\"path\":\"" + Json(inside) + "\""));
                Check("убранная папка закрывается сразу", output.ToString().Contains("вне разрешённых"), output.ToString());
                output.GetStringBuilder().Clear();
                policy.Problem = "bridge.json не читается: проверка.";
                server.HandleLine(ToolCall(8, "sp_status", ""));
                Check("нечитаемые настройки закрывают все инструменты", output.ToString().Contains("bridge.json не читается")
                    && output.ToString().Contains("\"isError\":true"), output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(9, "SP_STATUS", ""));
                Check("имя инструмента без учёта регистра", output.ToString().Contains("bridge.json не читается"), output.ToString());
            }

            BridgeSettings open = new BridgeSettings();
            open.Roots.Add(folder);
            using (StringWriter output = new StringWriter())
            using (McpServer server = new McpServer(new StringReader(""), output, new LabelStore(false),
                delegate { return open; }, new TestApproval(), null, null, 0))
            {
                server.HandleLine(ToolCall(1, "sp_run", "\"command\":\"Get-NetIPAddress | Where-Object { $_.IPAddress -notlike '127.*' }\""));
                Check("отказ ограниченного профиля объясняет краткую форму фильтра", output.ToString().Contains("Where-Object IPAddress -notlike")
                    && output.ToString().Contains("\"isError\":true") && !output.ToString().Contains("Воркер"), output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(2, "sp_run", "\"command\":\"Stop-Service Spooler\""));
                Check("запрещённая команда названа в отказе", output.ToString().Contains("не разрешена в ограниченном профиле: Stop-Service"),
                    output.ToString());
                output.GetStringBuilder().Clear();
                server.HandleLine(ToolCall(3, "sp_status", ""));
                Check("sp_status перечисляет команды ограниченного профиля", output.ToString().Contains("Команды ограниченного профиля")
                    && output.ToString().Contains("Get-NetIPAddress") && output.ToString().Contains("ipconfig"), output.ToString());
            }
            using (ExternalWorker restricted = new ExternalWorker(new FileTools(folder, new LabelStore(false), null), true))
            {
                RunTask refused = restricted.Start("Get-ChildItem Env:", 40);
                restricted.Wait(refused, 20);
                string said;
                lock (refused.Raw) said = refused.Raw.ToString();
                Check("воркер передаёт причину отказа, а не только код выхода", refused.Finished && refused.Error != null
                    && said.Contains("провайдеру"), said + " / " + refused.Error);
            }

            ApprovalGate gate = new ApprovalGate(delegate(string title, string details, WaitHandle cancel) { return cancel.WaitOne(10000) ? 0 : 1; });
            bool result = true;
            Thread asking = new Thread(delegate() { result = gate.Approve("sp_edit", "Проверка", "текст"); });
            asking.IsBackground = true;
            asking.Start();
            Thread.Sleep(100);
            gate.Cancel();
            Check("уход агента закрывает окно подтверждения", asking.Join(3000) && !result, "");
            Check("после ухода агента подтверждения не спрашиваются", !gate.Approve("sp_edit", "Проверка", "текст"), "");
            int prompts = 0;
            ApprovalGate remember = new ApprovalGate(delegate { prompts++; return 2; });
            remember.Approve("sp_run_full", "Проверка", "текст");
            remember.Approve("sp_run_full", "Проверка", "текст");
            Check("разрешение на сессию больше не спрашивается", prompts == 1, prompts.ToString());
        }

        private static void BridgeHostTests()
        {
            Section("Мост в приложении");
            string folder = Path.Combine(Paths.DataDirectory, "host-files");
            Directory.CreateDirectory(folder);
            string note = Path.Combine(folder, "note.txt");
            File.WriteAllText(note, "server=fs04.corp.example\n", new UTF8Encoding(false));
            BridgeSettings settings = new BridgeSettings();
            settings.Roots.Add(folder);
            settings.Save();
            TestApproval approval = new TestApproval();
            using (BridgeHost host = new BridgeHost(delegate { return approval; }))
            {
                host.Start();
                Check("мост поднимается внутри приложения", host.Running && host.Problem == null, host.Problem);
                bool busy = false;
                using (BridgeHost second = new BridgeHost(delegate { return new TestApproval(); }))
                {
                    try { second.Start(); }
                    catch (IOException) { busy = true; }
                }
                Check("второй мост на тот же канал не запускается", busy, "");
                using (AnonymousPipeServerStream toRelay = new AnonymousPipeServerStream(PipeDirection.Out))
                using (AnonymousPipeClientStream relayInput = new AnonymousPipeClientStream(PipeDirection.In, toRelay.ClientSafePipeHandle))
                using (AnonymousPipeServerStream fromRelay = new AnonymousPipeServerStream(PipeDirection.In))
                using (AnonymousPipeClientStream relayOutput = new AnonymousPipeClientStream(PipeDirection.Out, fromRelay.ClientSafePipeHandle))
                {
                    int relayResult = -1;
                    Thread relay = new Thread(delegate() { relayResult = McpRelay.Run(relayInput, relayOutput, BridgeHost.PipeName(), false, 2000); });
                    relay.IsBackground = true;
                    relay.Start();
                    StreamWriter agent = new StreamWriter(toRelay, new UTF8Encoding(false));
                    agent.AutoFlush = true;
                    agent.NewLine = "\n";
                    StreamReader answers = new StreamReader(fromRelay, new UTF8Encoding(false));
                    agent.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\","
                        + "\"clientInfo\":{\"name\":\"codex-mcp-client\",\"version\":\"9.1\"}}}");
                    string initialized = ReadLineWithin(answers, 10000);
                    Check("разъём передаёт рукопожатие", initialized != null && initialized.Contains("2025-06-18"), initialized);
                    agent.WriteLine(ToolCall(2, "sp_read", "\"path\":\"" + Json(note) + "\""));
                    string read = ReadLineWithin(answers, 10000);
                    Check("ответ через мост обезличен", read != null && read.Contains("[FQDN_") && !read.Contains("fs04.corp.example"), read);
                    BridgeStatus status = host.Snapshot();
                    Check("окно видит агента", status.Running && status.Sessions.Count == 1 && status.Sessions[0].Client == "Codex 9.1"
                        && status.Sessions[0].Calls == 1 && status.Sessions[0].State == "ждёт запроса",
                        status.Sessions.Count == 0 ? "" : status.Sessions[0].Client + " " + status.Sessions[0].State);
                    Check("окно знает рабочую папку агента", status.Sessions.Count == 1
                        && status.Sessions[0].Folder == Environment.CurrentDirectory, status.Sessions.Count == 0 ? "" : status.Sessions[0].Folder);
                    Check("журнал хранит обезличенный ответ", status.Calls.Count == 1 && status.Calls[0].Tool == "sp_read"
                        && status.Calls[0].Outcome == "готово" && status.Calls[0].Response.Contains("[FQDN_")
                        && !status.Calls[0].Response.Contains("fs04") && status.Calls[0].Request.Contains("note.txt"), "");
                    host.UpdateSettings(delegate(BridgeSettings next) { next.AllowEdits = false; });
                    agent.WriteLine(ToolCall(3, "sp_write", "\"path\":\"" + Json(note) + "\",\"text\":\"x\""));
                    string write = ReadLineWithin(answers, 10000);
                    Check("запрет из окна действует на подключённого агента", write != null && write.Contains("выключена в настройках")
                        && approval.LastDetails == null && File.ReadAllText(note).Contains("fs04"), write);
                    status = host.Snapshot();
                    Check("отказ по настройкам отмечен в журнале", status.Calls.Count == 2 && status.Calls[1].Outcome == "отклонено", "");
                    host.ClearLog();
                    Check("журнал очищается", host.Snapshot().Calls.Count == 0, "");
                    host.Disconnect(status.Sessions[0].Id);
                    Check("отключение агента закрывает разъём", relay.Join(5000) && relayResult == 0, relayResult.ToString());
                    Check("отключённый агент пропадает из подключённых", WaitFor(delegate { return host.Snapshot().Sessions.Count == 0; }, 5000), "");
                    BridgeStatus after = host.Snapshot();
                    Check("отключившийся агент виден в недавних", after.Recent.Count == 1 && after.Recent[0].Closed
                        && after.Recent[0].Client == "Codex 9.1" && after.Recent[0].Calls == 2
                        && after.Recent[0].State.StartsWith("отключился в", StringComparison.Ordinal),
                        after.Recent.Count == 0 ? "" : after.Recent[0].Client + " " + after.Recent[0].Calls + " " + after.Recent[0].State);
                }
                host.Stop();
                Check("мост останавливается", !host.Running && !host.Snapshot().Running, "");
            }
            string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n";
            using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(request)))
            using (MemoryStream output = new MemoryStream())
            {
                int code = McpRelay.Run(input, output, BridgeHost.PipeName(), false, 300);
                string text = Encoding.UTF8.GetString(output.ToArray());
                Check("без моста агент получает понятную ошибку", code == 1 && text.Contains("-32000") && text.Contains("не запущен"), text);
            }
            BridgeSettings off = BridgeSettings.Load();
            off.Enabled = false;
            off.Save();
            using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(request)))
            using (MemoryStream output = new MemoryStream())
            {
                McpRelay.Run(input, output, BridgeHost.PipeName(), false, 300);
                string text = Encoding.UTF8.GetString(output.ToArray());
                Check("выключенный мост так и называется", text.Contains("Мост выключен"), text);
            }
            File.Delete(Paths.BridgeSettingsFile);
        }

        private static string ToolCall(int id, string name, string arguments)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + name
                + "\",\"arguments\":{" + arguments + "}}}";
        }

        private static string Json(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ReadLineWithin(StreamReader reader, int milliseconds)
        {
            string line = null;
            Thread thread = new Thread(delegate()
            {
                try { line = reader.ReadLine(); }
                catch (Exception) { }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread.Join(milliseconds) ? line : null;
        }

        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }
    }
}
