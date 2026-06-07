using System.Text;

namespace VoiceBridge;

/// <summary>
/// Поиск окна Firefox с активной вкладкой ChatGPT.
///
/// Зачем: Firefox откладывает захват микрофона для вкладки в окне БЕЗ фокуса
/// (защита от скрытой записи). Программный клик «Start dictation» стартует UI,
/// но реальная запись не идёт, пока окно FF не на переднем плане. Поэтому на
/// старте сервер на мгновение выносит это окно вперёд — и для этого его надо найти.
/// </summary>
internal static class WindowFinder
{
    // Класс всех верхнеуровневых окон Firefox.
    private const string MozillaClass = "MozillaWindowClass";

    /// <summary>
    /// Возвращает HWND окна Firefox, у которого в заголовке есть «ChatGPT»
    /// (т.е. ChatGPT — активная вкладка). Если такого нет — первое видимое окно
    /// Firefox (верхнее по Z-порядку). IntPtr.Zero, если Firefox не найден вовсе.
    /// </summary>
    public static IntPtr FindFirefoxChatGpt()
    {
        IntPtr chatgpt = IntPtr.Zero;
        IntPtr anyFirefox = IntPtr.Zero;
        var classBuf = new StringBuilder(64);

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;

            classBuf.Clear();
            Native.GetClassName(hwnd, classBuf, classBuf.Capacity);
            if (classBuf.ToString() != MozillaClass) return true;

            string title = Native.GetWindowTitle(hwnd);
            if (string.IsNullOrEmpty(title)) return true; // служебные окна FF без заголовка

            // EnumWindows идёт сверху вниз по Z-порядку: первое FF-окно — верхнее.
            if (anyFirefox == IntPtr.Zero) anyFirefox = hwnd;

            if (title.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                chatgpt = hwnd;
                return false; // нашли нужное — прекращаем перебор
            }
            return true;
        }, IntPtr.Zero);

        return chatgpt != IntPtr.Zero ? chatgpt : anyFirefox;
    }
}
