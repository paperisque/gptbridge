using System.Runtime.InteropServices;

namespace VoiceBridge;

/// <summary>
/// Вставка текста в чужое окно (цель — Electron/VS Code).
///
/// Почему так, а не WM_PASTE / WM_CHAR / WM_SETTEXT:
///   В Electron/Chromium реальное текстовое поле — это не стандартный Win32 EDIT,
///   а виджет внутри рендерера. Он не реагирует на отправленные ему оконные
///   сообщения «снаружи». Зато реагирует на НАСТОЯЩИЙ ввод, прошедший через
///   системную очередь ввода. Поэтому: ставим текст в буфер обмена, выносим окно
///   на передний план и СИНТЕЗИРУЕМ Ctrl+V через SendInput скан-кодами — для
///   приложения это неотличимо от живой клавиатуры.
/// </summary>
internal static class Injector
{
    /// <param name="keepInClipboard">
    /// true (вариант Ctrl+Win+Y) — оставить продиктованный текст в буфере обмена.
    /// false — вернуть прежнее содержимое буфера после вставки.
    /// </param>
    /// <returns>true — вставка прошла; false — отменена (нет окна/текста/буфера).</returns>
    public static bool Inject(bool keepInClipboard = false)
    {
        IntPtr target = SharedState.TargetHwnd;
        if (target == IntPtr.Zero || !Native.IsWindow(target))
        {
            Log.Warn(Lang.T("inject.no_target"));
            Native.MessageBeep(0xFFFFFFFF);
            return false;
        }

        string text = SharedState.LastText;
        if (string.IsNullOrEmpty(text))
        {
            Log.Warn(Lang.T("inject.no_text"));
            Native.MessageBeep(0xFFFFFFFF);
            return false;
        }

        string? previous = Clipboard.SetUnicodeText(text, out bool clipboardOk);
        if (!clipboardOk)
        {
            Log.Error(Lang.T("inject.clipboard_fail"));
            return false;
        }

        if (!ForceForeground(target))
            Log.Warn(Lang.T("inject.fg_warn"));

        Thread.Sleep(Config.ForegroundSettleMs);
        SendCtrlV();
        Log.Ok(Lang.T("inject.done", text.Length, target.ToInt64(), Native.GetWindowTitle(target))
               + (keepInClipboard ? Lang.T("inject.kept_suffix") : "."));

        if (!keepInClipboard && previous is not null)
        {
            Thread.Sleep(Config.ClipboardRestoreDelayMs);
            Clipboard.SetUnicodeText(previous, out _);
        }
        return true;
    }

    /// <summary>
    /// Принудительно выносит окно на передний план в обход защиты Windows
    /// от «кражи фокуса». Подробности по шагам — в README, раздел «Обходы фокуса».
    /// Публичный: используется и для вставки (цель), и для «мигающего» фокуса на
    /// окно Firefox при старте записи (см. Program.HandleToggle).
    /// </summary>
    public static bool ForceForeground(IntPtr target)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == target) return true;

        uint thisThread = Native.GetCurrentThreadId();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint targetThread = Native.GetWindowThreadProcessId(target, out _);

        // (1) Снять системный таймаут блокировки фокуса (best-effort, восстановим в конце).
        IntPtr oldTimeout = IntPtr.Zero;
        Native.SystemParametersInfo(Native.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
        Native.SystemParametersInfo(Native.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, Native.SPIF_SENDCHANGE);

        // (2) Привязать нашу очередь ввода к очереди текущего foreground и к очереди цели.
        //     Тогда Windows считает нас «частью» активного ввода и пускает SetForegroundWindow.
        bool attachedFg = fgThread != thisThread && Native.AttachThreadInput(thisThread, fgThread, true);
        bool attachedTarget = targetThread != thisThread && targetThread != fgThread
                              && Native.AttachThreadInput(thisThread, targetThread, true);

        try
        {
            if (Native.IsIconic(target))
                Native.ShowWindow(target, Native.SW_RESTORE);

            Native.BringWindowToTop(target);
            Native.SetForegroundWindow(target);
            Native.SetFocus(target);

            return WaitForeground(target, timeoutMs: 500);
        }
        finally
        {
            // (3) Обязательно отвязаться, иначе очереди ввода останутся склеенными.
            if (attachedTarget) Native.AttachThreadInput(thisThread, targetThread, false);
            if (attachedFg) Native.AttachThreadInput(thisThread, fgThread, false);

            // (4) Вернуть прежний таймаут блокировки фокуса.
            Native.SystemParametersInfo(Native.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, oldTimeout, Native.SPIF_SENDCHANGE);
        }
    }

    private static bool WaitForeground(IntPtr target, int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += 10)
        {
            if (Native.GetForegroundWindow() == target) return true;
            Thread.Sleep(10);
        }
        return Native.GetForegroundWindow() == target;
    }

    /// <summary>Синтез Ctrl+V скан-кодами: Ctrl↓ V↓ V↑ Ctrl↑.</summary>
    private static void SendCtrlV()
    {
        var inputs = new Native.INPUT[4];
        inputs[0] = Native.KeyScan(Native.SCAN_LCONTROL, keyUp: false);
        inputs[1] = Native.KeyScan(Native.SCAN_V, keyUp: false);
        inputs[2] = Native.KeyScan(Native.SCAN_V, keyUp: true);
        inputs[3] = Native.KeyScan(Native.SCAN_LCONTROL, keyUp: true);

        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            Log.Warn(Lang.T("inject.sendinput_warn", sent, inputs.Length, Marshal.GetLastWin32Error()));
    }
}
