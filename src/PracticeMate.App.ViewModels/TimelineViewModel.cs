using CommunityToolkit.Mvvm.ComponentModel;

namespace PracticeMate.App.ViewModels;

/// <summary>
/// 分析頁共用的時間軸視窗。波形與頻譜圖綁定同一個執行個體，因此縮放與平移永遠同步。
/// 位置以取樣索引（分析取樣率）表示；SamplesPerPixel 可小於 1（放大到單一取樣以下）。
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    public const double MinSamplesPerPixel = 1.0 / 16;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleEndSample))]
    private double _startSample;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleEndSample))]
    private double _samplesPerPixel = 1;

    [ObservableProperty]
    private long _totalSamples;

    [ObservableProperty]
    private int _sampleRate = 48000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleEndSample))]
    private double _viewportWidth = 1000;

    public double VisibleEndSample => Math.Min(TotalSamples, StartSample + (SamplesPerPixel * ViewportWidth));

    /// <summary>全部顯示時的 SamplesPerPixel（縮小的上限）。</summary>
    public double MaxSamplesPerPixel => Math.Max(MinSamplesPerPixel, TotalSamples / Math.Max(1, ViewportWidth));

    /// <summary>載入新的錄音：顯示全部。</summary>
    public void Reset(long totalSamples, int sampleRate)
    {
        TotalSamples = totalSamples;
        SampleRate = sampleRate;
        FitAll();
    }

    public void FitAll()
    {
        SamplesPerPixel = MaxSamplesPerPixel;
        StartSample = 0;
    }

    /// <summary>以畫面上的 x 位置為錨點縮放（factor &gt; 1 放大）。錨點下的取樣位置保持不動（除非碰到邊界）。</summary>
    public void ZoomAt(double pixelX, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor))
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "縮放倍數必須為正數。");
        }
        var anchor = StartSample + (pixelX * SamplesPerPixel);
        var spp = Math.Clamp(SamplesPerPixel / factor, MinSamplesPerPixel, MaxSamplesPerPixel);
        SamplesPerPixel = spp;
        StartSample = ClampStart(anchor - (pixelX * spp));
    }

    /// <summary>平移（正值往右，即往後的時間）。</summary>
    public void Pan(double pixels) => StartSample = ClampStart(StartSample + (pixels * SamplesPerPixel));

    /// <summary>畫面 x 位置 → 取樣索引。</summary>
    public double SampleAt(double pixelX) => StartSample + (pixelX * SamplesPerPixel);

    /// <summary>取樣索引 → 畫面 x 位置。</summary>
    public double PixelOf(double sample) => (sample - StartSample) / SamplesPerPixel;

    partial void OnViewportWidthChanged(double value)
    {
        SamplesPerPixel = Math.Clamp(SamplesPerPixel, MinSamplesPerPixel, MaxSamplesPerPixel);
        StartSample = ClampStart(StartSample);
    }

    private double ClampStart(double start)
    {
        var maxStart = Math.Max(0, TotalSamples - (SamplesPerPixel * ViewportWidth));
        return Math.Clamp(start, 0, maxStart);
    }
}
