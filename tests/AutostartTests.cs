using System;
using Microsoft.Win32;
using SafePaste.Storage;

namespace SafePaste.Tests
{
    public static partial class TestProgram
    {
        /// <summary>Запуск при входе в Windows. Настоящий раздел Run не трогается: тест пишет в свой раздел.</summary>
        private static void AutostartTests()
        {
            Section("Запуск при входе");
            string root = "Software\\SafePaste-Tests-" + Guid.NewGuid().ToString("N");
            string runKey = Autostart.RunKey;
            string approvedKey = Autostart.ApprovedKey;
            Autostart.RunKey = root + "\\Run";
            Autostart.ApprovedKey = root + "\\Approved";
            string exe = "C:\\Tools\\SafePaste\\SafePaste.exe";
            try
            {
                Check("без строки Run автозапуск выключен", !Autostart.IsEnabled(exe), "");
                Autostart.Enable(exe);
                string command;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Autostart.RunKey))
                {
                    command = key.GetValue("SafePaste") as string;
                }
                Check("строка Run запускает этот exe с флагом --autostart",
                    command == "\"" + exe + "\" --autostart", command);
                Check("включённый автозапуск виден", Autostart.IsEnabled(exe), "");
                Check("путь сравнивается без учёта регистра", Autostart.IsEnabled(exe.ToUpperInvariant()), "");
                Check("строка на другой exe не считается включённой", !Autostart.IsEnabled("D:\\Other\\SafePaste.exe"), "");
                using (RegistryKey approved = Registry.CurrentUser.CreateSubKey(Autostart.ApprovedKey))
                {
                    approved.SetValue("SafePaste", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                }
                Check("выключенный в диспетчере задач автозапуск не считается включённым", !Autostart.IsEnabled(exe), "");
                Autostart.Enable(exe);
                Check("повторное включение снимает отметку диспетчера задач", Autostart.IsEnabled(exe), "");
                Autostart.Disable();
                Check("выключение убирает строку Run", !Autostart.IsEnabled(exe), "");
                Autostart.Disable();
                Check("повторное выключение не падает", !Autostart.IsEnabled(exe), "");
                Check("путь из команды в кавычках и без них",
                    Autostart.Target("\"C:\\Program Files\\SafePaste\\SafePaste.exe\" --autostart") == "C:\\Program Files\\SafePaste\\SafePaste.exe"
                    && Autostart.Target("C:\\Tools\\SafePaste.exe --autostart") == "C:\\Tools\\SafePaste.exe", "");
            }
            finally
            {
                Autostart.RunKey = runKey;
                Autostart.ApprovedKey = approvedKey;
                Registry.CurrentUser.DeleteSubKeyTree(root, false);
            }
        }
    }
}
