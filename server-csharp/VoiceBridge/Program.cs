namespace VoiceBridge;

/// <summary>
/// Точка входа. На главном потоке крутится цикл сообщений Win32 (нужен для
/// низкоуровневого хука клавиатуры); WS-сервер живёт в фоне.
///
/// Модель управления (как у WhisperFlow):
///   Ctrl+Win        — тоггл диктовки: 1-е нажатие старт (микрофон),
///                     2-е нажатие стоп (птичка) → текст → авто-вставка.
///   Ctrl+Win+Y      — то же завершение, но текст дополнительно остаётся в буфере.
///
/// Цель вставки — окно, активное в момент СТОПа (универсально, не только CC).
/// Кражи фокуса нет: ты и так в нужном окне. Хук и инъекция работают на одном
/// потоке (у него есть очередь ввода — это важно для AttachThreadInput-фолбэка).
/// </summary>
internal static class Program
{
    private enum DictState { Idle, Recording }

    private static uint _mainThreadId;
    private static WsServer _server = null!;
    private static KeyboardHook _hook = null!;

    private static DictState _state = DictState.Idle;
    private static bool _pendingInject;   // вооружены ли на вставку следующего текста
    private static bool _keepClipboard;   // вариант Ctrl+Win+Y
    private static IntPtr _injectTarget;  // окно, захваченное в момент СТОПа

    private static IntPtr _returnWindow;  // окно, активное до старта — вернуть фокус сюда
    private static bool _awaitingRecording; // ждём сигнал «запись пошла», чтобы вернуть фокус
    private static Timer? _focusBackTimer;  // страховка: вернуть фокус, даже если сигнала нет

    [STAThread]
    private static void Main()
    {
        Console.Title = "VoiceBridge";
        // Консоль Windows по умолчанию не UTF-8 -> кириллица превращается в '?'.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* перенаправленный вывод */ }
        Banner();

        // 1. WS-сервер — в фоне. По приходу текста дёрнет OnTextReceived.
        var cts = new CancellationTokenSource();
        _server = new WsServer { TextReceived = OnTextReceived, RecordingStarted = OnRecordingStarted };
        _ = _server.RunAsync(cts.Token);

        // 2. Хук клавиатуры на ЭТОМ потоке (он же крутит GetMessage).
        _mainThreadId = Native.GetCurrentThreadId();
        _hook = new KeyboardHook(_mainThreadId);
        _hook.Install();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // не убиваем процесс мгновенно — даём корректно выйти
            Native.PostThreadMessage(_mainThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        // 3. Цикл сообщений: дёргает колбэк хука и ловит наши WM_APP_*.
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
            }

            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        // 4. Завершение.
        _hook.Dispose();
        cts.Cancel();
        Log.Info("VoiceBridge остановлен.");
    }

    /// <summary>Нажатие Ctrl+Win (или +Y). Выполняется на потоке цикла сообщений.</summary>
    private static void HandleToggle(bool withY)
    {
        if (_state == DictState.Idle)
        {
            // Окно, в котором ты сейчас работаешь — сюда вернём фокус после старта записи.
            _returnWindow = Native.GetForegroundWindow();
            _state = DictState.Recording;

            // Firefox не начинает захват микрофона, пока его окно в фоне. Выносим
            // окно FF на передний план, потом шлём «микрофон» — клик ляжет уже на
            // активное окно, и запись стартует. Фокус вернём по сигналу «recording».
            IntPtr ff = WindowFinder.FindFirefoxChatGpt();
            if (ff != IntPtr.Zero)
            {
                _awaitingRecording = true;
                Injector.ForceForeground(ff);
                _server.Broadcast(new WsMessage { Type = "mic" });
                ArmFocusBackTimer();
                Log.Ok($"▶ Старт диктовки: поднял Firefox (0x{ff.ToInt64():X}), жду старта записи, верну фокус в «{Native.GetWindowTitle(_returnWindow)}».");
            }
            else
            {
                // FF не найден — шлём как раньше; запись может не начаться в фоне.
                _server.Broadcast(new WsMessage { Type = "mic" });
                Log.Warn("▶ Старт диктовки: окно Firefox с ChatGPT не найдено — фокус не поднимал (запись может не стартовать в фоне).");
            }
        }
        else
        {
            CancelFocusBack(); // на всякий: запись завершают — фокус-возврат больше не нужен
            _injectTarget = Native.GetForegroundWindow();
            _keepClipboard = withY;
            _pendingInject = true;
            _server.Broadcast(new WsMessage { Type = "stop" });
            _state = DictState.Idle;
            Log.Ok($"■ Стоп диктовки (команда «птичка»). Цель=0x{_injectTarget.ToInt64():X} «{Native.GetWindowTitle(_injectTarget)}»"
                   + (withY ? " — текст останется в буфере." : "."));
        }
    }

    /// <summary>Дёргается из потока WS, когда расширение сообщило «запись пошла».</summary>
    private static void OnRecordingStarted()
    {
        Native.PostThreadMessage(_mainThreadId, Native.WM_APP_FOCUS_BACK, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Запись началась (или сработала страховка по таймеру) — возвращаем фокус
    /// в окно, где работал пользователь. Выполняется на потоке цикла сообщений.
    /// </summary>
    private static void HandleFocusBack()
    {
        CancelFocusBack();
        if (!_awaitingRecording) return; // уже вернули / не ждём
        _awaitingRecording = false;

        if (_returnWindow != IntPtr.Zero && Native.IsWindow(_returnWindow))
        {
            Injector.ForceForeground(_returnWindow);
            Log.Ok($"Фокус возвращён в «{Native.GetWindowTitle(_returnWindow)}» — диктуй, не переключаясь в браузер.");
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

    /// <summary>Текст пришёл. Вставляем, только если вооружены завершением диктовки.</summary>
    private static void HandleInject()
    {
        if (!_pendingInject) return; // не наш текст (не было СТОПа) — игнор
        _pendingInject = false;

        SharedState.TargetHwnd = _injectTarget;
        Injector.Inject(_keepClipboard);

        // Чистим композер ChatGPT, чтобы следующая диктовка началась с пустого поля.
        _server.Broadcast(new WsMessage { Type = "clear" });
    }

    private static void Banner()
    {
        Log.Info("=== VoiceBridge — голосовой мост ChatGPT → активное окно ===");
        Log.Info("Ctrl+Win        — старт диктовки; повторно — стоп + вставка в активное окно.");
        Log.Info("Ctrl+Win+Y      — стоп + вставка + текст дополнительно остаётся в буфере обмена.");
        Log.Info("Ctrl+C          — выход.");
    }
}
