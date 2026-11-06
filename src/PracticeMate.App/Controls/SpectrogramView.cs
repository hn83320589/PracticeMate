using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PracticeMate.Core.Dsp.Stft;

namespace PracticeMate.App.Controls;

/// <summary>
/// 頻譜圖：每個像素欄取對應時間的訊框，每列依線性或對數頻率軸取對應頻率的 dB（相鄰 bin 內插），
/// 以色階畫進 WriteableBitmap。與波形共用 Timeline，縮放平移同步。
/// </summary>
public sealed class SpectrogramView : TimelineView
{
    public const double MinFrequency = 40;
    public const double DisplayFloorDb = -100;
    public const double DisplayCeilingDb = -10;

    public static readonly DependencyProperty SpectrogramProperty = DependencyProperty.Register(
        nameof(Spectrogram), typeof(Spectrogram), typeof(SpectrogramView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LogFrequencyProperty = DependencyProperty.Register(
        nameof(LogFrequency), typeof(bool), typeof(SpectrogramView), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxFrequencyProperty = DependencyProperty.Register(
        nameof(MaxFrequency), typeof(double), typeof(SpectrogramView), new FrameworkPropertyMetadata(8000.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly uint[] ColorMap = BuildColorMap();

    public Spectrogram? Spectrogram
    {
        get => (Spectrogram?)GetValue(SpectrogramProperty);
        set => SetValue(SpectrogramProperty, value);
    }

    public bool LogFrequency
    {
        get => (bool)GetValue(LogFrequencyProperty);
        set => SetValue(LogFrequencyProperty, value);
    }

    public double MaxFrequency
    {
        get => (double)GetValue(MaxFrequencyProperty);
        set => SetValue(MaxFrequencyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = (int)ActualWidth;
        var h = (int)ActualHeight;
        drawingContext.DrawRectangle(Palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Spectrogram is not { FrameCount: > 0 } s || Timeline is not { } t || w < 1 || h < 1)
        {
            return;
        }
        var fmax = Math.Min(MaxFrequency, s.SampleRate / 2.0);
        var fmin = LogFrequency ? MinFrequency : 0;
        var rowFrequencies = new double[h];
        for (var y = 0; y < h; y++)
        {
            var row = h - 1 - y; // 低頻在下
            rowFrequencies[y] = LogFrequency
                ? FrequencyAxis.LogFrequency(row + 0.5, h, fmin, fmax)
                : FrequencyAxis.LinearFrequency(row + 0.5, h, fmin, fmax);
        }

        var pixels = new uint[w * h];
        for (var x = 0; x < w; x++)
        {
            var sample = t.SampleAt(x + 0.5);
            if (sample >= t.TotalSamples)
            {
                break;
            }
            var frame = (int)Math.Clamp(Math.Round(sample / s.HopSize), 0, s.FrameCount - 1);
            for (var y = 0; y < h; y++)
            {
                var db = s.MagnitudeDbAt(frame, rowFrequencies[y]);
                var v = (db - DisplayFloorDb) / (DisplayCeilingDb - DisplayFloorDb);
                pixels[(y * w) + x] = ColorMap[(int)Math.Clamp(v * 255, 0, 255)];
            }
        }
        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bitmap.Freeze();
        drawingContext.DrawImage(bitmap, new Rect(0, 0, w, h));
    }

    /// <summary>深藍 → 紫 → 橘 → 淡黃的感知漸層。</summary>
    private static uint[] BuildColorMap()
    {
        (double Pos, byte R, byte G, byte B)[] stops =
        [
            (0.00, 0x10, 0x12, 0x1E), (0.30, 0x3B, 0x1F, 0x6E), (0.55, 0x9C, 0x2E, 0x7F),
            (0.78, 0xF0, 0x6E, 0x3A), (1.00, 0xFC, 0xF4, 0xA3),
        ];
        var map = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var p = i / 255.0;
            var k = 0;
            while (k < stops.Length - 2 && p > stops[k + 1].Pos)
            {
                k++;
            }
            var (p0, r0, g0, b0) = stops[k];
            var (p1, r1, g1, b1) = stops[k + 1];
            var f = (p - p0) / (p1 - p0);
            static byte Lerp(byte a, byte b, double f) => (byte)Math.Round(a + ((b - a) * f));
            map[i] = 0xFF000000u | ((uint)Lerp(r0, r1, f) << 16) | ((uint)Lerp(g0, g1, f) << 8) | Lerp(b0, b1, f);
        }
        return map;
    }
}
