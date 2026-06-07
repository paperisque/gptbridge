using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceBridge;

/// <summary>
/// WebSocket-сервер на localhost поверх HttpListener (http.sys).
/// Принимает текст от расширения, отвечает ack/hello. Двунаправленный.
/// Контракт сообщений — docs/PROTOCOL.md.
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

    /// <summary>Вызывается (на потоке WS) после сохранения пришедшего текста.</summary>
    public Action? TextReceived { get; set; }

    /// <summary>Вызывается (на потоке WS), когда расширение сообщило «запись пошла».</summary>
    public Action? RecordingStarted { get; set; }

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
            Log.Error($"Не удалось открыть {Config.WsPrefix}: {ex.Message}");
            Log.Error("Если это отказ доступа — зарезервируй URL (одноразово, в админ-консоли):");
            Log.Error($"  netsh http add urlacl url={Config.WsPrefix} user={Environment.UserDomainName}\\{Environment.UserName}");
            return;
        }

        Log.Ok($"WS-сервер слушает {Config.WsPrefix} — жду подключения расширения...");

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
    /// Разослать сообщение всем подключённым клиентам (fire-and-forget).
    /// Безопасно вызывать из потока цикла сообщений — отправка уходит в фон.
    /// </summary>
    public void Broadcast(WsMessage msg)
    {
        ClientConnection[] snapshot;
        lock (_clientsLock) snapshot = _clients.ToArray();

        if (snapshot.Length == 0)
        {
            Log.Warn($"Некому слать '{msg.Type}': расширение не подключено.");
            return;
        }

        foreach (var client in snapshot)
            _ = SendAsync(client, msg, CancellationToken.None);
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
            Log.Error($"Ошибка WS-рукопожатия: {ex.Message}");
            return;
        }

        var client = new ClientConnection(socket);
        lock (_clientsLock) _clients.Add(client);
        Log.Ok($"Расширение подключилось ({ctx.Request.RemoteEndPoint}).");

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
        catch (WebSocketException ex) { Log.Warn($"WS-соединение разорвано: {ex.Message}"); }
        catch (Exception ex) { Log.Error($"Ошибка чтения WS: {ex.Message}"); }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            client.Dispose();
            Log.Info("Расширение отключилось.");
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
            Log.Warn($"Некорректный JSON: {Preview(json)}");
            return;
        }

        if (msg is null) return;

        switch (msg.Type)
        {
            case "text":
                SharedState.LastText = msg.Payload;
                Log.Info($"Текст получен ({msg.Payload.Length} симв.): {Preview(msg.Payload)}");
                await SendAsync(client, new WsMessage { Type = "ack", Payload = msg.Payload.Length.ToString() }, ct);
                TextReceived?.Invoke();
                break;

            case "recording":
                Log.Info("Расширение: запись началась — возвращаю фокус в рабочее окно.");
                RecordingStarted?.Invoke();
                break;

            case "hello":
                Log.Info($"Расширение представилось: {Preview(msg.Payload)}");
                break;

            default:
                Log.Warn($"Неизвестный тип сообщения: {msg.Type}");
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
        catch (Exception ex) { Log.Warn($"Не удалось отправить '{msg.Type}': {ex.Message}"); }
        finally { client.SendLock.Release(); }
    }

    private static string Preview(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", "\\n");
        return s.Length <= 80 ? s : s[..80] + "…";
    }
}

/// <summary>Одно WS-соединение + его лок на отправку.</summary>
internal sealed class ClientConnection : IDisposable
{
    public WebSocket Socket { get; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    public ClientConnection(WebSocket socket) => Socket = socket;

    public void Dispose()
    {
        try { Socket.Dispose(); } catch { /* ignore */ }
        SendLock.Dispose();
    }
}
