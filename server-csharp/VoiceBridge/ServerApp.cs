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
    private static bool _recordingConfirmed; // сигнал «recording» в этой сессии реально приходил
    private static Timer? _injectTimer;      // страховка: текст после СТОПа так и не пришёл — отбой
    private static bool _discardNextText;    // распознавание отменено: поздний текст не вставлять, а вычистить

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
            EmptyReceived = () =>
                Native.PostThreadMessage(_mainThreadId, Native.WM_APP_EMPTY, IntPtr.Zero, IntPtr.Zero),
        };
        _ = _server.RunAsync(cts.Token);

        // 2. Хук клавиатуры на ЭТОМ потоке (он же крутит GetMessage) — локальный контроллер.
        _hook = new KeyboardHook(_mainThreadId);
        _hook.Install();

        // 2b. Системный индикатор статуса — окно-пилюля живёт на этом же потоке,
        // его WM_PAINT/WM_TIMER разруливает наш DispatchMessage ниже.
        StatusOverlay.Create();

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
                    // wParam=1 — реальный сигнал «recording» от расширения; 0 — страховочный таймер.
                    HandleFocusBack(recordingStarted: msg.wParam != IntPtr.Zero);
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
                case Native.WM_APP_INJECT_TIMEOUT:
                    HandleInjectTimeout();
                    break;
                case Native.WM_APP_EMPTY:
                    HandleEmpty();
                    break;
            }

            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        // 4. Завершение.
        StatusOverlay.Destroy();
        _hook.Dispose();
        cts.Cancel();
        Log.Info(Lang.T("server.stopped"));
    }

    /// <summary>
    /// Локальный хоткей Ctrl+Win (или +Y). На потоке цикла сообщений.
    /// Вне пары «старт/стоп записи» работает как УНИВЕРСАЛЬНАЯ ОТМЕНА (§6.20):
    /// во время подготовки — отмена старта; во время распознавания после СТОПа —
    /// отмена вставки; при записи без подтверждения захвата — отбой (AbortRecording).
    /// </summary>
    private static void HandleToggle(bool withY)
    {
        if (_state == DictState.Idle)
        {
            // Распознавание после СТОПа ещё идёт — это ОТМЕНА, а не новый старт.
            if (_pendingInject && _ownerId == LocalOwner)
            {
                CancelPendingInject();
                return;
            }
            RequestStart(LocalOwner);
        }
        else if (_state == DictState.Preparing)
        {
            // Повторное нажатие во время подготовки = передумал, отмена старта.
            if (_pendingStartOwner == LocalOwner)
                CancelLocalPrepare();
            else
                Log.Warn(Lang.T("server.busy_remote", _ownerId));
        }
        else if (_ownerId == LocalOwner)
        {
            // Запись так и не подтвердилась (нет сигнала «recording» — захват не стартовал:
            // нет звука/микрофона и т.п.). «Submit» тут зависнет на распознавании пустоты —
            // вместо него жмём «Cancel dictation» и возвращаемся в исходное состояние (§6.19).
            if (!_recordingConfirmed)
            {
                AbortRecording();
                return;
            }

            // Локальная сессия — локальный СТОП: цель вставки = активное сейчас окно.
            CancelFocusBack();
            _injectTarget = Native.GetForegroundWindow();
            _keepClipboard = withY;
            _pendingInject = true;
            ArmInjectTimer();
            _server.SendToFirefox(new WsMessage { Type = "stop" });
            _state = DictState.Idle;
            // Перезаякориваем пилюлю: пользователь сейчас в окне-цели — туда и придёт текст.
            StatusOverlay.Show(StatusOverlay.Phase.Transcribing, _injectTarget);
            Log.Ok(Lang.T("server.stop_local", _injectTarget.ToInt64(), Native.GetWindowTitle(_injectTarget))
                   + (withY ? Lang.T("suffix.keep_buffer") : "."));
        }
        else
        {
            Log.Warn(Lang.T("server.busy_remote", _ownerId));
        }
    }

    /// <summary>Сетевой клиент просит СТАРТ. На потоке цикла сообщений.</summary>
    private static void HandleCtrlStart(int id)
    {
        if (_state == DictState.Idle)
            RequestStart(id);
        else
            Log.Warn(Lang.T("server.ctrl_start_busy", id, OwnerLabel(_ownerId), _state));
    }

    /// <summary>Сетевой клиент просит СТОП. На потоке цикла сообщений.</summary>
    private static void HandleCtrlStop(int id)
    {
        // Клиент передумал, пока мы ещё готовили ChatGPT под его старт — отменяем подготовку.
        if (_state == DictState.Preparing && _pendingStartOwner == id)
        {
            CancelPrepare();
            _state = DictState.Idle;
            Log.Info(Lang.T("server.ctrl_cancel_prepare", id));
            return;
        }

        if (_state != DictState.Recording || _ownerId != id)
        {
            Log.Warn(Lang.T("server.ctrl_stop_ignored", id));
            return;
        }

        // Захват так и не стартовал — отбой вместо «Submit» (как у локального стопа, §6.19).
        if (!_recordingConfirmed)
        {
            AbortRecording();
            return;
        }

        // Удалённая сессия: окно-цель и буфер — забота клиента (вставит у себя).
        // Сервер лишь завершает диктовку и помечает, что текст уйдёт владельцу #id.
        CancelFocusBack();
        _pendingInject = true;
        ArmInjectTimer();
        _server.SendToFirefox(new WsMessage { Type = "stop" });
        _state = DictState.Idle;
        Log.Ok(Lang.T("server.stop_remote", id));
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
            // payload «start»: таб (если придётся создавать) открывать АКТИВНЫМ — фоновые
            // табы Firefox грузит с пониженным приоритетом, пассивный открывался долго (§6.19).
            _state = DictState.Preparing;
            _server.SendToFirefox(new WsMessage { Type = "ensureTab", Payload = EnsurePayload(start: true) });
            ArmPrepareTimer(Config.PrepareReadyTimeoutMs);
            Log.Ok(Lang.T("server.start_ensure", OwnerLabel(ownerId)));
        }
        else if (FirefoxLauncher.IsRunning() || FirefoxLauncher.IsStartingUp)
        {
            // FF запущен, но расширение ещё не на связи (мог только подниматься/переподключаться).
            // Ждём: подключится → OnFirefoxConnected пришлёт ensureTab → «ready» → старт.
            _state = DictState.Preparing;
            ArmPrepareTimer(Config.PrepareLaunchTimeoutMs);
            Log.Ok(Lang.T("server.start_wait_ext", OwnerLabel(ownerId)));
        }
        else if (Config.AutoLaunchFirefox && FirefoxLauncher.Launch())
        {
            _state = DictState.Preparing;
            ArmPrepareTimer(Config.PrepareLaunchTimeoutMs);
            Log.Ok(Lang.T("server.start_launch", OwnerLabel(ownerId)));
        }
        else
        {
            _state = DictState.Idle;
            _pendingStartOwner = NoOwner;
            if (!Config.AutoLaunchFirefox)
                Log.Warn(Lang.T("server.no_ff_noautolaunch"));
            // при неудаче запуска FirefoxLauncher уже залогировал причину
        }

        // Индикатор — только для локального владельца (пользователь за ЭТОЙ машиной);
        // сетевым клиентам сервер на своём экране ничего не рисует (скоуп v1).
        if (_state == DictState.Preparing && ownerId == LocalOwner)
            StatusOverlay.Show(StatusOverlay.Phase.Preparing, _returnWindow);
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
        bool localOwner = _pendingStartOwner == LocalOwner;
        CancelPrepare();
        _state = DictState.Idle;
        _pendingStartOwner = NoOwner;
        if (localOwner)
            StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("overlay.chatgpt_no_answer"));
        Log.Warn(Lang.T("server.prepare_timeout"));
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
            Log.Info(Lang.T("server.ff_already_running"));
            return;
        }
        if (FirefoxLauncher.Launch())
            Log.Info(Lang.T("server.warmup"));
    }

    /// <summary>
    /// Расширение Firefox подключилось (на потоке WS). Просим подготовить таб ChatGPT.
    /// Это же двигает отложенный старт: расширение ответит «ready», и если мы ждали
    /// поднятия Firefox под запись — HandleReady её начнёт. Чтение _state с чужого
    /// потока тут безобидно — влияет только на подсказку в payload.
    /// </summary>
    private static void OnFirefoxConnected()
    {
        bool startPending = _state == DictState.Preparing && _pendingStartOwner != NoOwner;
        _server.SendToFirefox(new WsMessage { Type = "ensureTab", Payload = EnsurePayload(startPending) });
    }

    /// <summary>
    /// payload для ensureTab — подсказки расширению (см. §6.19 и PROTOCOL.md):
    ///   «start»  — подготовка ПОД ЗАПИСЬ: новый таб открывать активным (грузится быстрее);
    ///   «warmup» — просто прогрев: новый таб открывать пассивно, пользователя не дёргать;
    ///   «,cold»  — Firefox только что запущен нами: его стартовый таб ChatGPT ещё может
    ///              грузиться — подольше ждать его появления, а не создавать второй.
    /// </summary>
    private static string EnsurePayload(bool start) =>
        (start ? "start" : "warmup") + (FirefoxLauncher.IsStartingUp ? ",cold" : "");

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
        _recordingConfirmed = false; // подтвердится сигналом «recording»
        _discardNextText = false;    // новая сессия — прошлые отмены не действуют
        CancelInjectTimer();
        _state = DictState.Recording;

        if (ownerId == LocalOwner)
            StatusOverlay.Set(StatusOverlay.Phase.Starting);

        IntPtr ff = WindowFinder.FindFirefoxChatGpt();
        if (ff != IntPtr.Zero)
        {
            _awaitingRecording = true;
            bool fg = Injector.ForceForeground(ff);
            _micSentTicks = Environment.TickCount64;
            _server.SendToFirefox(new WsMessage { Type = "mic" });
            ArmFocusBackTimer();
            Log.Ok(Lang.T("server.begin_recording", OwnerLabel(ownerId), ff.ToInt64(), fg, Native.GetWindowTitle(_returnWindow)));
        }
        else
        {
            _server.SendToFirefox(new WsMessage { Type = "mic" });
            Log.Warn(Lang.T("server.begin_no_ff_window", OwnerLabel(ownerId)));
        }
    }

    /// <summary>Дёргается из потока WS, когда расширение сообщило «запись пошла».</summary>
    private static void OnRecordingStarted()
    {
        // wParam=1 — отличаем реальный сигнал от страховочных таймеров (им важен индикатор).
        Native.PostThreadMessage(_mainThreadId, Native.WM_APP_FOCUS_BACK, new IntPtr(1), IntPtr.Zero);
    }

    /// <summary>
    /// Запись началась (или сработала страховка по таймеру) — возвращаем фокус
    /// в окно, где работал пользователь на машине-сервере. На потоке цикла сообщений.
    /// </summary>
    private static void HandleFocusBack(bool recordingStarted)
    {
        // Реальный сигнал «запись пошла»: подтверждаем сессию (иначе СТОП пойдёт по
        // abort-пути) и переводим индикатор в фазу записи (зелёный микрофон).
        if (recordingStarted && _state == DictState.Recording)
        {
            _recordingConfirmed = true;
            if (_ownerId == LocalOwner)
                StatusOverlay.Set(StatusOverlay.Phase.Recording);
        }

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
            Log.Info(Lang.T("server.hold_focus", held, remain));
            return;
        }

        CancelFocusBack();
        _awaitingRecording = false;

        if (_returnWindow != IntPtr.Zero && Native.IsWindow(_returnWindow))
        {
            Injector.ForceForeground(_returnWindow);
            Log.Ok(Lang.T("server.focus_back", Native.GetWindowTitle(_returnWindow), held));
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

    /// <summary>
    /// Отбой сессии, в которой захват так и не стартовал (сигнала «recording» не было):
    /// жмём «Cancel dictation» (закрыть зависший UI диктовки), НЕ вооружаемся на текст
    /// и возвращаемся в Idle. Приводит алгоритм в исходное состояние (§6.19).
    /// </summary>
    private static void AbortRecording()
    {
        CancelFocusBack();
        _awaitingRecording = false;
        _pendingInject = false;
        CancelInjectTimer();
        _server.SendToFirefox(new WsMessage { Type = "cancel" });
        _state = DictState.Idle;
        if (_ownerId == LocalOwner)
            StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("overlay.no_recording"));
        Log.Warn(Lang.T("server.abort_no_recording"));
    }

    /// <summary>Ctrl+Win во время подготовки — отмена старта (пользователь передумал, §6.20).
    /// Уже отправленный ensureTab не вредит: таб подготовится «прогревом», ready проигнорируется.</summary>
    private static void CancelLocalPrepare()
    {
        CancelPrepare();
        _state = DictState.Idle;
        _pendingStartOwner = NoOwner;
        StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("overlay.cancelled"));
        Log.Ok(Lang.T("server.cancel_prepare"));
    }

    /// <summary>Ctrl+Win во время распознавания после СТОПа — отмена вставки (§6.20).
    /// Поздний текст, если всё же приедет, будет вычищен из композера (_discardNextText).</summary>
    private static void CancelPendingInject()
    {
        _pendingInject = false;
        CancelInjectTimer();
        _discardNextText = true;
        StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("overlay.cancelled"));
        Log.Ok(Lang.T("server.cancel_transcribe"));
    }

    /// <summary>Страховка после СТОПа: текст должен прийти за InjectTimeoutMs, иначе отбой.</summary>
    private static void ArmInjectTimer()
    {
        CancelInjectTimer();
        _injectTimer = new Timer(
            _ => Native.PostThreadMessage(_mainThreadId, Native.WM_APP_INJECT_TIMEOUT, IntPtr.Zero, IntPtr.Zero),
            null, Config.InjectTimeoutMs, Timeout.Infinite);
    }

    private static void CancelInjectTimer()
    {
        _injectTimer?.Dispose();
        _injectTimer = null;
    }

    /// <summary>Текст после СТОПа так и не пришёл — разоружаемся, чтобы не зависнуть
    /// на «Распознаю…» и не вставить случайный поздний текст. На потоке цикла.</summary>
    private static void HandleInjectTimeout()
    {
        if (!_pendingInject) return; // текст уже доставлен / отбой был
        _pendingInject = false;
        if (_ownerId == LocalOwner)
            StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("overlay.no_text"));
        Log.Warn(Lang.T("server.inject_timeout"));
    }

    /// <summary>Расширение доложило «распознано пусто» (в записи была тишина) —
    /// немедленный отбой вставки, не дожидаясь страховочного таймаута. На потоке цикла.</summary>
    private static void HandleEmpty()
    {
        if (!_pendingInject)
        {
            _discardNextText = false; // отменённая сессия кончилась пустотой — вычищать нечего
            return;
        }
        _pendingInject = false;
        CancelInjectTimer();
        if (_ownerId == LocalOwner)
            StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("overlay.empty"));
        Log.Warn(Lang.T("server.empty_result"));
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
        if (!_pendingInject)
        {
            // Поздний текст ОТМЕНЁННОГО распознавания: не вставляем, но вычищаем композер,
            // чтобы он не примешался к следующей диктовке. Прочие чужие тексты — игнор.
            if (_discardNextText)
            {
                _discardNextText = false;
                _server.SendToFirefox(new WsMessage { Type = "clear" });
                Log.Info(Lang.T("server.discard_late_text"));
            }
            return;
        }
        _pendingInject = false;
        CancelInjectTimer();

        if (_ownerId == LocalOwner)
        {
            SharedState.TargetHwnd = _injectTarget;
            bool ok = Injector.Inject(_keepClipboard);
            if (ok) StatusOverlay.Set(StatusOverlay.Phase.Done);
            else StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("overlay.inject_failed"));
        }
        else
        {
            bool ok = _server.SendToController(_ownerId, new WsMessage { Type = "inject", Payload = SharedState.LastText });
            if (ok)
                Log.Ok(Lang.T("server.text_sent", SharedState.LastText.Length, _ownerId));
            else
                Log.Warn(Lang.T("server.client_gone", _ownerId));
        }

        // Чистим композер ChatGPT, чтобы следующая диктовка началась с пустого поля.
        _server.SendToFirefox(new WsMessage { Type = "clear" });
    }

    private static string OwnerLabel(int id) => id == LocalOwner ? Lang.T("owner.local") : Lang.T("owner.remote", id);

    private static void Banner()
    {
        Log.Info(Lang.T("server.banner"));
        Log.Info(Lang.T("server.banner.listen", Config.WsPrefix));
        Log.Info(Lang.T("server.banner.lang", Lang.Current));
        Log.Info(Lang.T("server.banner.hotkey1"));
        Log.Info(Lang.T("server.banner.hotkey2"));
        Log.Info(Lang.T("server.banner.net"));
        Log.Info(Lang.T("server.banner.exit"));
    }
}
