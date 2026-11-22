using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WebView2Poc;

/// <summary>
/// Тёмный вид меню трея в стиле программы (решение пользователя 22.11.2026: стандартное меню WinForms
/// «выглядит как из конца восьмидесятых»). ContextMenuStrip рисует себя сам и на Windows 11 не обновляется,
/// поэтому отрисовка своя: тёмный фон как у пилюли и плавающих кнопок, скруглённая подсветка пункта под мышью,
/// голубая галочка режима, подписи клавиш приглушённым цветом. Скругление углов и рамку окна даёт DWM
/// Windows 11 (DWMWA_WINDOW_CORNER_PREFERENCE / DWMWA_BORDER_COLOR); на Windows 10 атрибутов нет - углы прямые.
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Bg = Color.FromArgb(38, 38, 44);
    private static readonly Color Hover = Color.FromArgb(62, 62, 72);
    private static readonly Color Text = Color.FromArgb(230, 230, 234);
    private static readonly Color Muted = Color.FromArgb(138, 138, 150);
    private static readonly Color Disabled = Color.FromArgb(100, 100, 110);
    private static readonly Color Line = Color.FromArgb(58, 58, 68);
    private static readonly Color Accent = Color.FromArgb(0x81, 0xd4, 0xfa);
    private static readonly Color Border = Color.FromArgb(80, 80, 92);

    public TrayMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    /// <summary>Настроить меню: шрифт, отступы, тёмный вид, скругление окна.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new TrayMenuRenderer();
        menu.Font = PickFont();
        menu.BackColor = Bg;
        menu.ForeColor = Text;
        menu.Padding = new Padding(3, 3, 3, 3);
        menu.ShowImageMargin = true;   // в поле значка рисуем галочку режима
        menu.HandleCreated += (_, _) => RoundWindow(menu.Handle);
    }

    /// <summary>Отступы пункта: выше стандартного, текст не липнет к краям.</summary>
    public static void StyleItem(ToolStripItem it)
    {
        it.Padding = new Padding(0, 3, 4, 3);
        if (it is ToolStripMenuItem mi) mi.ForeColor = Text;
    }

    // Шрифт - Montserrat Regular 8pt, как у справки, но мельче и не жирный (решения пользователя 22.11.2026:
    // Medium 9.5pt показался жирным и крупным, Segoe UI Variable - «калечный»). Переменный файл справки WinForms
    // не годится: GDI/GDI+ берут его начертание по умолчанию, а у Montserrat это Thin (вес 100), поэтому
    // отдельный статичный fonts/Montserrat-Regular.ttf (OFL, репозиторий шрифта JulietaUla/Montserrat).
    // Грузим на два пути, как UiFonts: GDI+ - для Font, GDI (AddFontResourceEx) - ToolStrip меряет и
    // TextRenderer рисует текст по имени шрифта. Нет файла - Segoe UI.
    private static readonly System.Drawing.Text.PrivateFontCollection Private = new();

    private static Font PickFont()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "fonts", "Montserrat-Regular.ttf");
        try
        {
            if (File.Exists(file))
            {
                Private.AddFontFile(file);
                Win32.AddFontResourceEx(file, Win32.FR_PRIVATE, IntPtr.Zero);
                var fam = Private.Families.FirstOrDefault();
                if (fam is not null) { Diag.Write("шрифт меню трея: " + fam.Name + " (fonts/)"); return new Font(fam, 8f, FontStyle.Regular, GraphicsUnit.Point); }
            }
        }
        catch (Exception ex) { Diag.Write("шрифт меню трея: не загрузился " + file + ": " + ex.Message); }
        return new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    private static void RoundWindow(IntPtr h)
    {
        try
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int border = Border.R | (Border.G << 8) | (Border.B << 16);   // COLORREF: 0x00BBGGRR
            DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch { /* нет dwmapi или атрибута (Windows 10) - остаются прямые углы */ }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Bg);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { /* рамку рисует DWM */ }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { /* серой полосы слева нет - фон общий */ }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var path = RoundRect(r, 6);
        using var b = new SolidBrush(Hover);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Подпись клавиш приходит отдельным вызовом с текстом ShortcutKeyDisplayString - её приглушаем.
        bool shortcut = e.Item is ToolStripMenuItem mi && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)
                        && e.Text == mi.ShortcutKeyDisplayString && e.Text != mi.Text;
        var color = !e.Item.Enabled ? Disabled : shortcut ? Muted : Text;
        // Рисуем сами по центру ВСЕЙ высоты пункта: базовый отрисовщик ставит текст в свой прямоугольник
        // от верхнего отступа, и в подсветке он сидел выше середины (замечание пользователя 22.11.2026).
        // Подпись клавиш приходит с тем же прямоугольником, что и название, но с выравниванием вправо - флаги
        // выравнивания по горизонтали берём из e.TextFormat (без этого подпись легла поверх названия).
        var r = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, e.Item.Height);
        var flags = (e.TextFormat & (TextFormatFlags.Right | TextFormatFlags.HorizontalCenter))
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont ?? e.Item.Font, r, color, flags);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Голубая галочка без рамки-коробки, по центру поля значка.
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var pen = new Pen(Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4) });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Line);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    private static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Цвета для того, что базовый отрисовщик всё же рисует сам (фон выпадающего списка и т.п.).</summary>
    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }
}
