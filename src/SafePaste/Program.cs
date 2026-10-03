using System;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using SafePaste.Interop;
using SafePaste.Storage;
using SafePaste.Ui;
using SafePaste.Mcp;

namespace SafePaste
{
    internal static class Program
    {
        /// <summary>Мьютекс трея. По нему разъём моста понимает, запущен ли SafePaste.</summary>
        internal static string InstanceName
        {
            get { return "Local\\SafePaste-" + WindowsIdentity.GetCurrent().User.Value; }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            foreach (string argument in args)
                if (argument == "--mcp" || argument == "--hook" || argument == "--filter" || argument == "--worker"
                    || argument == "--bridge-host")
                    return McpProgram.Run(args);
            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // Запуск при входе в Windows: без сообщений о запуске.
            bool autostart = Array.IndexOf(args, Autostart.Flag) >= 0;

            // Мьютекс локальный для сеанса и учётной записи: горячие клавиши и буфер тоже свои у каждого сеанса.
            string name = InstanceName;
            bool createdNew;
            using (Mutex mutex = new Mutex(true, name, out createdNew))
            {
                if (!createdNew)
                {
                    if (!autostart)
                    {
                        Alerts.ShowSafe(null, AlertKind.Info, "SafePaste уже запущен",
                            "Значок программы находится в области уведомлений рядом с часами.");
                    }
                    return 0;
                }
                try
                {
                    Application.Run(new TrayApplication(autostart));
                }
                catch (Exception failure)
                {
                    ShowFatal(failure);
                }
                finally
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }
            }
            return 0;
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            // Ошибка в обработчике не должна ронять приложение вместе со значком в трее.
            Alerts.ShowSafe(null, AlertKind.Error, "Непредвиденная ошибка", e.Exception.Message);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception failure = e.ExceptionObject as Exception;
            ShowFatal(failure);
        }

        private static void ShowFatal(Exception failure)
        {
            string message = failure == null ? "Неизвестная ошибка." : failure.Message;
            Alerts.ShowSafe(null, AlertKind.Error, "SafePaste остановлен", message);
        }
    }
}
