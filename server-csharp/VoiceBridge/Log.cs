namespace VoiceBridge;

/// <summary>Простейший потокобезопасный консольный лог с таймстампами.</summary>
internal static class Log
{
    private static readonly object _gate = new();

    private static void Write(string level, ConsoleColor color, string msg)
    {
        lock (_gate)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level} {msg}");
            Console.ForegroundColor = prev;
        }
    }

    public static void Info(string m) => Write("INFO ", ConsoleColor.Gray, m);
    public static void Ok(string m) => Write("OK   ", ConsoleColor.Green, m);
    public static void Warn(string m) => Write("WARN ", ConsoleColor.Yellow, m);
    public static void Error(string m) => Write("ERROR", ConsoleColor.Red, m);
}
