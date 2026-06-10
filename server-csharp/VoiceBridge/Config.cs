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
    //
    // ВАЖНО (грабля): это ТОЛЬКО страховка. Нормальный возврат фокуса — по сигналу
    // «recording», т.е. строго ПОСЛЕ реального старта захвата. Таймер тикает с момента
    // отправки mic, а после клика «Start» ChatGPT ещё раскручивает захват, и content.js
    // ждёт появления записи до 3 c, прежде чем прислать «recording». Если таймер сработает
    // РАНЬШЕ сигнала — сервер вернёт фокус с FF ДО старта записи, и захват не начнётся
    // (§6.11). Поэтому держим его заведомо больше окна ожидания content.js (поллинг 3 c).
    public const int RecordingFocusTimeoutMs = 6000;

    // Минимальное удержание фокуса на окне FF после отправки mic, прежде чем вернуть
    // фокус в рабочее окно. Сигнал «recording» от расширения приходит по ПОЯВЛЕНИЮ UI
    // диктовки (кнопка Submit), а это РАНЬШЕ реального включения микрофона. Отпустишь
    // фокус сразу (как было — через ~11 мс) → захват не стартует (§6.11). Поэтому держим
    // фокус минимум столько с момента mic, даже если «recording» уже пришёл.
    public const int MinFocusHoldMs = 1500;

    // Авто-подготовка Firefox: если FF не запущен — поднять его с табом ChatGPT;
    // если запущен, но таба нет — расширение откроет таб (см. ensureTab/ready).
    // Отключается флагом --no-autolaunch (см. Program.ParseArgs).
    public static bool AutoLaunchFirefox = true;

    // URL, который открываем в Firefox при подготовке/запуске.
    public const string ChatGptUrl = "https://chatgpt.com/";

    // Явный путь к firefox.exe (если null — ищем в реестре и стандартных местах).
    public static string? FirefoxPath = null;

    // Старт через подготовку (Preparing): сколько ждать сигнал «ready» от расширения.
    //   ReadyMs — когда Firefox уже на связи (нужно лишь открыть/проверить таб);
    //   LaunchMs — когда Firefox поднимаем с нуля (холодный старт браузера дольше).
    public const int PrepareReadyTimeoutMs = 15000;
    public const int PrepareLaunchTimeoutMs = 25000;

    // Реконнект сетевого клиента к серверу: стартовая и максимальная пауза (мс).
    public const int ReconnectMinMs = 1000;
    public const int ReconnectMaxMs = 15000;
}
