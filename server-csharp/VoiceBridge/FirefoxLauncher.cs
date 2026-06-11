using System.Diagnostics;
using Microsoft.Win32;

namespace VoiceBridge;

/// <summary>
/// Запуск Firefox с табом ChatGPT, когда браузер не поднят (или для прогрева
/// на старте сервера). Только сервер может это сделать: пока FF не запущен,
/// ни расширения, ни WS-соединения нет.
///
/// ВАЖНО (грабля, см. CLAUDE.md §6.16): расширение, загруженное как ВРЕМЕННОЕ
/// (about:debugging), слетает при перезапуске браузера. Значит, запуск ПОЛНОСТЬЮ
/// закрытого Firefox даст браузер БЕЗ расширения — мост не подключится. Эта ветка
/// рассчитана на ПОСТОЯННО установленное расширение.
/// </summary>
internal static class FirefoxLauncher
{
    // Момент нашего последнего запуска Firefox. Пока не прошло FirefoxLaunchGraceMs,
    // повторные Launch подавляются: прогрев на старте сервера и старт записи — два
    // независимых вызова, без этой защиты они открывали ДВА окна браузера (§6.19).
    // long.MinValue = «ещё не запускали»; проверять ЯВНО, а не вычитанием — разность
    // TickCount64 - MinValue переполняется в отрицательное, и IsStartingUp был бы
    // вечно true (FF переставал стартовать вовсе, а ensureTab всегда уходил с «cold»).
    private static long _lastLaunchTicks = long.MinValue;

    /// <summary>Firefox недавно запущен нами и, вероятно, ещё поднимается.</summary>
    public static bool IsStartingUp =>
        _lastLaunchTicks != long.MinValue
        && Environment.TickCount64 - _lastLaunchTicks < Config.FirefoxLaunchGraceMs;

    /// <summary>Запущен ли сейчас процесс Firefox.</summary>
    public static bool IsRunning()
    {
        try { return Process.GetProcessesByName("firefox").Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// Запустить Firefox БЕЗ URL: табы поднимет восстановление сессии, а таб ChatGPT
    /// найдёт/создаст расширение (ensureTab) — единая точка истины. Раньше мы передавали
    /// URL ChatGPT — при включённом восстановлении сессии это давало ДУБЛЬ таба:
    /// один из сессии + один из командной строки (§6.19д).
    /// Возвращает false, если firefox.exe не найден / не стартовал.
    /// </summary>
    public static bool Launch()
    {
        // Уже запускается — второй firefox.exe дал бы второе окно (см. _lastLaunchTicks).
        if (IsStartingUp)
        {
            Log.Info(Lang.T("ff.already_starting"));
            return true;
        }

        string? exe = Config.FirefoxPath ?? FindExe();
        if (exe is null)
        {
            Log.Error(Lang.T("ff.not_found"));
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
            });
            _lastLaunchTicks = Environment.TickCount64;
            Log.Ok(Lang.T("ff.launching", exe));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(Lang.T("ff.launch_fail", exe, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Поиск firefox.exe: реестр App Paths (HKLM/HKCU), затем StartMenuInternet,
    /// затем стандартные папки Program Files. null — не нашли.
    /// </summary>
    private static string? FindExe()
    {
        // 1. App Paths — канонический способ узнать путь к exe по имени.
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            string? p = ReadDefault(root, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
            if (IsExe(p)) return p;
        }

        // 2. StartMenuInternet — путь вида "...\firefox.exe" -osint -url "%1".
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            string? cmd = ReadDefault(root, @"SOFTWARE\Clients\StartMenuInternet\FIREFOX.EXE\shell\open\command");
            string? p = ExtractExePath(cmd);
            if (IsExe(p)) return p;
        }

        // 3. Стандартные папки установки.
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            string? baseDir = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(baseDir)) continue;
            string p = Path.Combine(baseDir, "Mozilla Firefox", "firefox.exe");
            if (IsExe(p)) return p;
        }

        return null;
    }

    private static string? ReadDefault(RegistryKey root, string subkey)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subkey);
            return key?.GetValue(null) as string;
        }
        catch { return null; }
    }

    /// <summary>Достаёт путь к exe из командной строки реестра (с кавычками или без).</summary>
    private static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static bool IsExe(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);
}
