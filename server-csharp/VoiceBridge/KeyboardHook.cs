using System.Runtime.InteropServices;

namespace VoiceBridge;

/// <summary>
/// Низкоуровневый хук клавиатуры (WH_KEYBOARD_LL) ради «модификатор-тоггла»
/// Ctrl+Win, который RegisterHotKey не умеет (ему нужна обычная клавиша).
///
/// Жест распознаётся на ОТПУСКАНИИ: пока зажаты Ctrl+Win, следим, не нажата ли
/// клавиша «+буфер» (по СКАН-КОДУ, чтобы не зависеть от раскладки — вариант
/// «оставить текст в буфере») и не нажата ли посторонняя клавиша (тогда это
/// системный шорткат вроде Ctrl+Win+D — игнорируем). Ничего не «глотаем»: всегда
/// возвращаем CallNextHookEx, поэтому Пуск и системные Ctrl+Win+* работают как обычно.
///
/// Колбэк хука исполняется на потоке, установившем хук (наш цикл сообщений).
/// Внутри колбэка нельзя залипать (у LL-хука есть таймаут), поэтому мы лишь
/// постим себе сообщение в очередь, а тяжёлую работу делаем в цикле сообщений.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly uint _targetThreadId;
    private readonly Native.LowLevelKeyboardProc _proc; // держим ссылку от GC
    private IntPtr _hook = IntPtr.Zero;

    private bool _ctrlDown;
    private bool _winDown;
    private bool _armed;        // Ctrl+Win зажаты — жест начат
    private bool _yPressed;     // во время жеста нажималась Y
    private bool _otherPressed; // во время жеста нажималась посторонняя клавиша

    public KeyboardHook(uint targetThreadId)
    {
        _targetThreadId = targetThreadId;
        _proc = HookProc;
    }

    public void Install()
    {
        IntPtr hMod = Native.GetModuleHandle(null);
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
            Log.Error(Lang.T("hook.fail", Marshal.GetLastWin32Error()));
        else
            Log.Ok(Lang.T("hook.active"));
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            uint msg = (uint)wParam.ToInt32();
            int vk = (int)data.vkCode;

            if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN) HandleDown(vk, data.scanCode);
            else if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) HandleUp(vk);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsCtrl(int vk) =>
        vk == Native.VK_LCONTROL || vk == Native.VK_RCONTROL || vk == Native.VK_CONTROL;

    private static bool IsWin(int vk) =>
        vk == Native.VK_LWIN || vk == Native.VK_RWIN;

    private void HandleDown(int vk, uint scan)
    {
        if (IsCtrl(vk)) _ctrlDown = true;
        else if (IsWin(vk)) _winDown = true;
        else if (_armed)
        {
            // Ловим по СКАН-КОДУ (позиция клавиши), а не по vkCode: vkCode зависит
            // от раскладки (DE→0x59, RU→0x5A), скан-код одинаков (0x2C).
            if (scan == Native.SCAN_KEEPBUFFER) _yPressed = true;
            else _otherPressed = true;
        }

        if (_ctrlDown && _winDown && !_armed)
        {
            _armed = true;
            _yPressed = false;
            _otherPressed = false;
        }
    }

    private void HandleUp(int vk)
    {
        bool breaking = IsCtrl(vk) || IsWin(vk);
        if (_armed && breaking)
        {
            // Комбо распадается — тоггл только если не было посторонней клавиши.
            // _yPressed => вариант «+буфер» (Ctrl+Win+«Y»).
            if (!_otherPressed)
            {
                IntPtr withY = _yPressed ? new IntPtr(1) : IntPtr.Zero;
                Native.PostThreadMessage(_targetThreadId, Native.WM_APP_TOGGLE, withY, IntPtr.Zero);
            }
            _armed = false;
        }

        if (IsCtrl(vk)) _ctrlDown = false;
        else if (IsWin(vk)) _winDown = false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
