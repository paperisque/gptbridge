using System.Runtime.InteropServices;

namespace WebView2Poc;

/// <summary>
/// Низкоуровневый хук клавиатуры (WH_KEYBOARD_LL) ради «модификатор-тогглов», которые
/// RegisterHotKey не умеет. Жест распознаётся на ОТПУСКАНИИ Ctrl+Win (как у WhisperFlow):
///   • Ctrl+Win            → диктовка (тоггл);
///   • Ctrl+Win+«Y»        → диктовка с сохранением текста в буфере (по СКАН-КОДУ 0x2C);
///   • Ctrl+Win+Alt        → повторно вставить последний распознанный текст.
/// «Постороннее» нажатие (любая другая клавиша) отменяет жест — тогда это системный
/// шорткат, и мы его НЕ трогаем. Ничего не «глотаем»: всегда CallNextHookEx, поэтому Пуск
/// и Ctrl+Win+стрелки работают как обычно. Постим в HWND формы (в WinForms сообщения без
/// окна до WndProc не доходят).
/// </summary>
internal sealed class Hotkey : IDisposable
{
    private readonly IntPtr _targetHwnd;
    private readonly Win32.LowLevelKeyboardProc _proc; // держим от GC
    private IntPtr _hook;

    private bool _ctrl, _win, _alt;
    private bool _armed;        // Ctrl+Win зажаты — жест начат
    private bool _yPressed;     // во время жеста нажималась клавиша «+буфер»
    private bool _altSeen;      // во время жеста участвовал Alt → повторная вставка
    private bool _otherPressed; // нажата посторонняя клавиша → отмена жеста

    public Hotkey(IntPtr targetHwnd)
    {
        _targetHwnd = targetHwnd;
        _proc = HookProc;
    }

    public bool Install()
    {
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            uint msg = (uint)wParam.ToInt32();
            int vk = (int)data.vkCode;
            if (msg == Win32.WM_KEYDOWN || msg == Win32.WM_SYSKEYDOWN) Down(vk, data.scanCode);
            else if (msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP) Up(vk);
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsCtrl(int vk) => vk == Win32.VK_LCONTROL || vk == Win32.VK_RCONTROL || vk == Win32.VK_CONTROL;
    private static bool IsWin(int vk) => vk == Win32.VK_LWIN || vk == Win32.VK_RWIN;
    private static bool IsAlt(int vk) => vk == Win32.VK_MENU || vk == Win32.VK_LMENU || vk == Win32.VK_RMENU;

    private void Down(int vk, uint scan)
    {
        if (IsCtrl(vk)) _ctrl = true;
        else if (IsWin(vk)) _win = true;
        else if (IsAlt(vk)) { _alt = true; if (_armed) _altSeen = true; }
        else if (_armed)
        {
            // Скан-код, а не vkCode: позиция клавиши «+буфер» одинакова в любой раскладке.
            if (scan == Win32.SCAN_KEEPBUFFER) _yPressed = true;
            else _otherPressed = true;
        }

        if (_ctrl && _win && !_armed)
        {
            _armed = true;
            _yPressed = false;
            _otherPressed = false;
            _altSeen = _alt; // Alt мог быть зажат ещё до того, как собралось Ctrl+Win
        }
    }

    private void Up(int vk)
    {
        bool breaking = IsCtrl(vk) || IsWin(vk);
        if (_armed && breaking)
        {
            // Комбо распадается. Постороннего не было → решаем по Alt / «+буфер».
            if (!_otherPressed)
            {
                if (_altSeen)
                    Win32.PostMessage(_targetHwnd, Win32.WM_APP_REPASTE, IntPtr.Zero, IntPtr.Zero);
                else
                    Win32.PostMessage(_targetHwnd, Win32.WM_APP_TOGGLE,
                        _yPressed ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            }
            _armed = false;
        }

        if (IsCtrl(vk)) _ctrl = false;
        else if (IsWin(vk)) _win = false;
        else if (IsAlt(vk)) _alt = false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
