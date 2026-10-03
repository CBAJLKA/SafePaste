using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using SafePaste.Detecting;

namespace SafePaste.Storage
{
    /// <summary>Настройки из settings.json. Повреждённый или частичный файл не мешает работе.</summary>
    public sealed class SafePasteSettings
    {
        public int ClipboardClearDelayMs = 1500;
        public int MaxClipboardChars = 1000000;
        public int KeyReleaseTimeoutMs = 3000;
        public bool ShowClipboardWarning = true;
        public bool ReviewShowResult;
        public bool ReviewShowFindings;
        public ControlMode Mode = ControlMode.Balanced;
        /// <summary>Искать секреты после слов о пароле в обычных фразах во всех трёх режимах.</summary>
        public bool SmartSecrets = true;
        /// <summary>
        /// Записывать метки вставленного текста в labels.dat: значение получает один номер во всех вставках,
        /// и мост понимает метки из вставки.
        /// </summary>
        public bool SaveLabels = true;
        /// <summary>Скрывать запомненные значения и без подсказок вокруг.</summary>
        public bool HideRemembered = true;
        /// <summary>Сколько дней запомненное скрывается без подсказок. 0: по режиму (строгий 30, обычный 14).</summary>
        public int RememberDays;
        /// <summary>Сколько раз выбирали тип в «Пометить как»: частые типы стоят в меню выше.</summary>
        public readonly Dictionary<string, int> MarkTypeUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Заглушки на картинке: Text в цветах картинки, как обычный текст, Box тёмной плашкой.</summary>
        public string ImageStubStyle = "Text";
        /// <summary>Картинка больше этого числа пикселей не проверяется.</summary>
        public int MaxImagePixels = 40000000;

        private static readonly Regex TypeName = new Regex("^[A-Z][A-Z0-9_]{0,31}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Меняет настройки на диске: читает свежий файл, применяет изменение и сохраняет.
        /// Так окно проверки и меню трея не затирают изменения друг друга.
        /// </summary>
        public static SafePasteSettings Update(Action<SafePasteSettings> change)
        {
            SafePasteSettings settings = Load();
            change(settings);
            settings.Save();
            return settings;
        }

        public static SafePasteSettings Load()
        {
            SafePasteSettings settings = new SafePasteSettings();
            string path = Paths.SettingsFile;
            if (!File.Exists(path))
            {
                return settings;
            }
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                Dictionary<string, object> values =
                    new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (values != null)
                {
                    settings.ClipboardClearDelayMs = ReadInt(values, "ClipboardClearDelayMs",
                        settings.ClipboardClearDelayMs, 200, 60000);
                    settings.MaxClipboardChars = ReadInt(values, "MaxClipboardChars",
                        settings.MaxClipboardChars, 1000, 50000000);
                    settings.KeyReleaseTimeoutMs = ReadInt(values, "KeyReleaseTimeoutMs",
                        settings.KeyReleaseTimeoutMs, 0, 10000);
                    settings.ShowClipboardWarning = ReadBool(values, "ShowClipboardWarning",
                        settings.ShowClipboardWarning);
                    settings.ReviewShowResult = ReadBool(values, "ReviewShowResult", settings.ReviewShowResult);
                    settings.ReviewShowFindings = ReadBool(values, "ReviewShowFindings", settings.ReviewShowFindings);
                    settings.SaveLabels = ReadBool(values, "SaveLabels", settings.SaveLabels);
                    settings.HideRemembered = ReadBool(values, "HideRemembered", settings.HideRemembered);
                    settings.SmartSecrets = ReadBool(values, "SmartSecrets", settings.SmartSecrets);
                    settings.RememberDays = ReadInt(values, "RememberDays", settings.RememberDays, 0, 30);
                    settings.MaxImagePixels = ReadInt(values, "MaxImagePixels", settings.MaxImagePixels, 1000000, 200000000);
                    object stub;
                    if (values.TryGetValue("ImageStubStyle", out stub) && stub != null)
                    {
                        string style = Convert.ToString(stub, CultureInfo.InvariantCulture);
                        settings.ImageStubStyle = string.Equals(style, "Box", StringComparison.OrdinalIgnoreCase) ? "Box" : "Text";
                    }
                    object mode;
                    if (values.TryGetValue("ControlMode", out mode) && mode != null)
                    {
                        settings.Mode = ControlModes.Parse(Convert.ToString(mode, CultureInfo.InvariantCulture), settings.Mode);
                    }
                    object usage;
                    if (values.TryGetValue("MarkTypes", out usage))
                    {
                        ReadUsage(usage as Dictionary<string, object>, settings.MarkTypeUsage);
                    }
                }
            }
            catch (Exception)
            {
                // Настройки не критичны: продолжаем со значениями по умолчанию.
            }
            return settings;
        }

