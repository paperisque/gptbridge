namespace WebView2Poc;

/// <summary>
/// Локализация видимых надписей: язык берём из системы (как в сервере — через LANGID,
/// CultureInfo не используем). Поддержка en/de/ru; любой другой язык → английский.
/// Словари встроены в код (для автономного приложения проще, чем тащить JSON).
/// </summary>
internal static class Lang
{
    private static Dictionary<string, string>? _cur;

    public static void Init(string? langOverride = null)
    {
        // --lang en|de|ru перекрывает язык системы (как у сервера); иначе — детект по системе.
        _cur = (langOverride?.Trim().ToLowerInvariant()) switch
        {
            "en" => En,
            "de" => De,
            "ru" => Ru,
            _ => DetectFromSystem(),
        };
    }

    private static Dictionary<string, string> DetectFromSystem()
    {
        int lid = Win32.GetUserDefaultUILanguage() & 0x3FF; // младшие 10 бит = базовый язык
        return lid switch { 0x07 => De, 0x19 => Ru, _ => En };
    }

    public static string T(string key, params object[] args)
    {
        var cur = _cur ?? En;
        string s = cur.TryGetValue(key, out var v) ? v : (En.TryGetValue(key, out var e) ? e : key);
        return args is { Length: > 0 } ? string.Format(s, args) : s;
    }

    private static readonly Dictionary<string, string> En = new()
    {
        ["tray.show"] = "Show",
        ["tray.exit"] = "Exit",
        ["overlay.preparing"] = "Preparing ChatGPT…",
        ["overlay.starting"] = "Starting microphone…",
        ["overlay.recording"] = "Recording",
        ["overlay.transcribing"] = "Transcribing…",
        ["overlay.done"] = "Inserted",
        ["overlay.error"] = "Error",
        ["err.not_ready"] = "ChatGPT not ready",
        ["err.no_recording"] = "Recording didn't start",
        ["err.cancelled"] = "Cancelled",
        ["err.start_failed"] = "Start error",
        ["err.empty"] = "Empty — no text",
        ["err.paste_failed"] = "Paste failed",
        ["err.generic"] = "Error",
        ["err.no_text"] = "No text",
        ["err.not_pasted"] = "Not pasted",
        ["hint.login"] = "If not signed in, sign in to ChatGPT in the window above (the session is kept).",
        ["log.hotkeys_on"] = "Hotkeys active: Ctrl+Win — dictation, Ctrl+Win+Alt — re-paste.",
        ["log.hotkeys_fail"] = "WARNING: failed to install the keyboard hook.",
        ["log.profile"] = "Profile: {0}",
        ["log.webview_error"] = "WebView2 error — is the WebView2 Runtime (Evergreen) installed?",
        ["log.result"] = "Recognized: {0}",
        ["help.title"] = "Hotkeys",
        ["help.dictate"] = "Ctrl+Win — dictation: start, speak, stop → paste into the active window",
        ["help.keepbuf"] = "Ctrl+Win+Y — same, and the text also stays in the clipboard",
        ["help.repaste"] = "Ctrl+Win+Alt — paste the last recognized text again",
        ["help.cancel"] = "Ctrl+Win during prepare/recognize — cancel",
        ["help.flags_title"] = "Command-line flags (after the DLL path):",
        ["help.flag_lang"] = "--lang en|de|ru — interface language (overrides the system one)",
        ["help.flag_tray"] = "--tray — start minimized to the tray",
        ["help.flag_nobeep"] = "--no-beep — turn off the sound on paste",
    };

