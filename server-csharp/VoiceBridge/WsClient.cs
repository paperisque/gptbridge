using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceBridge;

/// <summary>
/// WebSocket-КЛИЕНТ для режима сетевого контроллера (VoiceBridge --connect &lt;host&gt;).
/// Подключается к серверу-хабу с авто-реконнектом, представляется ролью "controller",
/// шлёт start/stop (по локальному хоткею) и принимает inject (текст диктовки).
/// Контракт — docs/PROTOCOL.md. Зеркало серверного WsServer + background.js расширения.
/// </summary>
internal sealed class WsClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;

    /// <summary>Вызывается (на потоке WS) при приходе текста диктовки от сервера.</summary>
    public Action<string>? InjectReceived { get; set; }

    /// <summary>Вызывается (на потоке WS) при смене состояния связи (true — подключён).</summary>
    public Action<bool>? ConnectionChanged { get; set; }

    /// <summary>Цикл подключения с экспоненциальным реконнектом до отмены.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int delay = Config.ReconnectMinMs;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                Log.Info(Lang.T("wsc.connecting", Config.WsClientUrl));
                await socket.ConnectAsync(new Uri(Config.WsClientUrl), ct);

                _socket = socket;
                delay = Config.ReconnectMinMs;
                ConnectionChanged?.Invoke(true);
                Log.Ok(Lang.T("wsc.connected", Config.WsClientUrl));

                await SendAsync(new WsMessage { Type = "hello", Payload = "controller" }, ct);
                await ReceiveLoop(socket, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn(Lang.T("wsc.no_conn", ex.Message));
            }
            finally
            {
                _socket = null;
                ConnectionChanged?.Invoke(false);
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, Config.ReconnectMaxMs);
        }
    }

    /// <summary>Отправка серверу (fire-and-forget) — безопасно из потока цикла сообщений.</summary>
    public void Send(WsMessage msg) => _ = SendAsync(msg, CancellationToken.None);

    private async Task SendAsync(WsMessage msg, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            Log.Warn(Lang.T("wsc.send_no_conn", msg.Type));
            return;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOpts);

        // WebSocket запрещает параллельные SendAsync — сериализуем отправку.
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex) { Log.Warn(Lang.T("ws.send_fail", msg.Type, ex.Message)); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            OnMessage(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        }
    }

    private void OnMessage(string json)
    {
        WsMessage? msg;
        try { msg = JsonSerializer.Deserialize<WsMessage>(json, JsonOpts); }
        catch (JsonException)
        {
            Log.Warn(Lang.T("wsc.bad_json", Preview(json)));
            return;
        }

        if (msg is null) return;

        switch (msg.Type)
        {
            case "inject":
                Log.Info(Lang.T("wsc.inject", msg.Payload.Length));
                InjectReceived?.Invoke(msg.Payload);
                break;

            case "hello":
                Log.Info(Lang.T("wsc.hello", Preview(msg.Payload)));
                break;

            // ack/mic/stop/clear контроллеру не адресованы — молча игнорируем.
            default:
                break;
        }
    }

    private static string Preview(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", "\\n");
        return s.Length <= 80 ? s : s[..80] + "…";
    }
}
