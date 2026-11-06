using System.Windows;
using System.Windows.Media;

namespace PracticeMate.App.Controls;

/// <summary>水平輸入電平表：底色為 RMS，亮線為峰值；−6 dBFS 以上轉黃、−1 dBFS 以上轉紅（刻度 −60 到 0 dBFS）。</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty PeakFractionProperty = DependencyProperty.Register(
        nameof(PeakFraction), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RmsFractionProperty = DependencyProperty.Register(
        nameof(RmsFraction), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PeakFraction
    {
        get => (double)GetValue(PeakFractionProperty);
        set => SetValue(PeakFractionProperty, value);
    }

    public double RmsFraction
    {
        get => (double)GetValue(RmsFractionProperty);
        set => SetValue(RmsFractionProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        drawingContext.DrawRectangle(Palette.Panel, null, new Rect(0, 0, w, h));
        var rmsColor = RmsFraction >= 54.0 / 60 ? Palette.Warn : Palette.Good;
        drawingContext.DrawRectangle(rmsColor, null, new Rect(0, 0, w * Math.Clamp(RmsFraction, 0, 1), h));
        var peakX = w * Math.Clamp(PeakFraction, 0, 1);
        var peakColor = PeakFraction >= 59.0 / 60 ? Palette.Bad : PeakFraction >= 54.0 / 60 ? Palette.Warn : Palette.Text;
        drawingContext.DrawRectangle(peakColor, null, new Rect(Math.Max(0, peakX - 2), 0, 3, h));
        for (var db = -60; db <= 0; db += 6)
        {
            var x = w * (db + 60) / 60.0;
            drawingContext.DrawLine(Palette.GridPen, new Point(x, h * 0.7), new Point(x, h));
        }
    }
}
