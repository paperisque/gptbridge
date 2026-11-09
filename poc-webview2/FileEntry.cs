using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WebView2Poc;

/// <summary>Задание «распознай файл» от куклы: звук, кто прислал, как ответить.</summary>
internal sealed class FileJob
{
    public string Id = "";          // наш номер (в ответ и в журнал)
    public string RequestId = "";   // X-Request-Id клиента
    public string Client = "";      // X-Client
    public string InboxPath = "";   // копия присланного тела в data\inbox (удаляется после успеха)
    public byte[] Wav = Array.Empty<byte>();
    public double AudioSec;
    public readonly CancellationTokenSource Cts = new();
    public string CancelReason = "";   // preempted | disconnected
    public readonly TaskCompletionSource<FileResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Cancel(string reason)
    {
        if (CancelReason == "") CancelReason = reason;
        Cts.Cancel();
    }
}

/// <summary>Итог задания: HTTP-код и поля ответа по контракту (ошибка или текст).</summary>
internal sealed record FileResult(int Status, string? Error, string? Message,
    string? Text = null, int RecognizeMs = 0, int? RetryAfterSec = null);

/// <summary>Снимок состояния граббера для /status и для замка «занято».</summary>
internal sealed record FileStatus(bool Ready, string State, string Reason, int? RetryAfterSec);

/// <summary>
/// Вход «файл → текст» для кукол: HTTP, по умолчанию вся сеть на порту 17891 (контракт с «Голос сессии 1»
/// 08.11.2026; сеть открыта решением пользователя 09.11.2026). Адрес и порт - флаги --api-host / --api-port.
/// GET /status - снимок состояния, POST /transcribe - WAV (телом или путём в JSON) → текст, синхронно.
/// Путь в JSON принимается только с этого компьютера (адрес отправителя - петля или собственный адрес машины);
/// с чужой машины - только WAV в теле. Каждое присланное тело ложится копией в data\inbox\<id>.wav,
/// после успешного распознавания копия удаляется (неудачные остаются для разбора).
///
/// Зачем свой разбор HTTP поверх TcpListener, а не HttpListener: клиент может оборвать соединение посреди
/// /transcribe, и граббер обязан тут же прекратить подачу и снять замок (уточнение клиента 09.11.2026).
/// HttpListener (http.sys) обрыв не сообщает, пока мы не пишем ответ; у голого сокета обрыв виден
/// опросом (Poll + Available == 0). Протокол здесь крошечный: два пути, тело по Content-Length,
/// ответ с Connection: close, так что разбор умещается в сотню строк.
///
/// Защита от веб-страниц: запрос с заголовком Origin → 403 без тела; Host - только IP-адрес, localhost
/// или имя этого компьютера с нашим портом, иначе 403 (чужое доменное имя = подмена DNS). Ключа нет.
/// Вся работа со страницей идёт на UI-потоке формы: сюда она приходит через два делегата.
/// </summary>
internal sealed class FileEntry : IDisposable
{
    public const int DefaultPort = 17891;
    public const int MaxAudioSec = 360;
    public static readonly string InboxDir = Path.Combine(Diag.Dir, "inbox");
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 160 * 1024 * 1024;   // 360 с стерео 48 кГц 16 бит ≈ 69 МБ, запас вдвое

    private readonly Func<FileStatus> _status;      // снимок состояния (внутри сам маршалит на UI-поток)
    private readonly Action<FileJob> _submit;       // поставить задание на UI-поток
    private readonly string _version;
    private readonly IPAddress _bind;               // Any/IPv6Any = вся сеть, Loopback = только этот компьютер
    public readonly int Port;
    private readonly List<TcpListener> _listeners = new();
    private int _seq;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // кириллица в ответе как есть, не \uXXXX
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public FileEntry(Func<FileStatus> status, Action<FileJob> submit, string version, IPAddress bind, int port)
    {
        _status = status;
        _submit = submit;
        _version = version;
        _bind = bind;
        Port = port;
    }

    /// <summary>Слушает ли вход всю сеть (а не один адрес).</summary>
    public bool AllNetworks => _bind.Equals(IPAddress.Any) || _bind.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// Разобрать флаги --api-host / --api-port. Нет флага → вся сеть, порт 17891.
    /// Хост: 0.0.0.0, ::, * или + = вся сеть; localhost = петля; иначе IP-адрес. Ошибка - текст для журнала.
    /// </summary>
    public static string? ParseBind(string? host, string? port, out IPAddress bind, out int portNum)
    {
        bind = IPAddress.Any;
        portNum = DefaultPort;
        if (port != null && (!int.TryParse(port, out portNum) || portNum < 1 || portNum > 65535))
            return "--api-port: нужен номер порта 1..65535, а не «" + port + "»";
        if (host == null || host is "*" or "+") return null;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { bind = IPAddress.Loopback; return null; }
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return "--api-host: нужен IP-адрес, 0.0.0.0 или localhost, а не «" + host + "»";
        bind = ip;
        return null;
    }

