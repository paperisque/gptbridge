using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;

namespace WebView2Poc;

/// <summary>Палитра и единый шрифт тулбара. Меняется одним местом.</summary>
internal static class Palette
{
    public static readonly Color Bg      = Color.FromArgb(30, 30, 34);
    public static readonly Color Surface = Color.FromArgb(46, 46, 52);
    public static readonly Color Border  = Color.FromArgb(74, 74, 82);
    public static readonly Color Text    = Color.FromArgb(230, 230, 232);
    public static readonly Color Accent  = Color.FromArgb(64, 156, 255);
    // Rubik — круглый, с кириллицей (выбран пользователем); файл едет с программой в fonts/.
    public static readonly FontFamily Family = UiFonts.Resolve("Rubik", "Segoe UI");
    public static Font Ui() => new(Family, 13f);
}

/// <summary>Шрифт тулбара без установки в систему. Грабля: в 0.1.3 Rubik брался только из
/// установленных шрифтов — где его нет, new FontFamily бросал и программа падала на старте.
/// Грузим файл fonts/&lt;Имя&gt;-Regular.ttf на ДВА пути: GDI+ (PrivateFontCollection — DrawString,
/// Font(FontFamily)) и GDI (AddFontResourceEx FR_PRIVATE — MaterialSkin CreateFontIndirect и TextRenderer
/// ищут шрифт по имени). Нет файла — установленный шрифт с тем же именем, нет и его — запасной.</summary>
internal static class UiFonts
{
    private static readonly PrivateFontCollection Private = new(); // держим живой: семейства ссылаются на неё

    public static FontFamily Resolve(string name, string fallback)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "fonts", name + "-Regular.ttf");
        try
        {
            if (File.Exists(file))
            {
                Private.AddFontFile(file);
                Win32.AddFontResourceEx(file, Win32.FR_PRIVATE, IntPtr.Zero);
                var fam = Private.Families.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (fam is not null) { Diag.Write("шрифт тулбара: " + name + " (fonts/)"); return fam; }
            }
        }
        catch (Exception ex) { Diag.Write("шрифт тулбара: не загрузился " + file + ": " + ex.Message); }

        try { var fam = new FontFamily(name); Diag.Write("шрифт тулбара: " + name + " (системный)"); return fam; }
        catch (ArgumentException) { }
        Diag.Write("шрифт тулбара: " + name + " не найден, беру " + fallback);
        return new FontFamily(fallback);
    }
}

