using System.Runtime.InteropServices;
using System.Text;

namespace WebView2Poc;

/// <summary>
/// Минимальный Win32-интероп для живого режима: низкоуровневый хук клавиатуры,
/// синтез Ctrl+V (SendInput скан-кодами), буфер обмена, вынос окна на передний план.
/// Перенесено и ужато из основного сервера (VoiceBridge: Native/Injector/Clipboard).
/// </summary>
internal static partial class Win32
{
    // Свои сообщения от хука в окно (PostMessage на hwnd формы; для TOGGLE wParam=1 => вариант +буфер).
    public const uint WM_APP_TOGGLE = 0x8000 + 1;
    public const uint WM_APP_REPASTE = 0x8000 + 2; // Ctrl+Alt+V — повторно вставить последний текст

    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF); // PostMessage всем окнам верхнего уровня

    // Низкоуровневый хук клавиатуры.
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int VK_CONTROL = 0x11, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const int VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5; // Alt: общий / левый / правый (AltGr)
    // Клавиша «+буфер» по СКАН-КОДУ (позиция не зависит от раскладки; vk гуляет DE/RU).
    public const uint SCAN_KEEPBUFFER = 0x2C;

    public const int SW_RESTORE = 9;
    public const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000, SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001, SPIF_SENDCHANGE = 0x0002;
    public const ushort SCAN_LCONTROL = 0x1D, SCAN_V = 0x2F;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Событие клавиши по скан-коду (а не VK) — для синтеза Ctrl+V.</summary>
    public static INPUT KeyScan(ushort scan, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0u) }
        }
    };

    public static string GetWindowTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr GetModuleHandle(string? n);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string lpString); // системно-уникальное сообщение по строке
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage(); // язык UI Windows (LANGID)
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); // окно консоли хоста dotnet.exe (прячем на старте)

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool MessageBeep(uint t);

    [DllImport("kernel32.dll")] private static extern bool Beep(uint dwFreq, uint dwDuration);

    public static bool BeepEnabled = true; // --no-beep глушит звуковой отклик
    /// <summary>Свой звуковой отклик (НЕ системный MessageBeep — тот звучит «как ошибка» и сбивает):
    /// успех — короткий ВОСХОДЯЩИЙ тон (позитивный), ошибка — низкий короткий. На фоновом потоке,
    /// чтобы не блокировать вставку. Молчит при --no-beep.</summary>
    public static void FeedbackBeep(bool ok)
    {
        if (!BeepEnabled) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (ok)
                {
                    Beep(1175, 110);    // тон, что был вторым (D6) — теперь первый
                    Thread.Sleep(90);   // ощутимый промежуток между бик-бик
                    Beep(784, 200);     // тон, что был первым (G5) — теперь второй, низкий → мягче
                }
                else { Beep(440, 200); } // низкий короткий — ошибка
            }
            catch { /* нет аудиоустройства/занято — не критично */ }
        });
    }
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")] public static extern bool SystemParametersInfo(uint a, uint b, ref IntPtr pv, uint f);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")] public static extern bool SystemParametersInfo(uint a, uint b, IntPtr pv, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetClipboardData(uint f, IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetClipboardData(uint f);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint f, UIntPtr b);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalUnlock(IntPtr h);
}

/// <summary>Буфер обмена (CF_UNICODETEXT) через Win32; возвращает прежний текст для восстановления.</summary>
internal static class Clip
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static string? SetUnicodeText(string text, out bool ok)
    {
        ok = false;
        string? previous = null;
        if (!OpenWithRetry(10)) return null;
        try
        {
            IntPtr hPrev = Win32.GetClipboardData(CF_UNICODETEXT);
            if (hPrev != IntPtr.Zero)
            {
                IntPtr p = Win32.GlobalLock(hPrev);
                if (p != IntPtr.Zero) { previous = Marshal.PtrToStringUni(p); Win32.GlobalUnlock(hPrev); }
            }
            if (!Win32.EmptyClipboard()) return previous;

            int bytes = (text.Length + 1) * 2;
            IntPtr hMem = Win32.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) return previous;
            IntPtr dst = Win32.GlobalLock(hMem);
            if (dst == IntPtr.Zero) { Win32.GlobalFree(hMem); return previous; }
            try
            {
                if (text.Length > 0) Marshal.Copy(text.ToCharArray(), 0, dst, text.Length);
                Marshal.WriteInt16(dst, text.Length * 2, 0);
            }
            finally { Win32.GlobalUnlock(hMem); }

            if (Win32.SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) { Win32.GlobalFree(hMem); return previous; }
            ok = true;
            return previous;
        }
        finally { Win32.CloseClipboard(); }
    }

    private static bool OpenWithRetry(int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (Win32.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }
}

/// <summary>Вставка текста в чужое окно: буфер + вынос окна вперёд + синтез Ctrl+V (SendInput).</summary>
internal static class Injector
{
    private const int ForegroundSettleMs = 60;
    private const int ClipboardRestoreDelayMs = 300;

    public static bool Inject(string text, IntPtr target, bool keepInClipboard)
    {
        if (target == IntPtr.Zero || !Win32.IsWindow(target)) { Win32.FeedbackBeep(false); return false; }
        if (string.IsNullOrEmpty(text)) { Win32.FeedbackBeep(false); return false; }

        string? previous = Clip.SetUnicodeText(text, out bool ok);
        if (!ok) return false;

        ForceForeground(target);
        Thread.Sleep(ForegroundSettleMs);
        SendCtrlV();

        if (!keepInClipboard && previous is not null)
        {
            Thread.Sleep(ClipboardRestoreDelayMs);
            Clip.SetUnicodeText(previous, out _);
        }
        return true;
    }

    public static bool ForceForeground(IntPtr target)
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == target) return true;

        uint thisThread = Win32.GetCurrentThreadId();
        uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        uint targetThread = Win32.GetWindowThreadProcessId(target, out _);

        IntPtr oldTimeout = IntPtr.Zero;
        Win32.SystemParametersInfo(Win32.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
        Win32.SystemParametersInfo(Win32.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, Win32.SPIF_SENDCHANGE);

        bool attFg = fgThread != thisThread && Win32.AttachThreadInput(thisThread, fgThread, true);
        bool attTgt = targetThread != thisThread && targetThread != fgThread && Win32.AttachThreadInput(thisThread, targetThread, true);
        try
        {
            if (Win32.IsIconic(target)) Win32.ShowWindow(target, Win32.SW_RESTORE);
            Win32.BringWindowToTop(target);
            Win32.SetForegroundWindow(target);
            Win32.SetFocus(target);
            for (int w = 0; w < 500; w += 10)
            {
                if (Win32.GetForegroundWindow() == target) return true;
                Thread.Sleep(10);
            }
            return Win32.GetForegroundWindow() == target;
        }
        finally
        {
            if (attTgt) Win32.AttachThreadInput(thisThread, targetThread, false);
            if (attFg) Win32.AttachThreadInput(thisThread, fgThread, false);
            Win32.SystemParametersInfo(Win32.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, oldTimeout, Win32.SPIF_SENDCHANGE);
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new Win32.INPUT[4];
        inputs[0] = Win32.KeyScan(Win32.SCAN_LCONTROL, false);
        inputs[1] = Win32.KeyScan(Win32.SCAN_V, false);
        inputs[2] = Win32.KeyScan(Win32.SCAN_V, true);
        inputs[3] = Win32.KeyScan(Win32.SCAN_LCONTROL, true);
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }
}
