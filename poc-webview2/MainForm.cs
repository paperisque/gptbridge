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
///   Ctrl+Win+Alt  — повторно вставить последний распознанный текст.
/// Ctrl+Win в промежуточной фазе (готовлю/распознаю) = отмена.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new();
    private readonly TextBox _log = new();
    private readonly NotifyIcon _tray = new();
    private Icon? _icoWhite, _icoOrange, _icoGreen, _icoBlue, _icoRed; // эквалайзер по фазам (синхрон с пилюлей)
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

    private enum LiveState { Idle, Starting, Recording, Stopping }
    private LiveState _state = LiveState.Idle;

    // При возврате в покой иконку трея возвращаем к белой (активные цвета ставят фаза-обёртки).
    private LiveState State
    {
        get => _state;
        set { _state = value; if (value == LiveState.Idle && _icoWhite != null) _tray.Icon = _icoWhite; }
    }
    private CancellationTokenSource? _cts;
    private string _lastText = "";          // последний распознанный текст — для Ctrl+Win+Alt
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
            AdditionalBrowserArguments =
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
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
        SetupTray();
        Load += async (_, _) => await InitWebViewAsync();
    }

    // ------------------------------ Трей ------------------------------

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Lang.T("tray.show"), null, (_, _) => ShowFromTray());
        menu.Items.Add(Lang.T("tray.exit"), null, (_, _) => { _exiting = true; Close(); });
        _tray.Icon = _icoWhite;
        _tray.Text = "GPT Grabber";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
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
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                Diag.Write($"navigation completed, success={e.IsSuccess}");
                if (e.IsSuccess)
                {
                    await Exec(HideExtrasScript);    // спрятать лишний блок под #thread-bottom (CSS живёт в head)
                    await Exec(OverlayInjectScript); // плавающие кнопки (тулбар/справка) в правом нижнем углу
                    await PopulateMicsAsync();       // список микрофонов в тулбар (метки уже доступны — разрешение выдано)
                    if (!_firstClearDone)
                    {
                        _firstClearDone = true;
                        await Task.Delay(1500);          // дать странице осесть
                        await Exec(ClearComposerScript); // снять восстановленный из сессии черновик
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
        }
    }

    private async Task StartDictationAsync()
    {
        State = LiveState.Starting;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        OverlayShow(StatusOverlay.Phase.Preparing, Win32.GetForegroundWindow());
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
            Log("micdiag: " + await Exec(MicDiagScript));   // ВРЕМЕННО: факты о перехвате getUserMedia
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
    }

    private async Task StopAndInjectAsync(bool withY)
    {
        State = LiveState.Stopping;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // Окно-цель = где сейчас работает пользователь; туда же якорим пилюлю.
        IntPtr target = Win32.GetForegroundWindow();
        OverlayShow(StatusOverlay.Phase.Transcribing, target);
        try
        {
            Diag.Write("Submit: " + await Exec(SubmitScript));
            Log("micdiag@stop: " + await Exec(MicDiagScript));   // ВРЕМЕННО: пиковый уровень за время записи

            string text = "";
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500, ct);
                text = await Exec(ComposerReadScript);
                if (GoodText(text)) break;
            }

            if (GoodText(text))
            {
                _lastText = text; // запоминаем для повторной вставки (Ctrl+Win+Alt)
                bool ok = Injector.Inject(text, target, withY);
                Win32.FeedbackBeep(ok);
                OverlaySet(ok ? StatusOverlay.Phase.Done : StatusOverlay.Phase.Error, ok ? null : Lang.T("err.paste_failed"));
                Log(Lang.T("log.result", text));   // полный текст; обрезку делает само окно по правому краю
                Diag.Write($"inject ok={ok} window=«{Win32.GetWindowTitle(target)}»" + (withY ? " (+buffer)" : ""));
            }
            else
            {
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
            State = LiveState.Idle;
        }
    }

    /// <summary>Повторная вставка последнего текста (Ctrl+Win+Alt) — в текущее активное окно.</summary>
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
        _micCombo = new SmallComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Anchor = mid, Margin = gap };
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
        _gainBar = new MaterialSlider
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
            Font = new Font(Palette.FontName, 9f),   // значение в ~1.5× мельче контролов
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
        if (_helpPanel.Visible) _helpPanel.BringToFront();
    }

    /// <summary>Тёмная тема MaterialSkin + шрифт Rubik (вшитый Roboto подменяем рефлексией).</summary>
    private void SetupMaterialSkin()
    {
        var mgr = MaterialSkinManager.Instance;
        mgr.Theme = MaterialSkinManager.Themes.DARK;
        mgr.ColorScheme = new ColorScheme(
            Primary.BlueGrey800, Primary.BlueGrey900, Primary.BlueGrey700,
            Accent.LightBlue200, TextShade.WHITE);
        MaterialFonts.Apply(Palette.FontName, 13);
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
        Diag.Write("enum raw: " + json);           // ВРЕМЕННО: что реально вернул enumerateDevices
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

        // Панель помощи — скрыта; по «?» накрывает лог. Внутри read-only многострочное
        // поле: само скроллит/переносит, если справки больше, чем влезает в полосу лога.
        _helpPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 30, 34),
            // Отступ ТОЛЬКО слева (текст не липнет к краю). Право/верх/низ = 0, чтобы скролл-балка
            // вложенного поля прилегала ко всем стенкам окна (как у лога), без промежутков.
            Padding = new Padding(14, 0, 0, 0),
            Visible = false,
        };
        _helpPanel.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(30, 30, 34),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 10f),
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            TabStop = false,
            Cursor = Cursors.Default,
            Text = HotkeyHelpText(),
        });
        host.Controls.Add(_helpPanel);
        return host;
    }

    /// <summary>Локализованный список хоткеев для панели помощи.</summary>
    private static string HotkeyHelpText()
    {
        string nl = Environment.NewLine;
        return nl + Lang.T("help.title") + nl + nl   // пустая строка сверху — отступ от края
            + "•  " + Lang.T("help.dictate") + nl
            + "•  " + Lang.T("help.keepbuf") + nl
            + "•  " + Lang.T("help.repaste") + nl
            + "•  " + Lang.T("help.cancel") + nl + nl
            + Lang.T("help.mic_title") + nl
            + "•  " + Lang.T("help.mic_desc") + nl + nl
            + Lang.T("help.flags_title") + nl
            + "•  " + Lang.T("help.flag_lang") + nl
            + "•  " + Lang.T("help.flag_tray") + nl
            + "•  " + Lang.T("help.flag_nobeep") + nl
            + "•  " + Lang.T("help.flag_devtools");
    }

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
  function form() {
    return document.querySelector('form[data-type="unified-composer"]')
      || [...document.querySelectorAll('form')].find(function (f) { return (f.className || '').includes('composer'); })
      || null;
  }
  function root() { return form() || document; }
  function buttons() { return [...root().querySelectorAll('button')]; }
  function byAria(re) {
    return buttons().find(function (b) { return re.test(b.getAttribute('aria-label') || ''); }) || null;
  }
  // Кнопки-иконки трейлинг-группы: всё, что не опознано по id/testid и не имеет своего
  // текста (плюс и отправка отсеиваются по id, pill «Nachdenken» — по тексту). Признак не
  // зависит от языка, поэтому годится фолбэком, когда aria-label переименуют или переведут.
  // В покое остаются [диктовка, голосовой чат], во время записи — [отмена, отправка диктовки].
  function icons() {
    return buttons().filter(function (b) {
      return !b.id && !b.getAttribute('data-testid') && !(b.innerText || '').trim();
    });
  }
  function recording() {
    return !!byAria(/submit dictation|cancel dictation|diktat absenden|diktat senden|diktat abbrechen|диктов/i)
      || !document.querySelector('#prompt-textarea');
  }
  return { form: form, root: root, buttons: buttons, byAria: byAria, icons: icons, recording: recording };
})();
""";

    /// <summary>Скрипт-операция с прологом (GG.*).</summary>
    private static string Js(string body) => JsPrelude + Environment.NewLine + body;

    // Готова ли кнопка начала диктовки.
    private static readonly string StartReadyScript = Js("""
