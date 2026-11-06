using System.Windows.Media;

namespace PracticeMate.App.Controls;

/// <summary>自訂繪製控制項共用的顏色（凍結以便跨執行緒使用並提升效能）。</summary>
internal static class Palette
{
    public static readonly Brush Background = Frozen(Color.FromRgb(0x1E, 0x22, 0x2A));
    public static readonly Brush Panel = Frozen(Color.FromRgb(0x27, 0x2C, 0x36));
    public static readonly Brush Grid = Frozen(Color.FromRgb(0x3A, 0x40, 0x4C));
    public static readonly Brush Text = Frozen(Color.FromRgb(0xD8, 0xDE, 0xE9));
    public static readonly Brush Muted = Frozen(Color.FromRgb(0x8A, 0x93, 0xA3));
    public static readonly Brush Waveform = Frozen(Color.FromRgb(0x5A, 0xB4, 0xF0));
    public static readonly Brush Good = Frozen(Color.FromRgb(0x4C, 0xC3, 0x8A));
    public static readonly Brush Warn = Frozen(Color.FromRgb(0xF2, 0xB8, 0x4B));
    public static readonly Brush Bad = Frozen(Color.FromRgb(0xEF, 0x6B, 0x6B));
    public static readonly Brush Accent = Frozen(Color.FromRgb(0xB0, 0x8C, 0xF5));

    public static readonly Pen GridPen = FrozenPen(Grid, 1);
    public static readonly Pen ZeroPen = FrozenPen(Muted, 1);

    public static readonly Typeface Typeface = new("Microsoft JhengHei UI");

    /// <summary>依偏差大小（相對於「好/警告」門檻）挑顏色。</summary>
    public static Brush ForDeviation(double absValue, double good, double warn) =>
        absValue <= good ? Good : absValue <= warn ? Warn : Bad;

    public static Pen FrozenPen(Brush brush, double thickness)
    {
        var p = new Pen(brush, thickness);
        p.Freeze();
        return p;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
