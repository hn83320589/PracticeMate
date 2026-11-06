using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PracticeMate.App.Controls;

/// <summary>調音器指針：±50 音分對應 ±45°，刻度每 10 音分一格，中央 ±5 音分為「準」區。</summary>
public sealed class TunerGauge : FrameworkElement
{
    public static readonly DependencyProperty NeedleAngleProperty = DependencyProperty.Register(
        nameof(NeedleAngle), typeof(double), typeof(TunerGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasPitchProperty = DependencyProperty.Register(
        nameof(HasPitch), typeof(bool), typeof(TunerGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsInTuneProperty = DependencyProperty.Register(
        nameof(IsInTune), typeof(bool), typeof(TunerGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

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
        var center = new Point(w / 2, h * 0.92);
        var radius = Math.Min(w / 2 * 0.9, h * 0.85);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 「準」區
        var inTune = new StreamGeometry();
        using (var g = inTune.Open())
        {
            g.BeginFigure(center, true, true);
            g.LineTo(PointAt(center, radius, -4.5), false, false);
            g.ArcTo(PointAt(center, radius, 4.5), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
        }
        inTune.Freeze();
        drawingContext.DrawGeometry(IsInTune && HasPitch ? Palette.Good : Palette.Panel, null, inTune);

        for (var cents = -50; cents <= 50; cents += 10)
        {
            var angle = cents / 50.0 * 45;
            var inner = cents % 50 == 0 ? 0.82 : 0.88;
            drawingContext.DrawLine(Palette.GridPen, PointAt(center, radius * inner, angle), PointAt(center, radius, angle));
            var label = new FormattedText(cents.ToString("+0;−0;0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Palette.Typeface, 12, Palette.Muted, dpi);
            var p = PointAt(center, radius * 0.74, angle);
            drawingContext.DrawText(label, new Point(p.X - (label.Width / 2), p.Y - (label.Height / 2)));
        }

        if (HasPitch)
        {
            var color = IsInTune ? Palette.Good : Math.Abs(NeedleAngle) < 13.5 ? Palette.Warn : Palette.Bad;
            drawingContext.DrawLine(Palette.FrozenPen(color, 4), center, PointAt(center, radius * 0.95, NeedleAngle));
        }
        drawingContext.DrawEllipse(Palette.Text, null, center, 6, 6);
    }

    /// <summary>角度 0 為正上方，正值往右。</summary>
    private static Point PointAt(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Sin(rad)), center.Y - (radius * Math.Cos(rad)));
    }
}
