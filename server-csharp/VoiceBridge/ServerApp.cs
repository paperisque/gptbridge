namespace VoiceBridge;

/// <summary>
/// Режим СЕРВЕРА (хаб). На главном потоке крутится цикл сообщений Win32 (нужен для
/// низкоуровневого хука клавиатуры); WS-сервер живёт в фоне.
///
/// Сервер — самодостаточная конечная точка: локальный контроллер (хук Ctrl+Win →
/// захват окна → вставка на ЭТОЙ машине) встроен прямо сюда — это «владелец Id=0».
/// Поверх него сервер маршрутизирует диктовку для сетевых клиентов-контроллеров
/// (каждый — «владелец Id>0»): их хоткей шлёт start/stop по WS, а текст диктовки
/// сервер отправляет обратно тому клиенту, который сессию запустил.
///
/// Диктовка одна на один Firefox: пока идёт сессия одного владельца, запросы
/// старта от других игнорируются (занято).
///
/// Модель управления (как у WhisperFlow):
///   Ctrl+Win        — тоггл диктовки: 1-е нажатие старт (микрофон),
///                     2-е нажатие стоп (птичка) → текст → авто-вставка.
///   Ctrl+Win+Y      — то же завершение, но текст дополнительно остаётся в буфере.
///
/// Цель вставки — окно, активное в момент СТОПа (универсально, не только CC).
/// Хук и инъекция работают на одном потоке (у него есть очередь ввода — это важно
/// для AttachThreadInput-фолбэка).
/// </summary>
internal static class ServerApp
{
    // Idle — простой; Preparing — готовим Firefox/таб ChatGPT, ждём «ready»; Recording — идёт запись.
    private enum DictState { Idle, Preparing, Recording }

    private const int LocalOwner = 0; // владелец-локальный контроллер (Id сетевых клиентов начинаются с 1)
    private const int NoOwner = -1;   // нет отложенного старта

    private static uint _mainThreadId;
    private static WsServer _server = null!;
    private static KeyboardHook _hook = null!;

    private static DictState _state = DictState.Idle;
    private static int _ownerId = LocalOwner; // кто запустил текущую сессию: 0 — локально, >0 — сетевой клиент
    private static bool _pendingInject;   // вооружены ли на доставку следующего текста
    private static bool _keepClipboard;   // вариант Ctrl+Win+Y (только для локального владельца)
    private static IntPtr _injectTarget;  // окно, захваченное в момент локального СТОПа

    private static IntPtr _returnWindow;  // окно, активное до старта — вернуть фокус сюда
    private static bool _awaitingRecording; // ждём сигнал «запись пошла», чтобы вернуть фокус
    private static Timer? _focusBackTimer;  // страховка: вернуть фокус, даже если сигнала нет
    private static long _micSentTicks;      // момент отправки mic (для минимального удержания фокуса)

    private static int _pendingStartOwner = NoOwner; // владелец, для которого начнём запись по «ready»
    private static Timer? _prepareTimer;  // страховка: не дождались «ready» — отменить старт

