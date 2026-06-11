using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace VoiceBridge;

/// <summary>
/// Системный индикатор статуса диктовки — маленькая «пилюля» поверх всех окон.
///
/// Зачем: между нажатием Ctrl+Win и реальным стартом записи проходит время, а
/// пользователь сидит НЕ в браузере (VS Code и т.п.) и не видит, что происходит.
/// Пилюля появляется у места ввода и показывает фазу: звёздочка «готовлю» →
/// зелёный микрофон «говори» → звёздочка «распознаю» → галочка «вставлено».
///
/// Ключевое — окно НЕ крадёт фокус и НЕ ловит мышь: WS_EX_NOACTIVATE |
/// WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT,
/// показ только с SWP_NOACTIVATE.
///
/// Рисование — PER-PIXEL ALPHA (UpdateLayeredWindow): пилюля каждый кадр
/// рендерится в ARGB-битмап через GDI+ со сглаживанием (SmoothingMode.AntiAlias)
/// и целиком отдаётся окну. Так рамка и полукруглые торцы выходят гладкими —
/// прежний путь (SetWindowRgn + GDI-рисование в WM_PAINT) давал рваные края:
/// регионы и GDI не сглаживаются, пользователь забраковал. Размер битмапа
/// каждый раз считается по текущему тексту — пилюля «дышит» по ширине.
///
/// Живёт на ГЛАВНОМ потоке: его цикл сообщений (ServerApp.Run) уже зовёт
/// DispatchMessage, так что WM_TIMER доходит до WndProc без отдельного
/// UI-потока. Все методы дёргать ТОЛЬКО с этого потока. Делегат WndProc держим
/// в поле — иначе GC съест (та же грабля, что у хука, §6.5).
///
/// Значки — глифы встроенного иконочного шрифта Windows «Segoe MDL2 Assets»
/// (микрофон/галочка/крестик) плюс «звёздочка»-мерцалка из дингбатов (символ
/// ожидания); анимацию двигает таймер WM_TIMER.
///
/// Якорь — чуть ПРАВЕЕ ТЕКСТОВОЙ КАРЕТКИ активного окна
/// (GetGUIThreadInfo.rcCaret). Electron/Chromium (VS Code, браузеры) каретку
/// системе не отдают — тогда фолбэк: низ-центр монитора, «как у Whisper».
/// </summary>
internal static class StatusOverlay
{
    /// <summary>Фазы индикатора (в порядке хода диктовки).</summary>
    public enum Phase
    {
        Preparing,    // готовим Firefox/таб ChatGPT — звёздочка цвета ожидания
        Starting,     // таб готов, включаем микрофон — пульсирующий микрофон цвета ожидания
        Recording,    // запись реально пошла — зелёный пульсирующий микрофон
        Transcribing, // стоп нажат, ChatGPT распознаёт — синяя звёздочка
        Done,         // текст вставлен — зелёная галочка, гаснет сама
        Error,        // что-то не вышло — красный крестик, гаснет сам
    }

    // Геометрия (px; процесс DPI-unaware, масштабирование Windows доводит сам).
    // Значок и текст центрируются от Height/2, так что высота меняется без перекосов.
    private const int Height = 36;          // высота пилюли = диаметр компактного кружка
    private const int IconBox = 18;         // квадрат под значок внутри пилюли
    private const int PadLeft = 10;
    private const int IconTextGap = 5;
    private const int PadRight = 10;
    private const int CaretGapX = 3;        // насколько правее каретки вставать (просьба пользователя)
    private const int BottomMargin = 16;    // фолбэк «как у Whisper»: отступ от низа рабочей области
    private const int ScreenEdgePad = 8;    // не прилипать к краям рабочей области
    private const int BgAlpha = 235;        // лёгкая полупрозрачность фона пилюли
    private const float BorderWidth = 2.25f;
    // Глифы галочки/крестика занимают весь em-квадрат MDL2 и выглядят крупнее
    // микрофона — поджимаем масштабом (вертикаль ровняет ink-центрирование, см. DrawGlyph).
    private const float CheckScale = 0.85f;
    private const float CrossScale = 0.78f;

