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
    public const string FontName = "Rubik";          // круглый, с кириллицей (выбран пользователем)
    public static Font Ui() => new(FontName, 13f);
}

/// <summary>MaterialSkin вшит на Roboto. Рефлексией подменяем семейство и размер на двух путях:
/// GDI+-текст (getFontByType → RobotoFontFamilies) и нативный текст (getLogFontByType → logicalFonts).
/// Звать ОДИН раз на старте, ДО создания контролов. Привязано к MaterialSkin.2 2.3.1 (версию пиним).</summary>
internal static class MaterialFonts
{
    public static void Apply(string family, int size)
    {
        var mgr = MaterialSkinManager.Instance;
        var t = typeof(MaterialSkinManager);
        var of = new FontFamily(family);

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

/// <summary>MaterialComboBox, у которого пункты выпадающего списка нарисованы единым шрифтом
/// (13pt). MaterialSkin рисует их через getFontByType(Subtitle1)=16pt мимо нашего хака — переопределяем.
/// Закрытое поле рисует сам MaterialComboBox (там уже 13pt через logicalFonts) — его не трогаем.</summary>
internal sealed class SmallComboBox : MaterialComboBox
{
    private readonly Font _font = new(Palette.FontName, 13f);

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        base.OnMeasureItem(e);
        e.ItemHeight = 26;   // компактнее (у MaterialSkin пункт ~43px под 16pt)
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
