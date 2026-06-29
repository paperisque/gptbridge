using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    private const int LogRowNormalPx = 150, LogRowHelpPx = 250;
    private RowStyle _logRow = null!;

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
        Height = 550;
        // При старте в трей запускаемся за экраном — инициализация (WebView/хук) проходит,
        // но без видимой вспышки окна; затем в OnShown прячемся и центрируем на будущее.
        StartPosition = startInTray ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (startInTray) Location = new System.Drawing.Point(-32000, -32000);
        ShowInTaskbar = false; // утилита трея; задаём ДО создания хэндла (без пересоздания окна)

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // WebView с ChatGPT
        _logRow = new RowStyle(SizeType.Absolute, LogRowNormalPx); // лог (расширяется под открытую справку)
        root.RowStyles.Add(_logRow);

        _web.Dock = DockStyle.Fill;
        _web.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Diag.WebViewDataDir,
            // Не давать Chromium «засыпать» в фоне/свёрнутым — захват микрофона должен идти.
            AdditionalBrowserArguments =
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
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
        root.Controls.Add(BuildLogHost(), 0, 1);
        Controls.Add(root);

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

            _web.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    e.State = CoreWebView2PermissionState.Allow;
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                Diag.Write($"navigation completed, success={e.IsSuccess}");
                if (e.IsSuccess)
                {
                    await Exec(HideExtrasScript);    // спрятать лишний блок под #thread-bottom (CSS живёт в head)
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
            // Ждём появления кнопки Start (не фиксированную паузу).
            if (!await PollAsync(StartReadyScript, "ready", 20, 300, ct))
            {
                Diag.Write("кнопка Start не появилась");
                OverlaySet(StatusOverlay.Phase.Error, Lang.T("err.not_ready"));
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

    /// <summary>Низ окна: лог + кнопка «?» (всегда поверх). По «?» поверх лога всплывает
    /// панель со списком горячих клавиш; повторный клик — скрыть.</summary>
    private Panel BuildLogHost()
    {
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_log); // _log уже настроен (Dock=Fill)

        // Панель помощи — скрыта; по «?» накрывает лог. Внутри read-only многострочное
        // поле: само скроллит/переносит, если справки больше, чем влезает в полосу лога.
        var help = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 30, 34),
            // Отступ ТОЛЬКО слева (текст не липнет к краю). Право/верх/низ = 0, чтобы скролл-балка
            // вложенного поля прилегала ко всем стенкам окна (как у лога), без промежутков.
            Padding = new Padding(14, 0, 0, 0),
            Visible = false,
        };
        help.Controls.Add(new TextBox
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
        host.Controls.Add(help);

        // Круглая кнопка «?» в правом верхнем углу — всегда поверх лога/панели помощи.
        var btn = new Button
        {
            Text = "?",
            Size = new Size(26, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(50, 50, 58),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        var round = new System.Drawing.Drawing2D.GraphicsPath();
        round.AddEllipse(0, 0, btn.Width - 1, btn.Height - 1);
        btn.Region = new Region(round);
        btn.Click += (_, _) =>
        {
            help.Visible = !help.Visible;
            btn.Text = help.Visible ? "✕" : "?";   // открыто → крестик, закрыто → вопрос
            _logRow.Height = help.Visible ? LogRowHelpPx : LogRowNormalPx; // под справку лог повыше
            if (help.Visible) help.BringToFront();
            btn.BringToFront();
        };
        host.Controls.Add(btn);
        // Держим кнопку ЛЕВЕЕ вертикального скролла лога — резервируем его ширину.
        host.Resize += (_, _) => btn.Location =
            new Point(host.ClientSize.Width - btn.Width - 8 - SystemInformation.VerticalScrollBarWidth, 8);
        btn.BringToFront();
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
            + Lang.T("help.flags_title") + nl
            + "•  " + Lang.T("help.flag_lang") + nl
            + "•  " + Lang.T("help.flag_tray") + nl
            + "•  " + Lang.T("help.flag_nobeep");
    }

    // --------------------------- JS-скрипты ---------------------------
    // Селекторы кнопок ChatGPT локализованы — матчим EN/DE/RU (см. историю POC).

    // Готова ли кнопка начала диктовки.
    private const string StartReadyScript = """
(function () {
  var b = [...document.querySelectorAll('button')].find(x =>
    /start dictation|diktat starten|начать диктов/i.test(x.getAttribute('aria-label') || ''));
  return b ? 'ready' : 'no';
})()
""";

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

    // Найти и нажать кнопку начала диктовки (EN/DE/RU).
    private const string ClickStartScript = """
(function () {
  var b = [...document.querySelectorAll('button')].find(x =>
    /start dictation|diktat starten|начать диктов|диктовку начать/i.test(x.getAttribute('aria-label') || ''));
  if (!b) return 'Start НЕ найдена';
  b.click();
  return 'клик Start (aria="' + (b.getAttribute('aria-label') || '') + '")';
})()
""";

    // Идёт ли запись (есть кнопка Submit/Cancel dictation или исчез композер).
    private const string DictationStateScript = """
(function () {
  var btns = [...document.querySelectorAll('button')];
  var live = btns.some(x => /submit dictation|diktat absenden|diktat senden|cancel dictation|diktat abbrechen|диктов/i
    .test(x.getAttribute('aria-label') || ''));
  return JSON.stringify({ live: live, composerGone: !document.querySelector('#prompt-textarea') });
})()
""";

    // Отправить диктовку на распознавание (EN/DE/RU).
    private const string SubmitScript = """
(function () {
  var b = [...document.querySelectorAll('button')].find(x =>
    /submit dictation|diktat absenden|diktat senden|отправить диктов/i.test(x.getAttribute('aria-label') || ''));
  if (b) { b.click(); return 'клик Submit (aria="' + (b.getAttribute('aria-label') || '') + '")'; }
  return 'Submit НЕ найдена';
})()
""";

    // Отмена диктовки (EN/DE/RU) — закрыть UI записи без отправки.
    private const string CancelScript = """
(function () {
  var b = [...document.querySelectorAll('button')].find(x =>
    /cancel dictation|diktat abbrechen|отменить диктов/i.test(x.getAttribute('aria-label') || ''));
  if (b) { b.click(); return 'клик Cancel'; }
  return 'Cancel не найдена';
})()
""";

    // Прочитать распознанный текст из композера.
    private const string ComposerReadScript = """
(function () {
  var c = document.querySelector('#prompt-textarea');
  return c ? (c.innerText || '').replace(/ /g, ' ').trim() : '';
})()
""";
}
