using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PracticeMate.App.ViewModels.Services;

namespace PracticeMate.App.Services;

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

public sealed class FileDialogService(Func<string> initialFolder) : IFileDialogService
{
    public string? OpenWav()
    {
        var dlg = new OpenFileDialog
        {
            Title = "開啟錄音",
            Filter = "WAV 音訊 (*.wav)|*.wav|所有檔案 (*.*)|*.*",
            InitialDirectory = ExistingFolder(),
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? Save(string defaultFileName, string filter)
    {
        var dlg = new SaveFileDialog
        {
            Title = "另存新檔",
            FileName = defaultFileName,
            Filter = filter,
            InitialDirectory = ExistingFolder(),
            OverwritePrompt = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private string ExistingFolder()
    {
        var folder = initialFolder();
        return Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    }
}

/// <summary>
/// 圖表 PNG 匯出：控制項以附加屬性 <c>ChartExport.Kind</c> 登記自己，
/// 匯出時以 RenderTargetBitmap 畫出目前顯示的內容。
/// </summary>
public sealed class ChartImageExporter : IChartImageExporter
{
    private static readonly Dictionary<ChartKind, WeakReference<FrameworkElement>> Registered = [];

    public static void Register(ChartKind kind, FrameworkElement element) => Registered[kind] = new WeakReference<FrameworkElement>(element);

    public void ExportPng(ChartKind chart, string path)
    {
        if (!Registered.TryGetValue(chart, out var weak) || !weak.TryGetTarget(out var element) || element.ActualWidth < 1 || element.ActualHeight < 1)
        {
            throw new IOException("圖表尚未顯示，請先切換到該頁面後再匯出。");
        }
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // 不可直接 Render(element)：RenderTargetBitmap 會套用元素在父元素中的位移，
        // 圖表不在 (0, 0) 時匯出的圖會位移甚至全空白。改以 VisualBrush 畫在原點。
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

public static class ChartExport
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(ChartKind?), typeof(ChartExport), new PropertyMetadata(null, OnKindChanged));

    public static ChartKind? GetKind(DependencyObject d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return (ChartKind?)d.GetValue(KindProperty);
    }

    public static void SetKind(DependencyObject d, ChartKind? value)
    {
        ArgumentNullException.ThrowIfNull(d);
        d.SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && e.NewValue is ChartKind kind)
        {
            ChartImageExporter.Register(kind, fe);
        }
    }
}
