namespace VoiceBridge;

/// <summary>
/// Настройки сервера. Хоткеи (Ctrl+Win / Ctrl+Win+Y) живут в KeyboardHook —
/// это «модификатор-тоггл», который ловится низкоуровневым хуком, а не здесь.
/// </summary>
internal static class Config
{
    // WS-сервер. localhost обычно открывается без прав администратора;
    // если нет — см. подсказку про netsh urlacl в WsServer.
    public const string WsPrefix = "http://localhost:17890/";

    // Пауза после вывода окна на передний план, прежде чем слать Ctrl+V (мс).
    public const int ForegroundSettleMs = 60;

    // Задержка перед восстановлением буфера — даём цели прочитать наш текст (мс).
    public const int ClipboardRestoreDelayMs = 300;

    // Старт записи: окно Firefox выносится вперёд, ждём от расширения сигнал
    // «recording» и возвращаем фокус. Если сигнал не пришёл за этот срок —
    // возвращаем фокус всё равно (страховка, чтобы не «залипнуть» на FF), мс.
    public const int RecordingFocusTimeoutMs = 2500;
}
