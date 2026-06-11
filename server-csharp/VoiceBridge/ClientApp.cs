namespace VoiceBridge;

/// <summary>
/// Режим СЕТЕВОГО КЛИЕНТА (VoiceBridge --connect &lt;host&gt;). Тонкий контроллер на
/// другой машине: ловит локальный Ctrl+Win, по СТОПу захватывает СВОЁ активное окно
/// и вставляет туда текст, пришедший от сервера. Firefox здесь нет — движок диктовки
/// (и подъём окна FF) живёт на машине-сервере; клиент лишь шлёт start/stop и
/// получает обратно текст (inject).
///
/// Переиспользует весь Win32-код сервера: KeyboardHook, Injector, Clipboard, Native.
/// На главном потоке — цикл сообщений Win32 (нужен хуку и для AttachThreadInput).
///
///   Ctrl+Win    — старт диктовки (запрос серверу); повторно — стоп + вставка.
///   Ctrl+Win+Y  — то же завершение, но текст дополнительно остаётся в буфере.
/// </summary>
internal static class ClientApp
{
    private enum DictState { Idle, Recording }

    private static uint _mainThreadId;
    private static WsClient _client = null!;
    private static KeyboardHook _hook = null!;

    private static DictState _state = DictState.Idle;
    private static bool _pendingInject;  // ждём текст от сервера для вставки
    private static bool _keepClipboard;  // вариант Ctrl+Win+Y
    private static IntPtr _injectTarget;  // СВОЁ окно, захваченное в момент СТОПа

    public static void Run()
    {
        Banner();

        var cts = new CancellationTokenSource();
        _mainThreadId = Native.GetCurrentThreadId();

        // 1. WS-клиент к серверу — в фоне.
        _client = new WsClient
        {
            InjectReceived = OnInjectReceived,
            ConnectionChanged = connected =>
                Log.Info(Lang.T(connected ? "client.connected" : "client.disconnected")),
        };
        _ = _client.RunAsync(cts.Token);

        // 2. Хук клавиатуры на этом потоке (он же крутит GetMessage).
        _hook = new KeyboardHook(_mainThreadId);
        _hook.Install();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Native.PostThreadMessage(_mainThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        // 3. Цикл сообщений: тоггл от хука и приход текста от сервера.
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
            }

            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        _hook.Dispose();
        cts.Cancel();
        Log.Info(Lang.T("client.stopped"));
    }

    /// <summary>Локальный хоткей Ctrl+Win (или +Y). На потоке цикла сообщений.</summary>
    private static void HandleToggle(bool withY)
    {
        if (_state == DictState.Idle)
        {
            // Старт: фокус НЕ трогаем (FF удалённый, его поднимает сервер). Просто диктуй.
            _pendingInject = false;
            _state = DictState.Recording;
            _client.Send(new WsMessage { Type = "start" });
            Log.Ok(Lang.T("client.start"));
        }
        else
        {
            // Стоп: цель вставки = окно, активное сейчас на ЭТОЙ машине.
            _injectTarget = Native.GetForegroundWindow();
            _keepClipboard = withY;
            _pendingInject = true;
            _client.Send(new WsMessage { Type = "stop" });
            _state = DictState.Idle;
            Log.Ok(Lang.T("client.stop", _injectTarget.ToInt64(), Native.GetWindowTitle(_injectTarget))
                   + (withY ? Lang.T("suffix.keep_buffer") : "."));
        }
    }

    /// <summary>Дёргается из потока WS при приходе текста — перебрасываем в наш поток.</summary>
    private static void OnInjectReceived(string text)
    {
        SharedState.LastText = text;
        Native.PostThreadMessage(_mainThreadId, Native.WM_APP_INJECT, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Текст пришёл от сервера. Вставляем, только если вооружены завершением.</summary>
    private static void HandleInject()
    {
        if (!_pendingInject) return; // не наш текст (не было СТОПа) — игнор
        _pendingInject = false;

        SharedState.TargetHwnd = _injectTarget;
        Injector.Inject(_keepClipboard);
    }

    private static void Banner()
    {
        Log.Info(Lang.T("client.banner"));
        Log.Info(Lang.T("client.banner.server", Config.WsClientUrl));
        Log.Info(Lang.T("client.banner.hotkey1"));
        Log.Info(Lang.T("client.banner.hotkey2"));
        Log.Info(Lang.T("client.banner.exit"));
    }
}