(function () {
  var b = GG.byAria(/start dictation|diktat starten|начать диктов/i);
  if (!b && !GG.recording() && GG.icons().length >= 2) b = GG.icons()[0];   // позиционный фолбэк
  return b ? 'ready' : 'no';
})()
""");

    // Прячем через CSS (правило живёт в <head>, переживает перерисовки React, узел не удаляем):
    //  (1) блок СРАЗУ ЗА #thread-bottom — лишние кнопки, что прыгают при сужении окна;
    //  (2) элемент ПЕРЕД #thread-bottom-container (контейнер композера) — заголовок, он мешает.
    // *:has(+ X) = предыдущий сосед X. Идемпотентно.
    private const string HideExtrasScript = """
(function () {
  var id = 'gptgrabber-hide-extras';
  var s = document.getElementById(id);
  if (!s) { s = document.createElement('style'); s.id = id; (document.head || document.documentElement).appendChild(s); }
  s.textContent = '#thread-bottom + div, *:has(+ #thread-bottom-container) { display: none !important; }';
  return 'ok';
})()
""";

    // Плавающие круглые кнопки в правом нижнем углу страницы (поверх контента): «микрофон+волны»
    // (показать/скрыть тулбар) и «?» (справка). CSS даёт ровные бордеры/ховер (Region в WinForms не сглаживался).
    // Клик → postMessage → C# (WebMessageReceived). Идемпотентно + self-heal на случай перерисовок React.
    private const string OverlayInjectScript = """
