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

    // Авто-подготовка Firefox: если FF не запущен — поднять его (БЕЗ URL: табы поднимет
    // восстановление сессии, таб ChatGPT найдёт/создаст расширение через ensureTab —
    // иначе при восстановлении сессии получался дубль таба, §6.19д).
    // Отключается флагом --no-autolaunch (см. Program.ParseArgs).
    public static bool AutoLaunchFirefox = true;

    // Явный путь к firefox.exe (если null — ищем в реестре и стандартных местах).
    public static string? FirefoxPath = null;

    // Старт через подготовку (Preparing): сколько ждать сигнал «ready» от расширения.
    //   ReadyMs — когда Firefox уже на связи (нужно лишь открыть/проверить таб);
    //   LaunchMs — когда Firefox поднимаем с нуля (холодный старт браузера дольше).
    public const int PrepareReadyTimeoutMs = 15000;
    public const int PrepareLaunchTimeoutMs = 25000;

    // «Launch-grace»: столько после нашего запуска Firefox считается «уже запускающимся» —
    // повторный Launch в этот период подавляется. Без этого прогрев на старте сервера и
    // старт записи запускали firefox.exe независимо → ДВА окна браузера (§6.19).
    public const int FirefoxLaunchGraceMs = 30000;

    // Страховка после СТОПа: если распознанный текст не пришёл за этот срок —
    // отбой вставки (разоружаемся), индикатор показывает ошибку (§6.19).
    public const int InjectTimeoutMs = 20000;

    // Реконнект сетевого клиента к серверу: стартовая и максимальная пауза (мс).
    public const int ReconnectMinMs = 1000;
    public const int ReconnectMaxMs = 15000;

    // ---- Системный индикатор статуса (StatusOverlay) ----

    // Показывать ли индикатор-«пилюлю» вообще (--no-overlay отключает).
    public static bool ShowOverlay = true;

    // Компактный режим: только значок-кружок, без подписи (--overlay-compact).
    public static bool OverlayCompact = false;

    // Сколько держать финальные фазы («вставлено» / «ошибка») перед скрытием (мс).
    public const uint OverlayDoneHideMs = 1200;
    public const uint OverlayErrorHideMs = 2500;

    // Страховка: «Распознаю…» обычно живёт 1–3 с; если текст так и не пришёл —
    // не держать пилюлю вечно, скрыть через этот срок (мс).
    public const uint OverlayTranscribeStuckMs = 30000;

    // Период кадра анимации индикатора (мерцание звёздочки, пульс микрофона), мс.
    public const uint OverlayAnimTickMs = 80;

    // Цвета индикатора (COLORREF 0x00BBGGRR). Дефолт — тёмная пилюля с белой рамкой.
    // Меняются флагом --overlay-colors key=RRGGBB,… (см. Program.ParseOverlayColors):
    //   bg фон, border рамка, text подпись, wait ожидание (звёздочка/микрофон до записи),
    //   rec запись и галочка «вставлено», busy распознавание, err ошибка.
    public static uint OverlayColBg = Rgb(32, 32, 34);
    public static uint OverlayColBorder = Rgb(255, 255, 255);
    public static uint OverlayColText = Rgb(240, 240, 240);
    public static uint OverlayColWait = Rgb(255, 165, 0);
    public static uint OverlayColRec = Rgb(90, 205, 100);
    public static uint OverlayColBusy = Rgb(80, 155, 255);
    public static uint OverlayColErr = Rgb(235, 80, 80);

    /// <summary>RGB → COLORREF (у GDI порядок байтов 0x00BBGGRR).</summary>
    public static uint Rgb(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));
}
