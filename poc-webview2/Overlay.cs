using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WebView2Poc;

// Win32-добавки для индикатора-«пилюли» (окно с per-pixel alpha, каретка, монитор).
// Базовые объявления — в Interop.cs (Win32). Здесь — только то, что нужно overlay.
internal static partial class Win32
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const int SW_HIDE = 0;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint WM_TIMER = 0x0113;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const uint ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, UIntPtr id);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gti);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pdst, ref SIZE size, IntPtr src, ref POINT psrc, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
}

/// <summary>
/// «Пилюля»-индикатор статуса диктовки у каретки активного окна — перенос из боевого
/// сервера (VoiceBridge.StatusOverlay, §6.17). Окно НЕ крадёт фокус и пропускает клики
/// (WS_EX_NOACTIVATE|TOPMOST|TOOLWINDOW|LAYERED|TRANSPARENT), рисуется per-pixel alpha
/// через UpdateLayeredWindow. Живёт на UI-потоке WinForms (его Application.Run пампит
/// WM_TIMER). Строки и цвета захардкожены (в POC нет Lang/Config); глифы Segoe MDL2.
/// </summary>
internal static class StatusOverlay
{
    public enum Phase { Preparing, Starting, Recording, Transcribing, Done, Error }

    private const int Height = 36, IconBox = 18, PadLeft = 10, IconTextGap = 5, PadRight = 10;
    private const int CaretGapX = 3, BottomMargin = 16, ScreenEdgePad = 8, BgAlpha = 235;
    private const float BorderWidth = 2.25f, CheckScale = 0.85f, CrossScale = 0.78f;
    private const bool Compact = false;
    private const uint AnimTickMs = 80, DoneHideMs = 1200, ErrorHideMs = 2500, TranscribeStuckMs = 30000;

    private const string GlyphMic = "";
    private const string GlyphCheck = "";
    private const string GlyphCross = "";
    private static readonly string[] StarFrames =
        { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };

