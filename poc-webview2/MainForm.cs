using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using MaterialSkin;
using MaterialSkin.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2Poc;

/// <summary>
/// Главное окно GPT Grabber. Сверху — встроенный ChatGPT (WebView2), снизу — лог.
/// Окно живёт в трее (свёрнутое — захват микрофона при этом работает, проверено).
///
/// Хоткеи (глобальные, ловит Hotkey):
///   Ctrl+Win      — диктовка (старт → говори → стоп → вставка в активное окно);
///   Ctrl+Win+«Y»  — то же + текст остаётся в буфере;
///   Win+Alt  — повторно вставить последний распознанный текст.
/// Ctrl+Win в промежуточной фазе (готовлю/распознаю) = отмена.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new();
    private readonly TextBox _log = new();
    private readonly NotifyIcon _tray = new();
    private Icon? _icoWhite, _icoOrange, _icoGreen, _icoBlue, _icoRed; // эквалайзер по фазам (синхрон с пилюлей)
    private Icon? _icoYellow;                                          // идёт файл куклы (вход FileEntry)

    // Вход «файл → текст» для кукол (FileEntry, HTTP на 127.0.0.1:17891). Задание живёт на UI-потоке:
    // _fileJob - текущее, _fileTask - его работа (вытеснение ждёт её конца, потом стартует живую диктовку).
    private FileEntry? _fileEntry;
    private FileJob? _fileJob;
    private Task? _fileTask;
    private DateTime _fileEndsAt;        // оценка конца звука файла - для retryAfterSec другим куклам
    private DateTime _liveQuietUntil;    // пауза после живой диктовки: куклам «занято», сдвигается каждой диктовкой
    private const int LiveQuietSec = 10; // решение юзера 09.11.2026
    private bool _pageReady;             // кнопка Start на месте - опрос раз в 5 с (ReadyPollMs), только в покое
    private const int ReadyPollMs = 5000;
    private const int FileChunkChars = 4 * 1024 * 1024;   // base64 в страницу кусками по 4 МБ (проверено разведкой 08.11)
    private System.Windows.Forms.Timer _readyTimer = null!;
    private Hotkey? _hotkey;
    private bool _exiting;
    private readonly bool _startInTray; // --tray: стартовать сразу свёрнутым в трей
    private bool _trayStartDone;

    // Полоса лога снизу: обычная высота и увеличенная — под открытую справку по «?».
    private const int LogRowNormalPx = 150, LogRowHelpPx = 310;
    private RowStyle _logRow = null!;

    // Тулбар управления микрофоном (над логом): усиление/шумоподавление/громкость.
    private const int ToolbarPx = 57;
    private readonly MicSettings _mic = MicSettings.Load();
    private MaterialSlider _gainBar = null!;
    private Label _gainValue = null!;
    private ComboBox _micCombo = null!;
    private bool _micComboLoading;   // подавляем событие выбора во время программного заполнения
    private Panel _helpPanel = null!;     // всплывающая справка над логом
    private readonly ToolTip _tips = new();

    // Тулбар скрыт по умолчанию, раздвигается по высоте по кнопке (плавно).
    private RowStyle _toolbarRow = null!;
    private bool _toolbarShown;
    private int _toolbarTarget;
    private System.Windows.Forms.Timer _toolbarAnim = null!;
    // Плавающие кнопки (показать тулбар + «?») теперь ВПРЫСКИВАЮТСЯ в страницу (JS+CSS,
    // OverlayInjectScript) — ровные бордеры/ховер, поверх контента. Тумблеры зовут ToggleToolbar/ToggleHelp
    // через WebMessage.

    /// <summary>Элемент списка микрофонов (deviceId + метка). ToString — для показа в ComboBox.</summary>
    private sealed record MicDevice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    // Сообщение «развернись из трея» от второго экземпляра (single-instance). Значение системно-
    // уникально по строке — одинаково во всех процессах, поэтому второй шлёт именно его (broadcast).
    public static readonly uint WmShowExisting = Win32.RegisterWindowMessage("GptGrabber.ShowExistingInstance.v1");

    // File - идёт файл куклы (вход FileEntry); Ctrl+Win в нём = вытеснение файла живой диктовкой.
    private enum LiveState { Idle, Starting, Recording, Stopping, File }
    private LiveState _state = LiveState.Idle;
    private static bool IsLive(LiveState s) => s is LiveState.Starting or LiveState.Recording or LiveState.Stopping;

    // При возврате в покой иконку трея возвращаем к белой (активные цвета ставят фаза-обёртки).
    // Живая диктовка кончилась (как угодно: вставкой, отменой, ошибкой) → отсчёт паузы для кукол заново.
    private LiveState State
    {
        get => _state;
        set
        {
            bool wasLive = IsLive(_state);
            _state = value;
            if (value == LiveState.Idle)
            {
                if (wasLive) _liveQuietUntil = DateTime.Now.AddSeconds(LiveQuietSec);
                if (_icoWhite != null) _tray.Icon = _icoWhite;
            }
        }
    }
    private CancellationTokenSource? _cts;
    private string _lastText = "";          // последний распознанный текст — для Win+Alt
    private bool _firstClearDone;            // черновик композера чистим один раз после загрузки

    public MainForm(bool startInTray)
    {
        _startInTray = startInTray;
        Text = "GPT Grabber";
        Width = 740;   // компактнее (~на треть меньше прежних 1100×820)
        Height = 610;   // +60 под увеличенную область справки, чтобы она не залазила на композер ChatGPT
        // При старте в трей запускаемся за экраном — инициализация (WebView/хук) проходит,
        // но без видимой вспышки окна; затем в OnShown прячемся и центрируем на будущее.
        StartPosition = startInTray ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (startInTray) Location = new System.Drawing.Point(-32000, -32000);
        ShowInTaskbar = false; // утилита трея; задаём ДО создания хэндла (без пересоздания окна)

        SetupMaterialSkin();   // тёмная тема + шрифт Rubik ДО создания MaterialSkin-контролов

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // WebView с ChatGPT
        _toolbarRow = new RowStyle(SizeType.Absolute, 0);          // тулбар СКРЫТ по умолчанию (раздвигается по кнопке)
        root.RowStyles.Add(_toolbarRow);
        _logRow = new RowStyle(SizeType.Absolute, LogRowNormalPx); // лог (расширяется под открытую справку)
        root.RowStyles.Add(_logRow);

        _web.Dock = DockStyle.Fill;
        // Отладочный порт Chromium (--devtools-port N) — для внешнего анализа DOM по CDP.
        // Без флага строка аргументов ровно прежняя, поведение не меняется.
        var dbgPort = Program.GetOption("--devtools-port");
        _web.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Diag.WebViewDataDir,
            // Не давать Chromium «засыпать» в фоне/свёрнутым — захват микрофона должен идти.
            // --autoplay-policy: страховка для входа «файл → текст» - AudioContext с файлом стартует без жеста
            // пользователя (в разведке 08.11 контекст и так был running, но строго это не доказано).
            AdditionalBrowserArguments =
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
                + " --autoplay-policy=no-user-gesture-required"
                + (string.IsNullOrEmpty(dbgPort) ? "" : $" --remote-debugging-port={dbgPort}")
        };

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.Dock = DockStyle.Fill;
        _log.WordWrap = false;   // строки не переносим: текст идёт до правого края окна и там обрезается
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new System.Drawing.Font("Consolas", 9f);
        _log.BackColor = System.Drawing.Color.FromArgb(24, 24, 26);
        _log.ForeColor = System.Drawing.Color.Gainsboro;
        _log.Text = Environment.NewLine;   // верхний отступ: лог начинается не вплотную к краю

        root.Controls.Add(_web, 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);
        root.Controls.Add(BuildLogHost(), 0, 2);
        Controls.Add(root);

        _toolbarAnim = new System.Windows.Forms.Timer { Interval = 12 };
        _toolbarAnim.Tick += (_, _) => AnimateToolbar();

        _icoWhite = BuildEqIcon(Color.White);                  // покой
        _icoOrange = BuildEqIcon(Color.FromArgb(255, 165, 0)); // готовлю/включаю микрофон
        _icoGreen = BuildEqIcon(Color.FromArgb(90, 205, 100)); // идёт запись
        _icoBlue = BuildEqIcon(Color.FromArgb(80, 155, 255));  // распознаю
        _icoRed = BuildEqIcon(Color.FromArgb(235, 80, 80));    // ошибка
        _icoYellow = BuildEqIcon(Color.FromArgb(240, 200, 60)); // идёт файл куклы
        SetupTray();
        // Готовность страницы для /status: лёгкий опрос кнопки Start в покое (один маленький скрипт).
        _readyTimer = new System.Windows.Forms.Timer { Interval = ReadyPollMs };
        _readyTimer.Tick += async (_, _) => await PollPageReadyAsync();
        Load += async (_, _) => await InitWebViewAsync();
    }

    // ------------------------------ Трей ------------------------------

    // Меню трея дублирует все команды клавиш и переключатель режимов (просьба пользователя 22.11.2026: «все команды,
    // которые делаются с помощью комбинации клавиш, продублировались в менюхе», «и переключатель режимов там добавь»).
    // Состав меню собирается заново при каждом открытии - по состоянию диктовки, как ведёт себя Ctrl+Win.
    // Открывается одиночным щелчком левой или правой кнопкой; двойной щелчок - окно программы, как раньше.
    // Цель вставки: при клике по трею активной становится панель задач, поэтому программа всё время помнит
    // последнее активное окно человека (_lastUserWin) и по выбору пункта СРАЗУ возвращает его на передний план -
    // Windows сама ставит фокус и текстовый курсор туда, где они были в этом окне; дальше команда идёт тем же
    // путём, что и от клавиш (решение пользователя 22.11.2026). Указатель мыши не трогаем.

    private readonly ContextMenuStrip _trayMenu = new();
    private readonly System.Windows.Forms.Timer _trayClickTimer = new();
    private IntPtr _lastUserWin;
    private IntPtr _fgHook;
    private Win32.WinEventProc? _fgProc;   // держим от GC, как делегат хука клавиатуры

    private void SetupTray()
    {
        TrayMenuRenderer.Apply(_trayMenu);   // тёмный вид в стиле программы
        if (!_mic.Sound) Win32.BeepEnabled = false;   // выключенный в меню звук помним между запусками
        _trayMenu.Opening += (_, _) => BuildTrayMenu();
        _tray.Icon = _icoWhite;
        _tray.Text = "GPT Grabber";
        _tray.Visible = true;
        _tray.ContextMenuStrip = _trayMenu;   // правый щелчок открывает меню сам
        // Левый щелчок: меню - но выждав время двойного щелчка, иначе первый щелчок двойного открыл бы меню.
        _trayClickTimer.Interval = SystemInformation.DoubleClickTime;
        _trayClickTimer.Tick += (_, _) => { _trayClickTimer.Stop(); ShowTrayMenu(); };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _trayClickTimer.Start(); };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) { _trayClickTimer.Stop(); ShowFromTray(); } };

        _fgProc = OnForegroundChanged;
        _fgHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        IntPtr fg = Win32.GetForegroundWindow();
        if (IsUserWindow(fg)) _lastUserWin = fg;
    }

    /// <summary>Показать меню трея по левому щелчку - тем же закрытым методом NotifyIcon, что и правый щелчок
    /// (он правильно выводит меню на передний план и закрывает его щелчком мимо).</summary>
    private void ShowTrayMenu()
    {
        try
        {
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(_tray, null);
        }
        catch (Exception ex) { Diag.Write("трей: меню не открылось: " + ex.Message); }
    }

    private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject == 0 && IsUserWindow(hwnd)) _lastUserWin = hwnd;
    }

    // Окна оболочки Windows, куда команда идти не должна: панель задач, лоток и его переполнение, меню, рабочий стол,
    // «Пуск»/поиск, переключатель задач, миниатюры панели задач.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "#32768", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd",
    };

    /// <summary>Окно человека: живое, не наше (окно программы, пилюля, меню трея) и не оболочка Windows.</summary>
    private static bool IsUserWindow(IntPtr h)
    {
        if (h == IntPtr.Zero || !Win32.IsWindow(h)) return false;
        Win32.GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        var sb = new System.Text.StringBuilder(128);
        Win32.GetClassName(h, sb, sb.Capacity);
        return !ShellClasses.Contains(sb.ToString());
    }

    /// <summary>Собрать меню трея под текущее состояние (зовётся при каждом открытии).</summary>
    private void BuildTrayMenu()
    {
        _trayMenu.Items.Clear();
        string ctrlWin = Lang.T("key.ctrl") + "+Win";
        ToolStripMenuItem Cmd(string text, string keys, Action act)
        {
            var it = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
            it.Click += (_, _) => RunFromTray(act);
            return it;
        }
        switch (_state)
        {
            case LiveState.Idle:
            case LiveState.File:   // во время файла куклы - как Ctrl+Win: вытеснение и живая диктовка
                _trayMenu.Items.Add(Cmd(Lang.T("tray.start"), ctrlWin, () => OnToggle(false)));
                break;
            case LiveState.Recording:
                _trayMenu.Items.Add(Cmd(Lang.T("tray.stop"), ctrlWin, () => OnToggle(false)));
                _trayMenu.Items.Add(Cmd(Lang.T("tray.stop_keep"), ctrlWin + "+Y", () => OnToggle(true)));
                if (_mic.Stream && IsUserWindow(_lastUserWin) && _stream?.Target != _lastUserWin)
                    _trayMenu.Items.Add(Cmd(Lang.T("tray.move", Trunc(Win32.GetWindowTitle(_lastUserWin))), "Shift+Win", OnMoveStream));
                break;
            default:               // Starting / Stopping: Ctrl+Win здесь - отмена
                _trayMenu.Items.Add(Cmd(Lang.T("tray.cancel"), ctrlWin, () => OnToggle(false)));
                break;
        }
        var repaste = Cmd(Lang.T("tray.repaste"), "Win+Alt", OnRepaste);
        repaste.Enabled = !string.IsNullOrEmpty(_lastText);
        _trayMenu.Items.Add(repaste);

        _trayMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem Mode(string text, bool on, bool stream, bool auto)
        {
            var it = new ToolStripMenuItem(text) { Checked = on };
            it.Click += (_, _) => SetStreamMode(stream, auto);
            return it;
        }
        _trayMenu.Items.Add(Mode(Lang.T("mode.normal"), !_mic.Stream, false, false));
        _trayMenu.Items.Add(Mode(Lang.T("mode.manual"), _mic.Stream && !_mic.StreamAuto, true, false));
        _trayMenu.Items.Add(Mode(Lang.T("mode.auto"), _mic.Stream && _mic.StreamAuto, true, true));

        // Звук: все пики программы - вставка, ошибка, отсчёт, напоминание (просьба пользователя 22.11.2026).
        _trayMenu.Items.Add(new ToolStripSeparator());
        var sound = new ToolStripMenuItem(Lang.T("tray.sound")) { Checked = Win32.BeepEnabled };
        sound.Click += (_, _) =>
        {
            Win32.BeepEnabled = !Win32.BeepEnabled;
            _mic.Sound = Win32.BeepEnabled;
            _mic.Save();
            Diag.Write("звук: " + (Win32.BeepEnabled ? "включён" : "выключен"));
        };
        _trayMenu.Items.Add(sound);

        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(Lang.T("tray.show"), null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add(Lang.T("tray.exit"), null, (_, _) => { _exiting = true; Close(); });
        foreach (ToolStripItem it in _trayMenu.Items) TrayMenuRenderer.StyleItem(it);
    }

    /// <summary>Команда из меню трея: вернуть на передний план окно, где человек был до клика по трею, и выполнить
    /// команду так же, как от клавиш (обработчики берут цель из активного окна). Отложено через BeginInvoke -
    /// сперва меню закрывается до конца.</summary>
    private void RunFromTray(Action act)
    {
        BeginInvoke(new Action(() =>
        {
            IntPtr win = _lastUserWin;
            if (!IsUserWindow(win))
            {
                Win32.FeedbackBeep(false);
                Log(Lang.T("tray.no_window"));
                return;
            }
            bool ok = Injector.ForceForeground(win);
            Diag.Write($"трей: команда в «{Trunc(Win32.GetWindowTitle(win))}» (окно впереди={ok})");
            act();
        }));
    }

    /// <summary>
    /// Иконка-эквалайзер (5 капсул-столбиков, центрированы по средней линии; узор как eq1)
    /// заданным цветом, 32×32 — рисуем в коде, чтобы легко перекрашивать под состояние.
    /// </summary>
    private static Icon BuildEqIcon(Color color)
    {
        const int sz = 32;
        var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            int[] hh = { 12, 24, 18, 30, 11 }; // высоты столбиков (узор №1)
            const float bw = 3.4f, gap = 2.4f;
            float cy = sz / 2f;
            float total = 5 * bw + 4 * gap;
            float x = (sz - total) / 2f + bw / 2f;
            using var pen = new Pen(color, bw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 5; i++)
            {
                float half = Math.Max(0f, (hh[i] - bw) / 2f);
                float cx = x + i * (bw + gap);
                g.DrawLine(pen, cx, cy - half, cx, cy + half);
            }
        }
        Icon icon = Icon.FromHandle(bmp.GetHicon()); // несколько иконок на всё приложение — хэндлы ок
        bmp.Dispose();
        return icon;
    }

    // Иконка трея под фазу пилюли (синхрон цвета).
    private Icon? TrayFor(StatusOverlay.Phase p) => p switch
    {
        StatusOverlay.Phase.Preparing or StatusOverlay.Phase.Starting => _icoOrange,
        StatusOverlay.Phase.Recording => _icoGreen,
        StatusOverlay.Phase.Transcribing => _icoBlue,
        StatusOverlay.Phase.Done => _icoGreen,
        StatusOverlay.Phase.Error => _icoRed,
        _ => _icoWhite,
    };

    // Меняем фазу пилюли И синхронно цвет иконки трея.
    private void OverlayShow(StatusOverlay.Phase p, IntPtr anchor)
    {
        StatusOverlay.Show(p, anchor);
        var ic = TrayFor(p); if (ic != null) _tray.Icon = ic;
    }

    private void OverlaySet(StatusOverlay.Phase p, string? text = null)
    {
        StatusOverlay.Set(p, text);
        var ic = TrayFor(p); if (ic != null) _tray.Icon = ic;
    }

    private void ShowFromTray()
    {
        Show();                                  // окно было полностью скрыто
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Старт в трей: окно уже инициализировалось (за экраном) — прячем его, а Location
        // возвращаем на центр экрана, чтобы «Показать» из трея открыло окно по-человечески.
        if (_startInTray && !_trayStartDone)
        {
            _trayStartDone = true;
            Hide();
            var s = Screen.PrimaryScreen;
            if (s != null)
                Location = new System.Drawing.Point(
                    s.WorkingArea.X + (s.WorkingArea.Width - Width) / 2,
                    s.WorkingArea.Y + (s.WorkingArea.Height - Height) / 2);
        }
    }

    // Кнопка «свернуть» тоже уводит в трей: прячем окно целиком, иначе остаётся
    // минимизированный «огрызок» (окно без кнопки в таскбаре сворачивается криво).
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Крестик не закрывает приложение, а ПРЯЧЕТ его в трей (Hide — окно исчезает,
        // остаётся только иконка). Реальный выход — только через меню трея «Выход».
        // Захват микрофона при скрытом окне продолжается (аудио в Chromium не зависит
        // от видимости + флаги анти-засыпания).
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _hotkey?.Dispose();
        if (_fgHook != IntPtr.Zero) Win32.UnhookWinEvent(_fgHook);
        _fileEntry?.Dispose();
        StatusOverlay.Destroy();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    // --------------------------- Инициализация ---------------------------

    private async Task InitWebViewAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();

            // Перехват getUserMedia ставим ДО навигации (скрипт срабатывает при создании
            // документа, раньше кода ChatGPT) — иначе страница успеет захватить оригинал.
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MicBoostScript(_mic));
            Diag.Write($"mic boost injected (enabled={_mic.Enabled} gain={_mic.Gain.ToString(CultureInfo.InvariantCulture)} noise={_mic.Noise})");

            _web.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    e.State = CoreWebView2PermissionState.Allow;
            };
            // Клики впрыснутых в страницу кнопок приходят сюда (WebMessage) → в UI-поток.
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string msg;
                try { msg = e.TryGetWebMessageAsString(); } catch { return; }
                if (msg == "gg:toolbar") BeginInvoke(new Action(ToggleToolbar));
                else if (msg == "gg:help") BeginInvoke(new Action(ToggleHelp));
                else if (msg == "gg:mode:normal") BeginInvoke(new Action(() => SetStreamMode(false, false)));
                else if (msg == "gg:mode:stream") BeginInvoke(new Action(() => SetStreamMode(true, false)));
                else if (msg == "gg:mode:stream-auto") BeginInvoke(new Action(() => SetStreamMode(true, true)));
                else if (msg is not null && msg.StartsWith("gg:voice:")) BeginInvoke(new Action(() => OnVoiceMessage(msg)));
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                Diag.Write($"navigation completed, success={e.IsSuccess}");
                if (e.IsSuccess)
                {
                    await Exec(HideExtrasScript);    // спрятать лишний блок под #thread-bottom (CSS живёт в head)
                    await Exec(OverlayInjectScript   // плавающие кнопки: все справа внизу: режимы, промежуток, тулбар и справка
                        .Replace("__TIP_NORMAL__", System.Text.Json.JsonSerializer.Serialize(Lang.T("mode.normal_tip")))
                        .Replace("__TIP_STREAM__", System.Text.Json.JsonSerializer.Serialize(Lang.T("mode.stream_tip")))
                        .Replace("__TIP_MANUAL__", System.Text.Json.JsonSerializer.Serialize(Lang.T("mode.manual_tip")))
                        .Replace("__TIP_AUTO__", System.Text.Json.JsonSerializer.Serialize(Lang.T("mode.auto_tip")))
                        .Replace("__LBL_MANUAL__", System.Text.Json.JsonSerializer.Serialize(Lang.T("mode.manual")))
                        .Replace("__LBL_AUTO__", System.Text.Json.JsonSerializer.Serialize(Lang.T("mode.auto"))));
                    PushStreamMode();                // подсветить кнопку сохранённого режима
                    await PopulateMicsAsync();       // список микрофонов в тулбар (метки уже доступны — разрешение выдано)
                    await PollPageReadyAsync();      // /status.ready - сразу, не дожидаясь таймера
                    if (!_firstClearDone)
                    {
                        _firstClearDone = true;
                        await Task.Delay(1500);          // дать странице осесть
                        await Exec(ClearComposerScript); // снять восстановленный из сессии черновик
                        // Справку готовим заранее, в скрытой панели: при первом «?» запуск второго WebView2 был
                        // заметной паузой (замечание пользователя 22.11.2026: «хреновенько выглядит»).
                        await Task.Delay(1000);
                        await EnsureHelpViewAsync();
                    }
                }
            };
            _web.CoreWebView2.Navigate("https://chatgpt.com/");

            StatusOverlay.Create();
            _hotkey = new Hotkey(Handle);
            bool ok = _hotkey.Install();
            Log(ok ? Lang.T("log.hotkeys_on") : Lang.T("log.hotkeys_fail"));
            Log(Lang.T("log.profile", Diag.WebViewDataDir));
            Log(Lang.T("hint.login"));
            _readyTimer.Start();
            StartFileEntry();
        }
        catch (Exception ex)
        {
            Log(Lang.T("log.webview_error"));
            Diag.Write("WebView2 init exception: " + ex);
        }
    }

    // --------------------------- Хоткеи ---------------------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WmShowExisting) { ShowFromTray(); return; } // второй экземпляр попросил показать окно
        if (m.Msg == (int)Win32.WM_APP_TOGGLE) { OnToggle(m.WParam != IntPtr.Zero); return; }
        if (m.Msg == (int)Win32.WM_APP_REPASTE) { OnRepaste(); return; }
        if (m.Msg == (int)Win32.WM_APP_MOVE) { OnMoveStream(); return; }
        base.WndProc(ref m);
    }

    private void OnToggle(bool withY)
    {
        switch (_state)
        {
            case LiveState.Idle: _ = StartDictationAsync(); break;
            case LiveState.Recording: _ = StopAndInjectAsync(withY); break;
            // В промежуточных фазах Ctrl+Win = отмена.
            case LiveState.Starting:
            case LiveState.Stopping:
                _cts?.Cancel();
                Diag.Write("отмена (Ctrl+Win в промежуточной фазе)");
                break;
            // Идёт файл куклы → вытеснение: файл обрывается (кукле preempted), сразу живая диктовка.
            case LiveState.File:
                Diag.Write("Ctrl+Win во время файла — вытеснение");
                _fileJob?.Cancel("preempted");
                _ = PreemptThenStartAsync();
                break;
        }
    }

    /// <summary>Дождаться уборки вытесненного файла (Cancel на странице, замок снят) и начать живую диктовку.</summary>
    private async Task PreemptThenStartAsync()
    {
        if (_fileTask != null) { try { await _fileTask; } catch { /* итог уже ушёл кукле */ } }
        await StartDictationAsync();
    }

    // --------------------------- Вход «файл → текст» (куклы) ---------------------------

    private void StartFileEntry()
    {
        try
        {
            string ver = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0";
            // --api-host / --api-port: по умолчанию вся сеть, порт 17891 (решение пользователя 09.11.2026).
            string? bindErr = FileEntry.ParseBind(Program.GetOption("--api-host"), Program.GetOption("--api-port"), out var bind, out int port);
            if (bindErr != null) { Log(Lang.T("log.file_entry_fail", bindErr)); return; }
            _fileEntry = new FileEntry(
                () => { try { return (FileStatus)Invoke(FileStatusNow); } catch { return new FileStatus(false, "idle", "not_ready", null); } },
                job => { try { BeginInvoke(new Action(() => SubmitFileJob(job))); } catch (Exception ex) { job.Done.TrySetResult(new FileResult(503, "not_ready", "окно граббера недоступно: " + ex.Message)); } },
                ver, bind, port);
            _fileEntry.Start();
            if (_fileEntry.AllNetworks)
                Log(Lang.T("log.file_entry_net", port, FileEntry.LocalAddresses()));
            else
                Log(Lang.T("log.file_entry", bind.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + bind + "]" : bind.ToString(), port));
        }
        catch (Exception ex)
        {
            _fileEntry = null;
            Log(Lang.T("log.file_entry_fail", ex.Message));
        }
    }

    /// <summary>Снимок для /status и замка. Только UI-поток.</summary>
    private FileStatus FileStatusNow()
    {
        string state = _state == LiveState.File ? "file" : _state == LiveState.Idle ? "idle" : "live";
        string reason = "";
        int? retry = null;
        if (state == "live") reason = "live";                                   // без retryAfterSec: длина диктовки неизвестна
        else if (state == "file") { reason = "file"; retry = SecLeft(_fileEndsAt) + 3; }   // остаток звука + распознавание
        else if (DateTime.Now < _liveQuietUntil) { reason = "pause"; retry = SecLeft(_liveQuietUntil); }
        else if (!_pageReady) reason = "not_ready";
        return new FileStatus(reason == "", state, reason, retry);
    }

    private static int SecLeft(DateTime t) => Math.Max(1, (int)Math.Ceiling((t - DateTime.Now).TotalSeconds));

    /// <summary>Опрос «кнопка Start на месте» для /status.ready - только в покое (в записи её нет по определению).</summary>
    private async Task PollPageReadyAsync()
    {
        if (_state != LiveState.Idle || _web.CoreWebView2 is null) return;
        bool ready = (await Exec(StartReadyScript)) == "ready";
        if (ready != _pageReady) Diag.Write("вход: страница " + (ready ? "готова" : "не готова (нет кнопки Start)"));
        _pageReady = ready;
    }

    /// <summary>Задание от FileEntry (UI-поток): замок «занято» или в работу.</summary>
    private void SubmitFileJob(FileJob job)
    {
        var st = FileStatusNow();
        if (st.State != "idle" || st.Reason == "pause")
        {
            string why = st.Reason switch
            {
                "live" => "идёт живая диктовка",
                "file" => "идёт другой файл",
                _ => "пауза после живой диктовки",
            };
            job.Done.TrySetResult(new FileResult(409, "busy", why, RetryAfterSec: st.RetryAfterSec));
            return;
        }
        _fileTask = RunFileAsync(job);
    }

    /// <summary>
    /// Файл → текст на странице: тот же путь, что у живой диктовки, но вместо микрофона страница получает
    /// поток из декодированного WAV (перехватчик getUserMedia, режим файла - одноразовый). Текст никуда не
    /// вставляется, пилюля не показывается, буфер и фокус не трогаются. Отмена (вытеснение, обрыв клиента)
    /// → Cancel на странице, замок снят, итог кукле.
    /// </summary>
    private async Task RunFileAsync(FileJob job)
    {
        _fileJob = job;
        _fileEndsAt = DateTime.Now.AddSeconds(job.AudioSec + 3);
        State = LiveState.File;
        if (_icoYellow != null) _tray.Icon = _icoYellow;
        var ct = job.Cts.Token;
        string tag = $"файл {job.Id}";
        var result = new FileResult(500, "internal", "внутренняя ошибка");
        Log(Lang.T("log.file_start", job.Client == "" ? "?" : job.Client, job.AudioSec.ToString("0.0", CultureInfo.InvariantCulture)));
        try
        {
            // Зависший UI диктовки (как в StartDictationAsync) - сперва закрыть.
            if ((await Exec(DictationStateScript)).Contains("\"live\":true"))
            {
                Diag.Write($"{tag}: висит UI диктовки — отбой: " + await Exec(CancelScript));
                await Task.Delay(400, ct);
            }
            if (!await PollAsync(StartReadyScript, "ready", 5, 300, ct))
            {
                string api = await ProbeBackendAsync();
                bool sessionBad = api != "200";
                Diag.Write($"{tag}: кнопка Start не появилась (backend-api/me → {api})");
                _pageReady = false;
                result = sessionBad
                    ? new FileResult(503, "session", $"сессия ChatGPT протухла (backend-api/me → {api}), нужен перелогин")
                    : new FileResult(503, "not_ready", "ChatGPT не готов: нет кнопки диктовки");
                return;
            }

            // Звук в страницу кусками base64, затем декодирование и взвод перехватчика.
            await Exec(FileResetScript);
            string b64 = Convert.ToBase64String(job.Wav);
            for (int i = 0; i < b64.Length; i += FileChunkChars)
            {
                ct.ThrowIfCancellationRequested();
                await Exec("window.__ggFile.push('" + b64.Substring(i, Math.Min(FileChunkChars, b64.Length - i)) + "')");
            }
            Diag.Write($"{tag}: {job.Wav.Length} байт передано, arm: " + await Exec(FileArmScript));
            string fs = "";
            for (int i = 0; i < 150; i++)   // декодирование 5 мин звука - около секунды; 30 с с запасом
            {
                await Task.Delay(200, ct);
                fs = await Exec(FileStateScript);
                if (fs == "armed" || fs.StartsWith("error")) break;
            }
            if (fs != "armed")
            {
                result = new FileResult(400, "bad_file", "страница не декодировала WAV: " + fs);
                return;
            }

            await Exec(ClearComposerScript);
            Diag.Write($"{tag}: Start: " + await Exec(ClickStartScript));
            if (!await PollAsync(DictationStateScript, "\"live\":true", 20, 300, ct))
            {
                Diag.Write($"{tag}: запись не пошла (нет live), file=" + await Exec(FileStateScript));
                result = new FileResult(503, "not_ready", "запись на странице не пошла");
                return;
            }
            _fileEndsAt = DateTime.Now.AddSeconds(job.AudioSec + 1);
            Diag.Write($"{tag}: караоке: " + await Exec(LiveShowScript));

            // Конец звука: флаг onended со страницы; не пришёл за длину + 5 с → стоп по времени.
            int tries = (int)(job.AudioSec * 4) + 20;
            if (!await PollAsync(FileStateScript, "ended", tries, 250, ct))
                Diag.Write($"{tag}: onended не пришёл, стоп по времени (file=" + await Exec(FileStateScript) + ")");
            Diag.Write($"{tag}: Submit: " + await Exec(SubmitScript));

            // Распознавание: обычно ~3 с. Нет текста и запись на странице не идёт 20 с → empty;
            // страница всё ещё в записи (кнопки Stop/Cancel на месте) до 60 с → timeout.
            var rec = Stopwatch.StartNew();
            string text = "";
            bool timedOut = false;
            while (true)
            {
                await Task.Delay(500, ct);
                // Текст в композере есть ещё до стопа (поток, 19.11.2026), а хвост доезжает в фазе завершения:
                // берём его, только когда страница вернулась в покой (см. ReadFinalTextAsync). Страховка от
                // застрявшего завершения: через 20 с берём то, что есть.
                string fin = await Exec(FinalStateScript);
                text = await Exec(ComposerReadScript);
                if (GoodText(text) && (fin == "idle" || (fin == "unknown" && rec.ElapsedMilliseconds >= 1500)
                                       || rec.Elapsed.TotalSeconds >= 20)) break;
                bool stillRec = (await Exec(DictationStateScript)).Contains("\"live\":true");
                if (!stillRec && rec.Elapsed.TotalSeconds >= 20) break;
                if (rec.Elapsed.TotalSeconds >= 60) { timedOut = true; break; }
            }
            if (!GoodText(text))
            {
                result = timedOut
                    ? new FileResult(504, "timeout", "распознавание не завершилось за 60 с")
                    : new FileResult(422, "empty", "текста нет (тишина?)");
                return;
            }
            result = new FileResult(200, null, null, Text: text, RecognizeMs: (int)rec.ElapsedMilliseconds);
            await Exec(ClearComposerScript);   // поле к следующей диктовке
        }
        catch (OperationCanceledException)
        {
            await Exec(CancelScript);          // закрыть UI диктовки, если открылся
            await Exec(ClearComposerScript);   // недораспознанный хвост не должен приклеиться к следующей диктовке
            result = job.CancelReason == "preempted"
                ? new FileResult(409, "preempted", "вытеснен живой диктовкой")
                : new FileResult(499, "disconnected", "клиент оборвал соединение");
            Diag.Write($"{tag}: отменён ({job.CancelReason})");
        }
        catch (Exception ex)
        {
            Diag.Write($"{tag}: ошибка: " + ex);
            result = new FileResult(500, "internal", ex.Message);
        }
        finally
        {
            await Exec(FileDisarmScript);      // перехватчик снова отдаёт настоящий микрофон
            _fileJob = null;
            State = LiveState.Idle;            // File → Idle паузу не включает (она только после живой)
            job.Done.TrySetResult(result);
            if (result.Status == 200)
                Log(Lang.T("log.file_done", result.Text?.Length ?? 0, result.RecognizeMs));
            else
                Log(Lang.T("log.file_fail", result.Error ?? "?", result.Message ?? ""));
        }
    }

    /// <param name="fromVoice">Автозапуск сторожем голоса («Поток-авто»): сторож уже взвёл передачу буфера
    /// (VoiceArmScript), его микрофон и накопленное начало фразы уходят в запись - не глушить.</param>
    private async Task StartDictationAsync(bool fromVoice = false)
    {
        State = LiveState.Starting;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // Окно, где человек был в момент хоткея: якорь пилюли и приёмник потокового режима.
        IntPtr startWin = Win32.GetForegroundWindow();
        _stream = null;
        if (!fromVoice) StopVoiceWatch();   // запись включили руками - сторож голоса больше не нужен, микрофон отпускаем
        OverlayShow(StatusOverlay.Phase.Preparing, startWin);
        try
        {
            // Страница могла остаться в режиме записи от прошлого раза (окно закрыли, программу
            // перезапустили). Кнопки «Start» в этом состоянии на странице нет вообще, и любой
            // новый старт обречён — поэтому сперва закрываем зависший UI диктовки.
            if ((await Exec(DictationStateScript)).Contains("\"live\":true"))
            {
                Diag.Write("висит UI диктовки — отбой: " + await Exec(CancelScript));
                await Task.Delay(400, ct);
            }

            // Ждём появления кнопки Start (не фиксированную паузу).
            if (!await PollAsync(StartReadyScript, "ready", 20, 300, ct))
            {
                // Два разных отказа под одной надписью — разводим их пробой служебного API:
                // 200 → страница просто не догрузилась; иначе → сессия протухла, нужен перелогин.
                string api = await ProbeBackendAsync();
                bool sessionBad = api != "200";
                Diag.Write($"кнопка Start не появилась (backend-api/me → {api})");
                OverlaySet(StatusOverlay.Phase.Error, Lang.T(sessionBad ? "err.session" : "err.not_ready"));
                if (sessionBad) Log(Lang.T("log.session_hint", api));
                State = LiveState.Idle;
                return;
            }

            await Exec(ClearComposerScript); // черновик не должен приклеиться к диктовке
            Diag.Write("Start: " + await Exec(ClickStartScript));
            OverlaySet(StatusOverlay.Phase.Starting);

            if (!await PollAsync(DictationStateScript, "\"live\":true", 20, 300, ct))
            {
                Diag.Write("запись не пошла (нет live)");
                OverlaySet(StatusOverlay.Phase.Error, Lang.T("err.no_recording"));
                State = LiveState.Idle;
                return;
            }

            State = LiveState.Recording;
            OverlaySet(StatusOverlay.Phase.Recording);
            Diag.Write("micdiag: " + await Exec(MicDiagScript));   // факты о перехвате getUserMedia - только в файл журнала
            Diag.Write("караоке: " + await Exec(LiveShowScript));
            if (fromVoice)
            {
                // Подхватила ли страница наш поток с буфером (перехватчик getUserMedia → handover сторожа).
                string h = await Exec(VoiceHandedScript);
                Diag.Write("буфер: " + h);
                if (!h.StartsWith("handed")) ReleaseVoice();   // запись идёт с обычного микрофона - сторожу держать нечего
            }
            StartStream(startWin);
        }
        catch (OperationCanceledException)
        {
            await Exec(CancelScript); // закрыть UI диктовки, если уже открылся
            OverlaySet(StatusOverlay.Phase.Error, Lang.T("err.cancelled"));
            State = LiveState.Idle;
        }
        catch (Exception ex)
        {
            Diag.Write("старт ошибка: " + ex.Message);
            OverlaySet(StatusOverlay.Phase.Error, Lang.T("err.start_failed"));
            State = LiveState.Idle;
        }
        finally
        {
            if (fromVoice && _state != LiveState.Recording) ReleaseVoice();   // автозапуск сорвался - микрофон сторожа отпустить
        }
    }

    /// <param name="auto">Автостоп по тишине (поток), а не Ctrl+Win: приёмник силой не поднимаем.</param>
    private async Task StopAndInjectAsync(bool withY, bool auto = false)
    {
        State = LiveState.Stopping;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // Окно-цель = где сейчас работает пользователь; туда же якорим пилюлю.
        // В потоковом режиме цель - приёмник, выбранный на старте (часть текста уже в нём).
        var stream = _stream;
        IntPtr target = Win32.GetForegroundWindow();
        OverlayShow(StatusOverlay.Phase.Transcribing, target);
        string? fullForClipboard = null;   // поток: текст, который надо оставить в буфере (Ctrl+Win+Y или расхождение)
        try
        {
            await DrainVoiceBufferAsync(ct);   // запись с буфером отстаёт от живого голоса - конец фразы не обрезать
            Diag.Write("Submit: " + await Exec(SubmitScript));
            Diag.Write("micdiag@stop: " + await Exec(MicDiagScript));   // пиковый уровень за время записи - только в файл журнала

            string text = await ReadFinalTextAsync(ct);

            if (GoodText(text))
            {
                _lastText = text; // запоминаем для повторной вставки (Win+Alt)
                bool ok;
                string? errText = null;
                if (stream is null)
                {
                    ok = Injector.Inject(text, target, withY);
                    if (!ok) errText = Lang.T("err.paste_failed");
                    Diag.Write($"inject ok={ok} window=«{Win32.GetWindowTitle(target)}»" + (withY ? " (+buffer)" : ""));
                }
                else
                {
                    ok = await StreamFinishAsync(stream, text, ct, force: !auto);
                    if (stream.Broken || withY) fullForClipboard = text;
                    // Автостоп, а человек в другом окне: хвост силой не вставляем (не выдёргиваем фокус) - кладём в буфер.
                    else if (!ok && auto) fullForClipboard = text[stream.Sent.Length..];
                    if (!ok) errText = Lang.T(stream.Broken || auto ? "err.stream_broken" : "err.paste_failed");
                    Diag.Write($"поток: финал ok={ok} отдано {stream.Sent.Length} из {text.Length} window=«{Win32.GetWindowTitle(stream.Target)}»"
                        + (stream.Broken ? " (расхождение, текст в буфер)" : withY ? " (+buffer)" : ""));
                }
                Win32.FeedbackBeep(ok);
                OverlaySet(ok ? StatusOverlay.Phase.Done : StatusOverlay.Phase.Error, errText);
                Log(Lang.T("log.result", text));   // полный текст; обрезку делает само окно по правому краю
            }
            else
            {
                if (auto) Win32.FeedbackBeep(false);   // автостоп без текста: человек не нажимал стоп - пусть услышит, что запись закрылась
                OverlaySet(StatusOverlay.Phase.Error, Lang.T("err.empty"));
                Diag.Write("пусто (текста нет)");
            }

            await Exec(ClearComposerScript); // поле к следующей диктовке
        }
        catch (OperationCanceledException)
        {
            await Exec(ClearComposerScript); // текст не вставляем, но чистим — чтоб не приклеился
            OverlaySet(StatusOverlay.Phase.Error, Lang.T("err.cancelled"));
            Diag.Write("вставка отменена");
        }
        catch (Exception ex)
        {
            Diag.Write("стоп ошибка: " + ex.Message);
            OverlaySet(StatusOverlay.Phase.Error, Lang.T("err.generic"));
        }
        finally
        {
            if (stream is not null) await StreamCloseAsync(stream, fullForClipboard);
            _stream = null;
            ReleaseVoice();   // микрофон сторожа, если запись шла с буфером
            State = LiveState.Idle;
            // Окно сторожа после остановки записи (решение пользователя 21.11.2026): в «Потоке» - напоминание
            // голосом про выключенную запись; в «Потоке-авто» - автозапуск, но только после автостопа:
            // ручной Ctrl+Win-стоп автозапуск усыпляет до следующего ручного старта.
            if (_mic.Stream && (!_mic.StreamAuto || auto)) StartVoiceWatch();
        }
    }

    // --------------------------- Сторож голоса ---------------------------
    // После остановки записи в режимах потока страница VoiceWatchSec секунд слушает микрофон сама (оригинальный
    // getUserMedia, мимо ChatGPT; на сервер ничего не уходит). Услышала голос - шлёт gg:voice:<уровень>:<порог>
    // раз в ~0,25 с, пока он слышен. Дальше по режиму (решения пользователя 21.11.2026):
    //  - «Поток»: первое сообщение эпизода = пик + жёлтая пилюля «Запись выключена · Ctrl+Win»; следующие её
    //    держат, без голоса она гаснет сама через 3 с. Пик через колонки попадает в микрофон уже внутри эпизода.
    //  - «Поток-авто»: голос держится AutoStartVoiceSec - запись включается сама, как от Ctrl+Win, в окно,
    //    активное в этот момент. Чтобы начало фразы не потерялось (детект + подтверждение + 0,6-0,9 с до сервера),
    //    сторож держит кольцевой буфер последних секунд звука и отдаёт записи свой поток: сперва звук с начала
    //    фразы, затем живой; отставание съедается пропуском пауз длиннее 0,3 с (VoiceWatchScript, handover).
    // После VoiceWatchSec сторож засыпает до ручного Ctrl+Win. Ручной старт сторожа снимает.

    private const int VoiceWatchSec = 300;               // окно сторожа 5 мин (решение пользователя 21.11.2026)
    private const double AutoStartVoiceSec = 1.0;        // «буквально через секунду» голоса - автозапуск
    private DateTime _voiceFirst = DateTime.MinValue;    // начало текущего эпизода голоса (для автозапуска)
    private DateTime _voiceLast = DateTime.MinValue;     // последнее сообщение голоса

    private void StartVoiceWatch()
    {
        _voiceFirst = _voiceLast = DateTime.MinValue;
        Log(Lang.T(_mic.StreamAuto ? "log.voice_watch_auto" : "log.voice_watch", VoiceWatchSec / 60));
        _ = Exec(VoiceWatchScript.Replace("__MS__", (VoiceWatchSec * 1000).ToString()));
    }

    /// <summary>Снять сторожа: слушать перестаёт; микрофон отпускает, если запись не взяла его поток.</summary>
    private void StopVoiceWatch() => _ = Exec("window.__ggVoiceStop && window.__ggVoiceStop()");

    /// <summary>Освободить всё сторожево: поток микрофона, аудиоконтекст, буфер (конец записи с буфером).</summary>
    private void ReleaseVoice() => _ = Exec("window.__ggVoiceRelease && window.__ggVoiceRelease()");

    private void OnVoiceMessage(string msg)
    {
        // Итог прослушивания (сторож закончил): максимум уровня и шум - в журнал, по ним подбирается порог.
        if (msg.StartsWith("gg:voice:end") || msg.StartsWith("gg:voice:err")) { Diag.Write("сторож голоса: " + msg); return; }
        if (_state != LiveState.Idle) return;
        if (!_mic.Stream) { StopVoiceWatch(); return; }   // режим сменили на «Обычный» - сторож не нужен

        if (_mic.StreamAuto)
        {
            // Эпизод голоса: сообщения идут подряд с шагом ~0,25 с; разрыв больше 0,8 с - эпизод начинается заново.
            var now = DateTime.Now;
            if ((now - _voiceLast).TotalSeconds > 0.8) _voiceFirst = now;
            _voiceLast = now;
            if ((now - _voiceFirst).TotalSeconds < AutoStartVoiceSec - 0.05) return;
            _voiceFirst = _voiceLast = DateTime.MinValue;
            Diag.Write("сторож голоса: автозапуск, " + msg);
            Log(Lang.T("log.voice_autostart"));
            // Порядок держит очередь ExecuteScriptAsync: передача буфера взводится раньше, чем старт кликнет «Диктовать».
            _ = Exec(VoiceArmScript);
            _ = StartDictationAsync(fromVoice: true);
            return;
        }

        if (StatusOverlay.WarnVisible) { StatusOverlay.KeepWarn(); return; }
        Diag.Write("сторож голоса: голос, " + msg);
        Win32.VoiceBeep();
        OverlayShow(StatusOverlay.Phase.Warn, Win32.GetForegroundWindow());
    }

    /// <summary>
    /// Перед Submit: запись с буфером отстаёт от живого голоса, пока не съедены паузы. Ждём, пока страница
    /// получит хвост (отставание ≈ 0), не дольше 5 с - иначе последние слова отрезал бы стоп.
    /// </summary>
    private async Task DrainVoiceBufferAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double lag0 = -1;
        while (sw.Elapsed.TotalSeconds < 5)
        {
            string v = await Exec(VoiceLagScript);
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double lag) || lag <= 0.06) break;
            if (lag0 < 0) lag0 = lag;
            await Task.Delay(100, ct);
        }
        if (lag0 > 0) Diag.Write($"буфер: доигран хвост {lag0:0.00} с за {sw.ElapsedMilliseconds} мс");
    }

    // Взвести передачу: следующий getUserMedia страницы получит поток сторожа (буфер с начала фразы + живой звук).
    private const string VoiceArmScript = """
(function () {
  var V = window.__ggVoice;
  if (!V || !V.handover || V.handed) return 'none';
  V.armed = true;
  return 'armed';
})()
""";

    // Чем кончилась передача: handed (страница взяла поток сторожа: сколько секунд буфера, с какого момента) или нет.
    private const string VoiceHandedScript = """
(function () {
  var V = window.__ggVoice;
  if (!V) return 'none';
  if (V.handed) return 'handed buf=' + V.handBuf.toFixed(2) + 's lag=' + V.lag().toFixed(2) + 's';
  return V.armed ? 'armed, но страница поток не взяла' : 'не взведён';
})()
""";

    // Отставание записи от живого звука, секунды (0 - буфера нет или догнали).
    private const string VoiceLagScript = """
(function () {
  var V = window.__ggVoice;
  return (V && V.handed && V.lag) ? String(V.lag()) : '0';
})()
""";

    // Сторож в странице. Звук идёт через ScriptProcessor блоками по 2048 отсчётов (~43 мс при 48 кГц); каждый блок -
    // в кольцевой буфер (RING_SEC) и в детект. ScriptProcessor, а не AudioWorklet: модуль ворклета грузится по адресу
    // (blob/data), и CSP страницы ChatGPT может его не пустить; ScriptProcessor живёт прямо в нашем скрипте.
    // Детект: уровень - RMS сырого блока (без шумодава и АРУ). Первые 0,7 с пропускаем (в них попадает наш же сигнал
    // вставки), следующие 0,6 с слушаем шум комнаты; порог = max(0.003, шум × 3), шум в расчёте не выше 0.004 (если
    // человек заговорил прямо во время замера), дальше медленно подстраивается по тихим блокам. Голос = порог превышен
    // в 3 блоках подряд с допуском (~0,15 с). Замер 21.11.2026 на микрофоне пользователя (усиление выключено): шум
    // 0.0011-0.0014, голос 0.006-0.022; первая нижняя граница 0.012 была выше тихого голоса.
    // Передача (handover, «Поток-авто»): C# взводит V.armed, перехватчик getUserMedia (MicBoostTemplate) зовёт
    // V.handover() и отдаёт странице наш поток. В него идёт очередь: буфер с начала эпизода голоса минус PREROLL_SEC,
    // дальше живые блоки. Первые START_DELAY_MS - тишина (рекордер ChatGPT берёт поток не мгновенно, как у входа
    // «файл → текст»). Пока очередь длиннее одного блока (отставание), тихие блоки сверх SKIP_SEC подряд выкидываются -
    // так запись догоняет живой голос на паузах. Усиление из тулбара применяется множителем (без лимитера).
    // Освобождение - __ggVoiceRelease (конец записи, новый сторож); __ggVoiceStop только снимает детект и отпускает
    // микрофон, если поток не взведён и не передан.
    private const string VoiceWatchScript = """
(function () {
  if (window.__ggVoiceRelease) window.__ggVoiceRelease();
  var orig = window.__ggMicOrig;
  function post(s) { try { window.chrome.webview.postMessage(s); } catch (e) {} }
  if (!orig) { post('gg:voice:err:no-orig'); return 'no-orig'; }
  var BLOCK = 2048, RING_SEC = 15, PREROLL_SEC = 0.5, SKIP_SEC = 0.3, START_DELAY_MS = 600, EPISODE_GAP_SEC = 1.5;
  var V = { detect: true, armed: false, handed: false, ring: [], queue: null, n: 0, rate: 48000,
            floor: 0, floorN: 0, maxRms: 0, hot: 0, thr: 0.003, runStart: -1, voiceFrom: -1, lastHotN: -1e9,
            lastPost: 0, quiet: 0, skipped: 0, drainAt: 0, handBuf: 0 };
  window.__ggVoice = V;
  var stream = null, ctx = null, proc = null, timer = null, released = false;
  function endDetect() {
    if (!V.detect) return;
    V.detect = false;
    if (timer) { clearTimeout(timer); timer = null; }
    post('gg:voice:end:max=' + V.maxRms.toFixed(4) + ':floor=' + V.floor.toFixed(4) + ':skipped=' + V.skipped);
  }
  function release() {
    if (released) return;
    released = true;
    endDetect();
    V.armed = false; V.handed = false; V.queue = null; V.ring = [];
    if (window.__ggVoice === V) { window.__ggVoice = null; window.__ggVoiceStop = null; window.__ggVoiceRelease = null; }
    try { if (proc) proc.onaudioprocess = null; } catch (e) {}
    try { if (stream) stream.getTracks().forEach(function (t) { t.stop(); }); } catch (e) {}
    try { if (ctx) ctx.close(); } catch (e) {}
  }
  window.__ggVoiceRelease = release;
  window.__ggVoiceStop = function () { endDetect(); if (!V.armed && !V.handed) release(); };
  timer = setTimeout(function () { timer = null; if (!V.armed && !V.handed) release(); else endDetect(); }, __MS__);
  V.lag = function () { return V.queue ? V.queue.length * BLOCK / V.rate : 0; };
  V.handover = function () {
    if (!ctx || !proc || released) throw new Error('watch not ready');
    var from = V.voiceFrom >= 0 ? V.voiceFrom - Math.round(PREROLL_SEC * V.rate / BLOCK) : V.n - Math.round(V.rate / BLOCK);
    V.queue = V.ring.filter(function (b) { return b.n >= from; });
    V.ring = [];
    V.handBuf = V.queue.length * BLOCK / V.rate;
    V.armed = false; V.handed = true;
    endDetect();
    V.drainAt = Date.now() + START_DELAY_MS;
    var dest = ctx.createMediaStreamDestination();
    proc.connect(dest);
    return dest.stream;
  };
  var a = { echoCancellation: false, noiseSuppression: false, autoGainControl: false };
  if (window.__ggMic && window.__ggMic.deviceId) a.deviceId = { exact: window.__ggMic.deviceId };
  orig({ audio: a }).catch(function () { return orig({ audio: true }); }).then(function (s) {
    if (released) { s.getTracks().forEach(function (t) { t.stop(); }); return; }
    stream = s;
    ctx = new (window.AudioContext || window.webkitAudioContext)();
    try { ctx.resume(); } catch (e) {}
    V.rate = ctx.sampleRate;
    var ringMax = Math.ceil(RING_SEC * V.rate / BLOCK), gapN = Math.round(EPISODE_GAP_SEC * V.rate / BLOCK),
        skipN = Math.round(SKIP_SEC * V.rate / BLOCK);
    var src = ctx.createMediaStreamSource(s);
    proc = ctx.createScriptProcessor(BLOCK, 1, 1);
    var mute = ctx.createGain(); mute.gain.value = 0;   // ScriptProcessor работает, только пока его выход кто-то тянет
    src.connect(proc); proc.connect(mute); mute.connect(ctx.destination);
    proc.onaudioprocess = function (e) {
      var inp = e.inputBuffer.getChannelData(0), out = e.outputBuffer.getChannelData(0);
      var d = new Float32Array(inp), sum = 0;
      for (var i = 0; i < d.length; i++) sum += d[i] * d[i];
      var rms = Math.sqrt(sum / d.length), blk = { n: V.n++, d: d, rms: rms };
      if (V.handed) {
        V.queue.push(blk);
        if (Date.now() < V.drainAt) { out.fill(0); return; }
        var b;
        for (;;) {
          b = V.queue.shift();
          if (b.rms < V.thr) V.quiet++; else V.quiet = 0;
          if (V.quiet > skipN && V.queue.length > 0) { V.skipped++; continue; }
          break;
        }
        var m = window.__ggMic, g = (m && m.enabled && m.gain) ? m.gain : 1;
        for (var j = 0; j < out.length; j++) { var v = b.d[j] * g; out[j] = v > 1 ? 1 : (v < -1 ? -1 : v); }
        return;
      }
      out.fill(0);
      V.ring.push(blk);
      if (V.ring.length > ringMax) V.ring.shift();
      if (!V.detect || V.armed) return;
      var age = blk.n * BLOCK / V.rate;
      if (age < 0.7) return;
      if (age < 1.3) { V.floor = (V.floor * V.floorN + rms) / (V.floorN + 1); V.floorN++; return; }
      if (rms > V.maxRms) V.maxRms = rms;
      V.thr = Math.max(0.003, Math.min(V.floor, 0.004) * 3);
      if (rms > V.thr) { if (V.hot === 0) V.runStart = blk.n; V.hot = Math.min(6, V.hot + 1); }
      else { V.hot = Math.max(0, V.hot - 1); V.floor = V.floor * 0.98 + rms * 0.02; }
      if (V.hot >= 3) {
        // Начало эпизода голоса - отсюда (минус PREROLL_SEC) запись получит буфер; пауза больше EPISODE_GAP_SEC - новый эпизод.
        if (V.voiceFrom < 0 || blk.n - V.lastHotN > gapN) V.voiceFrom = V.runStart;
        V.lastHotN = blk.n;
        var now = Date.now();
        if (now - V.lastPost >= 250) { V.lastPost = now; post('gg:voice:' + rms.toFixed(4) + ':' + V.thr.toFixed(4)); }
      }
    };
  }).catch(function (e) { post('gg:voice:err:' + (e && e.name || e)); });
  return 'ok';
})()
""";

    /// <summary>
    /// Итоговый текст после Submit. С 19.11.2026 ChatGPT распознаёт потоком: слова идут в композер во время
    /// записи, а хвост доезжает уже ПОСЛЕ стопа, в короткой фазе «Finishing dictation» (~0,6 с). Брать первый
    /// непустой текст, как раньше, нельзя - он есть сразу, и последние слова терялись бы. Ждём, пока страница
    /// вернётся в покой, и читаем после этого. Покой с пустым композером = тишина, сразу «пусто».
    /// Подписи не узнаны ('unknown') - по времени: непустой текст, но не раньше 1,5 с после стопа.
    /// </summary>
    private async Task<string> ReadFinalTextAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string text = "";
        while (sw.Elapsed.TotalSeconds < 20)
        {
            await Task.Delay(250, ct);
            string st = await Exec(FinalStateScript);
            text = await Exec(ComposerReadScript);   // читаем ПОСЛЕ состояния: покой уже наступил - текст окончательный
            if (st == "idle") break;
            if (st == "unknown" && GoodText(text) && sw.ElapsedMilliseconds >= 1500) break;
        }
        return text;
    }

    // --------------------------- Потоковый режим ---------------------------
    // Режим выбирается кнопками на странице (справа внизу): «Обычный» / «Поток»; у «Потока» по клику всплывает выбор
    // «Поток» (ручной) или «Поток-авто». Пока идёт запись, раз в StreamPollMs читаем композер и отдаём в приёмник
    // (окно, активное в момент хоткея) всё новое. Ctrl+Win заканчивает запись и досылает хвост.
    // Опора (исследование 21.11.2026, ~4900 ревизий): сервер только дописывает текст, уже выданное не правит.
    // Страховка на случай, если начнёт править: расхождение с отданным → поток молчит до конца диктовки,
    // на стопе весь текст кладётся в буфер. Подмены текста в чужом окне нет.

    private const int StreamPollMs = 500;        // порции «раз в ~0,5 с» (решение пользователя 21.11.2026)
    private const int StreamPasteGapMs = 200;    // минимум между вставками: приёмник должен успеть прочитать буфер
    // Автостоп: столько секунд без новых слов - запись заканчивается сама, как от Ctrl+Win. В потоке текст уже
    // на месте, и про стоп забывают: микрофон слушал бы дальше, а следующее Ctrl+Win «на старт» останавливало бы
    // забытую запись (так и было на проверке 21.11.2026). Тишина меряется по тексту композера, не по звуку:
    // шум в комнате не мешает. Отсчёт идёт и до первого слова.
    private const int StreamSilenceSec = 6;      // было 10 (решение пользователя 22.11.2026: «сократим с десяти секунд до шести»)
    private const int StreamWarnSec = 2;         // после стольких секунд тишины - кружок отсчёта в пилюле
    private const int StreamBeepEverySec = 2;    // пик отсчёта раз в 2 с (решение пользователя 22.11.2026: «раз в две секунды, чуть пореже»)
    // Автостоп и отсчёт - только в «Потоке-авто». В ручном «Потоке» вместо них напоминание раз в StreamRemindSec тишины.
    private const int StreamRemindSec = 5;

    private sealed class StreamSession
    {
        public IntPtr Target;                    // окно-приёмник
        public string Sent = "";                 // что уже вставлено: точное начало текста композера
        public bool Broken;                      // уже вставленное изменилось на странице
        public bool ClipTouched;                 // мы уже клали порцию в буфер
        public string? PrevClip;                 // текст буфера до первой порции - вернуть в конце
        public DateTime LastPaste = DateTime.MinValue;
        public string LastText = "";             // текст композера на прошлом чтении - для отсчёта тишины
        public DateTime LastChange = DateTime.Now;   // когда текст композера менялся в последний раз (или старт потока)
        public int Count;                            // цифра отсчёта тишины, на которую уже прозвучали пики (0 - отсчёта нет)
    }
    private StreamSession? _stream;

    /// <summary>Запустить поток для начавшейся записи, если режим включён.</summary>
    private void StartStream(IntPtr startWin)
    {
        if (!_mic.Stream || _state != LiveState.Recording) return;
        // Приёмник - наше собственное окно: вставка попала бы в композер ChatGPT, из которого мы же читаем текст.
        Win32.GetWindowThreadProcessId(startWin, out uint pid);
        if (startWin == IntPtr.Zero || pid == (uint)Environment.ProcessId)
        {
            Log(Lang.T("log.stream_own"));
            return;
        }
        _stream = new StreamSession { Target = startWin };
        Log(Lang.T("log.stream_start", Win32.GetWindowTitle(startWin)));
        _ = StreamLoopAsync(_stream);
    }

    private async Task StreamLoopAsync(StreamSession s)
    {
        try
        {
            while (true)
            {
                await Task.Delay(StreamPollMs);
                if (_state != LiveState.Recording || !ReferenceEquals(_stream, s) || s.Broken) break;
                string text = await Exec(ComposerReadScript);
                // Пока читали страницу, мог прийти стоп - тогда хвост дошлёт StopAndInjectAsync, не мы.
                if (_state != LiveState.Recording || !ReferenceEquals(_stream, s)) break;
                StreamPush(s, text, final: false);

                if (GoodText(text) && text != s.LastText)
                {
                    s.LastText = text; s.LastChange = DateTime.Now;
                    s.Count = 0;
                    StatusOverlay.SetCountdown(0);   // заговорил снова - кружок отсчёта убираем
                }
                else if (!_mic.StreamAuto)
                {
                    // Ручной «Поток»: запись не кончается никогда; в тишине - кружок без цифры и пик-пик раз в
                    // StreamRemindSec (решение пользователя 21.11.2026: «напоминалка… раз в 5 секунд»).
                    double silence = (DateTime.Now - s.LastChange).TotalSeconds;
                    if (silence >= StreamRemindSec)
                    {
                        int n = (int)(silence / StreamRemindSec);
                        if (n != s.Count) { s.Count = n; Win32.CountBeep(); }
                        StatusOverlay.SetCountdown(StatusOverlay.CountdownDot);
                    }
                }
                else
                {
                    double silence = (DateTime.Now - s.LastChange).TotalSeconds;
                    if (silence >= StreamSilenceSec)
                    {
                        Log(Lang.T("log.stream_autostop", StreamSilenceSec));
                        _ = StopAndInjectAsync(withY: false, auto: true);
                        break;
                    }
                    if (silence >= StreamWarnSec)
                    {
                        // Кружок с остатком секунд до автостопа: 4…1; одинарный пик раз в StreamBeepEverySec - на 4 и 2
                        // (решения пользователя: 21.11.2026 «пикать всё время, что идёт таймер», «не двойные, а одинарные»;
                        // 22.11.2026 «раз в две секунды»).
                        int left = (int)Math.Ceiling(StreamSilenceSec - silence);
                        if (left != s.Count)
                        {
                            s.Count = left;
                            if ((StreamSilenceSec - left) % StreamBeepEverySec == 0) Win32.CountBeep();
                        }
                        StatusOverlay.SetCountdown(left);
                    }
                }
            }
        }
        catch (Exception ex) { Diag.Write("поток: ошибка цикла: " + ex.Message); }
    }

    /// <summary>
    /// Отдать в приёмник то, чего в нём ещё нет: приращение текста композера с прошлой порции.
    /// Без force - только в активный приёмник и не при зажатой клавише; force (стоп по Ctrl+Win) - приёмник поднимаем.
    /// false - порция не ушла (нечего слать, приёмник не активен, зажата клавиша, буфер занят, расхождение).
    /// </summary>
    private bool StreamPush(StreamSession s, string text, bool final, bool force = false)
    {
        if (s.Broken || !GoodText(text)) return false;
        if (!text.StartsWith(s.Sent, StringComparison.Ordinal))
        {
            // Композер на миг короче отданного (перерисовка) - не новость, ждём следующего чтения.
            if (!final && s.Sent.StartsWith(text, StringComparison.Ordinal)) return false;
            s.Broken = true;
            Log(Lang.T("log.stream_broken"));
            Diag.Write($"поток: расхождение: отдано «…{Tail(s.Sent)}», на странице «…{Tail(text.Length > s.Sent.Length ? text[..s.Sent.Length] : text)}»");
            return false;
        }

        // Отдаём всё, что есть, вместе с последним словом (проверка пользователя 21.11.2026: «последнее слово не
        // переносится», оно ждало следующего пробела). Слово может прийти кусками («Ев» → «Еврова»), но сервер
        // только дописывает, и кусок с продолжением склеиваются в приёмнике сами. Пробел перед словом приходит
        // вместе со словом: текст композера читается обрезанным по краям.
        int end = text.Length;
        if (end == s.Sent.Length) return final;                        // нового нет; на стопе это «досылать нечего»
        string delta = text[s.Sent.Length..end];

        if (!force)
        {
            // Человек ушёл в другое окно - копим, вставим, когда вернётся (решение пользователя 21.11.2026).
            if (Win32.GetForegroundWindow() != s.Target) return false;
            if (Injector.ModifierDown()) return false;
        }
        if (!Injector.PasteChunk(delta, s.Target, force, out string? prev))
        {
            Diag.Write("поток: порция не ушла (буфер занят или окна нет)");
            return false;
        }
        if (!s.ClipTouched) { s.ClipTouched = true; s.PrevClip = prev; }
        // Каждая порция - в файл журнала (не в окно): что и куда ушло. Нужна для разбора жалоб на лишний текст.
        Diag.Write($"поток: порция {s.Sent.Length}..{end}{(final ? " (хвост)" : "")} → «{Trunc(Win32.GetWindowTitle(s.Target))}»: «{delta}»");
        s.Sent = text[..end];
        s.LastPaste = DateTime.Now;
        return true;
    }

    /// <summary>Стоп в потоковом режиме: дослать хвост итогового текста в приёмник.</summary>
    /// <param name="force">Поднять приёмник (стоп по Ctrl+Win). false - автостоп: вставляем, только если приёмник
    /// и так активен; зажатую клавишу пережидаем до полутора секунд.</param>
    private async Task<bool> StreamFinishAsync(StreamSession s, string text, CancellationToken ct, bool force)
    {
        // Последняя порция могла уйти только что - дать приёмнику прочитать буфер, прежде чем класть в него хвост.
        int wait = StreamPasteGapMs - (int)(DateTime.Now - s.LastPaste).TotalMilliseconds;
        if (wait > 0) await Task.Delay(wait, ct);
        for (int i = 0; !force && i < 15 && Injector.ModifierDown(); i++) await Task.Delay(100, ct);
        bool ok = StreamPush(s, text, final: true, force: force);
        return ok && !s.Broken;
    }

    /// <summary>Конец потока (вставка, отмена, ошибка): вернуть буфер обмена. keep задан - оставить в буфере его
    /// (Ctrl+Win+Y или расхождение), иначе - то, что лежало там до первой порции.</summary>
    private async Task StreamCloseAsync(StreamSession s, string? keep)
    {
        try
        {
            if (keep is null && !(s.ClipTouched && s.PrevClip is not null)) return;
            int wait = 300 - (int)(DateTime.Now - s.LastPaste).TotalMilliseconds;   // как ClipboardRestoreDelayMs у обычной вставки
            if (wait > 0) await Task.Delay(wait);
            Clip.SetUnicodeText(keep ?? s.PrevClip!, out _);
        }
        catch (Exception ex) { Diag.Write("поток: возврат буфера: " + ex.Message); }
    }

    private static string Tail(string t) => t.Length > 40 ? t[^40..] : t;

    /// <summary>Кнопка режима на странице: «Обычный», «Поток» (ручной) или «Поток-авто». Поток/обычный - со
    /// следующей диктовки; ручной/авто внутри потока - сразу (отсчёт и автостоп читаются на каждом шаге цикла).</summary>
    private void SetStreamMode(bool stream, bool auto)
    {
        _mic.Stream = stream;   // в файл не пишется: следующий запуск снова в обычном режиме
        _mic.StreamAuto = stream && auto;
        PushStreamMode();
        if (!stream && _state == LiveState.Idle) StopVoiceWatch();   // «Обычный» - сторож не нужен
        Diag.Write("режим: " + (!stream ? "обычный" : auto ? "поток-авто" : "поток"));
    }

    /// <summary>Подсветить на странице кнопку текущего режима.</summary>
    private void PushStreamMode() =>
        _ = Exec("window.__ggSetMode && window.__ggSetMode(" + (_mic.Stream ? "true" : "false") + ", " + (_mic.StreamAuto ? "true" : "false") + ")");

    /// <summary>
    /// Shift+Win во время потоковой записи: приёмником становится окно, где человек сейчас. Уже вставленное
    /// остаётся в прежнем окне, продолжение (и накопленное за время перехода) идёт в новое - ближайшим тиком
    /// цикла. Пилюля перескакивает к новому окну: видимый знак, что перенос сработал.
    /// </summary>
    private void OnMoveStream()
    {
        if (_state != LiveState.Recording || !_mic.Stream) return;
        IntPtr win = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(win, out uint pid);
        if (win == IntPtr.Zero || pid == (uint)Environment.ProcessId)
        {
            Win32.FeedbackBeep(false);   // в собственное окно нельзя: вставка попала бы в композер ChatGPT
            Diag.Write("перенос потока: отказ (окно GPT Grabber)");
            return;
        }
        if (_stream is null)
        {
            // Запись начата из окна GPT Grabber - потока не было; перенос его заводит, всё накопленное уйдёт сюда.
            _stream = new StreamSession { Target = win };
            _ = StreamLoopAsync(_stream);
        }
        else if (_stream.Target == win) return;
        else _stream.Target = win;
        OverlayShow(StatusOverlay.Phase.Recording, win);
        Log(Lang.T("log.stream_moved", Win32.GetWindowTitle(win)));
    }

    /// <summary>Повторная вставка последнего текста (Win+Alt) — в текущее активное окно.</summary>
    private void OnRepaste()
    {
        IntPtr target = Win32.GetForegroundWindow();
        if (string.IsNullOrEmpty(_lastText))
        {
            Win32.FeedbackBeep(false);
            StatusOverlay.Show(StatusOverlay.Phase.Error, target);
            StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("err.no_text"));
            Diag.Write("повтор: пусто (ещё не было диктовки)");
            return;
        }
        // Модификаторы уже отпущены (жест ловится на release) → синтез Ctrl+V чистый.
        bool ok = Injector.Inject(_lastText, target, keepInClipboard: false);
        Win32.FeedbackBeep(ok);
        StatusOverlay.Show(ok ? StatusOverlay.Phase.Done : StatusOverlay.Phase.Error, target);
        if (!ok) StatusOverlay.Set(StatusOverlay.Phase.Error, Lang.T("err.not_pasted"));
        Diag.Write($"повтор → «{Trunc(_lastText)}» ok={ok} в «{Win32.GetWindowTitle(target)}»");
    }

    // --------------------------- Вспомогательное ---------------------------

    /// <summary>Опрашивать скрипт, пока в ответе не встретится needle (или таймаут). Уважает отмену.</summary>
    private async Task<bool> PollAsync(string script, string needle, int tries, int delayMs, CancellationToken ct)
    {
        for (int i = 0; i < tries; i++)
        {
            if ((await Exec(script)).Contains(needle)) return true;
            await Task.Delay(delayMs, ct); // бросит OperationCanceledException при отмене
        }
        return false;
    }

    private async Task<string> Exec(string script)
    {
        if (_web.CoreWebView2 is null) return "(WebView2 ещё не готов)";
        try { return TryUnwrap(await _web.CoreWebView2.ExecuteScriptAsync(script)); }
        catch (Exception ex) { return "JS ошибка: " + ex.Message; }
    }

    private static string TryUnwrap(string rawJson)
    {
        if (string.IsNullOrEmpty(rawJson) || rawJson == "null") return rawJson ?? "null";
        try { return System.Text.Json.JsonSerializer.Deserialize<string>(rawJson) ?? rawJson; }
        catch { return rawJson; }
    }

    private static bool GoodText(string t) =>
        !string.IsNullOrWhiteSpace(t) && !t.StartsWith("(") && !t.StartsWith("JS ошибка");

    private static string Trunc(string t) => t.Length > 60 ? t[..60] + "…" : t;

    private void Log(string msg)
    {
        Diag.Write(msg);
        if (InvokeRequired) { BeginInvoke(new Action(() => AppendLog(msg))); return; }
        AppendLog(msg);
    }

    private void AppendLog(string msg) =>
        _log.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");

    /// <summary>Тулбар управления микрофоном над логом: галочки «Усиление»/«Шумоподавление»
    /// и ползунок громкости. Есть задел под будущие кнопки (фильтры и пр.).</summary>
    private Control BuildToolbar()
    {
        // Тулбар — TableLayoutPanel: один ряд, авто-колонки, всё центрируется по вертикали (Anchor=None).
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Palette.Bg,
            Padding = new Padding(10, 0, 10, 0),
            ColumnCount = 5,
            RowCount = 1,
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Колонка-распорка справа: забирает всё лишнее место окна, иначе последняя AutoSize-колонка
        // (блок громкости) раздувалась и центрировала панель → большой зазор после RF.
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        const AnchorStyles mid = AnchorStyles.None;   // центр по вертикали в своей ячейке
        var gap = new Padding(0, 0, 4, 0);            // тесный зазор между контролами
        var gapBig = new Padding(0, 0, 8, 0);         // небольшой зазор RF → блок громкости (группа сразу за RF)

        // 1. Мастер вкл/выкл обработки — MaterialSwitch; пояснение — во всплывающей подсказке.
        var master = new MaterialSwitch { Text = "", Checked = _mic.Enabled, AutoSize = true, Anchor = mid, Margin = gap };
        _tips.SetToolTip(master, Lang.T("toolbar.enable_tip"));

        // 2. Селектор микрофона (без заголовка).
        _micCombo = new SmallComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Anchor = mid, Margin = gap };
        _micCombo.Items.Add(new MicDevice("", Lang.T("toolbar.device_default")));
        _micCombo.SelectedIndex = 0;
        _micCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_micComboLoading) return;
            if (_micCombo.SelectedItem is MicDevice d)
            {
                _mic.DeviceId = d.Id;
                _mic.Save();
                PushMicSettings();   // применится при следующем старте диктовки (перезапрос микрофона)
                Log($"микрофон выбран: {d.Label}");
            }
        };
        // Открытие списка — заново перечислить устройства (могли подключить/отключить).
        _micCombo.DropDown += async (_, _) => await PopulateMicsAsync();

        // 3. Фильтр (аббревиатура) — MaterialCheckbox; после него зазор побольше (отделить от блока громкости).
        var noise = new MaterialCheckbox { Text = Lang.T("toolbar.noise"), Checked = _mic.Noise, AutoSize = true, Anchor = mid, Margin = gapBig };
        _tips.SetToolTip(noise, Lang.T("toolbar.noise_tip"));

        // 4. Блок громкости в ОДНОЙ панели с ручными координатами: иконка «микрофон+волны»,
        //    ползунок и значение. (TableLayoutPanel зажимает отрицательные отступы; у MaterialSlider
        //    слева свой отступ, справа большой резерв — потому раскладываем сами.)
        const int SliderX = 18;          // ползунок правее иконки — дорожка стартует сразу за ней (иконка «вплотную»)
        const double ValueFrac = 0.70;   // позиция значения = доля ширины ползунка (у конца дорожки) — ТЮНИНГ
        var volIcon = new MicGainIcon();
        _gainBar = new SmallThumbSlider
        {
            RangeMin = 10,
            RangeMax = 80,
            Value = GainToBar(_mic.Gain),
            Width = 180,
            Height = 34,
            UseAccentColor = true,
            ShowText = false,
            ShowValue = false,
            Enabled = _mic.Enabled,   // громкость крутим только при включённом усилении
            Location = new Point(SliderX + 4, -3),   // только ползунок: правее и выше (иконка/цифра на месте)
        };
        _gainValue = new Label
        {
            Text = GainText(_mic.Gain),
            Font = new Font(Palette.Family, 9f),   // значение в ~1.5× мельче контролов
            ForeColor = Palette.Text,
            BackColor = Palette.Bg,
            AutoSize = true,
        };
        // Панель на 1px выше центра ячейки: Margin снизу 2 → Anchor=None сдвигает вверх ~1px.
        // Панель шире ползунка (запас справа), чтобы индикатор не обрезался; лишнее место забирает колонка-распорка.
        var gainGroup = new Panel { Size = new Size(SliderX + 320, 34), BackColor = Palette.Bg, Anchor = mid, Margin = new Padding(4, 0, 4, 2) };  // +4px вправо всей группе
        void PlaceGainIcon() => volIcon.Location = new Point(0, (gainGroup.Height - volIcon.Height) / 2);
        void PlaceGainValue() => _gainValue.Location =   // индикатор: +55px вправо, +1px вниз
            new Point(SliderX + (int)(_gainBar.Width * ValueFrac) + 55, (gainGroup.Height - _gainValue.Height) / 2 + 1);
        gainGroup.Controls.Add(_gainBar);
        gainGroup.Controls.Add(volIcon);       // иконка поверх пустого левого отступа ползунка
        gainGroup.Controls.Add(_gainValue);    // значение поверх пустого правого резерва ползунка
        volIcon.BringToFront();
        _gainValue.BringToFront();
        PlaceGainIcon();
        PlaceGainValue();
        _gainValue.TextChanged += (_, _) => PlaceGainValue();

        master.CheckedChanged += (_, _) =>
        {
            _mic.Enabled = master.Checked;
            _gainBar.Enabled = master.Checked;
            _mic.Save();
            PushMicSettings();
        };
        noise.CheckedChanged += (_, _) =>
        {
            _mic.Noise = noise.Checked;
            _mic.Save();
            PushMicSettings();
        };
        _gainBar.onValueChanged += (_, _) =>
        {
            _mic.Gain = _gainBar.Value / 10.0;
            _gainValue.Text = GainText(_mic.Gain);
            PushMicSettings();          // усиление меняется на лету (gain-нода уже в графе)
        };
        _gainBar.MouseUp += (_, _) => _mic.Save();   // на диск — по отпусканию, не на каждый тик

        bar.Controls.Add(master, 0, 0);
        bar.Controls.Add(_micCombo, 1, 0);
        bar.Controls.Add(noise, 2, 0);
        bar.Controls.Add(gainGroup, 3, 0);
        return bar;
    }

    /// <summary>Показать/скрыть справку (панель над логом). Зовётся впрыснутой в страницу кнопкой «?».</summary>
    private void ToggleHelp()
    {
        _helpPanel.Visible = !_helpPanel.Visible;
        _logRow.Height = _helpPanel.Visible ? LogRowHelpPx : LogRowNormalPx; // под справку лог повыше
        if (_helpPanel.Visible)
        {
            _helpPanel.BringToFront();
            if (_helpSpinner.Visible) { _helpSpinner.BringToFront(); _helpSpinner.Spin(true); }
            _ = EnsureHelpViewAsync();
        }
        else _helpSpinner.Spin(false);
    }

    /// <summary>Тёмная тема MaterialSkin + шрифт Rubik (вшитый Roboto подменяем рефлексией).</summary>
    private void SetupMaterialSkin()
    {
        var mgr = MaterialSkinManager.Instance;
        mgr.Theme = MaterialSkinManager.Themes.DARK;
        mgr.ColorScheme = new ColorScheme(
            Primary.BlueGrey800, Primary.BlueGrey900, Primary.BlueGrey700,
            Accent.LightBlue200, TextShade.WHITE);
        MaterialFonts.Apply(Palette.Family, 13);
    }

    /// <summary>Показать/скрыть тулбар — плавно по высоте (0 ↔ ToolbarPx).</summary>
    private void ToggleToolbar()
    {
        _toolbarShown = !_toolbarShown;
        _toolbarTarget = _toolbarShown ? ToolbarPx : 0;
        _toolbarAnim.Start();
    }

    private void AnimateToolbar()
    {
        int h = (int)_toolbarRow.Height;
        const int step = 8;
        if (h < _toolbarTarget) h = Math.Min(_toolbarTarget, h + step);
        else if (h > _toolbarTarget) h = Math.Max(_toolbarTarget, h - step);
        _toolbarRow.Height = h;
        if (h == _toolbarTarget) _toolbarAnim.Stop();
    }

    /// <summary>Заполнить список микрофонов из страницы (enumerateDevices) с сохранением выбора.</summary>
    private async Task PopulateMicsAsync()
    {
        await Exec(EnumMicsKickScript);            // запустить асинхронное перечисление
        string json = "";
        for (int i = 0; i < 20; i++)               // опрашивать результат до готовности (~3 с)
        {
            json = await Exec(MicListReadScript);
            if (!string.IsNullOrEmpty(json) && json != "null" && json != "\"\"") break;
            await Task.Delay(150);
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("mics", out var mics) || mics.ValueKind != System.Text.Json.JsonValueKind.Array)
                return;

            _micComboLoading = true;
            _micCombo.Items.Clear();
            _micCombo.Items.Add(new MicDevice("", Lang.T("toolbar.device_default")));
            int selIdx = _mic.DeviceId.Length == 0 ? 0 : -1;
            foreach (var m in mics.EnumerateArray())
            {
                string id = m.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
                string label = m.TryGetProperty("label", out var l) ? (l.GetString() ?? id) : id;
                if (id.Length == 0) continue;
                _micCombo.Items.Add(new MicDevice(id, label));
                if (id == _mic.DeviceId) selIdx = _micCombo.Items.Count - 1;
            }
            _micCombo.SelectedIndex = selIdx >= 0 ? selIdx : 0;   // выбранного устройства нет → «По умолчанию»
            _micComboLoading = false;
        }
        catch (Exception ex) { _micComboLoading = false; Diag.Write("PopulateMics: " + ex.Message + " | " + json); }
    }

    private static int GainToBar(double g) => Math.Clamp((int)Math.Round(g * 10), 10, 80);
    private static string GainText(double g) => g.ToString("0.0", CultureInfo.InvariantCulture) + "×";

    /// <summary>Пробросить текущие настройки микрофона в страницу. Gain применяется на лету
    /// (нода уже в графе); enabled/noise вступят в силу при следующем запросе микрофона.</summary>
    private void PushMicSettings()
    {
        string en = _mic.Enabled ? "true" : "false";
        string ns = _mic.Noise ? "true" : "false";
        string g = _mic.Gain.ToString(CultureInfo.InvariantCulture);
        string dev = System.Text.Json.JsonSerializer.Serialize(_mic.DeviceId); // строка с кавычками, экранирована
        string js = "(function(){var m=window.__ggMic||(window.__ggMic={});"
            + "m.enabled=" + en + ";m.gain=" + g + ";m.noise=" + ns + ";m.deviceId=" + dev + ";"
            + "if(window.__ggMicGain){try{window.__ggMicGain.gain.value=" + g + ";}catch(e){}}"
            + "return 'ok';})()";
        _ = Exec(js);
    }

    /// <summary>Низ окна: лог + скрытая панель справки (её включает кнопка «?» с тулбара).</summary>
    private Panel BuildLogHost()
    {
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_log); // _log уже настроен (Dock=Fill)

        // Панель помощи — скрыта; по «?» накрывает лог. Внутри - своя маленькая веб-страница (HelpView),
        // её WebView2 создаётся при первом открытии справки (22.11.2026: прежнее поле TextBox с Segoe UI
        // пользователь забраковал - «некрасиво, ужасно»).
        _helpPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 30, 34),
            Visible = false,
        };
        // Спиннер поверх панели, пока страница справки не загрузилась (просьба пользователя 22.11.2026:
        // быстрый клик по «?» показывал пустую тёмную панель). Только значок, без подписи.
        _helpSpinner = new HelpSpinner { Dock = DockStyle.Fill, BackColor = _helpPanel.BackColor };
        _helpPanel.Controls.Add(_helpSpinner);
        host.Controls.Add(_helpPanel);
        return host;
    }

    // --------------------------- Справка (кнопка «?») ---------------------------
    // Маленькая веб-страница в своём WebView2 внутри панели справки (22.11.2026, вместо TextBox с Segoe UI).
    // Страницу и шрифт отдаёт сама программа через WebResourceRequested с адреса https://gg-help.local/ -
    // в сеть ничего не уходит, файлов не пишем; один источник у страницы и шрифта - CORS для @font-face не мешает.
    // Шрифт - Montserrat (выбран пользователем по образцам 22.11.2026; Poppins похож, но без кириллицы),
    // переменный, файл fonts/Montserrat-Variable.ttf. Тексты - из Lang (help.*), клавиши рисуются кнопками.
    // Окружение WebView2 - то же, что у ChatGPT: второе окружение на том же профиле с другими параметрами не поднимется.
    // Ссылки (репозиторий) открываются в браузере по умолчанию.

    private const string HelpHost = "https://gg-help.local/";
    private const string RepoUrl = "https://github.com/paperisque/gptbridge";   // просьба пользователя 21.11.2026
    private WebView2? _helpView;
    private bool _helpViewStarting;
    private HelpSpinner _helpSpinner = null!;

    private async Task EnsureHelpViewAsync()
    {
        if (_helpView is not null || _helpViewStarting || _web.CoreWebView2 is null) return;
        _helpViewStarting = true;
        var helpSw = Stopwatch.StartNew();
        try
        {
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(30, 30, 34) };
            _helpPanel.Controls.Add(view);
            _helpSpinner.BringToFront();   // спиннер закрывает ещё пустую страницу
            _ = view.Handle;   // панель скрыта (фоновая подготовка) - окно контрола создаём явно, иначе WebView2 ждал бы показа
            await view.EnsureCoreWebView2Async(_web.CoreWebView2.Environment);
            var core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.AddWebResourceRequestedFilter(HelpHost + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => e.Response = HelpResponse(core.Environment, e.Request.Uri);
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(HelpHost, StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                OpenInBrowser(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenInBrowser(e.Uri); };
            core.NavigationCompleted += (_, e) =>
            {
                Diag.Write($"справка: страница загружена (ok={e.IsSuccess}) за {helpSw.ElapsedMilliseconds} мс от старта WebView2");
                _helpSpinner.Spin(false);
                _helpSpinner.Visible = false;
            };
            core.Navigate(HelpHost + "help.html");
            _helpView = view;
        }
        catch (Exception ex) { Diag.Write("справка: WebView2 не поднялся: " + ex.Message); }
        finally { _helpViewStarting = false; }
    }

    private static void OpenInBrowser(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Diag.Write("ссылка: " + ex.Message); }
    }

    /// <summary>Ответ на запрос страницы справки: help.html - собранный HTML, fonts/&lt;файл&gt; - шрифт из папки программы.</summary>
    private static CoreWebView2WebResourceResponse HelpResponse(CoreWebView2Environment env, string uri)
    {
        string path = Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.AbsolutePath.TrimStart('/') : "";
        try
        {
            if (path == "help.html")
                return env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(HelpHtml())),
                    200, "OK", "Content-Type: text/html; charset=utf-8");
            if (path.StartsWith("fonts/", StringComparison.Ordinal))
            {
                string file = Path.Combine(AppContext.BaseDirectory, "fonts", Path.GetFileName(path));   // только имя файла: наружу папки fonts не выйти
                if (File.Exists(file))
                    return env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK", "Content-Type: font/ttf");
            }
        }
        catch (Exception ex) { Diag.Write("справка: " + path + ": " + ex.Message); }
        return env.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private static string H(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Строка хоткея «Ctrl+Win — описание»: клавиши в начале → кнопки, остальное - текст.</summary>
    private static string HelpKeyRow(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line,
            @"^((?:Ctrl|Strg|Win|Alt|Shift|Y)(?:\+(?:Ctrl|Strg|Win|Alt|Shift|Y))*)\s*(?:[—-]\s+)?(.*)$");
        if (!m.Success) return "<div class=\"row\"><span class=\"txt\">" + H(line) + "</span></div>";
        string keys = string.Join("<span class=\"plus\">+</span>", m.Groups[1].Value.Split('+').Select(k => "<kbd>" + H(k) + "</kbd>"));
        return "<div class=\"row\"><span class=\"keys\">" + keys + "</span><span class=\"txt\">" + H(m.Groups[2].Value) + "</span></div>";
    }

    /// <summary>Строка параметра запуска «--flag ЗНАЧЕНИЕ — описание»: параметр - моноширинным, описание - текстом.</summary>
    private static string HelpFlagRow(string line)
    {
        int i = line.IndexOf(" — ", StringComparison.Ordinal), j = line.IndexOf(" - ", StringComparison.Ordinal);
        int k = i < 0 ? j : j < 0 ? i : Math.Min(i, j);
        if (k < 0) return "<div class=\"row\"><span class=\"txt\">" + H(line) + "</span></div>";
        return "<div class=\"row flag\"><code>" + H(line[..k]) + "</code><span class=\"txt\">" + H(line[(k + 3)..]) + "</span></div>";
    }

    /// <summary>Абзац о режимах: названия режимов - жирным (длинные первыми: «Поток-авто» раньше «Поток»).</summary>
    private static string HelpModesPara(string text)
    {
        string html = H(text);
        var names = new[] { Lang.T("mode.auto"), Lang.T("mode.manual"), Lang.T("mode.normal") }
            .Select(H).Distinct().OrderByDescending(n => n.Length).ToArray();
        string pattern = "(?<![\\p{L}-])(" + string.Join("|", names.Select(System.Text.RegularExpressions.Regex.Escape)) + ")(?![\\p{L}-])";
        return "<p>" + System.Text.RegularExpressions.Regex.Replace(html, pattern, "<b>$1</b>") + "</p>";
    }

    private static string HelpHtml()
    {
        string ver = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "";
        string Title(string key) => H(Lang.T(key).TrimEnd(':', ' '));
        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><style>").Append(HelpCss).Append("</style></head><body>");
        sb.Append("<header><span class=\"app\">GPT Grabber</span><span class=\"ver\">").Append(H(ver)).Append("</span>")
          .Append("<a class=\"repo\" href=\"").Append(RepoUrl).Append("\">github.com/paperisque/gptbridge</a></header>");
        sb.Append("<main>");
        sb.Append("<section><h2>").Append(Title("help.title")).Append("</h2>");
        foreach (var key in new[] { "help.dictate", "help.keepbuf", "help.repaste", "help.move", "help.cancel" })
            sb.Append(HelpKeyRow(Lang.T(key)));
        sb.Append("</section>");
        sb.Append("<section><h2>").Append(Title("help.modes_title")).Append("</h2>")
          .Append(HelpModesPara(Lang.T("help.stream"))).Append(HelpModesPara(Lang.T("help.autostop"))).Append("</section>");
        sb.Append("<section><h2>").Append(Title("help.mic_title")).Append("</h2><p>").Append(H(Lang.T("help.mic_desc"))).Append("</p></section>");
        sb.Append("<section><h2>").Append(Title("help.flags_title")).Append("</h2>");
        foreach (var key in new[] { "help.flag_lang", "help.flag_tray", "help.flag_nobeep", "help.flag_api_host", "help.flag_api_port", "help.flag_devtools" })
            sb.Append(HelpFlagRow(Lang.T(key)));
        sb.Append("</section></main></body></html>");
        return sb.ToString();
    }

    // Тёмная гамма в тон кнопкам на странице ChatGPT (акцент #81d4fa). Карточки разделов встают в две колонки,
    // если хватает ширины. Полоса прокрутки тёмная, у правого края - как у лога.
    private const string HelpCss = """
@font-face { font-family: "Montserrat"; src: url("/fonts/Montserrat-Variable.ttf") format("truetype"); font-weight: 100 900; }
:root { color-scheme: dark; }
* { box-sizing: border-box; }
html, body { margin: 0; background: #1e1e22; }
body { font: 400 13px/1.55 "Montserrat", "Segoe UI", sans-serif; color: #cfcfd6; padding: 12px 14px 16px; }
::-webkit-scrollbar { width: 12px; }
::-webkit-scrollbar-track { background: #1e1e22; }
::-webkit-scrollbar-thumb { background: #3a3a44; border-radius: 6px; border: 3px solid #1e1e22; }
::-webkit-scrollbar-thumb:hover { background: #4a4a56; }
header { display: flex; align-items: baseline; gap: 10px; margin: 0 2px 12px; }
.app { font-weight: 600; font-size: 14px; color: #fff; }
.ver { font-size: 12px; color: #7c7c88; }
.repo { margin-left: auto; font-size: 12px; color: #81d4fa; text-decoration: none; }
.repo:hover { text-decoration: underline; }
main { display: grid; grid-template-columns: repeat(auto-fit, minmax(330px, 1fr)); gap: 12px; align-items: start; }
section { background: #26262c; border: 1px solid #33333c; border-radius: 12px; padding: 12px 16px 14px; }
h2 { font-size: 11.5px; font-weight: 600; color: #81d4fa; text-transform: uppercase; letter-spacing: .07em; margin: 0 0 8px; }
.row { display: flex; gap: 12px; align-items: baseline; margin: 6px 0; }
.keys { flex: 0 0 auto; min-width: 112px; white-space: nowrap; }
kbd { display: inline-block; font: 500 11.5px "Montserrat", "Segoe UI", sans-serif; color: #fff; background: #34343e;
      border: 1px solid #4a4a56; border-bottom-width: 2px; border-radius: 5px; padding: 0 6px; line-height: 1.6; }
.plus { color: #6f6f7a; margin: 0 3px; font-size: 11px; }
.flag { flex-direction: column; gap: 2px; margin: 8px 0; }
code { font: 500 12px Consolas, "Cascadia Mono", monospace; color: #e6e6ea; background: #34343e; border-radius: 4px; padding: 1px 6px; align-self: flex-start; }
p { margin: 6px 0; }
b { color: #fff; font-weight: 600; }
""";

    // --------------------------- JS-скрипты ---------------------------
    // Селекторы кнопок ChatGPT локализованы — матчим EN/DE/RU (см. историю POC).

    // Общий пролог для скриптов-операций: якорь на композер и поиск кнопок.
    // Зачем: у кнопок диктовки НЕТ ни id, ни data-testid — только локализованный aria-label
    // (проверено 11.09.2026: «Diktat starten/absenden/abbrechen»). Поэтому ищем в два захода —
    // по aria-label на всех языках, а если разметку переименуют/переведут иначе, по позиции
    // среди безымянных кнопок-иконок формы. Якорь формы: новый атрибут data-type="unified-composer"
    // (устойчивее класса), затем прежний класс с «composer», затем весь документ.
    private const string JsPrelude = """
var GG = (function () {
  // Якорь формы: с 10.10.2026 — form[data-chatgpt-composer]; прежние (data-type, класс composer) — на всякий случай.
  function form() {
    return document.querySelector('form[data-chatgpt-composer]')
      || document.querySelector('form[data-type="unified-composer"]')
      || [...document.querySelectorAll('form')].find(function (f) { return (f.className || '').includes('composer'); })
      || null;
  }
  function root() { return form() || document; }
  // Композер: с 10.10.2026 у ProseMirror нет id (#prompt-textarea исчез), есть data-composer-markdown.
  function composer() {
    return root().querySelector('[data-composer-markdown]')
      || root().querySelector('.ProseMirror[contenteditable="true"]')
      || document.querySelector('#prompt-textarea')
      || null;
  }
  function buttons() { return [...root().querySelectorAll('button')]; }
  function byAria(re) {
    return buttons().find(function (b) { return re.test(b.getAttribute('aria-label') || ''); }) || null;
  }
  // Подписи (сняты 10.10.2026, EN/DE/RU). Покой: Dictate / Diktieren / Диктовать. Запись: Cancel dictation /
  // Diktat abbrechen / Отменить диктовку; Stop dictation / Diktat beenden / Остановить диктовку; третья —
  // «Transcribe and send / Transkribieren und übermitteln / Расшифровать и отправить» (отправка боту, НЕ кликать).
  // Образцы с начала строки: /диктов/ ловил бы и «Диктовать» в покое. Старые подписи (до 10.10) оставлены.
  var START = /^(dictate|start dictation|diktieren|diktat starten|диктовать|начать диктов)/i;
  var STOP = /^(stop dictation|finish dictation|submit dictation|diktat beenden|diktat absenden|diktat senden|остановить диктов|отправить диктов)/i;
  var CANCEL = /^(cancel dictation|diktat abbrechen|отменить диктов)/i;
  // Кнопки-иконки формы: без id/testid и без своего текста (pill «Nachdenken/Think» отсеивается по тексту).
  // Признак не зависит от языка — фолбэк на случай новых подписей. С 10.10.2026 у «+» тоже нет id, поэтому
  // в покое это [+, диктовка, голосовой чат], в записи [отмена, стоп, расшифровать-и-отправить]:
  // нужная кнопка — ВТОРАЯ С КОНЦА в обоих состояниях, отмена — первая.
  function icons() {
    return buttons().filter(function (b) {
      return !b.id && !b.getAttribute('data-testid') && !(b.innerText || '').trim();
    });
  }
  function second() { var a = icons(); return a.length >= 2 ? a[a.length - 2] : null; }
  // Идёт ли запись — только по кнопкам. Раньше считалось и по пропавшему #prompt-textarea: 10.10 id исчез
  // навсегда, и «запись идёт» стало вечным, что отключило позиционный фолбэк.
  function recording() { return !!(byAria(STOP) || byAria(CANCEL)); }
  // Караоке (19.11.2026): слова по ходу записи показывает сама страница, но только после клика по кнопке
  // «Show live transcription». Кнопка - полоса звуковой волны между «Отменой» и «Стопом»: в записи иконки
  // формы [отмена, волна, стоп, отправить], и волна - единственная из них без svg. Подпись локализована
  // (сняты только английские «Show/Hide live transcription»), поэтому запас - по этому признаку.
  var LIVE = /^show live transcription/i;
  var LIVE_ON = /^hide live transcription/i;
  function wave() {
    var a = icons().filter(function (b) { return !b.querySelector('svg'); });
    return a.length === 1 ? a[0] : null;
  }
  return { form: form, root: root, composer: composer, buttons: buttons, byAria: byAria, icons: icons, second: second,
           recording: recording, wave: wave, START: START, STOP: STOP, CANCEL: CANCEL, LIVE: LIVE, LIVE_ON: LIVE_ON };
})();
""";

    /// <summary>Скрипт-операция с прологом (GG.*).</summary>
    private static string Js(string body) => JsPrelude + Environment.NewLine + body;

    // Готова ли кнопка начала диктовки.
    private static readonly string StartReadyScript = Js("""
(function () {
  var b = GG.byAria(GG.START);
  if (!b && !GG.recording()) b = GG.second();   // позиционный фолбэк
  return b ? 'ready' : 'no';
})()
""");

    // Прячем через CSS (правило живёт в <head>, переживает перерисовки React, узел не удаляем):
    //  (1) блок подсказок под композером («Создать изображение…»): с 10.10.2026 — section с классом
    //      group/home-suggestions; раньше — сосед сразу за #thread-bottom;
    //  (2) заголовок над композером («Можем начинать.»): с 10.10.2026 — контейнер h1.heading-xl
    //      (там два h1: невидимый для размера и видимый, оба в одном div); раньше — сосед перед
    //      #thread-bottom-container. Старые правила оставлены, они безвредны. Идемпотентно.
    private const string HideExtrasScript = """
(function () {
  var id = 'gptgrabber-hide-extras';
  var s = document.getElementById(id);
  if (!s) { s = document.createElement('style'); s.id = id; (document.head || document.documentElement).appendChild(s); }
  s.textContent = '#thread-bottom + div, *:has(+ #thread-bottom-container), '
    + 'main section[class*="home-suggestions"], main div:has(> h1.heading-xl) { display: none !important; }';
  return 'ok';
})()
""";

    // Плавающие круглые кнопки справа внизу страницы (поверх контента): режимы «Обычный» / «Поток» (решение
    // пользователя 21.11.2026: режимам не место в тулбаре усиления звука; 22.11.2026 переехали слева направо -
    // закрывали аватар), промежуток, «микрофон+волны» (показать/скрыть тулбар) и «?» (справка). Выбранный режим залит акцентом. __TIP_*__ - подсказки, подставляются из Lang. CSS даёт ровные бордеры/ховер (Region в WinForms не сглаживался).
    // Клик → postMessage → C# (WebMessageReceived). Идемпотентно + self-heal на случай перерисовок React.
    private const string OverlayInjectScript = """
(function () {
  var MIC = '<svg width="24" height="19" viewBox="0 0 32 24" fill="none" xmlns="http://www.w3.org/2000/svg">'
    + '<rect x="3" y="3" width="7" height="12" rx="3.5" fill="currentColor"/>'
    + '<path d="M1.5 11.5a5 5 0 0 0 10 0" stroke="currentColor" stroke-width="1.6" fill="none" stroke-linecap="round"/>'
    + '<line x1="6.5" y1="16.5" x2="6.5" y2="20.5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>'
    + '<path d="M15 8a7 7 0 0 1 0 8" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" opacity="1"/>'
    + '<path d="M19 5a11 11 0 0 1 0 14" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" opacity="0.5"/>'
    + '<path d="M23 2.5a15 15 0 0 1 0 19" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" opacity="0.26"/>'
    + '</svg>';
  // Значки режимов (выбраны пользователем 21.11.2026 по образцам): «Обычный» - пустой квадрат (залитый читался бы
  // как «идёт запись»), «Поток» - волна над строчками (звук переходит в текст).
  var SVG24 = '<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" xmlns="http://www.w3.org/2000/svg">';
  var NORMAL = SVG24 + '<rect x="5" y="5" width="14" height="14" rx="1.5"/></svg>';
  var STREAM = SVG24 + '<path d="M2.5 8c1.6-4 3.2-4 4.8 0s3.2 4 4.8 0 3.2-4 4.8 0 3.2 4 4.6 0"/><path d="M3 15h18"/><path d="M3 19.5h11"/></svg>';
  // Вид кнопки: выбранная (b.__ggOn - кнопка текущего режима) залита акцентом, остальные - тёмные с подсветкой под мышью.
  function paint(b, hover) {
    if (b.__ggOn) { b.style.background = '#81d4fa'; b.style.color = '#1b1b20'; b.style.borderColor = '#81d4fa'; }
    else if (hover) { b.style.background = 'rgba(62,62,72,.96)'; b.style.color = '#fff'; b.style.borderColor = 'rgba(255,255,255,.6)'; }
    else { b.style.background = 'rgba(38,38,44,.92)'; b.style.color = '#c9c9cf'; b.style.borderColor = 'rgba(255,255,255,.3)'; }
  }
  // Кнопки 44 px (было 36, +20% - решение пользователя 22.11.2026), значки увеличены в той же пропорции.
  function mkBtn(id, html, title) {
    var b = document.createElement('button');
    b.id = id; b.title = title; b.innerHTML = html;
    b.style.cssText = 'width:44px;height:44px;border-radius:50%;border:2px solid rgba(255,255,255,.35);'
      + 'background:rgba(38,38,44,.92);color:#c9c9cf;cursor:pointer;padding:0;margin:0;'
      + 'display:flex;align-items:center;justify-content:center;font:600 19px Rubik,sans-serif;'
      + 'box-shadow:0 2px 7px rgba(0,0,0,.4);transition:background .12s,color .12s,border-color .12s;';
    b.addEventListener('mouseenter', function () { paint(b, true); });
    b.addEventListener('mouseleave', function () { paint(b, false); });
    return b;
  }
  function post(m) { try { window.chrome.webview.postMessage(m); } catch (e) {} }
  // Режим приходит из программы (сохранённая настройка) и после клика; помним в window - переживает перерисовки.
  // auto - подрежим потока «Поток-авто»: на кнопке «Поток» значок «A».
  window.__ggSetMode = function (stream, auto) {
    window.__ggMode = !!stream;
    window.__ggAuto = !!stream && !!auto;
    var n = document.getElementById('ggModeNormal'), s = document.getElementById('ggModeStream');
    var badge = document.getElementById('ggModeAutoBadge');
    if (n) { n.__ggOn = !stream; paint(n, false); }
    if (s) { s.__ggOn = !!stream; paint(s, false); }
    if (badge) badge.style.display = window.__ggAuto ? 'flex' : 'none';
  };
  // Выбор подрежима потока (решение пользователя 21.11.2026): кнопка «Поток» одна; каждый клик по ней
  // открывает над ней два варианта - «Поток-авто» и «Поток» (ручной), текущий залит. Ничего не скрыто.
  function closeMenu() { var m = document.getElementById('ggModeMenu'); if (m) m.remove(); }
  function openMenu(anchor) {
    closeMenu();
    var r = anchor.getBoundingClientRect();
    var m = document.createElement('div');
    m.id = 'ggModeMenu';
    m.style.cssText = 'position:fixed;right:' + Math.round(window.innerWidth - r.right) + 'px;bottom:' + Math.round(window.innerHeight - r.top + 8) + 'px;'
      + 'z-index:2147483647;display:flex;flex-direction:column;gap:4px;padding:5px;border-radius:12px;'
      + 'background:rgba(30,30,36,.97);border:1px solid rgba(255,255,255,.28);box-shadow:0 4px 14px rgba(0,0,0,.5);';
    function item(label, tip, msg, on) {
      var b = document.createElement('button');
      b.textContent = label; b.title = tip;
      b.style.cssText = 'text-align:left;white-space:nowrap;padding:7px 14px;border-radius:8px;border:none;cursor:pointer;'
        + 'font:500 14px Rubik,sans-serif;' + (on ? 'background:#81d4fa;color:#1b1b20;' : 'background:transparent;color:#e6e6ea;');
      if (!on) {
        b.addEventListener('mouseenter', function () { b.style.background = 'rgba(62,62,72,.96)'; });
        b.addEventListener('mouseleave', function () { b.style.background = 'transparent'; });
      }
      b.addEventListener('click', function (ev) { ev.stopPropagation(); closeMenu(); post(msg); });
      return b;
    }
    m.appendChild(item(__LBL_AUTO__, __TIP_AUTO__, 'gg:mode:stream-auto', window.__ggMode && window.__ggAuto));
    m.appendChild(item(__LBL_MANUAL__, __TIP_MANUAL__, 'gg:mode:stream', window.__ggMode && !window.__ggAuto));
    document.documentElement.appendChild(m);
  }
  if (!window.__ggMenuDocHook) {
    window.__ggMenuDocHook = true;
    // Клик мимо выбора закрывает его (клик по самой кнопке «Поток» решает её обработчик).
    document.addEventListener('mousedown', function (ev) {
      var m = document.getElementById('ggModeMenu');
      if (!m || m.contains(ev.target)) return;
      if (ev.target.closest && ev.target.closest('#ggModeStream')) return;
      closeMenu();
    }, true);
  }
  function ensureModes() {
    if (document.getElementById('ggModes')) return;
    var wrap = document.createElement('div');
    wrap.id = 'ggModes';
    // Справа внизу, левее кнопок панели микрофона и «?» (16 + 44 + 8 + 44 = 112 px) с промежутком 20 px -
    // слева они закрывали аватар пользователя в боковой панели ChatGPT (решение пользователя 22.11.2026).
    wrap.style.cssText = 'position:fixed;right:132px;bottom:16px;z-index:2147483647;display:flex;gap:8px;';
    var n = mkBtn('ggModeNormal', NORMAL, __TIP_NORMAL__);
    var s = mkBtn('ggModeStream', STREAM, __TIP_STREAM__);
    s.style.position = 'relative';
    var badge = document.createElement('span');
    badge.id = 'ggModeAutoBadge';
    badge.textContent = 'A';
    badge.style.cssText = 'position:absolute;right:-4px;top:-4px;width:17px;height:17px;border-radius:50%;'
      + 'background:#ffca28;color:#1b1b20;font:700 12px Rubik,sans-serif;display:none;align-items:center;'
      + 'justify-content:center;box-shadow:0 1px 3px rgba(0,0,0,.5);pointer-events:none;';
    s.appendChild(badge);
    n.addEventListener('click', function () { closeMenu(); post('gg:mode:normal'); });
    s.addEventListener('click', function () {
      if (document.getElementById('ggModeMenu')) closeMenu(); else openMenu(s);
    });
    wrap.appendChild(n); wrap.appendChild(s);
    document.documentElement.appendChild(wrap);
    window.__ggSetMode(window.__ggMode, window.__ggAuto);
  }
  function ensure() {
    if (document.getElementById('ggOverlay')) return;
    var wrap = document.createElement('div');
    wrap.id = 'ggOverlay';
    wrap.style.cssText = 'position:fixed;right:16px;bottom:16px;z-index:2147483647;display:flex;gap:8px;';
    var mic = mkBtn('ggMic', MIC, 'Mic panel');
    var help = mkBtn('ggHelp', '?', 'Help');
    mic.addEventListener('click', function () { try { window.chrome.webview.postMessage('gg:toolbar'); } catch (e) {} });
    help.addEventListener('click', function () { try { window.chrome.webview.postMessage('gg:help'); } catch (e) {} });
    wrap.appendChild(mic); wrap.appendChild(help);
    document.documentElement.appendChild(wrap);
  }
  ensure(); ensureModes();
  if (!window.__ggOverlayHeal) { window.__ggOverlayHeal = setInterval(function () { ensure(); ensureModes(); }, 1500); }
  return 'ok';
})()
""";

    // Очистка композера (фокус + выделить всё + execCommand delete) — работает и в фоне.
    private static readonly string ClearComposerScript = Js("""
(function () {
  var el = GG.composer();
  if (!el) return 'no composer';
  el.focus();
  var sel = window.getSelection();
  if (sel) { var r = document.createRange(); r.selectNodeContents(el); sel.removeAllRanges(); sel.addRange(r); }
  var ok = document.execCommand('delete', false, null);
  if (!ok) ok = document.execCommand('insertText', false, '');
  return 'clear ok=' + ok;
})()
""");

    // Найти и нажать кнопку начала диктовки (EN/DE/RU, с позиционным фолбэком).
    private static readonly string ClickStartScript = Js("""
(function () {
  var b = GG.byAria(GG.START);
  var how = 'aria';
  if (!b && !GG.recording()) { b = GG.second(); how = 'по позиции'; }
  if (!b) return 'Start НЕ найдена';
  b.click();
  return 'клик Start (' + how + ', aria="' + (b.getAttribute('aria-label') || '') + '")';
})()
""");

    // Идёт ли запись (есть кнопка Submit/Cancel dictation или исчез композер).
    private static readonly string DictationStateScript = Js("""
(function () {
  return JSON.stringify({ live: GG.recording(), composerGone: !GG.composer() });
})()
""");

    // Включить караоке на идущей записи. Состояние между записями страница не помнит - жмём на каждой.
    // Уже включено - не трогаем, иначе выключили бы: признак aria-pressed="true" (от языка не зависит,
    // срез 21.11.2026) или подпись «Hide…».
    private static readonly string LiveShowScript = Js("""
(function () {
  if (!GG.recording()) return 'не в записи';
  var b = GG.byAria(GG.LIVE) || GG.byAria(GG.LIVE_ON);
  var how = 'aria';
  if (!b) { b = GG.wave(); how = 'по признаку (иконка без svg)'; }
  if (!b) return 'кнопка НЕ найдена';
  if (b.getAttribute('aria-pressed') === 'true' || GG.LIVE_ON.test(b.getAttribute('aria-label') || '')) return 'уже включено';
  b.click();
  return 'клик (' + how + ', aria="' + (b.getAttribute('aria-label') || '') + '")';
})()
""");

    // Где страница после Submit: 'idle' - вернулась кнопка начала диктовки (по подписи, без позиционного запаса:
    // в фазе «Finishing dictation» запас указал бы на чужую иконку); 'busy' - запись или завершение ещё идут;
    // 'unknown' - подписи не узнаны (ChatGPT их сменил) - вызывающий работает по времени.
    private static readonly string FinalStateScript = Js("""
(function () {
  if (GG.byAria(GG.START)) return 'idle';
  if (GG.recording() || GG.wave()) return 'busy';
  return 'unknown';
})()
""");

    // Отправить диктовку на распознавание (EN/DE/RU, с позиционным фолбэком).
    private static readonly string SubmitScript = Js("""
(function () {
  var b = GG.byAria(GG.STOP);
  var how = 'aria';
  if (!b) { b = GG.second(); how = 'по позиции'; }   // [отмена, стоп, расшифровать-и-отправить]
  if (!b) return 'Submit НЕ найдена';
  b.click();
  return 'клик Submit (' + how + ', aria="' + (b.getAttribute('aria-label') || '') + '")';
})()
""");

    // Отмена диктовки (EN/DE/RU, с позиционным фолбэком) — закрыть UI записи без отправки.
    private static readonly string CancelScript = Js("""
(function () {
  var b = GG.byAria(GG.CANCEL);
  var how = 'aria';
  if (!b && GG.recording() && GG.icons().length >= 2) { b = GG.icons()[0]; how = 'по позиции'; }   // [отмена, стоп, …]
  if (!b) return 'Cancel не найдена';
  b.click();
  return 'клик Cancel (' + how + ')';
})()
""");

    // Проба служебного API ChatGPT. Зачем: кнопки диктовки не будет и тогда, когда страница
    // цела, но сессия протухла — служебные запросы (me, settings/user, sentinel) отбиваются 503,
    // и композер молча рисуется без микрофона; лечится перелогином, ждать бесполезно.
    // Два шага, потому что ExecuteScriptAsync не ждёт промис: сперва пуск, потом чтение.
    private const string BackendProbeStartScript = """
(function () {
  window.__ggApiProbe = 'wait';
  fetch('/backend-api/me', { headers: { accept: 'application/json' } })
    .then(function (r) { window.__ggApiProbe = String(r.status); })
    .catch(function () { window.__ggApiProbe = 'err'; });
  return 'started';
})()
""";

    private const string BackendProbeReadScript = "window.__ggApiProbe || 'wait'";

    /// <summary>Код ответа служебного API («200», «503», «err», «wait» — если не дождались).</summary>
    private async Task<string> ProbeBackendAsync()
    {
        await Exec(BackendProbeStartScript);
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(300);
            var v = (await Exec(BackendProbeReadScript)).Trim('"');
            if (v != "wait") return v;
        }
        return "wait";
    }

    // ---- Вход «файл → текст»: звук для страницы (window.__ggFile) ----
    // Порядок: Reset (новый контейнер) → push(b64) кусками → Arm (склеить, декодировать, взвести) →
    // перехватчик getUserMedia (MicBoostTemplate, режим файла) отдаёт поток из буфера → State: playing → ended
    // (onended источника) → Disarm (снять, освободить контекст). Всё в window, перерисовки React не задевают.
    private const string FileResetScript = """
(function () {
  var f = window.__ggFile;
  if (f && f.source) { try { f.source.stop(); } catch (e) {} }
  if (f && f.ctx) { try { f.ctx.close(); } catch (e) {} }
  window.__ggFile = { chunks: [], armed: false, buffer: null, ctx: null, source: null, state: 'empty',
    push: function (b64) { this.chunks.push(b64); return this.chunks.length; } };
  return 'reset';
})()
""";

    private const string FileArmScript = """
(function () {
  var f = window.__ggFile;
  if (!f || !f.chunks.length) return 'no data';
  f.state = 'decoding';
  try {
    var raw = atob(f.chunks.join('')); f.chunks = [];
    var bytes = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
    raw = null;
    var Ctx = window.AudioContext || window.webkitAudioContext;
    f.ctx = new Ctx();
    if (f.ctx.state === 'suspended') { try { f.ctx.resume(); } catch (e) {} }
    f.ctx.decodeAudioData(bytes.buffer).then(
      function (buf) { f.buffer = buf; f.armed = true; f.state = 'armed'; },
      function (e) { f.state = 'error: decode ' + (e && e.message); });
  } catch (e) { f.state = 'error: ' + (e && e.message); }
  return f.state;
})()
""";

    private const string FileStateScript = "(window.__ggFile && window.__ggFile.state) || 'none'";

    private const string FileDisarmScript = """
(function () {
  var f = window.__ggFile;
  if (!f) return 'none';
  f.armed = false; f.buffer = null;
  if (f.source) { try { f.source.stop(); } catch (e) {} f.source = null; }
  if (f.ctx) { try { f.ctx.close(); } catch (e) {} f.ctx = null; }
  f.state = 'empty';
  return 'disarmed';
})()
""";

    // Прочитать распознанный текст из композера.
    private static readonly string ComposerReadScript = Js("""
(function () {
  var c = GG.composer();
  return c ? (c.innerText || '').replace(/ /g, ' ').trim() : '';
})()
""");

    // Перехват getUserMedia: микрофонный поток ChatGPT прогоняем через компрессор + gain
    // (вытянуть тихий микрофон, напр. AirPods HFP). Компрессор поднимает тихую речь и режет
    // пики (без клиппинга при усилении), gain задаёт итоговую громкость.
    // ВАЖНО: патчим БРАУЗЕРНЫЙ API на navigator — не DOM. Перерисовки React (§6.1) его не
    // трогают, ставится один раз на document-created и живёт всё время жизни страницы.
    // Значения — из window.__ggMic: gain меняется на лету (нода __ggMicGain в графе),
    // enabled/noise вступают в силу при следующем запросе микрофона (перезапрос на старте
    // диктовки). Плейсхолдеры __EN__/__GAIN__/__NS__ подставляются из сохранённых настроек.
    private const string MicBoostTemplate = """
(function () {
  if (window.__ggMicPatched) return;
  window.__ggMicPatched = true;
  window.__ggMic = window.__ggMic || { enabled: __EN__, gain: __GAIN__, noise: __NS__ };
  // Замер пикового уровня — объективная проверка «идёт ли звук» (а не по глазам на метре).
  // setInterval, а не requestAnimationFrame: rAF замирает при скрытом окне, а таймеры у нас
  // не троттлятся (--disable-background-timer-throttling), значит меряется и в трее.
  window.__ggMicPeak = 0;
  if (!window.__ggMicMeterRunning) {
    window.__ggMicMeterRunning = true;
    setInterval(function () {
      var an = window.__ggMicAnalyser;
      if (!an) return;
      var buf = new Float32Array(an.fftSize);
      an.getFloatTimeDomainData(buf);
      var m = 0; for (var i = 0; i < buf.length; i++) { var v = Math.abs(buf[i]); if (v > m) m = v; }
      if (m > window.__ggMicPeak) window.__ggMicPeak = m;
    }, 100);
  }
  // ДИАГНОСТИКА: пользуется ли диктовка ChatGPT браузерным Web Speech API (webkitSpeechRecognition)?
  // Если да — он берёт микрофон в ОБХОД getUserMedia, и наш gain до распознавания не доходит в принципе.
  window.__ggSpeechUsed = false;
  try {
    var SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (SR && SR.prototype && !SR.prototype.__ggWrapped) {
      var origStart = SR.prototype.start;
      SR.prototype.start = function () { window.__ggSpeechUsed = true; return origStart.apply(this, arguments); };
      SR.prototype.__ggWrapped = true;
    }
  } catch (e) {}
  var md = navigator.mediaDevices;
  if (!md || !md.getUserMedia) return;
  var orig = md.getUserMedia.bind(md);
  window.__ggMicOrig = orig;       // оригинал наружу — для «разблокировки» списка устройств без нашего графа
  window.__ggMicCalls = 0;         // сколько раз ChatGPT дёрнул getUserMedia (диагностика)
  window.__ggMicLast = 'none';     // что перехватчик сделал в последний раз
  md.getUserMedia = async function (constraints) {
    window.__ggMicCalls++;
    constraints = constraints || {};
    var wantAudio = !!constraints.audio;
    // РЕЖИМ ФАЙЛА (вход для кукол): взведён декодированный WAV - вместо микрофона отдаём поток из него,
    // один раз (armed сбрасывается сразу). В ctx.destination источник НЕ подключён - в колонки ничего.
    // Старт через 0.6 с: рекордер ChatGPT берёт поток не мгновенно, иначе теряется начало (разведка 08.11).
    var f = window.__ggFile;
    if (wantAudio && f && f.armed && f.buffer && f.ctx) {
      f.armed = false;
      try {
        var fsrc = f.ctx.createBufferSource(); fsrc.buffer = f.buffer;
        var fdest = f.ctx.createMediaStreamDestination();
        fsrc.connect(fdest);
        fsrc.onended = function () { f.state = 'ended'; };
        f.source = fsrc; f.state = 'playing';
        setTimeout(function () { try { fsrc.start(); } catch (e) { f.state = 'error: start ' + (e && e.message); } }, 600);
        window.__ggMicTrack = 'file ' + Math.round(f.buffer.duration) + 's';
        window.__ggMicLast = 'file';
        return fdest.stream;
      } catch (e) { f.state = 'error: ' + (e && e.message); window.__ggMicLast = 'file error'; }
    }
    // АВТОЗАПУСК С БУФЕРОМ («Поток-авто»): сторож голоса взведён - отдаём его поток (начало фразы из буфера +
    // живой звук, VoiceWatchScript). Один раз: handover сам снимает armed. Не вышло - обычный путь ниже.
    var vw = window.__ggVoice;
    if (wantAudio && vw && vw.armed && vw.handover) {
      try {
        var vs = vw.handover();
        window.__ggMicTrack = 'voice buffer ' + vw.handBuf.toFixed(2) + 's';
        window.__ggMicLast = 'voice-buffer';
        return vs;
      } catch (e) { vw.armed = false; window.__ggMicLast = 'voice-buffer error: ' + (e && e.message); }
    }
    // ПОЛНЫЙ ОБХОД, когда усиление выключено: ведём себя ровно как обычный браузер —
    // никакого deviceId/NS/AGC/графа. Тогда «Усиление выкл» = честный baseline «как до внедрения».
    if (!wantAudio || !window.__ggMic.enabled) {
      var s0 = await orig(constraints);
      var t0 = (wantAudio && s0.getAudioTracks) ? s0.getAudioTracks()[0] : null;
      window.__ggMicTrack = t0
        ? ((t0.label || '?') + ' | ' + ((t0.getSettings && t0.getSettings().deviceId) || '?'))
        : (wantAudio ? 'none' : 'video');
      window.__ggMicLast = wantAudio ? 'bypass-disabled' : 'passthrough-video';
      return s0;
    }
    // === усиление ВКЛЮЧЕНО: применяем нашу обработку (устройство + NS + gain) ===
    var a = (typeof constraints.audio === 'object') ? Object.assign({}, constraints.audio) : {};
    a.autoGainControl = false;                        // глушим AGC, чтобы ползунок реально управлял уровнем
    a.noiseSuppression = !!window.__ggMic.noise;
    a.echoCancellation = !!window.__ggMic.noise;
    if (window.__ggMic.deviceId) a.deviceId = { exact: window.__ggMic.deviceId }; // жёстко: выбранный микрофон
    constraints = Object.assign({}, constraints, { audio: a });
    window.__ggMicFallback = '';
    var stream = null;
    var forced = constraints.audio && typeof constraints.audio === 'object' && constraints.audio.deviceId;
    if (forced) {
      // Bluetooth-мик (AirPods) виден Windows только через HFP, который поднимается не сразу.
      // Поэтому exact пробуем НЕСКОЛЬКО раз с паузой — даём ОС включить HFP, а не откатываемся молча.
      var lastErr = null;
      for (var attempt = 0; attempt < 3; attempt++) {
        try { stream = await orig(constraints); lastErr = null; break; }
        catch (e) { lastErr = e; stream = null; await new Promise(function (r) { setTimeout(r, 900); }); }
      }
      if (!stream) {
        window.__ggMicFallback = 'exact failed x3: ' + (lastErr && lastErr.message);
        var c2 = Object.assign({}, constraints.audio); delete c2.deviceId;
        constraints = Object.assign({}, constraints, { audio: c2 });
        stream = await orig(constraints);   // откат: без принуждения deviceId
      }
    } else {
      stream = await orig(constraints);
    }
    // Какое устройство РЕАЛЬНО захвачено — снимает вопрос «переключилось ли».
    var at0 = stream.getAudioTracks ? stream.getAudioTracks()[0] : null;
    window.__ggMicTrack = at0
      ? ((at0.label || '?') + ' | ' + ((at0.getSettings && at0.getSettings().deviceId) || '?'))
      : 'none';
    if (!stream.getAudioTracks || !stream.getAudioTracks().length) { window.__ggMicLast = 'passthrough-noaudio'; return stream; }
    try {
      if (window.__ggMicCtx) { try { window.__ggMicCtx.close(); } catch (e) {} }
      var Ctx = window.AudioContext || window.webkitAudioContext;
      var ctx = new Ctx();
      window.__ggMicCtx = ctx;
      if (ctx.state === 'suspended') { try { ctx.resume(); } catch (e) {} }
      var src = ctx.createMediaStreamSource(stream);
      // Сначала УСИЛЕНИЕ (поднимаем весь сигнал), затем лимитер-страховка.
      var gain = ctx.createGain();
      gain.gain.value = window.__ggMic.gain;
      window.__ggMicGain = gain;
      // Лимитер ПОСЛЕ gain: высокий порог (−3 dB) + большой ratio ловят только пики у 0 dBFS,
      // не давя полезную речь. Если поставить компрессор ДО gain с низким порогом — он сожмёт
      // сигнал, а makeup-gain его не компенсирует, и усиления не слышно (была эта грабля).
      var lim = ctx.createDynamicsCompressor();
      lim.threshold.value = -3; lim.knee.value = 0; lim.ratio.value = 20;
      lim.attack.value = 0.002; lim.release.value = 0.1;
      // Анализатор после лимитера — меряем итоговый пиковый уровень (диагностика «идёт ли звук»).
      var an = ctx.createAnalyser(); an.fftSize = 512;
      window.__ggMicAnalyser = an; window.__ggMicPeak = 0;
      var dest = ctx.createMediaStreamDestination();
      src.connect(gain); gain.connect(lim); lim.connect(an); an.connect(dest);
      var out = dest.stream;
      if (stream.getVideoTracks) stream.getVideoTracks().forEach(function (t) { out.addTrack(t); });
      window.__ggMicLast = 'boosted gain=' + gain.gain.value + ' ctx=' + ctx.state;
      return out;
    } catch (e) { window.__ggMicLast = 'error: ' + (e && e.message); return stream; }
  };
})()
""";

    // Диагностика перехвата: сработал ли патч, сколько раз ChatGPT звал getUserMedia,
    // что перехватчик сделал в последний раз, состояние gain-ноды и аудиоконтекста.
    private const string MicDiagScript = """
(function () {
  return JSON.stringify({
    patched: !!window.__ggMicPatched,
    calls: window.__ggMicCalls || 0,
    last: window.__ggMicLast || 'none',
    gain: window.__ggMicGain ? window.__ggMicGain.gain.value : null,
    ctx: window.__ggMicCtx ? window.__ggMicCtx.state : null,
    peak: window.__ggMicPeak != null ? Math.round(window.__ggMicPeak * 1000) / 1000 : null,
    track: window.__ggMicTrack || null,
    fb: window.__ggMicFallback || '',
    speech: !!window.__ggSpeechUsed
  });
})()
""";

    // Перечисление микрофонов АСИНХРОННО, а ExecuteScriptAsync НЕ дожидается Promise
    // (сериализует само обещание) — поэтому запуск и чтение разнесены: этот скрипт стартует
    // перечисление и кладёт результат в window.__ggMicList, а C# опрашивает MicListReadScript.
    // Метки/ID устройств закрыты, пока странице не выдан доступ к микрофону — разово
    // разблокируем через ОРИГИНАЛЬНЫЙ getUserMedia (не наш патч) и сразу отпускаем устройство.
    private const string EnumMicsKickScript = """
(function () {
  window.__ggMicListReady = false;
  window.__ggMicList = null;
  window.__ggMicListErr = '';
  (async function () {
    try {
      var devs = await navigator.mediaDevices.enumerateDevices();
      var mics = devs.filter(function (d) { return d.kind === 'audioinput'; });
      var haveLabels = mics.some(function (d) { return d.deviceId && d.label; });
      if (!haveLabels && window.__ggMicOrig) {
        try {
          var s = await window.__ggMicOrig({ audio: true });
          s.getTracks().forEach(function (t) { t.stop(); });
          devs = await navigator.mediaDevices.enumerateDevices();
          mics = devs.filter(function (d) { return d.kind === 'audioinput'; });
        } catch (e) { window.__ggMicListErr = 'prime: ' + (e && e.message); }
      }
      // Только РЕАЛЬНЫЕ устройства: роли-псевдонимы Windows (default/communications) убираем —
      // они нестабильны (соскальзывают на другое устройство) и путают («Kommunikation - AirPods»).
      window.__ggMicList = mics
        .filter(function (d) { return d.deviceId && d.deviceId !== 'default' && d.deviceId !== 'communications'; })
        .map(function (d) { return { id: d.deviceId, label: d.label || d.deviceId }; });
    } catch (e) {
      window.__ggMicList = [];
      window.__ggMicListErr = 'enum: ' + (e && e.message);
    }
    window.__ggMicListReady = true;
  })();
  return 'started';
})()
""";

    // Чтение результата перечисления (пусто, пока не готово).
    private const string MicListReadScript = """
(function () {
  if (!window.__ggMicListReady) return '';
  return JSON.stringify({
    sel: window.__ggMic ? (window.__ggMic.deviceId || '') : '',
    mics: window.__ggMicList || [],
    error: window.__ggMicListErr || ''
  });
})()
""";

    private static string MicBoostScript(MicSettings m) => MicBoostTemplate
        .Replace("__EN__", m.Enabled ? "true" : "false")
        .Replace("__GAIN__", m.Gain.ToString(CultureInfo.InvariantCulture))
        .Replace("__NS__", m.Noise ? "true" : "false");
}

/// <summary>Спиннер загрузки справки: голубая дуга крутится по таймеру, рисуется GDI+ со сглаживанием.</summary>
internal sealed class HelpSpinner : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private int _angle;

    public HelpSpinner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _timer.Tick += (_, _) => { _angle = (_angle + 12) % 360; Invalidate(); };
    }

    /// <summary>Запустить/остановить вращение (таймер крутится, только пока спиннер виден).</summary>
    public void Spin(bool on) { if (on) _timer.Start(); else _timer.Stop(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        const int size = 36, width = 3;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        using var track = new Pen(Color.FromArgb(55, 55, 64), width);
        using var arc = new Pen(Color.FromArgb(0x81, 0xd4, 0xfa), width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(track, r);
        g.DrawArc(arc, r, _angle, 100);
    }

    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
}