    public static void Run()
    {
        Banner();

        // 1. WS-сервер — в фоне. Колбэки маршалятся в наш поток через WM_APP_*.
        var cts = new CancellationTokenSource();
        _mainThreadId = Native.GetCurrentThreadId();
        _server = new WsServer
        {
            TextReceived = OnTextReceived,
            RecordingStarted = OnRecordingStarted,
            ControllerStartRequested = id =>
                Native.PostThreadMessage(_mainThreadId, Native.WM_APP_CTRL_START, new IntPtr(id), IntPtr.Zero),
            ControllerStopRequested = id =>
                Native.PostThreadMessage(_mainThreadId, Native.WM_APP_CTRL_STOP, new IntPtr(id), IntPtr.Zero),
            // Расширение подключилось — прогреваем таб ChatGPT (ensureTab); это же
            // двигает отложенный старт, если мы ждали поднятия Firefox.
            FirefoxConnected = OnFirefoxConnected,
            ReadyReceived = () =>
                Native.PostThreadMessage(_mainThreadId, Native.WM_APP_READY, IntPtr.Zero, IntPtr.Zero),
        };
        _ = _server.RunAsync(cts.Token);

        // 2. Хук клавиатуры на ЭТОМ потоке (он же крутит GetMessage) — локальный контроллер.
        _hook = new KeyboardHook(_mainThreadId);
        _hook.Install();

        // 2a. Прогрев Firefox на старте сервера: если FF не запущен — поднять его с
        // табом ChatGPT (без записи). Если запущен — таб подготовит расширение, как
        // только подключится (OnFirefoxConnected → ensureTab).
        PrepareFirefoxAtStartup();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // не убиваем процесс мгновенно — даём корректно выйти
            Native.PostThreadMessage(_mainThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        // 3. Цикл сообщений: колбэк хука, наши WM_APP_*.
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            switch (msg.message)
            {
                case Native.WM_APP_TOGGLE:
                    HandleToggle(withY: msg.wParam != IntPtr.Zero);
                    break;
                case Native.WM_APP_INJECT:
                    HandleInject();
                    break;
                case Native.WM_APP_FOCUS_BACK:
                    HandleFocusBack();
                    break;
                case Native.WM_APP_CTRL_START:
                    HandleCtrlStart((int)msg.wParam.ToInt64());
                    break;
                case Native.WM_APP_CTRL_STOP:
                    HandleCtrlStop((int)msg.wParam.ToInt64());
                    break;
                case Native.WM_APP_READY:
                    HandleReady();
                    break;
                case Native.WM_APP_PREPARE_TIMEOUT:
                    HandlePrepareTimeout();
                    break;
            }

            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        // 4. Завершение.
        _hook.Dispose();
        cts.Cancel();
        Log.Info("VoiceBridge остановлен.");
    }

    /// <summary>Локальный хоткей Ctrl+Win (или +Y). На потоке цикла сообщений.</summary>
    private static void HandleToggle(bool withY)
    {
        if (_state == DictState.Idle)
        {
            RequestStart(LocalOwner);
        }
        else if (_state == DictState.Preparing)
        {
            Log.Warn("Готовлю ChatGPT — секунду, нажми ещё раз чуть позже.");
        }
        else if (_ownerId == LocalOwner)
        {
            // Локальная сессия — локальный СТОП: цель вставки = активное сейчас окно.
            CancelFocusBack();
            _injectTarget = Native.GetForegroundWindow();
            _keepClipboard = withY;
            _pendingInject = true;
            _server.SendToFirefox(new WsMessage { Type = "stop" });
            _state = DictState.Idle;
            Log.Ok($"■ Стоп диктовки (локально). Цель=0x{_injectTarget.ToInt64():X} «{Native.GetWindowTitle(_injectTarget)}»"
                   + (withY ? " — текст останется в буфере." : "."));
        }
        else
        {
            Log.Warn($"Идёт удалённая сессия (клиент #{_ownerId}) — локальный Ctrl+Win проигнорирован (занято).");
        }
    }

    /// <summary>Сетевой клиент просит СТАРТ. На потоке цикла сообщений.</summary>
    private static void HandleCtrlStart(int id)
    {
        if (_state == DictState.Idle)
            RequestStart(id);
        else
            Log.Warn($"Старт сетевого клиента #{id} проигнорирован — занято (владелец {OwnerLabel(_ownerId)}, состояние {_state}).");
    }

    /// <summary>Сетевой клиент просит СТОП. На потоке цикла сообщений.</summary>
    private static void HandleCtrlStop(int id)
    {
        // Клиент передумал, пока мы ещё готовили ChatGPT под его старт — отменяем подготовку.
        if (_state == DictState.Preparing && _pendingStartOwner == id)
        {
            CancelPrepare();
            _state = DictState.Idle;
            Log.Info($"Сетевой клиент #{id} отменил старт во время подготовки.");
            return;
        }

        if (_state != DictState.Recording || _ownerId != id)
        {
            Log.Warn($"Стоп сетевого клиента #{id} проигнорирован (он не владелец текущей сессии).");
            return;
        }

        // Удалённая сессия: окно-цель и буфер — забота клиента (вставит у себя).
        // Сервер лишь завершает диктовку и помечает, что текст уйдёт владельцу #id.
        CancelFocusBack();
        _pendingInject = true;
        _server.SendToFirefox(new WsMessage { Type = "stop" });
        _state = DictState.Idle;
        Log.Ok($"■ Стоп диктовки (сетевой клиент #{id}). Текст уйдёт ему для вставки.");
    }

    /// <summary>
    /// Запрос старта (Idle → Preparing). Сначала убеждаемся, что Firefox с табом
    /// ChatGPT готов: если расширение на связи — просим открыть/проверить таб
    /// (ensureTab); если Firefox не запущен — поднимаем его. Сама запись начнётся в
    /// HandleReady, когда придёт «ready». Окно для возврата фокуса запоминаем СЕЙЧАС —
    /// пока пользователь ещё в своём рабочем окне (до манёвров с Firefox).
    /// </summary>
    private static void RequestStart(int ownerId)
    {
        _ownerId = ownerId;
        _pendingStartOwner = ownerId;
        _returnWindow = Native.GetForegroundWindow();

        if (_server.HasFirefox)
        {
            // Расширение на связи — просим открыть/проверить таб ChatGPT, ждём «ready».
            _state = DictState.Preparing;
            _server.SendToFirefox(new WsMessage { Type = "ensureTab" });
            ArmPrepareTimer(Config.PrepareReadyTimeoutMs);
            Log.Ok($"▶ Старт диктовки (владелец {OwnerLabel(ownerId)}): готовлю таб ChatGPT, жду готовности…");
        }
        else if (FirefoxLauncher.IsRunning())
        {
            // FF запущен, но расширение ещё не на связи (мог только подниматься/переподключаться).
            // Ждём: подключится → OnFirefoxConnected пришлёт ensureTab → «ready» → старт.
            _state = DictState.Preparing;
            ArmPrepareTimer(Config.PrepareLaunchTimeoutMs);
            Log.Ok($"▶ Старт диктовки (владелец {OwnerLabel(ownerId)}): Firefox запущен, жду подключения расширения…");
        }
        else if (Config.AutoLaunchFirefox && FirefoxLauncher.Launch(Config.ChatGptUrl))
        {
            _state = DictState.Preparing;
            ArmPrepareTimer(Config.PrepareLaunchTimeoutMs);
            Log.Ok($"▶ Старт диктовки (владелец {OwnerLabel(ownerId)}): поднимаю Firefox, запись начнётся, как он будет готов…");
        }
        else
        {
            _state = DictState.Idle;
            _pendingStartOwner = NoOwner;
            if (!Config.AutoLaunchFirefox)
                Log.Warn("Firefox не запущен, а авто-запуск отключён (--no-autolaunch). Открой ChatGPT в Firefox вручную.");
            // при неудаче запуска FirefoxLauncher уже залогировал причину
        }
    }

    /// <summary>
    /// Пришёл «ready» — таб ChatGPT готов. Если мы ждали этого для старта — начинаем
    /// запись. Иначе (прогрев на старте/реконнекте) просто игнорируем. На потоке цикла.
    /// </summary>
    private static void HandleReady()
    {
        if (_state != DictState.Preparing || _pendingStartOwner == NoOwner)
            return; // просто прогрев — таб готов, старта не ждали

        int owner = _pendingStartOwner;
        _pendingStartOwner = NoOwner;
        CancelPrepare();
        BeginRecording(owner);
    }

    /// <summary>Не дождались «ready» за отведённый срок — отменяем старт. На потоке цикла.</summary>
    private static void HandlePrepareTimeout()
    {
        if (_state != DictState.Preparing) return;
        CancelPrepare();
        _state = DictState.Idle;
        _pendingStartOwner = NoOwner;
        Log.Warn("Не дождался готовности ChatGPT (таб не открылся / расширение не ответило). Старт отменён — "
                 + "проверь Firefox и расширение, затем повтори Ctrl+Win.");
    }

    private static void ArmPrepareTimer(int ms)
    {
        CancelPrepare();
        _prepareTimer = new Timer(
            _ => Native.PostThreadMessage(_mainThreadId, Native.WM_APP_PREPARE_TIMEOUT, IntPtr.Zero, IntPtr.Zero),
            null, ms, Timeout.Infinite);
    }

    private static void CancelPrepare()
    {
        _prepareTimer?.Dispose();
        _prepareTimer = null;
    }

    /// <summary>
    /// Прогрев Firefox при старте сервера (без записи). FF не запущен и авто-запуск
    /// включён → поднимаем его с табом ChatGPT. Если запущен — таб подготовит само
    /// расширение, когда подключится (OnFirefoxConnected → ensureTab).
    /// </summary>
    private static void PrepareFirefoxAtStartup()
    {
        if (!Config.AutoLaunchFirefox) return;
        if (FirefoxLauncher.IsRunning())
        {
            Log.Info("Firefox уже запущен — таб ChatGPT подготовлю, как подключится расширение.");
            return;
        }
        if (FirefoxLauncher.Launch(Config.ChatGptUrl))
            Log.Info("Прогрев: поднимаю Firefox с ChatGPT (запись не начинаю — только подготовка).");
    }

    /// <summary>
    /// Расширение Firefox подключилось (на потоке WS). Просим подготовить таб ChatGPT.
    /// Это же двигает отложенный старт: расширение ответит «ready», и если мы ждали
    /// поднятия Firefox под запись — HandleReady её начнёт.
    /// </summary>
    private static void OnFirefoxConnected()
    {
        _server.SendToFirefox(new WsMessage { Type = "ensureTab" });
    }

    /// <summary>
    /// Общий старт записи для любого владельца. Firefox не начинает захват
    /// микрофона, пока его окно в фоне — выносим окно FF вперёд, потом шлём «микрофон».
    /// Фокус на машине-сервере вернём по сигналу «recording». Вызывается из HandleReady,
    /// когда таб ChatGPT уже готов; окно для возврата фокуса уже запомнено в RequestStart.
    /// </summary>
    private static void BeginRecording(int ownerId)
    {
        _ownerId = ownerId;
        _pendingInject = false; // на случай незавершённой прошлой сессии
        _state = DictState.Recording;

        IntPtr ff = WindowFinder.FindFirefoxChatGpt();
        if (ff != IntPtr.Zero)
        {
            _awaitingRecording = true;
            bool fg = Injector.ForceForeground(ff);
            _micSentTicks = Environment.TickCount64;
            _server.SendToFirefox(new WsMessage { Type = "mic" });
            ArmFocusBackTimer();
            Log.Ok($"▶ Старт диктовки (владелец {OwnerLabel(ownerId)}): поднял Firefox (0x{ff.ToInt64():X}, передний план={fg}), "
                   + $"жду старта записи, верну фокус в «{Native.GetWindowTitle(_returnWindow)}».");
        }
        else
        {
            _server.SendToFirefox(new WsMessage { Type = "mic" });
            Log.Warn($"▶ Старт диктовки (владелец {OwnerLabel(ownerId)}): окно Firefox с ChatGPT не найдено — "
                     + "фокус не поднимал (запись может не стартовать в фоне).");
        }
    }

    /// <summary>Дёргается из потока WS, когда расширение сообщило «запись пошла».</summary>
    private static void OnRecordingStarted()
    {
        Native.PostThreadMessage(_mainThreadId, Native.WM_APP_FOCUS_BACK, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Запись началась (или сработала страховка по таймеру) — возвращаем фокус
    /// в окно, где работал пользователь на машине-сервере. На потоке цикла сообщений.
    /// </summary>
    private static void HandleFocusBack()
    {
        if (!_awaitingRecording) { CancelFocusBack(); return; } // уже вернули / не ждём

        // Сигнал «recording» приходит по ПОЯВЛЕНИЮ UI диктовки (кнопка Submit), а это
        // раньше реального включения микрофона. Если отпустить фокус сразу — захват не
        // успеет стартовать (§6.11). Держим фокус на окне FF минимум MinFocusHoldMs с
        // момента mic; если ещё рано — ждём остаток и выходим (вернёмся сюда по таймеру).
        long held = Environment.TickCount64 - _micSentTicks;
        if (held < Config.MinFocusHoldMs)
        {
            long remain = Config.MinFocusHoldMs - held;
            CancelFocusBack();
            _focusBackTimer = new Timer(
                _ => Native.PostThreadMessage(_mainThreadId, Native.WM_APP_FOCUS_BACK, IntPtr.Zero, IntPtr.Zero),
                null, remain, Timeout.Infinite);
            Log.Info($"Запись отрапортована через {held} мс — держу фокус на Firefox ещё {remain} мс (захвату нужно время стартовать).");
            return;
        }

        CancelFocusBack();
        _awaitingRecording = false;

        if (_returnWindow != IntPtr.Zero && Native.IsWindow(_returnWindow))
        {
            Injector.ForceForeground(_returnWindow);
            Log.Ok($"Фокус возвращён в «{Native.GetWindowTitle(_returnWindow)}» (фокус держался {held} мс).");
        }
    }

    /// <summary>Страховочный таймер: если «recording» не пришёл — вернуть фокус всё равно.</summary>
    private static void ArmFocusBackTimer()
    {
        CancelFocusBack();
        _focusBackTimer = new Timer(
            _ => Native.PostThreadMessage(_mainThreadId, Native.WM_APP_FOCUS_BACK, IntPtr.Zero, IntPtr.Zero),
            null, Config.RecordingFocusTimeoutMs, Timeout.Infinite);
    }

    private static void CancelFocusBack()
    {
        _focusBackTimer?.Dispose();
        _focusBackTimer = null;
    }

    /// <summary>Дёргается из потока WS при приходе текста — перебрасываем в наш поток.</summary>
    private static void OnTextReceived()
    {
        Native.PostThreadMessage(_mainThreadId, Native.WM_APP_INJECT, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Текст пришёл. Доставляем, только если вооружены завершением диктовки.
    /// Локальному владельцу — вставка на месте; сетевому — отправка текста ему по WS.
    /// </summary>
    private static void HandleInject()
    {
        if (!_pendingInject) return; // не наш текст (не было СТОПа) — игнор
        _pendingInject = false;

        if (_ownerId == LocalOwner)
        {
            SharedState.TargetHwnd = _injectTarget;
            Injector.Inject(_keepClipboard);
        }
        else
        {
            bool ok = _server.SendToController(_ownerId, new WsMessage { Type = "inject", Payload = SharedState.LastText });
            if (ok)
                Log.Ok($"Текст ({SharedState.LastText.Length} симв.) отправлен сетевому клиенту #{_ownerId} для вставки.");
            else
                Log.Warn($"Сетевой клиент #{_ownerId} отключился — текст не доставлен.");
        }

        // Чистим композер ChatGPT, чтобы следующая диктовка началась с пустого поля.
        _server.SendToFirefox(new WsMessage { Type = "clear" });
    }

    private static string OwnerLabel(int id) => id == LocalOwner ? "локальный" : $"сетевой #{id}";

    private static void Banner()
    {
        Log.Info("=== VoiceBridge (сервер) — голосовой мост ChatGPT → активное окно ===");
        Log.Info($"Слушаю WS: {Config.WsPrefix}");
        Log.Info("Ctrl+Win        — старт диктовки; повторно — стоп + вставка в активное окно (локально).");
        Log.Info("Ctrl+Win+Y      — стоп + вставка + текст дополнительно остаётся в буфере обмена.");
        Log.Info("Сетевые клиенты — подключаются по WS и диктуют в своё активное окно (см. --connect).");
        Log.Info("Ctrl+C          — выход.");
    }
}
