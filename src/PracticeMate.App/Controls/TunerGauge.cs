using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PracticeMate.App.Controls;

/// <summary>
/// 調音器指針：±50 音分對應 ±45° 的弧形刻度帶，每 10 音分一格；
/// 帶上中央 ±5 音分為「準」區（準時亮綠），指針顏色依偏差轉黃、轉紅。沒有音高時不畫指針。
/// </summary>
public sealed class TunerGauge : FrameworkElement
{
    public static readonly DependencyProperty NeedleAngleProperty = DependencyProperty.Register(
        nameof(NeedleAngle), typeof(double), typeof(TunerGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasPitchProperty = DependencyProperty.Register(
        nameof(HasPitch), typeof(bool), typeof(TunerGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsInTuneProperty = DependencyProperty.Register(
        nameof(IsInTune), typeof(bool), typeof(TunerGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double SweepDegrees = 45;
    private const double InTuneDegrees = 4.5; // ±5 音分

    private static readonly Brush InTuneIdle = CreateBrush(Color.FromRgb(0x2B, 0x4D, 0x3D));

    public double NeedleAngle
    {
        get => (double)GetValue(NeedleAngleProperty);
        set => SetValue(NeedleAngleProperty, value);
    }

    public bool HasPitch
    {
        get => (bool)GetValue(HasPitchProperty);
        set => SetValue(HasPitchProperty, value);
    }

    public bool IsInTune
    {
        get => (bool)GetValue(IsInTuneProperty);
        set => SetValue(IsInTuneProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        drawingContext.DrawRectangle(Palette.Background, null, new Rect(0, 0, w, h));
        // 弧的寬度 = 2·r·sin45°，高度 ≈ r：依可用空間取最大半徑
        var radius = Math.Max(10, Math.Min(w * 0.9 / (2 * Math.Sin(SweepDegrees * Math.PI / 180)), h * 0.78));
        var center = new Point(w / 2, (h / 2) + (radius * 0.55));
        var band = Math.Max(8, radius * 0.06);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DrawArc(drawingContext, center, radius, -SweepDegrees, SweepDegrees, Palette.Panel, band);
        DrawArc(drawingContext, center, radius, -InTuneDegrees, InTuneDegrees, IsInTune && HasPitch ? Palette.Good : InTuneIdle, band);

        for (var cents = -50; cents <= 50; cents += 10)
        {
            var angle = cents / 50.0 * SweepDegrees;
            var major = cents % 50 == 0 || cents == 0;
            drawingContext.DrawLine(Palette.GridPen, PointAt(center, radius + (band / 2), angle), PointAt(center, radius + (band / 2) + (major ? 14 : 8), angle));
            var label = new FormattedText(cents.ToString("+0;−0;0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Palette.Typeface, major ? 14 : 12, major ? Palette.Text : Palette.Muted, dpi);
            var p = PointAt(center, radius + (band / 2) + 30, angle);
            drawingContext.DrawText(label, new Point(p.X - (label.Width / 2), p.Y - (label.Height / 2)));
        }

        if (HasPitch)
        {
            var color = IsInTune ? Palette.Good : Math.Abs(NeedleAngle) < 13.5 ? Palette.Warn : Palette.Bad;
            var pen = new Pen(color, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            drawingContext.DrawLine(pen, PointAt(center, radius * 0.25, NeedleAngle), PointAt(center, radius + (band / 2), NeedleAngle));
        }
        drawingContext.DrawEllipse(Palette.Grid, null, PointAt(center, radius * 0.25, 0), 5, 5);
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double from, double to, Brush brush, double thickness)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(center, radius, from), false, false);
            ctx.ArcTo(PointAt(center, radius, to), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    /// <summary>角度 0 為正上方，正值往右。</summary>
    private static Point PointAt(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Sin(rad)), center.Y - (radius * Math.Cos(rad)));
    }

    private static SolidColorBrush CreateBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
