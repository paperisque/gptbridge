using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceBridge;

/// <summary>Роль WS-соединения. Сервер — хаб: один движок диктовки (Firefox) и
/// сколько угодно контроллеров (локальный — в самом сервере, сетевые — по WS).</summary>
internal enum ConnectionRole { Unknown, Firefox, Controller }

/// <summary>
/// WebSocket-сервер поверх HttpListener (http.sys). Хаб между Firefox-расширением
/// (движок диктовки) и сетевыми клиентами-контроллерами. Двунаправленный.
/// Контракт сообщений и роли — docs/PROTOCOL.md.
///
/// mic/stop/clear шлём адресно расширению (SendToFirefox), текст диктовки —
/// конкретному контроллеру (SendToController) по его Id. Локальный контроллер
/// живёт в ServerApp и по WS не ходит — он «владелец Id=0».
/// </summary>
internal sealed class WsServer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<ClientConnection> _clients = new();
    private readonly object _clientsLock = new();
    private int _nextId; // Id соединений; Interlocked, начинается с 1 (0 зарезервирован под локальный контроллер)

    /// <summary>Вызывается (на потоке WS) после сохранения пришедшего текста.</summary>
    public Action? TextReceived { get; set; }

    /// <summary>Вызывается (на потоке WS), когда расширение сообщило «запись пошла».</summary>
    public Action? RecordingStarted { get; set; }

    /// <summary>Вызывается (на потоке WS), когда сетевой клиент (его Id) просит СТАРТ диктовки.</summary>
    public Action<int>? ControllerStartRequested { get; set; }

    /// <summary>Вызывается (на потоке WS), когда сетевой клиент (его Id) просит СТОП диктовки.</summary>
    public Action<int>? ControllerStopRequested { get; set; }

    /// <summary>Вызывается (на потоке WS), когда расширение Firefox представилось (hello, роль Firefox).</summary>
    public Action? FirefoxConnected { get; set; }

    /// <summary>Вызывается (на потоке WS), когда расширение сообщило «таб ChatGPT готов к диктовке» (ready).</summary>
    public Action? ReadyReceived { get; set; }

    /// <summary>Вызывается (на потоке WS), когда расширение сообщило «распознано пусто» (empty — в записи была тишина).</summary>
    public Action? EmptyReceived { get; set; }

    /// <summary>Есть ли подключённое расширение Firefox (движок диктовки на связи).</summary>
    public bool HasFirefox
    {
        get { lock (_clientsLock) return _clients.Any(c => c.Role == ConnectionRole.Firefox); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(Config.WsPrefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log.Error(Lang.T("ws.listen_fail", Config.WsPrefix, ex.Message));
            Log.Error(Lang.T("ws.listen_fail_hint"));
            Log.Error($"  netsh http add urlacl url={Config.WsPrefix} user={Environment.UserDomainName}\\{Environment.UserName}");
            return;
        }

        Log.Ok(Lang.T("ws.listening", Config.WsPrefix));

        // Корректно прерываем GetContextAsync по отмене.
        using (ct.Register(listener.Stop))
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (HttpListenerException) { break; }   // listener.Stop()
                catch (ObjectDisposedException) { break; }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 426; // Upgrade Required
                    ctx.Response.Close();
                    continue;
                }

                _ = HandleClientAsync(ctx, ct);
            }
        }

        listener.Close();
    }

    /// <summary>
    /// Команда движку диктовки (mic/stop/clear) — всем соединениям, кроме контроллеров
    /// (т.е. Firefox-расширению; ещё не представившиеся Unknown тоже получают — FF мог
    /// не успеть прислать hello). Fire-and-forget; безопасно из потока цикла сообщений.
    /// </summary>
    public void SendToFirefox(WsMessage msg)
    {
        ClientConnection[] snapshot;
        lock (_clientsLock)
            snapshot = _clients.Where(c => c.Role != ConnectionRole.Controller).ToArray();

        if (snapshot.Length == 0)
        {
            Log.Warn(Lang.T("ws.no_firefox", msg.Type));
            return;
        }

        foreach (var client in snapshot)
            _ = SendAsync(client, msg, CancellationToken.None);
    }

    /// <summary>
    /// Текст диктовки конкретному сетевому клиенту по его Id (fire-and-forget).
    /// Возвращает false, если такого соединения уже нет (клиент отключился).
    /// </summary>
    public bool SendToController(int id, WsMessage msg)
    {
        ClientConnection? client;
        lock (_clientsLock) client = _clients.FirstOrDefault(c => c.Id == id);
        if (client is null) return false;

        _ = SendAsync(client, msg, CancellationToken.None);
        return true;
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket socket;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            socket = wsCtx.WebSocket;
        }
        catch (Exception ex)
        {
            Log.Error(Lang.T("ws.handshake_fail", ex.Message));
            return;
        }

        int id = Interlocked.Increment(ref _nextId);
        var client = new ClientConnection(socket, id);
        lock (_clientsLock) _clients.Add(client);
        Log.Ok(Lang.T("ws.client_connected", id, ctx.Request.RemoteEndPoint));

        await SendAsync(client, new WsMessage { Type = "hello", Payload = "VoiceBridge ready" }, ct);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        try
        {
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

                string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                await OnMessageAsync(client, json, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log.Warn(Lang.T("ws.conn_broken", client.Id, ex.Message)); }
        catch (Exception ex) { Log.Error(Lang.T("ws.read_error", client.Id, ex.Message)); }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            client.Dispose();
            Log.Info(Lang.T("ws.client_disconnected", client.Id, client.Role));
        }
    }

    private async Task OnMessageAsync(ClientConnection client, string json, CancellationToken ct)
    {
        WsMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<WsMessage>(json, JsonOpts);
        }
        catch (JsonException)
        {
            Log.Warn(Lang.T("ws.bad_json", Preview(json)));
            return;
        }

        if (msg is null) return;

        switch (msg.Type)
        {
            case "text":
                SharedState.LastText = msg.Payload;
                Log.Info(Lang.T("ws.text_received", msg.Payload.Length, Preview(msg.Payload)));
                await SendAsync(client, new WsMessage { Type = "ack", Payload = msg.Payload.Length.ToString() }, ct);
                TextReceived?.Invoke();
                break;

            case "recording":
                Log.Info(Lang.T("ws.recording"));
                RecordingStarted?.Invoke();
                break;

            case "ready":
                // Расширение: таб ChatGPT загружен, кнопка «Start dictation» доступна.
                Log.Info(Lang.T("ws.ready"));
                ReadyReceived?.Invoke();
                break;

            case "empty":
                // Расширение: распознавание дало пустоту (тишина) — текста не будет.
                Log.Info(Lang.T("ws.empty"));
                EmptyReceived?.Invoke();
                break;

            case "hello":
                // Роль по payload: "controller" → сетевой клиент, иначе движок диктовки (Firefox).
                client.Role = msg.Payload.Contains("controller", StringComparison.OrdinalIgnoreCase)
                    ? ConnectionRole.Controller
                    : ConnectionRole.Firefox;
                Log.Info(Lang.T("ws.hello", client.Id, client.Role, Preview(msg.Payload)));
                if (client.Role == ConnectionRole.Firefox) FirefoxConnected?.Invoke();
                break;

            case "start":
                // Сетевой клиент просит старт диктовки. Тяжёлую работу делает ServerApp на своём потоке.
                Log.Info(Lang.T("ws.ctrl_start", client.Id));
                ControllerStartRequested?.Invoke(client.Id);
                break;

            case "stop":
                Log.Info(Lang.T("ws.ctrl_stop", client.Id));
                ControllerStopRequested?.Invoke(client.Id);
                break;

            default:
                Log.Warn(Lang.T("ws.unknown_type", msg.Type));
                break;
        }
    }

    private static async Task SendAsync(ClientConnection client, WsMessage msg, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOpts);

        // WebSocket запрещает параллельные SendAsync — сериализуем отправку.
        await client.SendLock.WaitAsync(ct);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex) { Log.Warn(Lang.T("ws.send_fail", msg.Type, ex.Message)); }
        finally { client.SendLock.Release(); }
    }

    private static string Preview(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", "\\n");
        return s.Length <= 80 ? s : s[..80] + "…";
    }
}

/// <summary>Одно WS-соединение: Id, роль (Firefox/контроллер) и лок на отправку.</summary>
internal sealed class ClientConnection : IDisposable
{
    public WebSocket Socket { get; }
    public int Id { get; }
    public ConnectionRole Role { get; set; } = ConnectionRole.Unknown;
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    public ClientConnection(WebSocket socket, int id)
    {
        Socket = socket;
        Id = id;
    }

    public void Dispose()
    {
        try { Socket.Dispose(); } catch { /* ignore */ }
        SendLock.Dispose();
    }
}
