using System.Text.Json;

namespace VoiceBridge;

/// <summary>
/// Локализация всех статичных надписей (консоль сервера/клиента + индикатор).
/// Языковые модули — JSON-словари «ключ → строка» в папке lang/ рядом с DLL
/// (копируются при сборке). Поддерживаются en / de / ru.
///
/// Выбор языка: язык UI Windows (GetUserDefaultUILanguage — CultureInfo
/// бесполезен из-за InvariantGlobalization в csproj); флаг --lang перебивает.
/// Фолбэк: нет ключа в выбранном языке → английский → сам ключ (чтобы никогда
/// не упасть из-за недостающего перевода).
///
/// Строки с параметрами — шаблоны string.Format ({0}, {1:X} и т.п.).
/// </summary>
internal static class Lang
{
    public static readonly string[] Supported = { "en", "de", "ru" };

    /// <summary>Текущий код языка (en/de/ru).</summary>
    public static string Current { get; private set; } = "en";

    private static Dictionary<string, string> _strings = new();
    private static Dictionary<string, string> _fallback = new();

    /// <summary>Инициализация: явный код языка или null — взять язык системы.</summary>
    public static void Init(string? code = null)
    {
        code ??= DetectSystemLanguage();
        if (!Supported.Contains(code)) code = "en";
        Current = code;
        _fallback = LoadFile("en");
        _strings = code == "en" ? _fallback : LoadFile(code);
    }

    /// <summary>Строка по ключу (+ string.Format-аргументы, если шаблон с {0}…).</summary>
    public static string T(string key, params object[] args)
    {
        if (!_strings.TryGetValue(key, out string? s) && !_fallback.TryGetValue(key, out s))
            s = key; // перевод потерян — показываем ключ, но не падаем
        return args.Length == 0 ? s : string.Format(s, args);
    }

    private static string DetectSystemLanguage()
    {
        // Младшие 10 бит LANGID — первичный язык: 0x07 немецкий, 0x19 русский, 0x09 английский.
        return (Native.GetUserDefaultUILanguage() & 0x3FF) switch
        {
            0x07 => "de",
            0x19 => "ru",
            _ => "en",
        };
    }

    private static Dictionary<string, string> LoadFile(string code)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "lang", code + ".json");
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            // Лог до инициализации языка — по-английски, без ключей.
            Log.Error($"Language file load failed: {path} ({ex.Message})");
            return new();
        }
    }
}
