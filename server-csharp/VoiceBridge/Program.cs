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
                case "--port":
                    string p = NextValue(args, ref i);
                    if (!int.TryParse(p, out int pv) || pv < 1 || pv > 65535)
                        throw new ArgumentException($"Некорректный порт: «{p}» (ожидается 1..65535).");
                    port = pv;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Неизвестный аргумент: «{args[i]}».");
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
            throw new ArgumentException($"Аргумент «{args[i]}» требует значение.");
        return args[++i];
    }

    private static void PrintUsage()
    {
        Log.Info("Использование:");
        Log.Info("  VoiceBridge [--host <addr>] [--port <n>] [--no-autolaunch]   режим сервера (хаб); по умолчанию localhost:17890.");
        Log.Info("  VoiceBridge --connect <host> [--port <n>]                    режим сетевого клиента к серверу-хабу.");
        Log.Info("");
        Log.Info("  --no-autolaunch  не поднимать Firefox автоматически (по умолчанию сервер сам запускает FF с ChatGPT).");
        Log.Info("");
        Log.Info("Примеры:");
        Log.Info("  VoiceBridge                          локально на этой машине (как раньше).");
        Log.Info("  VoiceBridge --host +                 сервер доступен по сети (нужны netsh urlacl + правило брандмауэра).");
        Log.Info("  VoiceBridge --connect 192.168.1.50   клиент к серверу 192.168.1.50:17890.");
    }
}