    private static uint Rgb(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));
    private static readonly uint ColBg = Rgb(32, 32, 34);
    private static readonly uint ColBorder = Rgb(255, 255, 255);
    private static readonly uint ColText = Rgb(240, 240, 240);
    private static readonly uint ColWait = Rgb(255, 165, 0);
    private static readonly uint ColRec = Rgb(90, 205, 100);
    private static readonly uint ColBusy = Rgb(80, 155, 255);
    private static readonly uint ColErr = Rgb(235, 80, 80);

    private static readonly string[] TextFontChain = { "Google Sans", "Poppins", "Roboto", "Segoe UI" };
    private static readonly PrivateFontCollection PrivateFonts = LoadPrivateFonts();
    private static readonly Font TextFont = PickFont(TextFontChain, 9.75f, FontStyle.Bold);
    private static readonly Font IconFont = new("Segoe MDL2 Assets", 11f, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font StarFont = new("Segoe UI Symbol", 11f, FontStyle.Regular, GraphicsUnit.Point);

    private static PrivateFontCollection LoadPrivateFonts()
    {
        var pfc = new PrivateFontCollection();
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "fonts");
            if (Directory.Exists(dir))
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (!file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                        && !file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                    try { pfc.AddFontFile(file); } catch { }
                }
        }
        catch { }
        return pfc;
    }

    private static Font PickFont(string[] families, float sizePt, FontStyle style)
    {
        foreach (string name in families)
        {
            FontFamily? fam = PrivateFonts.Families.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (fam is null)
            {
                try { fam = new FontFamily(name); }
                catch (ArgumentException) { continue; }
            }
            foreach (FontStyle s in new[] { style, FontStyle.Regular })
                if (fam.IsStyleAvailable(s))
                    return new Font(fam, sizePt, s, GraphicsUnit.Point);
        }
        return new Font(FontFamily.GenericSansSerif, sizePt, style, GraphicsUnit.Point);
    }

    private const long HideTimerId = 1, AnimTimerId = 2;
    private enum AnchorMode { CaretRight, BottomCenter }

    private static IntPtr _hwnd;
    private static Win32.WndProc? _wndProc;
    private static Phase _phase;
    private static string _text = "";
    private static int _animTick;
    private static AnchorMode _anchorMode;
    private static int _anchorX, _anchorY, _left, _top, _width;

    public static void Create()
    {
        if (_hwnd != IntPtr.Zero) return;
        _wndProc = WndProc;
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = Win32.GetModuleHandle(null),
            lpszClassName = "WebView2PocOverlay",
        };
        if (Win32.RegisterClassEx(ref wc) == 0) { Diag.Write("overlay: RegisterClassEx fail " + Marshal.GetLastWin32Error()); return; }

        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT,
            "WebView2PocOverlay", "", Win32.WS_POPUP, 0, 0, Height, Height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { Diag.Write("overlay: CreateWindowEx fail " + Marshal.GetLastWin32Error()); return; }
        Diag.Write("overlay: создан, шрифт подписи = " + TextFont.FontFamily.Name);
    }

    public static void Show(Phase phase, IntPtr anchorWindow)
    {
        if (_hwnd == IntPtr.Zero) return;
        ComputeAnchor(anchorWindow);
        Apply(phase, null);
    }

    public static void Set(Phase phase, string? customText = null)
    {
        if (_hwnd == IntPtr.Zero) return;
        Apply(phase, customText);
    }

    public static void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.KillTimer(_hwnd, (UIntPtr)HideTimerId);
        Win32.KillTimer(_hwnd, (UIntPtr)AnimTimerId);
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
    }

    public static void Destroy()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static void Apply(Phase phase, string? customText)
    {
        _phase = phase;
        _text = customText ?? phase switch
        {
            Phase.Preparing => Lang.T("overlay.preparing"),
            Phase.Starting => Lang.T("overlay.starting"),
            Phase.Recording => Lang.T("overlay.recording"),
            Phase.Transcribing => Lang.T("overlay.transcribing"),
            Phase.Done => Lang.T("overlay.done"),
            _ => Lang.T("overlay.error"),
        };

        _width = Compact ? Height : PadLeft + IconBox + IconTextGap + MeasureText(_text) + PadRight;

        if (_anchorMode == AnchorMode.CaretRight)
        {
            _left = _anchorX;
            _top = _anchorY - Height / 2;
        }
        else
        {
            _left = _anchorX - _width / 2;
            _top = _anchorY - Height;
        }
        var mi = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        IntPtr mon = Win32.MonitorFromPoint(new Win32.POINT { X = _anchorX, Y = _anchorY }, Win32.MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero && Win32.GetMonitorInfo(mon, ref mi))
        {
            _left = Math.Clamp(_left, mi.rcWork.Left + ScreenEdgePad, Math.Max(mi.rcWork.Left + ScreenEdgePad, mi.rcWork.Right - _width - ScreenEdgePad));
            _top = Math.Clamp(_top, mi.rcWork.Top + ScreenEdgePad, Math.Max(mi.rcWork.Top + ScreenEdgePad, mi.rcWork.Bottom - Height - ScreenEdgePad));
        }

        Render();
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        bool animated = phase is Phase.Preparing or Phase.Starting or Phase.Recording or Phase.Transcribing;
        if (animated) Win32.SetTimer(_hwnd, (UIntPtr)AnimTimerId, AnimTickMs, IntPtr.Zero);
        else Win32.KillTimer(_hwnd, (UIntPtr)AnimTimerId);

        Win32.KillTimer(_hwnd, (UIntPtr)HideTimerId);
        if (phase == Phase.Done) Win32.SetTimer(_hwnd, (UIntPtr)HideTimerId, DoneHideMs, IntPtr.Zero);
        else if (phase == Phase.Error) Win32.SetTimer(_hwnd, (UIntPtr)HideTimerId, ErrorHideMs, IntPtr.Zero);
        else if (phase == Phase.Transcribing) Win32.SetTimer(_hwnd, (UIntPtr)HideTimerId, TranscribeStuckMs, IntPtr.Zero);
    }

    private static void ComputeAnchor(IntPtr anchorWindow)
    {
        int monX = Win32.GetSystemMetrics(Win32.SM_CXSCREEN) / 2;
        int monY = 0;

        if (anchorWindow != IntPtr.Zero && Win32.IsWindow(anchorWindow))
        {
            uint tid = Win32.GetWindowThreadProcessId(anchorWindow, out _);
            var gti = new Win32.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Win32.GUITHREADINFO>() };
            if (tid != 0 && Win32.GetGUIThreadInfo(tid, ref gti)
                && gti.hwndCaret != IntPtr.Zero && gti.rcCaret.Bottom > gti.rcCaret.Top)
            {
                var pt = new Win32.POINT { X = gti.rcCaret.Right, Y = (gti.rcCaret.Top + gti.rcCaret.Bottom) / 2 };
                if (Win32.ClientToScreen(gti.hwndCaret, ref pt))
                {
                    _anchorMode = AnchorMode.CaretRight;
                    _anchorX = pt.X + CaretGapX;
                    _anchorY = pt.Y;
                    return;
                }
            }
            if (Win32.GetWindowRect(anchorWindow, out var r) && r.Right > r.Left)
            {
                monX = (r.Left + r.Right) / 2;
                monY = (r.Top + r.Bottom) / 2;
            }
        }

        _anchorMode = AnchorMode.BottomCenter;
        var mi = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        IntPtr mon = Win32.MonitorFromPoint(new Win32.POINT { X = monX, Y = monY }, Win32.MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero && Win32.GetMonitorInfo(mon, ref mi))
        {
            _anchorX = (mi.rcWork.Left + mi.rcWork.Right) / 2;
            _anchorY = mi.rcWork.Bottom - BottomMargin;
        }
        else
        {
            _anchorX = monX;
            _anchorY = Win32.GetSystemMetrics(Win32.SM_CYSCREEN) - BottomMargin;
        }
    }

    private static void Render()
    {
        using var bmp = new Bitmap(_width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float inset = BorderWidth / 2 + 0.25f;
            using var pill = PillPath(inset, inset, _width - 2 * inset, Height - 2 * inset);
            using (var bg = new SolidBrush(FromColorRef(ColBg, BgAlpha))) g.FillPath(bg, pill);
            using (var border = new Pen(FromColorRef(ColBorder), BorderWidth)) g.DrawPath(border, pill);

            float iconCx = Compact ? _width / 2f : PadLeft + IconBox / 2f;
            float iconCy = Height / 2f;
            switch (_phase)
            {
                case Phase.Preparing: DrawGlyph(g, StarFont, StarFrame(), iconCx, iconCy, ColWait); break;
                case Phase.Starting: DrawGlyph(g, IconFont, GlyphMic, iconCx, iconCy, Pulse(ColWait)); break;
                case Phase.Recording: DrawGlyph(g, IconFont, GlyphMic, iconCx, iconCy, Pulse(ColRec)); break;
                case Phase.Transcribing: DrawGlyph(g, StarFont, StarFrame(), iconCx, iconCy, ColBusy); break;
                case Phase.Done: DrawGlyph(g, IconFont, GlyphCheck, iconCx, iconCy, ColRec, CheckScale); break;
                case Phase.Error: DrawGlyph(g, IconFont, GlyphCross, iconCx, iconCy, ColErr, CrossScale); break;
            }

            if (!Compact)
            {
                using var brush = new SolidBrush(FromColorRef(ColText));
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
                g.DrawString(_text, TextFont, brush, new PointF(PadLeft + IconBox + IconTextGap, Height / 2f), sf);
            }
        }
        Push(bmp);
    }

    private static void Push(Bitmap bmp)
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Win32.SelectObject(memDc, hBmp);

        var size = new Win32.SIZE { Cx = bmp.Width, Cy = bmp.Height };
        var src = new Win32.POINT { X = 0, Y = 0 };
        var dst = new Win32.POINT { X = _left, Y = _top };
        var blend = new Win32.BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };
        Win32.UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Win32.ULW_ALPHA);

        Win32.SelectObject(memDc, old);
        Win32.DeleteObject(hBmp);
        Win32.DeleteDC(memDc);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static GraphicsPath PillPath(float x, float y, float w, float h)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, h, h, 90, 180);
        p.AddArc(x + w - h, y, h, h, 270, 180);
        p.CloseFigure();
        return p;
    }

    private static void DrawGlyph(Graphics g, Font font, string glyph, float cx, float cy, uint colorref, float scale = 1f)
    {
        using var path = new GraphicsPath();
        path.AddString(glyph, font.FontFamily, (int)font.Style, font.Size * g.DpiY / 72f, PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();
        if (ink.Width <= 0 || ink.Height <= 0) return;

        using var m = new Matrix();
        m.Translate(cx, cy);
        m.Scale(scale, scale);
        m.Translate(-(ink.Left + ink.Width / 2f), -(ink.Top + ink.Height / 2f));
        path.Transform(m);

        using var brush = new SolidBrush(FromColorRef(colorref));
        g.FillPath(brush, path);
    }

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
        if (msg == Win32.WM_TIMER)
        {
            if (wParam.ToInt64() == AnimTimerId) { _animTick++; Render(); }
            else if (wParam.ToInt64() == HideTimerId) Hide();
            return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static uint Pulse(uint color)
    {
        double f = 0.65 + 0.35 * (0.5 + 0.5 * Math.Sin(_animTick * 2 * Math.PI / 16));
        return Dim(color, f);
    }

    private static uint Dim(uint color, double f) =>
        Rgb((int)((color & 0xFF) * f), (int)(((color >> 8) & 0xFF) * f), (int)(((color >> 16) & 0xFF) * f));

    private static Color FromColorRef(uint c, int alpha = 255) =>
        Color.FromArgb(alpha, (int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
}
