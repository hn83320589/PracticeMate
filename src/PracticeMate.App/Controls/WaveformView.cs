using System.Windows;
using System.Windows.Media;
using PracticeMate.Core.Buffers;

namespace PracticeMate.App.Controls;

/// <summary>波形：每個像素欄以最小最大抽樣（<see cref="MinMaxIndex.Columns"/>）畫一條垂直線。</summary>
public sealed class WaveformView : TimelineView
{
    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(
        nameof(Waveform), typeof(MinMaxIndex), typeof(WaveformView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen WavePen = Palette.FrozenPen(Palette.Waveform, 1);

    public MinMaxIndex? Waveform
    {
        get => (MinMaxIndex?)GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        drawingContext.DrawRectangle(Palette.Background, null, new Rect(0, 0, w, h));
        var mid = h / 2;
        drawingContext.DrawLine(Palette.GridPen, new Point(0, mid), new Point(w, mid));
        if (Waveform is not { Length: > 0 } wave || Timeline is not { } t || w < 1)
        {
            return;
        }
        var start = (long)Math.Floor(t.StartSample);
        var end = Math.Min(wave.Length, (long)Math.Ceiling(t.VisibleEndSample));
        if (end <= start)
        {
            return;
        }
        var width = (int)Math.Max(1, Math.Min(w, t.PixelOf(end)));
        var columns = wave.Columns(start, end, width);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var x = 0; x < columns.Length; x++)
            {
                var (min, max) = (columns[x].Min, columns[x].Max);
                var y1 = mid - (max * mid);
                var y2 = mid - (min * mid);
                g.BeginFigure(new Point(x + 0.5, y1), false, false);
                g.LineTo(new Point(x + 0.5, Math.Max(y2, y1 + 1)), true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, WavePen, geometry);
    }
}
