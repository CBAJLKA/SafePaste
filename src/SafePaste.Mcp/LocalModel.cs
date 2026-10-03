using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using SafePaste.Bridge;
using SafePaste.Storage;

namespace SafePaste.Mcp
{
    internal sealed class LocalModel
    {
        private readonly FileTools files;
        private readonly LabelStore labels;
        private readonly Anonymizer anonymizer;
        private readonly string endpoint;
        private readonly string modelId;

        internal LocalModel(FileTools files, LabelStore labels, Anonymizer anonymizer)
            : this(files, labels, anonymizer, BridgeSettings.DefaultLocalModel) { }

        internal LocalModel(FileTools files, LabelStore labels, Anonymizer anonymizer, string endpoint)
            : this(files, labels, anonymizer, endpoint, "local") { }

        internal LocalModel(FileTools files, LabelStore labels, Anonymizer anonymizer, string endpoint, string modelId)
        {
            // Модель получает файлы с реальными значениями, поэтому адрес только на этом компьютере.
            if (!BridgeSettings.IsLocalEndpoint(endpoint))
                throw new ArgumentException("Адрес локальной модели должен указывать на этот компьютер.");
            if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("Не указано имя локальной модели.");
            this.files = files; this.labels = labels; this.anonymizer = anonymizer; this.endpoint = endpoint;
            this.modelId = modelId;
        }

        internal string Ask(string task, IList<string> paths)
        {
            if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("Не указана задача для локальной модели.");
            if (paths != null && paths.Count > 5) throw new ArgumentException("Не более пяти файлов за запрос.");
            string instruction = "Работай только локально. Имена, адреса и пути из источников пиши точно, без склонений и сокращений. Не включай в ответ большие фрагменты файлов, если это не нужно для задачи.";
            StringBuilder prompt = new StringBuilder(Labels.ResolveSimple(task, labels));
            if (paths != null) foreach (string path in paths)
            {
                string full = files.CheckPath(path);
                byte[] bytes = File.ReadAllBytes(full);
                if (bytes.Length > 2097152) throw new IOException("Файл больше 2 МБ: " + full);
                string content;
                try { content = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { content = Encoding.Default.GetString(bytes); }
                prompt.Append("\n\nФайл: ").Append(full).Append("\n").Append(content);
            }
            JavaScriptSerializer json = new JavaScriptSerializer();
            json.MaxJsonLength = int.MaxValue;
            object payload = new Dictionary<string, object> {
                { "model", modelId }, { "stream", false }, { "temperature", 0.2 },
                { "messages", new object[] {
                    new Dictionary<string, object> { { "role", "system" }, { "content", instruction } },
                    new Dictionary<string, object> { { "role", "user" }, { "content", prompt.ToString() } }
                } }
            };
            byte[] body = Encoding.UTF8.GetBytes(json.Serialize(payload));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 120000;
            request.ReadWriteTimeout = 120000;
            request.Proxy = null;
            request.ServicePoint.Expect100Continue = false;
            request.ContentLength = body.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            string response;
            using (WebResponse reply = request.GetResponse())
            using (Stream stream = reply.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                char[] buffer = new char[8192]; int count; StringBuilder output = new StringBuilder();
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Append(buffer, 0, count);
                    if (output.Length > 2000000) throw new IOException("Ответ локальной модели слишком большой.");
                }
                response = output.ToString();
            }
            Dictionary<string, object> root = json.DeserializeObject(response) as Dictionary<string, object>;
            object[] choices = root != null && root.ContainsKey("choices") ? root["choices"] as object[] : null;
            Dictionary<string, object> first = choices != null && choices.Length > 0 ? choices[0] as Dictionary<string, object> : null;
            Dictionary<string, object> message = first != null && first.ContainsKey("message") ? first["message"] as Dictionary<string, object> : null;
            string contentText = message != null && message.ContainsKey("content") ? message["content"] as string : null;
            if (contentText == null) throw new IOException("Локальная модель вернула ответ без текста.");
            return anonymizer.Anonymize(contentText);
        }
    }
}
