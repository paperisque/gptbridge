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
    private enum DictState { Idle, Recording }

    private const int LocalOwner = 0; // владелец-локальный контроллер (Id сетевых клиентов начинаются с 1)

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
        };
        _ = _server.RunAsync(cts.Token);

        // 2. Хук клавиатуры на ЭТОМ потоке (он же крутит GetMessage) — локальный контроллер.
        _hook = new KeyboardHook(_mainThreadId);
        _hook.Install();

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
            BeginRecording(LocalOwner);
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
            BeginRecording(id);
        else
            Log.Warn($"Старт сетевого клиента #{id} проигнорирован — занято (владелец {OwnerLabel(_ownerId)}).");
    }

    /// <summary>Сетевой клиент просит СТОП. На потоке цикла сообщений.</summary>
    private static void HandleCtrlStop(int id)
    {
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
    /// Общий старт записи для любого владельца. Firefox не начинает захват
    /// микрофона, пока его окно в фоне — выносим окно FF вперёд, потом шлём «микрофон».
    /// Фокус на машине-сервере вернём по сигналу «recording».
    /// </summary>
    private static void BeginRecording(int ownerId)
    {
        _ownerId = ownerId;
        _pendingInject = false; // на случай незавершённой прошлой сессии
        _returnWindow = Native.GetForegroundWindow();
        _state = DictState.Recording;

        IntPtr ff = WindowFinder.FindFirefoxChatGpt();
        if (ff != IntPtr.Zero)
        {
            _awaitingRecording = true;
            Injector.ForceForeground(ff);
            _server.SendToFirefox(new WsMessage { Type = "mic" });
            ArmFocusBackTimer();
            Log.Ok($"▶ Старт диктовки (владелец {OwnerLabel(ownerId)}): поднял Firefox (0x{ff.ToInt64():X}), "
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
        CancelFocusBack();
        if (!_awaitingRecording) return; // уже вернули / не ждём
        _awaitingRecording = false;

        if (_returnWindow != IntPtr.Zero && Native.IsWindow(_returnWindow))
        {
            Injector.ForceForeground(_returnWindow);
            Log.Ok($"Фокус возвращён в «{Native.GetWindowTitle(_returnWindow)}».");
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
