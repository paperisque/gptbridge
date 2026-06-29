namespace WebView2Poc;

/// <summary>
/// Диагностика POC: единые пути (профиль WebView2 + лог-файл) и потокобезопасная
/// запись в лог. Лог дублирует то, что видно в окне, — чтобы результаты тестов
/// (Probe/getUserMedia) и любые краши можно было прочитать из файла, а не из GUI.
/// </summary>
internal static class Diag
{
    private static readonly object Gate = new();

    // Данные программы (профиль WebView2 + лог) лежат РЯДОМ с программой — в подпапке data
    // от расположения DLL (AppContext.BaseDirectory), а НЕ в общем %LOCALAPPDATA%.
    // → у установленной версии это {папка установки}\data (установка per-user в
    //   %LOCALAPPDATA%\<имя> — туда писать можно), у Debug-сборки — bin\…\data.
    // Так разные сборки не делят одну папку, а деинсталлятор сносит хвосты целиком.
    // ВАЖНО: рассчитано на ПИСЬМАЕМУЮ папку программы (per-user install). Если когда-нибудь
    // ставить per-machine в Program Files — профиль придётся вернуть в %LOCALAPPDATA%.
    public static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "data");

    // Папка профиля Chromium (внутри Dir).
    public static readonly string WebViewDataDir = Path.Combine(Dir, "WebView2");

    public static readonly string LogPath = Path.Combine(Dir, "poc.log");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Новый сеанс отбиваем заголовком; файл не обнуляем — история сеансов полезна.
            File.AppendAllText(LogPath,
                $"{Environment.NewLine}===== сеанс {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
        }
        catch { /* лог — не критичный путь, молча игнорируем */ }
    }

    public static void Write(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, line); } catch { /* игнор */ }
        }
    }
}
