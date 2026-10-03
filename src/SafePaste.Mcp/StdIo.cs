using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Management.Automation.Language;
using SafePaste.Bridge;
namespace SafePaste.Mcp
{
    internal static class StdIo
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int which, IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        /// <summary>Кодовая страница консоли по умолчанию (866 в русской Windows).</summary>
        internal static int OemCodePage
        {
            get { return (int)GetOEMCP(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int Inherit;
        }

        /// <summary>
        /// Протокол идёт через унаследованные каналы. Дочерние процессы (ping, ipconfig) не должны
        /// ни читать из них, ни писать в них, поэтому стандартные дескрипторы переводятся на NUL.
        /// </summary>
        internal static void Isolate()
        {
            foreach (int which in new int[] { -10, -11, -12 })
            {
                SetHandleInformation(GetStdHandle(which), 1, 0);
            }
            SecurityAttributes security = new SecurityAttributes();
            security.Length = Marshal.SizeOf(typeof(SecurityAttributes));
            security.Inherit = 1;
            IntPtr nul = CreateFileW("NUL", 0xC0000000, 3, ref security, 3, 0, IntPtr.Zero);
            SetStdHandle(-10, nul);
            SetStdHandle(-11, nul);
            SetStdHandle(-12, nul);
        }

        /// <summary>
        /// ipconfig, ping и cmd пишут в перенаправленный вывод в кодировке консоли (cp866), а PowerShell
        /// читает его по [Console]::OutputEncoding. У воркера без консоли это ANSI (cp1251), и кириллица
        /// превращалась в «Ќ бва®©Є ». Консоль создаётся на отдельном рабочем столе воркера, её не видно.
        /// </summary>
        internal static void UseConsoleCodePage()
        {
            if (AllocConsole())
            {
                IntPtr window = GetConsoleWindow();
                if (window != IntPtr.Zero) ShowWindow(window, 0);
            }
            try { Console.OutputEncoding = Encoding.GetEncoding(OemCodePage); }
            catch (Exception) { }
        }
    }

}