        public void Save()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"ClipboardClearDelayMs\": " + ClipboardClearDelayMs.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"MaxClipboardChars\": " + MaxClipboardChars.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"KeyReleaseTimeoutMs\": " + KeyReleaseTimeoutMs.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"ShowClipboardWarning\": " + (ShowClipboardWarning ? "true" : "false") + ",");
            builder.AppendLine("  \"ReviewShowResult\": " + (ReviewShowResult ? "true" : "false") + ",");
            builder.AppendLine("  \"ReviewShowFindings\": " + (ReviewShowFindings ? "true" : "false") + ",");
            builder.AppendLine("  \"SaveLabels\": " + (SaveLabels ? "true" : "false") + ",");
            builder.AppendLine("  \"HideRemembered\": " + (HideRemembered ? "true" : "false") + ",");
            builder.AppendLine("  \"SmartSecrets\": " + (SmartSecrets ? "true" : "false") + ",");
            builder.AppendLine("  \"RememberDays\": " + RememberDays.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"MaxImagePixels\": " + MaxImagePixels.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"ImageStubStyle\": \"" + (ImageStubStyle == "Box" ? "Box" : "Text") + "\",");
            List<string> usage = new List<string>();
            foreach (KeyValuePair<string, int> pair in MarkTypeUsage)
            {
                if (TypeName.IsMatch(pair.Key) && pair.Value > 0)
                {
                    usage.Add("\"" + pair.Key + "\": " + pair.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            builder.AppendLine("  \"MarkTypes\": {" + string.Join(", ", usage.ToArray()) + "},");
            builder.AppendLine("  \"ControlMode\": \"" + ControlModes.ToKey(Mode) + "\"");
            builder.Append("}");
            File.WriteAllText(Paths.SettingsFile, builder.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Счётчики из файла: неизвестный вид имени или числа пропускается.</summary>
        private static void ReadUsage(Dictionary<string, object> values, Dictionary<string, int> target)
        {
            if (values == null)
            {
                return;
            }
            foreach (KeyValuePair<string, object> pair in values)
            {
                string type = pair.Key == null ? string.Empty : pair.Key.ToUpperInvariant();
                if (!TypeName.IsMatch(type))
                {
                    continue;
                }
                try
                {
                    int count = Convert.ToInt32(pair.Value, CultureInfo.InvariantCulture);
                    if (count > 0)
                    {
                        target[type] = Math.Min(count, 1000000);
                    }
                }
                catch (Exception)
                {
                    // Не число: счётчик просто начнётся заново.
                }
            }
        }

        private static int ReadInt(Dictionary<string, object> values, string key, int fallback, int minimum, int maximum)
        {
            object raw;
            if (!values.TryGetValue(key, out raw) || raw == null)
            {
                return fallback;
            }
            int parsed;
            try
            {
                parsed = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
            if (parsed < minimum)
            {
                return minimum;
            }
            return parsed > maximum ? maximum : parsed;
        }

        private static bool ReadBool(Dictionary<string, object> values, string key, bool fallback)
        {
            object raw;
            if (!values.TryGetValue(key, out raw) || raw == null)
            {
                return fallback;
            }
            try
            {
                return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