    // Глифы Segoe MDL2 Assets.
    private const string GlyphMic = "";   // Microphone
    private const string GlyphCheck = ""; // CheckMark
    private const string GlyphCross = ""; // Cancel

    // Кадры «звёздочки»-мерцалки (символ ожидания).
    private static readonly string[] StarFrames =
        { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };

    // Цвета — в Config (OverlayCol*): дефолт — тёмная тема, настраиваются --overlay-colors.

    // Шрифт подписи — первый ДОСТУПНЫЙ из цепочки (просьба пользователя: Google Sans;
    // он проприетарный и в Windows его обычно нет — тогда Roboto, ближайший родственник,
    // затем системный Segoe UI). Источника два: приватная папка fonts/ рядом с DLL
    // (.ttf/.otf подхватываются БЕЗ установки в систему — PrivateFontCollection,
    // см. fonts/README.md) и установленные шрифты Windows; приватные — приоритетнее.
    private static readonly string[] TextFontChain = { "Google Sans", "Poppins", "Roboto", "Segoe UI" };

    private static readonly PrivateFontCollection PrivateFonts = LoadPrivateFonts();
    private static readonly Font TextFont = PickFont(TextFontChain, 9.75f, FontStyle.Bold);
    private static readonly Font IconFont = new("Segoe MDL2 Assets", 11f, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font StarFont = new("Segoe UI Symbol", 11f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>Загрузить все .ttf/.otf из папки fonts/ рядом с DLL (без установки в систему).</summary>
    private static PrivateFontCollection LoadPrivateFonts()
    {
        var pfc = new PrivateFontCollection();
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "fonts");
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (!file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                        && !file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                    try { pfc.AddFontFile(file); }
                    catch { /* битый/неподдерживаемый файл — пропускаем */ }
                }
            }
        }
        catch { /* папки нет / нет доступа — работаем на системных */ }
        return pfc;
    }

    /// <summary>
    /// Первый доступный шрифт из списка: сперва приватные (fonts/), затем установленные.
    /// Если у семьи нет запрошенного начертания (у файла нет Bold) — берём Regular,
    /// иначе конструктор Font бросает.
    /// </summary>
    private static Font PickFont(string[] families, float sizePt, FontStyle style)
    {
        foreach (string name in families)
        {
            FontFamily? fam = PrivateFonts.Families.FirstOrDefault(
                f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (fam is null)
            {
                try { fam = new FontFamily(name); }
                catch (ArgumentException) { continue; } // не установлен — следующий
            }
            foreach (FontStyle s in new[] { style, FontStyle.Regular })
                if (fam.IsStyleAvailable(s))
                    return new Font(fam, sizePt, s, GraphicsUnit.Point);
        }
        return new Font(FontFamily.GenericSansSerif, sizePt, style, GraphicsUnit.Point);
    }

    private const long HideTimerId = 1;
    private const long AnimTimerId = 2;

    // Как пилюля привязана к якорю.
    private enum AnchorMode
    {
        CaretRight,   // каретка нашлась: левый край пилюли правее каретки, центр по её высоте
        BottomCenter, // фолбэк «как у Whisper»: низ-центр рабочей области монитора
    }

    private static IntPtr _hwnd;
    private static Native.WndProc? _wndProc;

    private static Phase _phase;
    private static string _text = "";
    private static int _animTick; // кадр анимации (мерцание звёздочки / пульс микрофона)
    private static AnchorMode _anchorMode;
    private static int _anchorX; // CaretRight: левый край и центр по Y; BottomCenter: центр по X и низ
    private static int _anchorY;
    private static int _left;    // итоговая позиция и ширина текущей пилюли
    private static int _top;
    private static int _width;

    /// <summary>Создать окно (один раз, на главном потоке, до цикла сообщений).</summary>
    public static void Create()
    {
        if (!Config.ShowOverlay || _hwnd != IntPtr.Zero) return;

        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = "VoiceBridgeOverlay",
        };
        if (Native.RegisterClassEx(ref wc) == 0)
        {
            Log.Warn(Lang.T("overlay.regclass_fail", Marshal.GetLastWin32Error()));
            return;
        }

        _hwnd = Native.CreateWindowEx(
            Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
            | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT,
            "VoiceBridgeOverlay", "", Native.WS_POPUP,
            0, 0, Height, Height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Warn(Lang.T("overlay.createwin_fail", Marshal.GetLastWin32Error()));
            return;
        }

        Log.Info(Lang.T("overlay.font", TextFont.FontFamily.Name));
    }

    /// <summary>Показать фазу, заякорив пилюлю к окну: каретка → низ-центр монитора.</summary>
    public static void Show(Phase phase, IntPtr anchorWindow)
    {
        if (_hwnd == IntPtr.Zero) return;
        ComputeAnchor(anchorWindow);
        Apply(phase, null);
    }

    /// <summary>Сменить фазу, не двигая пилюлю (текст по умолчанию — или свой).</summary>
    public static void Set(Phase phase, string? customText = null)
    {
        if (_hwnd == IntPtr.Zero) return;
        Apply(phase, customText);
    }

    public static void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.KillTimer(_hwnd, (UIntPtr)HideTimerId);
        Native.KillTimer(_hwnd, (UIntPtr)AnimTimerId);
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
    }

    public static void Destroy()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    // ---------------------------------------------------------------------

    private static void Apply(Phase phase, string? customText)
    {
        _phase = phase;
        _text = customText ?? Lang.T(phase switch
        {
            Phase.Preparing => "overlay.preparing",
            Phase.Starting => "overlay.starting",
            Phase.Recording => "overlay.recording",
            Phase.Transcribing => "overlay.transcribing",
            Phase.Done => "overlay.done",
            _ => "overlay.error",
        });

        // Размер: компактный кружок — или пилюля по ширине подписи.
        _width = Config.OverlayCompact ? Height : PadLeft + IconBox + IconTextGap + MeasureText(_text) + PadRight;

        // Позиция от якоря (смысл координат зависит от режима), кламп в рабочую область монитора.
        if (_anchorMode == AnchorMode.CaretRight)
        {
            _left = _anchorX;             // левый край — сразу правее каретки
            _top = _anchorY - Height / 2; // центр пилюли — на высоте каретки
        }
        else
        {
            _left = _anchorX - _width / 2; // по центру…
            _top = _anchorY - Height;      // …над нижним краем рабочей области
        }
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        IntPtr mon = Native.MonitorFromPoint(new Native.POINT { X = _anchorX, Y = _anchorY }, Native.MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
        {
            _left = Math.Clamp(_left, mi.rcWork.Left + ScreenEdgePad,
                Math.Max(mi.rcWork.Left + ScreenEdgePad, mi.rcWork.Right - _width - ScreenEdgePad));
            _top = Math.Clamp(_top, mi.rcWork.Top + ScreenEdgePad,
                Math.Max(mi.rcWork.Top + ScreenEdgePad, mi.rcWork.Bottom - Height - ScreenEdgePad));
        }

        Render();

        // Позицию и размер задал UpdateLayeredWindow — тут только показ без активации и topmost.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        // Анимация — только пока есть что мерцать/пульсировать.
        bool animated = phase is Phase.Preparing or Phase.Starting or Phase.Recording or Phase.Transcribing;
        if (animated) Native.SetTimer(_hwnd, (UIntPtr)AnimTimerId, Config.OverlayAnimTickMs, IntPtr.Zero);
        else Native.KillTimer(_hwnd, (UIntPtr)AnimTimerId);

        // Финальные фазы гаснут сами; «Распознаю…» — со страховкой (вдруг текст не придёт).
        Native.KillTimer(_hwnd, (UIntPtr)HideTimerId);
        if (phase == Phase.Done)
            Native.SetTimer(_hwnd, (UIntPtr)HideTimerId, Config.OverlayDoneHideMs, IntPtr.Zero);
        else if (phase == Phase.Error)
            Native.SetTimer(_hwnd, (UIntPtr)HideTimerId, Config.OverlayErrorHideMs, IntPtr.Zero);
        else if (phase == Phase.Transcribing)
            Native.SetTimer(_hwnd, (UIntPtr)HideTimerId, Config.OverlayTranscribeStuckMs, IntPtr.Zero);
    }

    /// <summary>
    /// Якорь: каретка окна (пилюля чуть правее курсора ввода) → низ-центр монитора
    /// с окном-целью («как у Whisper» — решение пользователя для случая, когда каретку
    /// не достать: Electron/Chromium её системе не отдают).
    /// </summary>
    private static void ComputeAnchor(IntPtr anchorWindow)
    {
        int monX = Native.GetSystemMetrics(Native.SM_CXSCREEN) / 2; // точка для выбора монитора
        int monY = 0;

        if (anchorWindow != IntPtr.Zero && Native.IsWindow(anchorWindow))
        {
            // 1) Текстовая каретка (решение пользователя: индикатор у места ввода).
            uint tid = Native.GetWindowThreadProcessId(anchorWindow, out _);
            var gti = new Native.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Native.GUITHREADINFO>() };
            if (tid != 0 && Native.GetGUIThreadInfo(tid, ref gti)
                && gti.hwndCaret != IntPtr.Zero && gti.rcCaret.Bottom > gti.rcCaret.Top)
            {
                var pt = new Native.POINT { X = gti.rcCaret.Right, Y = (gti.rcCaret.Top + gti.rcCaret.Bottom) / 2 };
                if (Native.ClientToScreen(gti.hwndCaret, ref pt))
                {
                    _anchorMode = AnchorMode.CaretRight;
                    _anchorX = pt.X + CaretGapX;
                    _anchorY = pt.Y;
                    return;
                }
            }

            // Каретки нет — фолбэк к монитору, на котором окно-цель.
            if (Native.GetWindowRect(anchorWindow, out var r) && r.Right > r.Left)
            {
                monX = (r.Left + r.Right) / 2;
                monY = (r.Top + r.Bottom) / 2;
            }
        }

        // 2) Низ-центр рабочей области монитора (как индикатор Whisper).
        _anchorMode = AnchorMode.BottomCenter;
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        IntPtr mon = Native.MonitorFromPoint(new Native.POINT { X = monX, Y = monY }, Native.MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
        {
            _anchorX = (mi.rcWork.Left + mi.rcWork.Right) / 2;
            _anchorY = mi.rcWork.Bottom - BottomMargin;
        }
        else
        {
            _anchorX = monX;
            _anchorY = Native.GetSystemMetrics(Native.SM_CYSCREEN) - BottomMargin;
        }
    }

    // ------------------------------ Рендер ------------------------------

    /// <summary>Нарисовать пилюлю в ARGB-битмап и отдать окну (UpdateLayeredWindow).</summary>
    private static void Render()
    {
        using var bmp = new Bitmap(_width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Контур-«капсула» с отступом на толщину рамки, чтобы штрих не резался краем битмапа.
            float inset = BorderWidth / 2 + 0.25f;
            using var pill = PillPath(inset, inset, _width - 2 * inset, Height - 2 * inset);
            using (var bg = new SolidBrush(FromColorRef(Config.OverlayColBg, BgAlpha)))
                g.FillPath(bg, pill);
            using (var border = new Pen(FromColorRef(Config.OverlayColBorder), BorderWidth))
                g.DrawPath(border, pill);

            float iconCx = Config.OverlayCompact ? _width / 2f : PadLeft + IconBox / 2f;
            float iconCy = Height / 2f;

            switch (_phase)
            {
                case Phase.Preparing:
                    DrawGlyph(g, StarFont, StarFrame(), iconCx, iconCy, Config.OverlayColWait);
                    break;
                case Phase.Starting:
                    // Пульсирующий микрофон цвета ожидания: «вот-вот»; цвет записи — по её факту.
                    DrawGlyph(g, IconFont, GlyphMic, iconCx, iconCy, Pulse(Config.OverlayColWait));
                    break;
                case Phase.Recording:
                    DrawGlyph(g, IconFont, GlyphMic, iconCx, iconCy, Pulse(Config.OverlayColRec));
                    break;
                case Phase.Transcribing:
                    DrawGlyph(g, StarFont, StarFrame(), iconCx, iconCy, Config.OverlayColBusy);
                    break;
                case Phase.Done:
                    DrawGlyph(g, IconFont, GlyphCheck, iconCx, iconCy, Config.OverlayColRec, CheckScale);
                    break;
                case Phase.Error:
                    DrawGlyph(g, IconFont, GlyphCross, iconCx, iconCy, Config.OverlayColErr, CrossScale);
                    break;
            }

            if (!Config.OverlayCompact)
            {
                using var brush = new SolidBrush(FromColorRef(Config.OverlayColText));
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
                g.DrawString(_text, TextFont, brush, new PointF(PadLeft + IconBox + IconTextGap, Height / 2f), sf);
            }
        }
        Push(bmp);
    }

    /// <summary>Отдать готовый битмап layered-окну: и картинка, и позиция за один вызов.</summary>
    private static void Push(Bitmap bmp)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0)); // ARGB-битмап для UpdateLayeredWindow
        IntPtr old = Native.SelectObject(memDc, hBmp);

        var size = new Native.SIZE { Cx = bmp.Width, Cy = bmp.Height };
        var src = new Native.POINT { X = 0, Y = 0 };
        var dst = new Native.POINT { X = _left, Y = _top };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            SourceConstantAlpha = 255, // прозрачность уже в пикселях (BgAlpha)
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        Native.UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(hBmp);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }

    /// <summary>Капсула: два полукруглых торца + прямые кромки (вырождается в круг при w==h).</summary>
    private static GraphicsPath PillPath(float x, float y, float w, float h)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, h, h, 90, 180);          // левый полукруг
        p.AddArc(x + w - h, y, h, h, 270, 180); // правый полукруг
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// Глиф, ВИЗУАЛЬНО отцентрованный по точке (cx, cy): контур кладётся в GraphicsPath,
    /// берётся его фактический ink-прямоугольник (GetBounds) и центр контура совмещается
    /// с точкой. DrawString так не умеет — он центрирует строчную коробку шрифта
    /// (ascent/descent), а значки MDL2/дингбаты сидят в ней несимметрично, отсюда были
    /// ручные сдвиги под каждый глиф (MicNudgeY). Теперь любой значок любого размера
    /// центрируется автоматически. scale — необязательное уменьшение слишком «жирных»
    /// глифов (галочка/крестик занимают весь em-квадрат).
    /// </summary>
    private static void DrawGlyph(Graphics g, Font font, string glyph, float cx, float cy, uint colorref, float scale = 1f)
    {
        using var path = new GraphicsPath();
        // emSize для AddString — в пикселях: пункты шрифта * dpi / 72.
        path.AddString(glyph, font.FontFamily, (int)font.Style, font.Size * g.DpiY / 72f,
            PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();
        if (ink.Width <= 0 || ink.Height <= 0) return;

        // Порядок (Prepend): сдвиг контура в ноль своим центром → масштаб → перенос в (cx, cy).
        using var m = new Matrix();
        m.Translate(cx, cy);
        m.Scale(scale, scale);
        m.Translate(-(ink.Left + ink.Width / 2f), -(ink.Top + ink.Height / 2f));
        path.Transform(m);

        using var brush = new SolidBrush(FromColorRef(colorref));
        g.FillPath(brush, path);
    }

    /// <summary>Текущий кадр «звёздочки» (каждый 2-й тик, ~160 мс на кадр).</summary>
    private static string StarFrame() => StarFrames[_animTick / 2 % StarFrames.Length];

    private static int MeasureText(string text)
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return (int)Math.Ceiling(g.MeasureString(text, TextFont).Width);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_TIMER)
        {
            if (wParam.ToInt64() == AnimTimerId)
            {
                _animTick++;
                Render();
            }
            else if (wParam.ToInt64() == HideTimerId)
            {
                Hide();
            }
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Мягкая пульсация яркости (период ~1.3 с при кадре 80 мс).</summary>
    private static uint Pulse(uint color)
    {
        double f = 0.65 + 0.35 * (0.5 + 0.5 * Math.Sin(_animTick * 2 * Math.PI / 16));
        return Dim(color, f);
    }

    private static uint Dim(uint color, double f) => Config.Rgb(
        (int)((color & 0xFF) * f), (int)(((color >> 8) & 0xFF) * f), (int)(((color >> 16) & 0xFF) * f));

    /// <summary>COLORREF (0x00BBGGRR) → System.Drawing.Color.</summary>
    private static Color FromColorRef(uint c, int alpha = 255) =>
        Color.FromArgb(alpha, (int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
}
