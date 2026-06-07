namespace VoiceBridge;

/// <summary>
/// Разделяемое состояние между потоком WS (пишет текст) и потоком цикла сообщений
/// (читает текст, держит целевой HWND). Текст защищён локом; HWND трогается только
/// из потока сообщений (захват и инъекция), поэтому достаточно простого поля.
/// </summary>
internal static class SharedState
{
    private static readonly object _gate = new();
    private static string _lastText = "";

    /// <summary>HWND окна, куда вставлять. Фиксируется хоткеем захвата.</summary>
    public static IntPtr TargetHwnd;

    /// <summary>Последний полученный от расширения текст.</summary>
    public static string LastText
    {
        get { lock (_gate) return _lastText; }
        set { lock (_gate) _lastText = value ?? ""; }
    }
}
