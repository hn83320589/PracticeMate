using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PracticeMate.App.Controls;
using PracticeMate.App.Services;
using PracticeMate.App.ViewModels;
using PracticeMate.App.ViewModels.Services;

namespace PracticeMate.App.Tests;

/// <summary>
/// 實際以 RenderTargetBitmap 匯出 PNG。圖表在版面中通常不在父元素的 (0, 0)（有邊距、在第二列…），
/// 匯出的圖必須只包含圖表本身，不得位移或空白。
/// </summary>
public sealed class ChartImageExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-png-" + Guid.NewGuid().ToString("N"));

    public ChartImageExporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>WPF 需要 STA 執行緒。</summary>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("STA 測試失敗：" + error.Message, error);
        }
    }

    private static BitmapSource Decode(string path) =>
        new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

    private static Color PixelAt(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    [Fact]
    public void Chart_offset_inside_its_parent_is_exported_without_shift_or_blank()
    {
        var path = Path.Combine(_dir, "notes.png");
        RunSta(() =>
        {
            var chart = new NoteDeviationChart
            {
                Width = 400,
                Height = 200,
                Margin = new Thickness(120, 80, 0, 0), // 刻意不在父元素的 (0, 0)
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Items = new[] { new NoteRow(1, 0, 1, "C4", -30), new NoteRow(2, 1, 2, "D4", 20) },
            };
            ChartExport.SetKind(chart, ChartKind.NoteDeviation);
            var root = new Grid { Width = 800, Height = 600 };
            root.Children.Add(chart);
            root.Measure(new Size(800, 600));
            root.Arrange(new Rect(0, 0, 800, 600));
            root.UpdateLayout();

            new ChartImageExporter().ExportPng(ChartKind.NoteDeviation, path);

            var image = Decode(path);
            Assert.Equal(400, image.PixelWidth);
            Assert.Equal(200, image.PixelHeight);
            // 圖表背景為不透明深色；位移或空白時四角會是透明
            foreach (var (x, y) in new[] { (2, 2), (397, 2), (2, 197), (397, 197) })
            {
                var c = PixelAt(image, x, y);
                Assert.True(c.A == 255 && c.R < 0x40 && c.G < 0x40 && c.B < 0x40, $"({x},{y}) = {c}，應為圖表的深色背景");
            }
        });
    }

    [Fact]
    public void Exporting_a_chart_that_is_not_displayed_explains_why()
    {
        RunSta(() =>
        {
            var chart = new BeatDeviationChart();
            ChartExport.SetKind(chart, ChartKind.BeatDeviation); // 未經版面配置，ActualWidth = 0
            var ex = Assert.Throws<IOException>(() => new ChartImageExporter().ExportPng(ChartKind.BeatDeviation, Path.Combine(_dir, "x.png")));
            Assert.Contains("尚未顯示", ex.Message, StringComparison.Ordinal);
        });
    }
}
