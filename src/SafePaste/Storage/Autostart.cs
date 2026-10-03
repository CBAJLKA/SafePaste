using System;
using System.IO;
using Microsoft.Win32;

namespace SafePaste.Storage
{
    /// <summary>
    /// Запуск при входе в Windows: строка SafePaste в HKCU\...\Run, права администратора не нужны.
    /// Флаг --autostart отличает такой запуск от ручного: при входе SafePaste стартует молча.
    /// </summary>
    public static class Autostart
    {
        public const string Flag = "--autostart";
        private const string ValueName = "SafePaste";

        /// <summary>Разделы реестра. Тесты подменяют их своими, чтобы не трогать настоящий автозапуск.</summary>
        internal static string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        internal static string ApprovedKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";

        /// <summary>
        /// Включён ли запуск именно этого exe. Строка на другой файл (программу перенесли) не считается:
        /// переключатель покажет «выключено», а включение перепишет путь.
        /// </summary>
        public static bool IsEnabled(string executable)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                string command = key == null ? null : key.GetValue(ValueName) as string;
                if (string.IsNullOrEmpty(command) || !SameFile(Target(command), executable))
                {
                    return false;
                }
            }
            return !DisabledInTaskManager();
        }

        public static void Enable(string executable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                key.SetValue(ValueName, Command(executable), RegistryValueKind.String);
            }
            // Отметка «Отключено» из диспетчера задач сильнее строки Run: снимаем её, раз пользователь включил сам.
            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
            {
                if (approved != null && approved.GetValue(ValueName) != null)
                {
                    approved.DeleteValue(ValueName, false);
                }
            }
        }

        public static void Disable()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key != null)
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }

        internal static string Command(string executable)
        {
            return "\"" + executable + "\" " + Flag;
        }

        /// <summary>Путь к exe из командной строки: в кавычках или до первого пробела.</summary>
        internal static string Target(string command)
        {
            string text = command.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : text.Trim('"');
            }
            int space = text.IndexOf(' ');
            return space < 0 ? text : text.Substring(0, space);
        }

        private static bool SameFile(string left, string right)
        {
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>В «Автозагрузке» диспетчера задач запуск выключен: нечётный первый байт отметки.</summary>
        private static bool DisabledInTaskManager()
        {
            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
            {
                byte[] state = approved == null ? null : approved.GetValue(ValueName) as byte[];
                return state != null && state.Length > 0 && (state[0] & 1) == 1;
            }
        }
    }
}
