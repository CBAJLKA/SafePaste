using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace SafePaste.Storage
{
    /// <summary>
    /// Настройки моста из bridge.json. Отдельный файл: окно проверки сохраняет settings.json целиком
    /// и затёрло бы изменения, сделанные в окне моста.
    /// </summary>
    public sealed class BridgeSettings
    {
        public const string DefaultLocalModel = "http://127.0.0.1:8081/v1/chat/completions";
        public const int MinPageChars = 100;
        public const int MaxPageChars = 200000;

        public bool Enabled = true;
        public List<string> Roots = new List<string>();
        public int PageChars = 12000;
        public bool AllowFull = true;
        public bool AllowEdits = true;
        public bool AllowLocalModel = true;
        public string LocalModel = DefaultLocalModel;
        public string LocalModelId = "local";

        /// <summary>
        /// Файл есть, но не читается. Мост тогда отказывает во всех вызовах: значения по умолчанию
        /// открыли бы агенту все папки, даже если пользователь их ограничил.
        /// </summary>
        public string Problem;

        public string RootsText
        {
            get { return string.Join(";", Roots.ToArray()); }
        }

        public BridgeSettings Clone()
        {
            BridgeSettings copy = (BridgeSettings)MemberwiseClone();
            copy.Roots = new List<string>(Roots);
            return copy;
        }

        public static BridgeSettings Load()
        {
            BridgeSettings settings = new BridgeSettings();
            string path = Paths.BridgeSettingsFile;
            if (!File.Exists(path))
            {
                return settings;
            }
            try
            {
                Dictionary<string, object> values = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                if (values == null)
                {
                    throw new FormatException("файл пуст");
                }
                settings.Enabled = ReadBool(values, "Enabled", settings.Enabled);
                settings.AllowFull = ReadBool(values, "AllowFull", settings.AllowFull);
                settings.AllowEdits = ReadBool(values, "AllowEdits", settings.AllowEdits);
                settings.AllowLocalModel = ReadBool(values, "AllowLocalModel", settings.AllowLocalModel);
                object raw;
                if (values.TryGetValue("PageChars", out raw) && raw != null)
                {
                    if (!(raw is int || raw is long || raw is decimal || raw is double))
                    {
                        throw new FormatException("PageChars должно быть числом");
                    }
                    double pageChars = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    settings.PageChars = (int)Math.Max(MinPageChars, Math.Min(MaxPageChars, pageChars));
                }
                if (values.TryGetValue("LocalModel", out raw) && raw != null)
                {
                    string endpoint = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim();
                    if (!IsLocalEndpoint(endpoint))
                    {
                        throw new FormatException("адрес локальной модели указывает не на этот компьютер");
                    }
                    settings.LocalModel = endpoint;
                }
                if (values.TryGetValue("LocalModelId", out raw) && raw != null)
                {
                    settings.LocalModelId = raw as string;
                    if (string.IsNullOrWhiteSpace(settings.LocalModelId))
                        throw new FormatException("имя локальной модели задано неверно");
                }
                if (values.TryGetValue("Roots", out raw) && raw != null)
                {
                    List<string> roots = new List<string>();
                    IEnumerable list = raw is string ? new object[] { raw } : raw as IEnumerable;
                    if (list == null)
                    {
                        throw new FormatException("список папок задан неверно");
                    }
                    foreach (object item in list)
                    {
                        string problem;
                        string root = NormalizeRoot(item as string, out problem);
                        if (root == null)
                        {
                            throw new FormatException(problem);
                        }
                        AddUnique(roots, root);
                    }
                    settings.Roots = roots;
                }
            }
            catch (Exception failure)
            {
                BridgeSettings broken = new BridgeSettings();
                broken.Problem = "bridge.json не читается: " + Reason(failure) + ".";
                return broken;
            }
            return settings;
        }

        /// <summary>Не даёт записать то, что Load потом не примет.</summary>
        public void Validate()
        {
            if (!IsLocalEndpoint(LocalModel))
            {
                throw new ArgumentException("адрес локальной модели должен указывать на этот компьютер");
            }
            if (string.IsNullOrWhiteSpace(LocalModelId) || LocalModelId.Length > 300)
                throw new ArgumentException("нужно указать имя локальной модели (до 300 символов)");
            if (PageChars < MinPageChars || PageChars > MaxPageChars)
            {
                throw new ArgumentException("размер страницы вне допустимых границ");
            }
            foreach (string root in Roots)
            {
                string problem;
                if (NormalizeRoot(root, out problem) == null)
                {
                    throw new ArgumentException(problem);
                }
            }
        }

        public void Save()
        {
            Validate();
            JavaScriptSerializer json = new JavaScriptSerializer();
            List<string> roots = new List<string>();
            foreach (string root in Roots)
            {
                roots.Add(json.Serialize(root));
            }
            StringBuilder builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"Enabled\": ").Append(Enabled ? "true" : "false").Append(",\n");
            builder.Append("  \"Roots\": [").Append(string.Join(", ", roots.ToArray())).Append("],\n");
            builder.Append("  \"PageChars\": ").Append(PageChars.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            builder.Append("  \"AllowFull\": ").Append(AllowFull ? "true" : "false").Append(",\n");
            builder.Append("  \"AllowEdits\": ").Append(AllowEdits ? "true" : "false").Append(",\n");
            builder.Append("  \"AllowLocalModel\": ").Append(AllowLocalModel ? "true" : "false").Append(",\n");
            builder.Append("  \"LocalModel\": ").Append(json.Serialize(LocalModel)).Append(",\n");
            builder.Append("  \"LocalModelId\": ").Append(json.Serialize(LocalModelId)).Append("\n");
            builder.Append("}\n");
            string path = Paths.BridgeSettingsFile;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            Problem = null;
        }

        /// <summary>
        /// Полный путь к папке без завершающей черты. Точка с запятой и кавычки не допускаются:
        /// папки передаются воркеру одной строкой через «;» в кавычках.
        /// </summary>
        public static string NormalizeRoot(string path, out string problem)
        {
            problem = null;
            string value = (path ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                problem = "пустой путь к папке";
                return null;
            }
            if (value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0)
            {
                problem = "в пути к папке есть ; или кавычка";
                return null;
            }
            bool drive = value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');
            bool network = value.StartsWith("\\\\", StringComparison.Ordinal) && !value.StartsWith("\\\\?\\", StringComparison.Ordinal)
                && !value.StartsWith("\\\\.\\", StringComparison.Ordinal);
            if (!drive && !network)
            {
                problem = "нужен полный путь к папке: " + value;
                return null;
            }
            string full;
            try
            {
                full = Path.GetFullPath(value);
            }
            catch (Exception)
            {
                problem = "неверный путь к папке: " + value;
                return null;
            }
            string root = Path.GetPathRoot(full);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(Path.DirectorySeparatorChar);
        }

        /// <summary>Модель получает сырые данные, поэтому адрес допустим только на этом компьютере.</summary>
        public static bool IsLocalEndpoint(string endpoint)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint ?? string.Empty, UriKind.Absolute, out uri))
            {
                return false;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }
            string host = uri.Host.Trim('[', ']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            IPAddress address;
            return (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
                && IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
        }

        public static void AddUnique(List<string> roots, string root)
        {
            foreach (string existing in roots)
            {
                if (existing.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            roots.Add(root);
        }

        private static bool ReadBool(Dictionary<string, object> values, string key, bool fallback)
        {
            object raw;
            if (!values.TryGetValue(key, out raw) || raw == null)
            {
                return fallback;
            }
            if (!(raw is bool))
            {
                throw new FormatException("значение " + key + " должно быть true или false");
            }
            return (bool)raw;
        }

        private static string Reason(Exception failure)
        {
            if (failure is FormatException)
            {
                return failure.Message;
            }
            if (failure is ArgumentException || failure is InvalidOperationException)
            {
                return "неверный JSON";
            }
            return "ошибка чтения";
        }
    }
}
