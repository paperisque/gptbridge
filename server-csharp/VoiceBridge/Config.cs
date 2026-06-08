namespace VoiceBridge;

/// <summary>
/// Настройки сервера. Хоткеи (Ctrl+Win / Ctrl+Win+Y) живут в KeyboardHook —
/// это «модификатор-тоггл», который ловится низкоуровневым хуком, а не здесь.
/// </summary>
internal static class Config
{
    // WS-адрес. Host/Port задаются из аргументов запуска (см. Program.Main):
    //   сервер по умолчанию слушает localhost (без прав администратора);
    //   для работы по сети — запускать с --host + (все интерфейсы) или --host <IP>,
    //   тогда нужен разовый netsh urlacl (подсказку печатает WsServer) и правило брандмауэра.
    // Сетевой клиент (--connect <host>) коннектится по этому же Host:Port.
    public static string Host = "localhost";
    public static int Port = 17890;

    // Префикс для HttpListener (режим сервера): http://<host>:<port>/.
    public static string WsPrefix => $"http://{Host}:{Port}/";

    // URL для ClientWebSocket (режим сетевого клиента): ws://<host>:<port>/.
    public static string WsClientUrl => $"ws://{Host}:{Port}/";

    // Пауза после вывода окна на передний план, прежде чем слать Ctrl+V (мс).
    public const int ForegroundSettleMs = 60;

    // Задержка перед восстановлением буфера — даём цели прочитать наш текст (мс).
    public const int ClipboardRestoreDelayMs = 300;

    // Старт записи: окно Firefox выносится вперёд, ждём от расширения сигнал
    // «recording» и возвращаем фокус. Если сигнал не пришёл за этот срок —
    // возвращаем фокус всё равно (страховка, чтобы не «залипнуть» на FF), мс.
    public const int RecordingFocusTimeoutMs = 2500;

    // Реконнект сетевого клиента к серверу: стартовая и максимальная пауза (мс).
    public const int ReconnectMinMs = 1000;
    public const int ReconnectMaxMs = 15000;
}