    /// <summary>
    /// Поднять слушатели. Вся сеть - один сокет IPv6 в двойном режиме (принимает и IPv4), без IPv6 - IPv4.
    /// Петля - IPv4 и IPv6 (localhost у клиента может разрешиться в ::1). Иначе - один заданный адрес.
    /// </summary>
    public void Start()
    {
        if (AllNetworks)
        {
            try
            {
                var l = new TcpListener(IPAddress.IPv6Any, Port);
                l.Server.DualMode = true;
                Listen(l);
            }
            catch (Exception ex)
            {
                Diag.Write("вход: IPv6 недоступен (" + ex.Message + "), слушаю только IPv4");
                Listen(new TcpListener(IPAddress.Any, Port));
            }
        }
        else if (IPAddress.IsLoopback(_bind))
        {
            Listen(new TcpListener(IPAddress.Loopback, Port));
            try { Listen(new TcpListener(IPAddress.IPv6Loopback, Port)); } catch (Exception ex) { Diag.Write("вход: IPv6-петля не поднялась: " + ex.Message); }
        }
        else Listen(new TcpListener(_bind, Port));
    }

    private void Listen(TcpListener l)
    {
        l.Start();
        _listeners.Add(l);
        _ = AcceptLoopAsync(l);
    }

    /// <summary>IPv4-адреса этого компьютера в сети (для строки журнала: куда стучаться с других машин).</summary>
    public static string LocalAddresses()
    {
        try
        {
            var list = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                         && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
                .Select(a => a.ToString())
                .Distinct();
            return string.Join(", ", list);
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
        _listeners.Clear();
    }

    private async Task AcceptLoopAsync(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); }
            catch { return; }   // слушатель остановлен
            _ = HandleAsync(c);
        }
    }

    // ------------------------------ Разбор запроса ------------------------------

    private sealed class Request
    {
        public string Method = "", Path = "";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
        public string H(string name) => Headers.TryGetValue(name, out var v) ? v : "";
    }

    /// <summary>Прочитать заголовки и тело по Content-Length. null - запрос не разобрать (клиенту 400).</summary>
    private static async Task<Request?> ReadRequestAsync(NetworkStream ns)
    {
        var buf = new byte[8192];
        var acc = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await ns.ReadAsync(buf);
            if (n <= 0) return null;
            acc.Write(buf, 0, n);
            if (acc.Length > MaxHeaderBytes) return null;
            headerEnd = IndexOfCrLfCrLf(acc.GetBuffer(), (int)acc.Length);
        }
        string head = Encoding.Latin1.GetString(acc.GetBuffer(), 0, headerEnd);
        var lines = head.Split("\r\n");
        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return null;
        var r = new Request { Method = reqLine[0].ToUpperInvariant(), Path = reqLine[1] };
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            r.Headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        int bodyStart = headerEnd + 4;
        int have = (int)acc.Length - bodyStart;
        long len = 0;
        if (long.TryParse(r.H("Content-Length"), out var cl)) len = cl;
        if (len < 0 || len > MaxBodyBytes) return null;
        if (r.H("Transfer-Encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase)) return null; // только Content-Length
        r.Body = new byte[len];
        Array.Copy(acc.GetBuffer(), bodyStart, r.Body, 0, Math.Min(have, (int)len));
        int got = Math.Min(have, (int)len);
        while (got < len)
        {
            int n = await ns.ReadAsync(r.Body.AsMemory(got, (int)len - got));
            if (n <= 0) return null;
            got += n;
        }
        return r;
    }

    private static int IndexOfCrLfCrLf(byte[] b, int len)
    {
        for (int i = 0; i + 3 < len; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    // ------------------------------ Обслуживание ------------------------------

    private async Task HandleAsync(TcpClient c)
    {
        using var _ = c;
        try
        {
            c.NoDelay = true;
            var ns = c.GetStream();
            var req = await ReadRequestAsync(ns);
            if (req is null) { await WriteAsync(ns, 400, Json(new { error = "bad_request", message = "запрос не разобран" })); return; }

            // Защита от веб-страниц (любой браузер шлёт Origin на cross-origin fetch) и от подмены DNS.
            if (req.Headers.ContainsKey("Origin")) { await WriteAsync(ns, 403, null); return; }
            if (!HostAllowed(req.H("Host"))) { Diag.Write("вход: отказ 403, Host «" + req.H("Host") + "»"); await WriteAsync(ns, 403, null); return; }

            if (req.Method == "GET" && req.Path == "/status")
            {
                var s = _status();
                await WriteAsync(ns, 200, Json(new { ready = s.Ready, state = s.State, reason = s.Reason, version = _version, maxAudioSec = MaxAudioSec, retryAfterSec = s.RetryAfterSec }));
                return;
            }
            if (req.Method == "POST" && req.Path == "/transcribe") { await TranscribeAsync(c, ns, req, FromThisComputer(c)); return; }
            await WriteAsync(ns, 404, Json(new { error = "not_found", message = "нет такого пути: " + req.Method + " " + req.Path }));
        }
        catch (Exception ex)
        {
            Diag.Write("вход: соединение оборвано с ошибкой: " + ex.Message);
        }
    }

    /// <summary>
    /// Host запроса: IP-адрес (любой - это и есть «реальный адрес компьютера»), localhost или имя этого
    /// компьютера, и порт ровно наш. Чужое доменное имя отбиваем: так выглядит подмена DNS, когда страница
    /// в браузере стучится сюда под своим именем (Origin при этом браузер может и не прислать).
    /// </summary>
    private bool HostAllowed(string host)
    {
        string name, port;
        if (host.StartsWith('['))   // [::1]:17891
        {
            int close = host.IndexOf(']');
            if (close < 0) return false;
            name = host[1..close];
            port = host[(close + 1)..].TrimStart(':');
        }
        else
        {
            int colon = host.LastIndexOf(':');
            if (colon < 0) return false;   // без порта = порт 80, не наш
            name = host[..colon];
            port = host[(colon + 1)..];
        }
        if (port != Port.ToString()) return false;
        if (IPAddress.TryParse(name, out _)) return true;
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        string me = Dns.GetHostName();
        if (name.Equals(me, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            string domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (domain != "" && name.Equals(me + "." + domain, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Запрос с этого компьютера: адрес отправителя - петля или собственный адрес машины (стук на свой сетевой IP).</summary>
    private static bool FromThisComputer(TcpClient c)
    {
        try
        {
            var remote = ((IPEndPoint)c.Client.RemoteEndPoint!).Address;
            var local = ((IPEndPoint)c.Client.LocalEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
            return IPAddress.IsLoopback(remote) || remote.Equals(local);
        }
        catch { return false; }
    }

    private async Task TranscribeAsync(TcpClient c, NetworkStream ns, Request req, bool local)
    {
        var job = new FileJob
        {
            Id = $"gg-{DateTime.Now:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _seq):0000}",
            RequestId = req.H("X-Request-Id"),
            Client = req.H("X-Client"),
        };
        string from = "";
        try { from = local ? "этот компьютер" : ((IPEndPoint)c.Client.RemoteEndPoint!).Address.ToString(); } catch { }
        string tag = $"вход {job.Id} ← {job.Client} {job.RequestId} [{from}]";

        async Task Fail(int status, string error, string message)
        {
            Diag.Write($"{tag}: {status} {error}: {message}");
            await WriteAsync(ns, status, Json(new { id = job.Id, requestId = job.RequestId, error, message }));
        }

        // Тело: JSON с путём (так шлёт клиент) или сам WAV.
        string ctype = req.H("Content-Type");
        byte[] wav;
        if (ctype.Contains("json", StringComparison.OrdinalIgnoreCase) || (req.Body.Length > 0 && req.Body[0] == (byte)'{'))
        {
            string path;
            try
            {
                using var doc = JsonDocument.Parse(req.Body);
                path = doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            }
            catch (Exception ex) { await Fail(400, "bad_file", "JSON не разобран: " + ex.Message); return; }
            if (path == "") { await Fail(400, "bad_file", "в JSON нет поля path"); return; }
            // Чужой путь указывает в диск граббера, а не отправителя; по сети - только сам файл.
            if (!local) { await Fail(400, "bad_file", "путь принимается только с этого компьютера; по сети присылай сам WAV в теле запроса"); return; }
            if (!File.Exists(path)) { await Fail(400, "bad_file", "файл не найден: " + path); return; }
            try { wav = await File.ReadAllBytesAsync(path); }
            catch (Exception ex) { await Fail(400, "bad_file", "файл не прочитан: " + ex.Message); return; }
            Diag.Write($"{tag}: path={path} ({wav.Length} байт)");
        }
        else
        {
            wav = req.Body;
            Diag.Write($"{tag}: тело {ctype} ({wav.Length} байт)");
        }

        string? wavErr = WavDuration(wav, out double sec);
        if (wavErr != null) { await Fail(400, "bad_file", wavErr); return; }
        if (sec > MaxAudioSec) { await Fail(413, "too_long", $"звук {Sec(sec)} с, предел {MaxAudioSec} с"); return; }
        if (sec <= 0.05) { await Fail(422, "empty", "в WAV нет звука"); return; }
        job.Wav = wav;
        job.AudioSec = sec;

        // Присланное телом - копией во входящие (по пути файл и так лежит у владельца).
        if (wav == req.Body)
        {
            try
            {
                Directory.CreateDirectory(InboxDir);
                job.InboxPath = Path.Combine(InboxDir, job.Id + ".wav");
                await File.WriteAllBytesAsync(job.InboxPath, wav);
            }
            catch (Exception ex) { job.InboxPath = ""; Diag.Write($"{tag}: копия во входящие не легла: {ex.Message}"); }
        }

        _submit(job);

        // Ждём итог с UI-потока, попутно следя за сокетом: клиент ушёл → отменяем работу, ответ не пишем.
        while (!job.Done.Task.IsCompleted)
        {
            if (Disconnected(c))
            {
                Diag.Write($"{tag}: клиент оборвал соединение, отмена");
                job.Cancel("disconnected");
                await job.Done.Task;   // дождаться уборки на странице (замок снимается там)
                return;
            }
            await Task.WhenAny(job.Done.Task, Task.Delay(250));
        }
        var r = await job.Done.Task;
        if (r.Status == 200)
        {
            Diag.Write($"{tag}: 200, {r.Text?.Length ?? 0} зн., звук {Sec(sec)} с, распознавание {r.RecognizeMs} мс");
            if (job.InboxPath != "") { try { File.Delete(job.InboxPath); } catch { } }   // успех - копия не нужна
            await WriteAsync(ns, 200, Json(new { id = job.Id, requestId = job.RequestId, text = r.Text, audioSec = Math.Round(sec, 2), recognizeMs = r.RecognizeMs }));
        }
        else
        {
            Diag.Write($"{tag}: {r.Status} {r.Error}: {r.Message}");
            await WriteAsync(ns, r.Status, Json(new { id = job.Id, requestId = job.RequestId, error = r.Error, message = r.Message, retryAfterSec = r.RetryAfterSec }));
        }
    }

    // Секунды с точкой независимо от культуры процесса (на немецкой Windows было «911,0»).
    private static string Sec(double s) => s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Сокет закрыт с той стороны: «есть что читать» при нуле доступных байт.</summary>
    private static bool Disconnected(TcpClient c)
    {
        try { return c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0; }
        catch { return true; }
    }

    /// <summary>Длительность WAV по заголовку (fmt → byteRate, data → размер). Ошибка - текст для клиента.</summary>
    private static string? WavDuration(byte[] b, out double sec)
    {
        sec = 0;
        if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
            return "не WAV (нет RIFF/WAVE)";
        int pos = 12;
        uint byteRate = 0; long dataLen = -1;
        while (pos + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, pos, 4);
            uint len = BitConverter.ToUInt32(b, pos + 4);
            pos += 8;
            if (id == "fmt " && pos + 16 <= b.Length) byteRate = BitConverter.ToUInt32(b, pos + 8);
            if (id == "data") { dataLen = Math.Min(len, (uint)(b.Length - pos)); break; }   // усечённый файл: по факту
            pos += (int)(len + (len & 1));   // чанки выровнены по 2 байта
        }
        if (byteRate == 0) return "не WAV (нет fmt)";
        if (dataLen < 0) return "не WAV (нет data)";
        sec = dataLen / (double)byteRate;
        return null;
    }

    // ------------------------------ Ответ ------------------------------

    private static string Json(object o) => JsonSerializer.Serialize(o, JsonOpts);

    private static async Task WriteAsync(NetworkStream ns, int status, string? json)
    {
        byte[] body = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        if (json != null) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        try
        {
            await ns.WriteAsync(head);
            if (body.Length > 0) await ns.WriteAsync(body);
            await ns.FlushAsync();
        }
        catch { /* клиент ушёл - ответ некому */ }
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict",
        413 => "Payload Too Large", 422 => "Unprocessable Entity", 499 => "Client Closed Request",
        503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Error",
    };
}
