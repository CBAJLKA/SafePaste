using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    internal sealed class LocalModelChoice
    {
        internal string Provider;
        internal string Name;
        internal string Endpoint;
        internal string ModelId;
        /// <summary>Модель уже в памяти. Скачанную модель Ollama сервер загрузит сам при первом запросе.</summary>
        internal bool Loaded = true;
    }

    /// <summary>Что с Ollama на этом компьютере: не установлена, установлена, но сервер молчит, или работает.</summary>
    internal enum OllamaState
    {
        Missing,
        Stopped,
        Running
    }

    /// <summary>Checks only loopback APIs. Native lists distinguish models loaded now from downloaded models.</summary>
    internal static class LocalModelDiscovery
    {
        internal const string OllamaDefault = "http://127.0.0.1:11434";

        internal static List<LocalModelChoice> Scan(string configuredEndpoint)
        {
            List<LocalModelChoice> found = new List<LocalModelChoice>();
            List<int> probed = new List<int> { 1234, 8080, 8081 };
            foreach (string origin in OllamaOrigins())
            {
                ProbeOllama(origin, found);
                probed.Add(new Uri(origin).Port);
            }
            ProbeLmStudio("http://127.0.0.1:1234", found);
            ProbeOpenAi("http://127.0.0.1:8080", "OpenAI API", found);
            ProbeOpenAi("http://127.0.0.1:8081", "OpenAI API", found);

            Uri configured;
            if (BridgeSettings.IsLocalEndpoint(configuredEndpoint)
                && Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out configured))
            {
                string origin = configured.GetLeftPart(UriPartial.Authority);
                if (!probed.Contains(configured.Port))
                    ProbeOpenAi(origin, "OpenAI API", found);
            }
            return found;
        }

        /// <summary>
        /// Ollama отдаёт два списка: /api/ps с моделями в памяти и /api/tags со всеми скачанными.
        /// Нужны оба: простаивающую модель Ollama выгружает через 5 минут, а по запросу загружает сама,
        /// поэтому по одному /api/ps работающий сервер часто выглядел пустым. Модели для векторов
        /// (embed) отвечать текстом не умеют и в список не попадают.
        /// </summary>
        internal static void ProbeOllama(string origin, List<LocalModelChoice> found)
        {
            string endpoint = origin + "/v1/chat/completions";
            object[] running = Array(GetJson(origin + "/api/ps"), "models");
            if (running != null)
            {
                foreach (object item in running)
                {
                    Dictionary<string, object> model = item as Dictionary<string, object>;
                    string id = OllamaId(model);
                    if (id != null && !IsEmbedding(model, id)) Add(found, "Ollama", id, endpoint, id, true);
                }
            }
            object[] downloaded = Array(GetJson(origin + "/api/tags"), "models");
            if (downloaded == null) return;
            foreach (object item in downloaded)
            {
                Dictionary<string, object> model = item as Dictionary<string, object>;
                string id = OllamaId(model);
                if (id != null && !IsEmbedding(model, id)) Add(found, "Ollama", id, endpoint, id, false);
            }
        }

        private static string OllamaId(Dictionary<string, object> model)
        {
            return String(model, "name") ?? String(model, "model");
        }

        private static bool IsEmbedding(Dictionary<string, object> model, string id)
        {
            if (id.IndexOf("embed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            object value;
            Dictionary<string, object> details = model != null && model.TryGetValue("details", out value)
                ? value as Dictionary<string, object> : null;
            string family = String(details, "family");
            return family != null && family.EndsWith("bert", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Адреса Ollama: порт по умолчанию и порт из OLLAMA_HOST, всегда на этом компьютере.</summary>
        internal static List<string> OllamaOrigins()
        {
            List<string> origins = new List<string> { OllamaDefault };
            string custom = OllamaOrigin(ReadOllamaHost());
            if (custom != null && !origins.Contains(custom)) origins.Add(custom);
            return origins;
        }

        /// <summary>
        /// Адрес из OLLAMA_HOST: «0.0.0.0:11500», «:11500», «http://localhost:11500». Берётся только порт:
        /// SafePaste обращается к модели только на этом компьютере. null: значение не разобрать.
        /// </summary>
        internal static string OllamaOrigin(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            string value = host.Trim();
            int scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) value = value.Substring(scheme + 3);
            int slash = value.IndexOf('/');
            if (slash >= 0) value = value.Substring(0, slash);
            int colon = value.LastIndexOf(':');
            // Без порта или IPv6 без порта ([::1]): Ollama слушает порт по умолчанию.
            if (colon < 0 || value.IndexOf(']', colon) >= 0) return OllamaDefault;
            int port;
            if (!int.TryParse(value.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port < 1 || port > 65535) return null;
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Переменную могли задать после запуска SafePaste, поэтому смотрим и в реестр.</summary>
        private static string ReadOllamaHost()
        {
            foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process,
                EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                try
                {
                    string value = Environment.GetEnvironmentVariable("OLLAMA_HOST", target);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Отвечает ли сервер Ollama, а если нет, установлена ли она вообще.</summary>
        internal static OllamaState DetectOllama()
        {
            foreach (string origin in OllamaOrigins())
                if (GetJson(origin + "/api/version") != null) return OllamaState.Running;
            return OllamaExecutable() != null ? OllamaState.Stopped : OllamaState.Missing;
        }

        /// <summary>Ollama из обычной установки: сначала приложение в трее, потом сам сервер.</summary>
        internal static string OllamaExecutable()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string folder in new[] { Path.Combine(local, "Programs\\Ollama"), Path.Combine(programs, "Ollama") })
            {
                foreach (string name in new[] { "ollama app.exe", "ollama.exe" })
                {
                    string candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        /// <summary>Запускает установленную Ollama. Сервер без приложения в трее запускается без окна.</summary>
        internal static void StartOllama()
        {
            string executable = OllamaExecutable();
            if (executable == null) throw new FileNotFoundException("Ollama не установлена.");
            ProcessStartInfo start = new ProcessStartInfo(executable);
            start.WorkingDirectory = Path.GetDirectoryName(executable);
            if (Path.GetFileName(executable).Equals("ollama.exe", StringComparison.OrdinalIgnoreCase))
            {
                start.Arguments = "serve";
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
            }
            using (Process.Start(start)) { }
        }

        internal static void ProbeLmStudio(string origin, List<LocalModelChoice> found)
        {
            Dictionary<string, object> root = GetJson(origin + "/api/v1/models");
            object[] models = Array(root, "models");
            if (models == null) return;
            foreach (object item in models)
            {
                Dictionary<string, object> model = item as Dictionary<string, object>;
                if (String(model, "type") != "llm") continue;
                object[] loaded = Array(model, "loaded_instances");
                if (loaded == null || loaded.Length == 0) continue;
                string id = String(model, "key");
                string name = String(model, "display_name") ?? id;
                Add(found, "LM Studio", name, origin + "/v1/chat/completions", id, true);
            }
        }

        internal static void ProbeOpenAi(string origin, string provider, List<LocalModelChoice> found)
        {
            Dictionary<string, object> root = GetJson(origin + "/v1/models");
            object[] models = Array(root, "data");
            if (models == null) return;
            foreach (object item in models)
            {
                Dictionary<string, object> model = item as Dictionary<string, object>;
                string id = String(model, "id");
                Add(found, provider, id, origin + "/v1/chat/completions", id, true);
            }
        }

        private static void Add(List<LocalModelChoice> found, string provider, string name, string endpoint, string id, bool loaded)
        {
            if (string.IsNullOrWhiteSpace(id) || !BridgeSettings.IsLocalEndpoint(endpoint)) return;
            foreach (LocalModelChoice existing in found)
                if (existing.Endpoint == endpoint && existing.ModelId == id) return;
            found.Add(new LocalModelChoice { Provider = provider, Name = name, Endpoint = endpoint, ModelId = id, Loaded = loaded });
        }

        private static Dictionary<string, object> GetJson(string url)
        {
            if (!BridgeSettings.IsLocalEndpoint(url)) return null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 700;
                request.ReadWriteTimeout = 700;
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    char[] buffer = new char[4096];
                    StringBuilder body = new StringBuilder();
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        body.Append(buffer, 0, count);
                        if (body.Length > 262144) return null;
                    }
                    return new JavaScriptSerializer().DeserializeObject(body.ToString()) as Dictionary<string, object>;
                }
            }
            catch (Exception) { return null; }
        }

        private static object[] Array(Dictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? item as object[] : null;
        }

        private static string String(Dictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? item as string : null;
        }
    }
}