(function () {
  var MIC = '<svg width="20" height="16" viewBox="0 0 32 24" fill="none" xmlns="http://www.w3.org/2000/svg">'
    + '<rect x="3" y="3" width="7" height="12" rx="3.5" fill="currentColor"/>'
    + '<path d="M1.5 11.5a5 5 0 0 0 10 0" stroke="currentColor" stroke-width="1.6" fill="none" stroke-linecap="round"/>'
    + '<line x1="6.5" y1="16.5" x2="6.5" y2="20.5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>'
    + '<path d="M15 8a7 7 0 0 1 0 8" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" opacity="1"/>'
    + '<path d="M19 5a11 11 0 0 1 0 14" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" opacity="0.5"/>'
    + '<path d="M23 2.5a15 15 0 0 1 0 19" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" opacity="0.26"/>'
    + '</svg>';
  function mkBtn(id, html, title) {
    var b = document.createElement('button');
    b.id = id; b.title = title; b.innerHTML = html;
    b.style.cssText = 'width:36px;height:36px;border-radius:50%;border:2px solid rgba(255,255,255,.35);'
      + 'background:rgba(38,38,44,.92);color:#c9c9cf;cursor:pointer;padding:0;margin:0;'
      + 'display:flex;align-items:center;justify-content:center;font:600 16px Rubik,sans-serif;'
      + 'box-shadow:0 2px 7px rgba(0,0,0,.4);transition:background .12s,color .12s,border-color .12s;';
    b.addEventListener('mouseenter', function () { b.style.background = 'rgba(62,62,72,.96)'; b.style.color = '#fff'; b.style.borderColor = 'rgba(255,255,255,.6)'; });
    b.addEventListener('mouseleave', function () { b.style.background = 'rgba(38,38,44,.92)'; b.style.color = '#c9c9cf'; b.style.borderColor = 'rgba(255,255,255,.3)'; });
    return b;
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
  ensure();
  if (!window.__ggOverlayHeal) { window.__ggOverlayHeal = setInterval(ensure, 1500); }
  return 'ok';
})()
""";

    // Очистка композера (фокус + выделить всё + execCommand delete) — работает и в фоне.
    private const string ClearComposerScript = """
(function () {
  var el = document.querySelector('#prompt-textarea');
  if (!el) return 'no composer';
  el.focus();
  var sel = window.getSelection();
  if (sel) { var r = document.createRange(); r.selectNodeContents(el); sel.removeAllRanges(); sel.addRange(r); }
  var ok = document.execCommand('delete', false, null);
  if (!ok) ok = document.execCommand('insertText', false, '');
  return 'clear ok=' + ok;
})()
""";

    // Найти и нажать кнопку начала диктовки (EN/DE/RU, с позиционным фолбэком).
    private static readonly string ClickStartScript = Js("""
(function () {
  var b = GG.byAria(/start dictation|diktat starten|начать диктов|диктовку начать/i);
  var how = 'aria';
  if (!b && !GG.recording() && GG.icons().length >= 2) { b = GG.icons()[0]; how = 'по позиции'; }
  if (!b) return 'Start НЕ найдена';
  b.click();
  return 'клик Start (' + how + ', aria="' + (b.getAttribute('aria-label') || '') + '")';
})()
""");

    // Идёт ли запись (есть кнопка Submit/Cancel dictation или исчез композер).
    private static readonly string DictationStateScript = Js("""
(function () {
  var live = !!GG.byAria(/submit dictation|diktat absenden|diktat senden|cancel dictation|diktat abbrechen|диктов/i);
  return JSON.stringify({ live: live, composerGone: !document.querySelector('#prompt-textarea') });
})()
""");

    // Отправить диктовку на распознавание (EN/DE/RU, с позиционным фолбэком).
    private static readonly string SubmitScript = Js("""
(function () {
  var b = GG.byAria(/submit dictation|diktat absenden|diktat senden|отправить диктов/i);
  var how = 'aria';
  if (!b && GG.icons().length >= 2) { b = GG.icons()[1]; how = 'по позиции'; }   // [отмена, отправка]
  if (!b) return 'Submit НЕ найдена';
  b.click();
  return 'клик Submit (' + how + ', aria="' + (b.getAttribute('aria-label') || '') + '")';
})()
""");

    // Отмена диктовки (EN/DE/RU, с позиционным фолбэком) — закрыть UI записи без отправки.
    private static readonly string CancelScript = Js("""
(function () {
  var b = GG.byAria(/cancel dictation|diktat abbrechen|отменить диктов/i);
  var how = 'aria';
  if (!b && GG.icons().length >= 2) { b = GG.icons()[0]; how = 'по позиции'; }   // [отмена, отправка]
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

    // Прочитать распознанный текст из композера.
    private const string ComposerReadScript = """
(function () {
  var c = document.querySelector('#prompt-textarea');
  return c ? (c.innerText || '').replace(/ /g, ' ').trim() : '';
})()
""";

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
