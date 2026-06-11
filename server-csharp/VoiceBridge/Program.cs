namespace VoiceBridge;

/// <summary>
/// Точка входа и разбор аргументов. Один exe — два режима:
///   • СЕРВЕР (хаб) — без --connect. Самодостаточен: ловит локальный Ctrl+Win и
///     вставляет на этой машине, плюс обслуживает сетевых клиентов. Логика — ServerApp.
///   • СЕТЕВОЙ КЛИЕНТ — с --connect &lt;host&gt;. Тонкий контроллер на другой машине.
///     Логика — ClientApp.
///
///   VoiceBridge [--host &lt;addr&gt;] [--port &lt;n&gt;]      сервер (по умолчанию localhost:17890)
///   VoiceBridge --connect &lt;host&gt; [--port &lt;n&gt;]      клиент к серверу-хабу
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.Title = "VoiceBridge";
        // Консоль Windows по умолчанию не UTF-8 -> кириллица превращается в '?'.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* перенаправленный вывод */ }

        // Язык интерфейса: сперва язык системы; --lang в аргументах перебьёт (см. ParseArgs).
        Lang.Init();

        bool clientMode;
        try
        {
            clientMode = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex.Message);
            PrintUsage();
            return;
        }

        if (clientMode) ClientApp.Run();
        else ServerApp.Run();
    }

    /// <summary>Разбирает аргументы, настраивает Config. Возвращает true для режима клиента.</summary>
    private static bool ParseArgs(string[] args)
    {
        bool clientMode = false;
        string? connectHost = null;
        string? listenHost = null;
        int? port = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connect":
                case "--client":
                    connectHost = NextValue(args, ref i);
                    clientMode = true;
                    break;
                case "--host":
                    listenHost = NextValue(args, ref i);
                    break;
                case "--no-autolaunch":
                    Config.AutoLaunchFirefox = false;
                    break;
                case "--no-overlay":
                    Config.ShowOverlay = false;
                    break;
                case "--overlay-compact":
                    Config.OverlayCompact = true;
                    break;
                case "--overlay-colors":
                    ParseOverlayColors(NextValue(args, ref i));
                    break;
                case "--lang":
                    string lang = NextValue(args, ref i).ToLowerInvariant();
                    if (!Lang.Supported.Contains(lang))
                        throw new ArgumentException(Lang.T("err.bad_lang", lang));
                    Lang.Init(lang); // сразу: дальнейшие сообщения (в т.ч. ошибки разбора) уже на нём
                    break;
                case "--port":
                    string p = NextValue(args, ref i);
                    if (!int.TryParse(p, out int pv) || pv < 1 || pv > 65535)
                        throw new ArgumentException(Lang.T("err.bad_port", p));
                    port = pv;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException(Lang.T("err.unknown_arg", args[i]));
            }
        }

        if (port.HasValue) Config.Port = port.Value;

        if (clientMode)
            Config.Host = connectHost!;        // ws://<host>:<port>/ — куда коннектиться
        else if (listenHost is not null)
            Config.Host = listenHost;          // http://<host>:<port>/ — что слушать
        // иначе сервер слушает localhost по умолчанию (как раньше, без прав администратора)

        return clientMode;
    }

    private static string NextValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException(Lang.T("err.arg_needs_value", args[i]));
        return args[++i];
    }

    /// <summary>
    /// Разбор --overlay-colors: список key=RRGGBB через запятую, hex без «#»
    /// (например «bg=1E1E20,rec=00C853»). Незаданные ключи остаются дефолтными.
    /// </summary>
    private static void ParseOverlayColors(string spec)
    {
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=', 2);
            if (kv.Length != 2 || kv[1].Length != 6
                || !uint.TryParse(kv[1], System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
                throw new ArgumentException(Lang.T("err.overlay_colors_format", part));

            uint color = Config.Rgb((int)(rgb >> 16) & 0xFF, (int)(rgb >> 8) & 0xFF, (int)rgb & 0xFF);
            switch (kv[0])
            {
                case "bg": Config.OverlayColBg = color; break;
                case "border": Config.OverlayColBorder = color; break;
                case "text": Config.OverlayColText = color; break;
                case "wait": Config.OverlayColWait = color; break;
                case "rec": Config.OverlayColRec = color; break;
                case "busy": Config.OverlayColBusy = color; break;
                case "err": Config.OverlayColErr = color; break;
                default:
                    throw new ArgumentException(Lang.T("err.overlay_colors_key", kv[0]));
            }
        }
    }

    private static void PrintUsage()
    {
        Log.Info(Lang.T("usage.title"));
        Log.Info(Lang.T("usage.server"));
        Log.Info(Lang.T("usage.client"));
        Log.Info("");
        Log.Info(Lang.T("usage.opt_noautolaunch"));
        Log.Info(Lang.T("usage.opt_nooverlay"));
        Log.Info(Lang.T("usage.opt_overlaycompact"));
        Log.Info(Lang.T("usage.opt_overlaycolors1"));
        Log.Info(Lang.T("usage.opt_overlaycolors2"));
        Log.Info(Lang.T("usage.opt_overlaycolors3"));
        Log.Info(Lang.T("usage.opt_lang"));
        Log.Info("");
        Log.Info(Lang.T("usage.examples"));
        Log.Info(Lang.T("usage.ex_local"));
        Log.Info(Lang.T("usage.ex_host"));
        Log.Info(Lang.T("usage.ex_connect"));
    }
}
