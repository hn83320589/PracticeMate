using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PracticeMate.App.ViewModels;

namespace PracticeMate.App.Controls;

/// <summary>
/// 綁定 <see cref="TimelineViewModel"/> 的檢視基底：滾輪以游標為中心縮放、左鍵拖曳平移、雙擊顯示全部。
/// 波形與頻譜圖綁定同一個 Timeline，因此任何一邊的操作都會同步到另一邊。
/// </summary>
public abstract class TimelineView : FrameworkElement
{
    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(
        nameof(Timeline), typeof(TimelineViewModel), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTimelineChanged));

    private Point? _dragStart;

    protected TimelineView()
    {
        Focusable = true;
        ClipToBounds = true;
        SizeChanged += (_, e) =>
        {
            if (Timeline is { } t && e.WidthChanged && e.NewSize.Width > 0)
            {
                t.ViewportWidth = e.NewSize.Width;
            }
        };
    }

    public TimelineViewModel? Timeline
    {
        get => (TimelineViewModel?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Timeline?.ZoomAt(e.GetPosition(this).X, e.Delta > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ClickCount == 2)
        {
            Timeline?.FitAll();
            return;
        }
        _dragStart = e.GetPosition(this);
        CaptureMouse();
        Focus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_dragStart is { } start && Timeline is { } t)
        {
            var p = e.GetPosition(this);
            t.Pan(start.X - p.X);
            _dragStart = p;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
    }

    private static void OnTimelineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TimelineView)d;
        if (e.OldValue is INotifyPropertyChanged oldT)
        {
            oldT.PropertyChanged -= view.OnTimelinePropertyChanged;
        }
        if (e.NewValue is TimelineViewModel newT)
        {
            newT.PropertyChanged += view.OnTimelinePropertyChanged;
            if (view.ActualWidth > 0)
            {
                newT.ViewportWidth = view.ActualWidth;
            }
        }
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
}
