using System.Runtime.InteropServices;

namespace VoiceBridge;

/// <summary>
/// Работа с буфером обмена через чистый Win32 (без WinForms/STA-зависимостей).
/// Формат CF_UNICODETEXT.
/// </summary>
internal static class Clipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// Кладёт <paramref name="text"/> в буфер. Возвращает прежний текст буфера
    /// (или null, если его не было) — для последующего восстановления.
    /// </summary>
    public static string? SetUnicodeText(string text, out bool ok)
    {
        ok = false;
        string? previous = null;

        if (!OpenClipboardWithRetry(IntPtr.Zero, attempts: 10))
            return null;

        try
        {
            // 1. Прочитать прежнее содержимое ДО EmptyClipboard (он инвалидирует хэндлы).
            IntPtr hPrev = Native.GetClipboardData(CF_UNICODETEXT);
            if (hPrev != IntPtr.Zero)
            {
                IntPtr p = Native.GlobalLock(hPrev);
                if (p != IntPtr.Zero)
                {
                    previous = Marshal.PtrToStringUni(p);
                    Native.GlobalUnlock(hPrev);
                }
            }

            if (!Native.EmptyClipboard())
                return previous;

            // 2. Выделить глобальную память под UTF-16 + завершающий ноль.
            int bytes = (text.Length + 1) * 2;
            IntPtr hMem = Native.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero)
                return previous;

            IntPtr dst = Native.GlobalLock(hMem);
            if (dst == IntPtr.Zero)
            {
                Native.GlobalFree(hMem);
                return previous;
            }

            try
            {
                if (text.Length > 0)
                    Marshal.Copy(text.ToCharArray(), 0, dst, text.Length);
                Marshal.WriteInt16(dst, text.Length * 2, 0); // \0
            }
            finally
            {
                Native.GlobalUnlock(hMem);
            }

            // 3. Передать владение системе. Если не удалось — освобождаем сами.
            if (Native.SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
            {
                Native.GlobalFree(hMem);
                return previous;
            }

            ok = true; // hMem теперь принадлежит системе — НЕ освобождаем.
            return previous;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static bool OpenClipboardWithRetry(IntPtr owner, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (Native.OpenClipboard(owner)) return true;
            Thread.Sleep(20); // буфер мог быть занят другим процессом — подождём
        }
        return false;
    }
}