/// <summary>MaterialSkin вшит на Roboto. Рефлексией подменяем семейство и размер на двух путях:
/// GDI+-текст (getFontByType → RobotoFontFamilies) и нативный текст (getLogFontByType → logicalFonts).
/// Звать ОДИН раз на старте, ДО создания контролов. Привязано к MaterialSkin.2 2.3.1 (версию пиним).</summary>
internal static class MaterialFonts
{
    public static void Apply(FontFamily of, int size)
    {
        var mgr = MaterialSkinManager.Instance;
        var t = typeof(MaterialSkinManager);
        string family = of.Name;   // нативный путь ищет по имени (приватный шрифт виден через AddFontResourceEx)

        var fams = (System.Collections.IDictionary)t
            .GetField("RobotoFontFamilies", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mgr)!;
        foreach (var k in fams.Keys.Cast<object>().ToArray())
            fams[k] = of;

        var dict = (System.Collections.IDictionary)t
            .GetField("logicalFonts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mgr)!;
        var make = t.GetMethod("createLogicalFont", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        var wt = make.GetParameters()[2].ParameterType;
        object weight = Enum.Parse(wt, Array.Find(Enum.GetNames(wt), n => n.Contains("Regular")) ?? Enum.GetNames(wt)[0]);
        object hf = make.Invoke(mgr, new object[] { family, size, weight, (byte)0 })!;
        foreach (var k in new[] { "Button", "Body1", "Body2", "Subtitle1", "Subtitle2", "SubtleEmphasis", "Caption", "Overline" })
            if (dict.Contains(k)) dict[k] = hf;
    }
}

/// <summary>MaterialComboBox, у которого пункты выпадающего списка нарисованы тем же шрифтом, что и
/// закрытое поле. MaterialSkin рисует их через getFontByType(Subtitle1)=16pt мимо нашего хака — переопределяем.
/// Закрытое поле рисует сам MaterialComboBox (logicalFonts, 13 px) — его не трогаем.
/// Грабля: logicalFonts задают размер в ПИКСЕЛЯХ (createLogicalFont(13) → lfHeight=-13), а new Font(…, 13f)
/// — в ПУНКТАХ (≈17 px): список выходил на треть крупнее поля. Поэтому здесь явно GraphicsUnit.Pixel.</summary>
internal sealed class SmallComboBox : MaterialComboBox
{
    private readonly Font _font = new(Palette.Family, 13f, FontStyle.Regular, GraphicsUnit.Pixel);

    // Высота закрытого поля. MaterialSkin знает только 36 (компакт) / 50 (UseTallSize) и навязывает их
    // в setHeightVars на КАЖДОМ OnResize/OnCreateControl: HEIGHT, LINE_Y=HEIGHT-3 (линия снизу),
    // ItemHeight=HEIGHT-7. Фактическая высота DropDownList = ItemHeight + 6 (рамки) → у него выходит 35.
    // Перебиваем эти поля своими ПОСЛЕ него (рефлексия; MaterialSkin.2 2.3.1 пиним, см. MaterialFonts).
    private const int FieldPx = 30;
    private const int ListItemPx = 26;   // пункт раскрытого списка (OwnerDrawVariable → OnMeasureItem)
    private static readonly FieldInfo HeightField = typeof(MaterialComboBox).GetField("HEIGHT", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo LineYField = typeof(MaterialComboBox).GetField("LINE_Y", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private bool _sizing;

    public SmallComboBox()
    {
        UseTallSize = false;   // база — компактный режим MaterialSkin; дальше ужимаем до FieldPx
        MaxDropDownItems = 8;  // у MaterialSkin 4 — список микрофонов вдвое выше до прокрутки
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        ApplyHeight();
    }

    protected override void OnResize(EventArgs e)
    {
        if (_sizing) return;   // наш же пересчёт — не пускать MaterialSkin, иначе он вернёт свои 36
        base.OnResize(e);
        ApplyHeight();
    }

    private void ApplyHeight()
    {
        _sizing = true;
        try
        {
            HeightField.SetValue(this, FieldPx);
            LineYField.SetValue(this, FieldPx - 3);
            ItemHeight = FieldPx - 6;
            DropDownHeight = ListItemPx * MaxDropDownItems + 2;
        }
        finally { _sizing = false; }
        Invalidate();
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        base.OnMeasureItem(e);
        e.ItemHeight = ListItemPx;   // компактнее (у MaterialSkin пункт ~43px под 16pt)
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit)
        {
            base.OnDrawItem(e);   // закрытое поле — как рисует MaterialSkin
            return;
        }
        bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var bg = new SolidBrush(sel ? Color.FromArgb(64, 64, 74) : Palette.Surface))
            e.Graphics.FillRectangle(bg, e.Bounds);
        var r = e.Bounds; r.X += 8; r.Width -= 10;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), _font, r, Palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>MaterialSlider с маркером поменьше (16 px вместо 20). У MaterialSkin размеры зашиты числами в закрытом
/// RecalcutlateIndicator (маркер 20, ореол наведения/нажатия 40, центр = _indicatorRectangle.X + 10);
/// поля _thumbRadius отрисовка НЕ читает. Поэтому перед каждой отрисовкой пересчитываем оба круга от того
/// же центра (от нетронутого _indicatorRectangle — повторный OnPaint без пересчёта не ужимает дважды).</summary>
internal sealed class SmallThumbSlider : MaterialSlider
{
    private const int ThumbPx = 16, HaloPx = 31;   // было 20 / 40 (подобрано с пользователем)
    private static readonly FieldInfo Ind = Field("_indicatorRectangle");
    private static readonly FieldInfo Normal = Field("_indicatorRectangleNormal");
    private static readonly FieldInfo Pressed = Field("_indicatorRectanglePressed");

    private static FieldInfo Field(string name) =>
        typeof(MaterialSlider).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    protected override void OnPaint(PaintEventArgs e)
    {
        var ind = (Rectangle)Ind.GetValue(this)!;
        int cx = ind.X + 10, cy = Height / 2;
        Normal.SetValue(this, new Rectangle(cx - ThumbPx / 2, cy - ThumbPx / 2, ThumbPx, ThumbPx));
        Pressed.SetValue(this, new Rectangle(cx - HaloPx / 2, cy - HaloPx / 2, HaloPx, HaloPx));
        base.OnPaint(e);
    }
}

/// <summary>Иконка «усиление громкости микрофона»: микрофон + расходящиеся волны справа.</summary>
internal sealed class MicGainIcon : Control
{
    private readonly Font _glyph = new("Segoe MDL2 Assets", 12f);

    public MicGainIcon()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Bg;
        Size = new Size(26, 24);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Bg);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        // микрофончик (Segoe MDL2 E720) слева
        TextRenderer.DrawText(g, "", _glyph, new Rectangle(0, 0, 15, Height), Palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        // расходящиеся волны справа = «громкость увеличивается»
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = 14, cy = Height / 2f;
        int[] alpha = { 255, 150, 80 };   // затухание: ярче → тусклее
        for (int i = 0; i < 3; i++)
        {
            using var pen = new Pen(Color.FromArgb(alpha[i], Palette.Text), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float r = (i + 1) * 3.8f;
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -45, 90);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _glyph.Dispose(); base.Dispose(disposing); }
}

// (Угловые кнопки «тулбар/справка» теперь впрыскиваются в страницу — OverlayInjectScript в MainForm;
//  прежний WinForms-RoundIconButton не нужен.)