    private static readonly Dictionary<string, string> De = new()
    {
        ["tray.show"] = "Anzeigen",
        ["tray.exit"] = "Beenden",
        ["overlay.preparing"] = "ChatGPT wird vorbereitet…",
        ["overlay.starting"] = "Mikrofon wird gestartet…",
        ["overlay.recording"] = "Aufnahme läuft",
        ["overlay.transcribing"] = "Wird erkannt…",
        ["overlay.done"] = "Eingefügt",
        ["overlay.error"] = "Fehler",
        ["err.not_ready"] = "ChatGPT nicht bereit",
        ["err.no_recording"] = "Aufnahme nicht gestartet",
        ["err.cancelled"] = "Abgebrochen",
        ["err.start_failed"] = "Startfehler",
        ["err.empty"] = "Leer — kein Text",
        ["err.paste_failed"] = "Einfügen fehlgeschlagen",
        ["err.generic"] = "Fehler",
        ["err.no_text"] = "Kein Text",
        ["err.not_pasted"] = "Nicht eingefügt",
        ["hint.login"] = "Falls nicht angemeldet: im Fenster oben bei ChatGPT anmelden (Sitzung bleibt erhalten).",
        ["log.hotkeys_on"] = "Hotkeys aktiv: Ctrl+Win — Diktat, Ctrl+Win+Alt — erneut einfügen.",
        ["log.hotkeys_fail"] = "WARNUNG: Tastatur-Hook konnte nicht gesetzt werden.",
        ["log.profile"] = "Profil: {0}",
        ["log.webview_error"] = "WebView2-Fehler — ist die WebView2-Runtime (Evergreen) installiert?",
        ["log.result"] = "Erkannt: {0}",
        ["help.title"] = "Tastenkürzel",
        ["help.dictate"] = "Ctrl+Win — Diktat: Start, sprechen, Stopp → Einfügen ins aktive Fenster",
        ["help.keepbuf"] = "Ctrl+Win+Y — dasselbe, Text bleibt zusätzlich in der Zwischenablage",
        ["help.repaste"] = "Ctrl+Win+Alt — letzten erkannten Text erneut einfügen",
        ["help.cancel"] = "Ctrl+Win während Vorbereitung/Erkennung — Abbrechen",
        ["help.flags_title"] = "Startparameter (nach dem DLL-Pfad):",
        ["help.flag_lang"] = "--lang en|de|ru — Sprache der Oberfläche (überschreibt die Systemsprache)",
        ["help.flag_tray"] = "--tray — minimiert in den Infobereich starten",
        ["help.flag_nobeep"] = "--no-beep — Ton beim Einfügen ausschalten",
    };

    private static readonly Dictionary<string, string> Ru = new()
    {
        ["tray.show"] = "Показать",
        ["tray.exit"] = "Выход",
        ["overlay.preparing"] = "Готовлю ChatGPT…",
        ["overlay.starting"] = "Включаю микрофон…",
        ["overlay.recording"] = "Идёт запись",
        ["overlay.transcribing"] = "Распознаю…",
        ["overlay.done"] = "Вставлено",
        ["overlay.error"] = "Ошибка",
        ["err.not_ready"] = "ChatGPT не готов",
        ["err.no_recording"] = "Запись не пошла",
        ["err.cancelled"] = "Отменено",
        ["err.start_failed"] = "Ошибка старта",
        ["err.empty"] = "Пусто — текста нет",
        ["err.paste_failed"] = "Вставка не удалась",
        ["err.generic"] = "Ошибка",
        ["err.no_text"] = "Нет текста",
        ["err.not_pasted"] = "Не вставилось",
        ["hint.login"] = "Если не залогинен — войди в ChatGPT в окне выше (сессия сохранится).",
        ["log.hotkeys_on"] = "Хоткеи активны: Ctrl+Win — диктовка, Ctrl+Win+Alt — повторная вставка.",
        ["log.hotkeys_fail"] = "ВНИМАНИЕ: не удалось поставить хук клавиатуры.",
        ["log.profile"] = "Профиль: {0}",
        ["log.webview_error"] = "Ошибка WebView2 — установлен ли WebView2 Runtime (Evergreen)?",
        ["log.result"] = "Распознано: {0}",
        ["help.title"] = "Горячие клавиши",
        ["help.dictate"] = "Ctrl+Win — диктовка: старт, говори, стоп → вставка в активное окно",
        ["help.keepbuf"] = "Ctrl+Win+Y — то же, и текст остаётся в буфере обмена",
        ["help.repaste"] = "Ctrl+Win+Alt — снова вставить последний распознанный текст",
        ["help.cancel"] = "Ctrl+Win в фазе подготовки/распознавания — отмена",
        ["help.flags_title"] = "Параметры запуска (после пути к DLL):",
        ["help.flag_lang"] = "--lang en|de|ru — язык интерфейса (перекрывает системный)",
        ["help.flag_tray"] = "--tray — запуститься свёрнутым в трей",
        ["help.flag_nobeep"] = "--no-beep — выключить звук при вставке",
    };
}
