using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PracticeMate.App.ViewModels;
using PracticeMate.Core.Analysis.Loudness;

namespace PracticeMate.App.Controls;

/// <summary>綁定集合的圖表基底：集合內容變動時自動重畫。</summary>
public abstract class CollectionChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(CollectionChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    protected FormattedText Label(string text, double size = 11, Brush? brush = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Palette.Typeface, size, brush ?? Palette.Muted, PixelsPerDip);

    /// <summary>畫水平格線與 Y 軸標籤；回傳 value → y 的映射。</summary>
    protected Func<double, double> DrawYAxis(DrawingContext drawingContext, Rect plot, double min, double max, double step, string unit)
    {
        double Y(double v) => plot.Bottom - ((v - min) / (max - min) * plot.Height);
        for (var v = min; v <= max + 1e-9; v += step)
        {
            var y = Y(v);
            drawingContext.DrawLine(Math.Abs(v) < 1e-9 ? Palette.ZeroPen : Palette.GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Label(v.ToString("0", CultureInfo.InvariantCulture) + unit);
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 4, y - (label.Height / 2)));
        }
        return Y;
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (CollectionChart)d;
        if (e.OldValue is INotifyCollectionChanged o)
        {
            o.CollectionChanged -= chart.OnCollectionChanged;
        }
        if (e.NewValue is INotifyCollectionChanged n)
        {
            n.CollectionChanged += chart.OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}

/// <summary>音準報告：每個音符一根長條（±50 音分），綠 ≤ 5、黃 ≤ 15、紅 &gt; 15 音分。</summary>
public sealed class NoteDeviationChart : CollectionChart
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var notes = Items?.OfType<NoteRow>().ToList() ?? [];
        var plot = new Rect(44, 10, Math.Max(1, ActualWidth - 54), Math.Max(1, ActualHeight - 40));
        var y = DrawYAxis(drawingContext, plot, -50, 50, 10, "");
        if (notes.Count == 0)
        {
            return;
        }
        var slot = plot.Width / notes.Count;
        var barWidth = Math.Max(1, slot * 0.7);
        for (var i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            var x = plot.Left + (i * slot) + ((slot - barWidth) / 2);
            var c = Math.Clamp(n.Cents, -50, 50);
            var top = Math.Min(y(0), y(c));
            drawingContext.DrawRectangle(Palette.ForDeviation(Math.Abs(n.Cents), 5, 15), null, new Rect(x, top, barWidth, Math.Max(1, Math.Abs(y(c) - y(0)))));
            if (slot >= 22)
            {
                var label = Label(n.Name, 10, Palette.Text);
                drawingContext.DrawText(label, new Point(x + (barWidth / 2) - (label.Width / 2), plot.Bottom + 4));
            }
        }
        drawingContext.DrawText(Label("音分"), new Point(4, 0));
    }
}

/// <summary>節拍報告：每拍的偏差（毫秒，正值為拖拍），綠 ≤ 20、黃 ≤ 50、紅 &gt; 50 毫秒。</summary>
public sealed class BeatDeviationChart : CollectionChart
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var beats = Items?.OfType<BeatRow>().ToList() ?? [];
        var limit = beats.Count == 0 ? 100 : Math.Max(50, Math.Ceiling(beats.Max(b => Math.Abs(b.DeviationMs)) / 25) * 25);
        var plot = new Rect(54, 10, Math.Max(1, ActualWidth - 64), Math.Max(1, ActualHeight - 40));
        var y = DrawYAxis(drawingContext, plot, -limit, limit, limit / 4, "");
        if (beats.Count == 0)
        {
            return;
        }
        var first = beats.Min(b => b.BeatNumber);
        var last = beats.Max(b => b.BeatNumber);
        var slot = plot.Width / Math.Max(1, last - first + 1);
        var barWidth = Math.Max(1, slot * 0.6);
        foreach (var b in beats)
        {
            var x = plot.Left + ((b.BeatNumber - first) * slot) + ((slot - barWidth) / 2);
            var top = Math.Min(y(0), y(b.DeviationMs));
            drawingContext.DrawRectangle(Palette.ForDeviation(Math.Abs(b.DeviationMs), 20, 50), null, new Rect(x, top, barWidth, Math.Max(1, Math.Abs(y(b.DeviationMs) - y(0)))));
        }
        drawingContext.DrawText(Label("毫秒（＋拖拍／−搶拍）"), new Point(4, 0));
        var axis = Label($"第 {first}–{last} 拍");
        drawingContext.DrawText(axis, new Point(plot.Right - axis.Width, plot.Bottom + 4));
    }
}

/// <summary>響度隨時間：瞬時（細線）、短期（粗線）與正規化目標（虛線）。</summary>
public sealed class LoudnessChart : FrameworkElement
{
    public static readonly DependencyProperty LoudnessProperty = DependencyProperty.Register(
        nameof(Loudness), typeof(LoudnessResult), typeof(LoudnessChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetLufsProperty = DependencyProperty.Register(
        nameof(TargetLufs), typeof(double), typeof(LoudnessChart), new FrameworkPropertyMetadata(-16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen MomentaryPen = Palette.FrozenPen(Palette.Muted, 1);
    private static readonly Pen ShortTermPen = Palette.FrozenPen(Palette.Waveform, 2);
    private static readonly Pen TargetPen = CreateTargetPen();

    public LoudnessResult? Loudness
    {
        get => (LoudnessResult?)GetValue(LoudnessProperty);
        set => SetValue(LoudnessProperty, value);
    }

    public double TargetLufs
    {
        get => (double)GetValue(TargetLufsProperty);
        set => SetValue(TargetLufsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = new Rect(54, 10, Math.Max(1, ActualWidth - 64), Math.Max(1, ActualHeight - 30));
        const double min = -60;
        const double max = 0;
        double Y(double v) => plot.Bottom - ((Math.Clamp(v, min, max) - min) / (max - min) * plot.Height);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var v = min; v <= max; v += 10)
        {
            drawingContext.DrawLine(Palette.GridPen, new Point(plot.Left, Y(v)), new Point(plot.Right, Y(v)));
            var label = new FormattedText(v.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Palette.Typeface, 11, Palette.Muted, dpi);
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 4, Y(v) - (label.Height / 2)));
        }
        drawingContext.DrawLine(TargetPen, new Point(plot.Left, Y(TargetLufs)), new Point(plot.Right, Y(TargetLufs)));
        if (Loudness is not { } l || l.Momentary.Count == 0)
        {
            return;
        }
        var end = (double)l.Momentary[^1].EndSample;
        double X(long sample) => plot.Left + (sample / end * plot.Width);
        DrawSeries(drawingContext, l.Momentary, MomentaryPen, X, Y);
        DrawSeries(drawingContext, l.ShortTerm, ShortTermPen, X, Y);
    }

    private static void DrawSeries(DrawingContext drawingContext, IReadOnlyList<LoudnessPoint> points, Pen pen, Func<long, double> x, Func<double, double> y)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var started = false;
            foreach (var p in points)
            {
                if (double.IsNegativeInfinity(p.Lufs))
                {
                    started = false; // 靜音段斷開
                    continue;
                }
                var pt = new Point(x(p.EndSample), y(p.Lufs));
                if (!started)
                {
                    ctx.BeginFigure(pt, false, false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(pt, true, false);
                }
            }
        }
        g.Freeze();
        drawingContext.DrawGeometry(null, pen, g);
    }

    private static Pen CreateTargetPen()
    {
        var p = new Pen(Palette.Accent, 1) { DashStyle = DashStyles.Dash };
        p.Freeze();
        return p;
    }
}
