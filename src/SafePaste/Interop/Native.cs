using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SafePaste.Interop
{
    /// <summary>Всё общение с Windows: горячие клавиши, окна, ввод и уровень целостности процесса.</summary>
    internal static class Native
    {
        internal const int WM_HOTKEY = 0x0312;
        internal const int WM_CLIPBOARDUPDATE = 0x031D;
        internal const int HotkeyReview = 0x5350;
        internal const int HotkeyQuick = 0x5351;
        internal const int HotkeyDecrypt = 0x5352;

        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const uint MOD_NOREPEAT = 0x4000;
        internal const uint VK_V = 0x56;
        internal const uint VK_C = 0x43;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_INSERT = 0x2D;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MAPVK_VK_TO_VSC = 0;

        private const int SW_RESTORE = 9;
        private const uint GA_ROOTOWNER = 3;

        private const uint EVENT_SYSTEM_FOREGROUND = 3;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint WINEVENT_SKIPOWNPROCESS = 2;

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_TRANSIENTWINDOW = 3;
        private const int DWMWCP_ROUND = 2;

        internal const int WS_MINIMIZEBOX = 0x00020000;
        internal const int WS_SYSMENU = 0x00080000;
        internal const int WS_EX_TOPMOST = 0x00000008;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_NOACTIVATE = 0x08000000;

        internal const int WM_NCHITTEST = 0x0084;
        internal const int WM_NCLBUTTONDBLCLK = 0x00A3;
        internal const int WM_MOUSEACTIVATE = 0x0021;
        internal const int MA_NOACTIVATE = 3;
        internal const int HTTRANSPARENT = -1;
        internal const int HTCLIENT = 1;
        internal const int HTCAPTION = 2;
        internal const int HTLEFT = 10;
        internal const int HTRIGHT = 11;
        internal const int HTTOP = 12;
        internal const int HTTOPLEFT = 13;
        internal const int HTTOPRIGHT = 14;
        internal const int HTBOTTOM = 15;
        internal const int HTBOTTOMLEFT = 16;
        internal const int HTBOTTOMRIGHT = 17;

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint time);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool AddClipboardFormatListener(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RemoveClipboardFormatListener(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        /// <summary>Номер последнего изменения буфера обмена: растёт при каждой записи в буфер.</summary>
        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>false: окно заблокировано, например модальным окном проверки.</summary>
        [DllImport("user32.dll")]
        internal static extern bool IsWindowEnabled(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, ref bool value, uint update);

        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        /// <summary>
        /// «Эффекты анимации» в параметрах специальных возможностей Windows. Выключены: элементы
        /// меняют вид сразу, без плавных переходов.
        /// </summary>
        internal static bool ClientAreaAnimation()
        {
            try
            {
                bool enabled = true;
                return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0) || enabled;
            }
            catch (Exception)
            {
                return true;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info,
            int length, out int returnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref MARGINS margins);

        /// <summary>Окна оболочки: вставлять в них нечего.</summary>
        private static readonly HashSet<string> ShellClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
            "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskSwitcherWnd",
            "TaskListThumbnailWnd", "#32768", "tooltips_class32", "SysShadow", "Xaml_WindowedPopupClass"
        };

        private static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;
        private static WinEventProc foregroundCallback;
        private static IntPtr foregroundHook;
        private static IntPtr lastExternalWindow;

        internal static void EnableDpiAwareness()
        {
            try
            {
                // Per-monitor v2 в WinForms .NET Framework требует app.config, поэтому берём system aware.
                if (SetProcessDpiAwarenessContext(new IntPtr(-2)))
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Windows 8.1 и старше.
            }
            try
            {
                SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        internal static void EnableDarkTitleBar(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return;
            }
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
                }
            }
            catch (Exception)
            {
                // Оформление не должно мешать работе.
            }
        }

        /// <summary>Размытый фон за окном появился в Windows 11 22H2.</summary>
        internal static readonly bool BackdropSupported = Environment.OSVersion.Version.Major >= 10
            && Environment.OSVersion.Version.Build >= 22621;

        /// <summary>
        /// Скругляет углы и, если можно, кладёт под окно размытый фон (Acrylic).
        /// false: стекла нет, окно должно рисовать непрозрачный фон само.
        /// </summary>
        internal static bool EnableGlass(IntPtr window, bool allowBackdrop)
        {
            if (window == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                EnableDarkTitleBar(window);
                int corners = DWMWCP_ROUND;
                DwmSetWindowAttribute(window, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
                if (!allowBackdrop || !BackdropSupported)
                {
                    return false;
                }
                int backdrop = DWMSBT_TRANSIENTWINDOW;
                if (DwmSetWindowAttribute(window, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
                {
                    return false;
                }
                MARGINS margins = new MARGINS();
                margins.Left = -1;
                margins.Right = -1;
                margins.Top = -1;
                margins.Bottom = -1;
                return DwmExtendFrameIntoClientArea(window, ref margins) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static Point PointFromLParam(IntPtr value)
        {
            long raw = value.ToInt64();
            return new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
        }

        // ---------------------------------------------------------------- окно для вставки

        internal static void StartForegroundTracking()
        {
            if (foregroundHook != IntPtr.Zero)
            {
                return;
            }
            Remember(GetForegroundWindow());
            foregroundCallback = OnForegroundChanged;
            foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        internal static void StopForegroundTracking()
        {
            if (foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(foregroundHook);
                foregroundHook = IntPtr.Zero;
            }
            foregroundCallback = null;
        }

        private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint time)
        {
            if (objectId == 0)
            {
                Remember(window);
            }
        }

        private static void Remember(IntPtr window)
        {
            if (IsPasteTarget(window))
            {
                lastExternalWindow = window;
            }
        }

        /// <summary>
        /// Куда вставлять: активное окно, а при вызове из трея последнее активное окно
        /// чужого приложения (в этот момент активна панель задач).
        /// </summary>
        internal static IntPtr GetPasteTarget(bool allowRecent)
        {
            IntPtr foreground = GetForegroundWindow();
            if (IsPasteTarget(foreground))
            {
                return foreground;
            }
            if (allowRecent && IsPasteTarget(lastExternalWindow))
            {
                return lastExternalWindow;
            }
            return IntPtr.Zero;
        }

        internal static bool IsPasteTarget(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window))
            {
                return false;
            }
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == 0 || processId == OwnProcessId)
            {
                return false;
            }
            return !ShellClasses.Contains(GetWindowClass(window));
        }

        internal static string GetWindowTitle(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return string.Empty;
            }
            StringBuilder text = new StringBuilder(256);
            int length = GetWindowText(window, text, text.Capacity);
            return length > 0 ? text.ToString() : string.Empty;
        }

        private static string GetWindowClass(IntPtr window)
        {
            StringBuilder name = new StringBuilder(256);
            int length = GetClassName(window, name, name.Capacity);
            return length > 0 ? name.ToString() : string.Empty;
        }

        internal static bool ActivateWindow(IntPtr window, int timeoutMs)
        {
            if (!IsWindow(window))
            {
                return false;
            }
            if (IsSameTopLevel(GetForegroundWindow(), window))
            {
                return true;
            }
            if (IsIconic(window))
            {
                ShowWindow(window, SW_RESTORE);
            }
            SetForegroundWindow(window);
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (IsSameTopLevel(GetForegroundWindow(), window))
                {
                    return true;
                }
                Thread.Sleep(15);
            }
            return false;
        }

        private static bool IsSameTopLevel(IntPtr left, IntPtr right)
        {
            if (left == IntPtr.Zero || right == IntPtr.Zero)
            {
                return false;
            }
            return left == right || GetAncestor(left, GA_ROOTOWNER) == GetAncestor(right, GA_ROOTOWNER);
        }

        // ---------------------------------------------------------------- ввод

        /// <summary>
        /// Ждёт, пока пользователь отпустит Ctrl/Shift/Alt/Win и V. Иначе синтетическое нажатие
        /// превратится в Ctrl+Shift+V и снова сработает как горячая клавиша.
        /// </summary>
        internal static bool WaitForKeysReleased(int timeoutMs)
        {
            return WaitForKeysReleased(timeoutMs, VK_V);
        }

        /// <summary>То же для другой клавиши сочетания, например C в Ctrl+Shift+C.</summary>
        internal static bool WaitForKeysReleased(int timeoutMs, uint letter)
        {
            int[] keys = new int[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN, (int)letter };
            Stopwatch watch = Stopwatch.StartNew();
            while (true)
            {
                bool anyDown = false;
                foreach (int key in keys)
                {
                    if ((GetAsyncKeyState(key) & 0x8000) != 0)
                    {
                        anyDown = true;
                        break;
                    }
                }
                if (!anyDown)
                {
                    return true;
                }
                if (watch.ElapsedMilliseconds >= timeoutMs)
                {
                    return false;
                }
                Thread.Sleep(15);
            }
        }

        internal static bool SendCtrlV()
        {
            INPUT[] inputs = new INPUT[4];
            inputs[0] = Key(VK_CONTROL, false);
            inputs[1] = Key((int)VK_V, false);
            inputs[2] = Key((int)VK_V, true);
            inputs[3] = Key(VK_CONTROL, true);
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            return sent == inputs.Length;
        }

        /// <summary>
        /// Ctrl+Insert копирует выделенное почти везде: в полях Windows, браузерах, Electron, терминалах.
        /// В терминале, в отличие от Ctrl+C, он не прерывает работающую команду.
        /// </summary>
        internal static bool SendCtrlInsert()
        {
            INPUT[] inputs = new INPUT[4];
            inputs[0] = Key(VK_CONTROL, false);
            inputs[1] = Key(VK_INSERT, false, true);
            inputs[2] = Key(VK_INSERT, true, true);
            inputs[3] = Key(VK_CONTROL, true);
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            return sent == inputs.Length;
        }

        /// <summary>Окно принадлежит самому SafePaste.</summary>
        internal static bool IsOwnWindow(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return false;
            }
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            return processId == OwnProcessId;
        }

        private static INPUT Key(int virtualKey, bool up)
        {
            return Key(virtualKey, up, false);
        }

        /// <summary>
        /// extended: клавиша из блока навигации. Без этого флага Insert со скан-кодом читается
        /// как клавиша 0 цифрового блока, и программа напечатает «0» вместо копирования.
        /// </summary>
        private static INPUT Key(int virtualKey, bool up, bool extended)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.u.ki.wVk = (ushort)virtualKey;
            // Скан-код нужен клиентам RDP и виртуальных машин: без него нажатие может не дойти.
            input.u.ki.wScan = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
            input.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
            input.u.ki.time = 0;
            input.u.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }

        // ---------------------------------------------------------------- уровень целостности

        /// <summary>
        /// true, если окно принадлежит процессу с более высоким уровнем целостности:
        /// Windows не пропустит в него синтетическое нажатие Ctrl+V.
        /// </summary>
        internal static bool IsElevatedTarget(IntPtr window)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == 0)
            {
                return true;
            }
            int own = GetIntegrityLevel(GetCurrentProcess());
            if (own < 0)
            {
                return false; // свой уровень неизвестен, не мешаем вставке
            }
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero)
            {
                return true;
            }
            try
            {
                int target = GetIntegrityLevel(process);
                if (target < 0)
                {
                    return true;
                }
                return target > own;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static int GetIntegrityLevel(IntPtr process)
        {
            IntPtr token;
            if (!OpenProcessToken(process, TOKEN_QUERY, out token))
            {
                return -1;
            }
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int length;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                if (length <= 0)
                {
                    return -1;
                }
                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out length))
                {
                    return -1;
                }
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                IntPtr countPointer = GetSidSubAuthorityCount(sid);
                if (sid == IntPtr.Zero || countPointer == IntPtr.Zero)
                {
                    return -1;
                }
                int count = Marshal.ReadByte(countPointer);
                if (count <= 0)
                {
                    return -1;
                }
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                CloseHandle(token);
            }
        }
    }
}
