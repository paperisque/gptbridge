using System.Windows.Forms;

namespace WebView2Poc;

// GPT Grabber — голосовой ввод поверх встроенного ChatGPT (WebView2).
//   Ctrl+Win       — диктовка: старт/стоп, распознанный текст вставляется в активное окно.
//   Ctrl+Win+«Y»   — то же, но текст дополнительно остаётся в буфере обмена.
//   Ctrl+Win+Alt   — повторно вставить последний распознанный текст в текущее окно.
// Автономное приложение: внешний браузер и сетевой хаб не нужны.
internal static class Program
{
    private const string NoConsoleFlag = "GPTGRABBER_NOCONSOLE";
    private const string MutexName = @"Local\GptGrabber.SingleInstance";
    private static Mutex? _instanceMutex;

    [STAThread]
    private static void Main()
    {
        // Один экземпляр: если уже запущен — показать его окно и выйти (быстрый путь, без
        // перезапуска ниже). Гонку при одновременном старте дополнительно решает createdNew.
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var running))
            {
                running.Dispose();
                ShowRunningInstance();
                return;
            }
        }
        catch { /* нет доступа к мьютексу — не блокируем запуск */ }

        // Убираем чёрное окно консоли. dotnet.exe — консольный хост (свой apphost не собираем,
        // SAC §6.14). На Windows 11 с Windows Terminal спрятать его через ShowWindow(GetConsoleWindow())
        // НЕЛЬЗЯ: GetConsoleWindow отдаёт скрытое псевдоконсольное окно, а видимое окно — это
        // отдельный процесс Terminal. Надёжный способ — перезапустить себя БЕЗ окна
        // (CREATE_NO_WINDOW) и выйти; перезапущенный экземпляр метим переменной окружения,
        // чтобы не зациклиться. Короткое мелькание при старте неизбежно (консоль dotnet.exe
        // создаёт раньше нашего кода) — но постоянного окна в панели задач больше нет.
        var con = Win32.GetConsoleWindow();
        if (con != IntPtr.Zero && Environment.GetEnvironmentVariable(NoConsoleFlag) is null)
        {
            Win32.ShowWindow(con, Win32.SW_HIDE); // на классическом conhost убирает и мелькание
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,   // dotnet.exe (хост)
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                // dotnet <наша.dll> <исходные аргументы>
                psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
                foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
                psi.Environment[NoConsoleFlag] = "1";
                System.Diagnostics.Process.Start(psi);
                return; // первый экземпляр уходит — его консоль/Terminal закрывается
            }
            catch { /* перезапуск не удался — работаем как есть (лучше с консолью, чем не стартовать) */ }
        }

        // Единственный экземпляр: держим мьютекс всю жизнь процесса. createdNew=false ⇒ кто-то
        // успел раньше (гонка при старте) — показываем его и выходим.
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew) { ShowRunningInstance(); return; }

        Diag.Init();
        // Язык интерфейса: по умолчанию — система (de/ru/иначе → en); --lang en|de|ru перекрывает.
        Lang.Init(GetOption("--lang"));
        Diag.Write("старт GPT Grabber");

        // Любое необработанное исключение — в лог-файл (иначе WinExe падает «молча»).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diag.Write("UnhandledException: " + (e.ExceptionObject?.ToString() ?? "?"));
        Application.ThreadException += (_, e) => Diag.Write("ThreadException: " + e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // DPI-unaware — чтобы пилюля-индикатор считалась в тех же координатах, что в сервере
        // (иначе она смещается; страница в WebView на масштабе чуть мягче — приемлемо).
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Опции старта (как флаги у сервера). --tray: запуститься сразу свёрнутым в трей.
        bool startInTray = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        try
        {
            Application.Run(new MainForm(startInTray));
            Diag.Write("GPT Grabber закрыт");
        }
        catch (Exception ex)
        {
            Diag.Write("Application.Run упал: " + ex);
        }
    }

    /// <summary>Просим уже запущенный экземпляр развернуться из трея — broadcast зарегистрированного
    /// сообщения (его ловит MainForm.WndProc). Окно может быть скрыто — broadcast всё равно доходит.</summary>
    private static void ShowRunningInstance()
    {
        try { Win32.PostMessage(Win32.HWND_BROADCAST, MainForm.WmShowExisting, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    /// <summary>Значение опции вида «--name значение» из аргументов запуска (или null).</summary>
    private static string? GetOption(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
