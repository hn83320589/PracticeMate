using PracticeMate.Testing.Reference;

namespace PracticeMate.App.ViewModels.Tests;

/// <summary>分析頁的縮放同步：波形與頻譜圖綁定同一個 TimelineViewModel。</summary>
public class TimelineViewModelTests
{
    private static TimelineViewModel Timeline(long total = 48000 * 60, double width = 1000)
    {
        var t = new TimelineViewModel { ViewportWidth = width };
        t.Reset(total, 48000);
        return t;
    }

    [Fact]
    public void Reset_fits_whole_recording()
    {
        var t = Timeline();
        Assert.Equal(0, t.StartSample);
        Assert.Equal(48000 * 60 / 1000.0, t.SamplesPerPixel, 9);
        Assert.Equal(48000 * 60, t.VisibleEndSample, 6);
    }

    [Fact]
    public void Zoom_keeps_sample_under_cursor()
    {
        var t = Timeline();
        var before = t.SampleAt(250);
        t.ZoomAt(250, 4);
        Assert.Equal(before, t.SampleAt(250), 6);
        Assert.Equal(48000 * 60 / 4000.0, t.SamplesPerPixel, 9);
    }

    [Fact]
    public void Property_zoom_and_pan_stay_within_bounds()
    {
        Prop.ForAll(baseSeed: 12001, iterations: 30, rng =>
        {
            var t = Timeline(rng.Next(1000, 48000 * 600), rng.Next(100, 3000));
            for (var i = 0; i < 50; i++)
            {
                if (rng.Next(2) == 0)
                {
                    t.ZoomAt(rng.NextDouble() * t.ViewportWidth, Math.Pow(2, (rng.NextDouble() * 6) - 3));
                }
                else
                {
                    t.Pan((rng.NextDouble() - 0.5) * 4 * t.ViewportWidth);
                }
                Assert.InRange(t.StartSample, 0, t.TotalSamples);
                Assert.InRange(t.SamplesPerPixel, TimelineViewModel.MinSamplesPerPixel, t.MaxSamplesPerPixel + 1e-9);
                Assert.True(t.VisibleEndSample <= t.TotalSamples + 1e-6);
            }
        });
    }

    [Fact]
    public void Cannot_zoom_out_beyond_whole_recording()
    {
        var t = Timeline();
        t.ZoomAt(500, 0.01);
        Assert.Equal(t.MaxSamplesPerPixel, t.SamplesPerPixel, 9);
        Assert.Equal(0, t.StartSample);
    }

    [Fact]
    public void Can_zoom_in_below_one_sample_per_pixel()
    {
        var t = Timeline();
        t.ZoomAt(0, 1e9);
        Assert.Equal(TimelineViewModel.MinSamplesPerPixel, t.SamplesPerPixel);
    }

    [Fact]
    public void Pan_clamps_at_edges()
    {
        var t = Timeline();
        t.ZoomAt(500, 10);
        t.Pan(-1e9);
        Assert.Equal(0, t.StartSample);
        t.Pan(1e9);
        Assert.Equal(t.TotalSamples, t.VisibleEndSample, 6);
    }

    [Fact]
    public void Pixel_and_sample_conversions_are_inverse()
    {
        var t = Timeline();
        t.ZoomAt(100, 3);
        t.Pan(77);
        Assert.Equal(321.5, t.PixelOf(t.SampleAt(321.5)), 9);
    }

    [Fact]
    public void Waveform_and_spectrogram_views_see_the_same_changes()
    {
        // 兩個檢視訂閱同一個 Timeline 的 PropertyChanged
        var t = Timeline();
        var waveformNotified = new List<string?>();
        var spectrogramNotified = new List<string?>();
        t.PropertyChanged += (_, e) => waveformNotified.Add(e.PropertyName);
        t.PropertyChanged += (_, e) => spectrogramNotified.Add(e.PropertyName);
        t.ZoomAt(500, 2);
        Assert.Contains(nameof(TimelineViewModel.StartSample), waveformNotified);
        Assert.Equal(waveformNotified, spectrogramNotified);
    }

    [Fact]
    public void Resizing_viewport_keeps_range_valid()
    {
        var t = Timeline();
        t.ViewportWidth = 4000;
        Assert.True(t.SamplesPerPixel <= t.MaxSamplesPerPixel + 1e-9);
        Assert.True(t.VisibleEndSample <= t.TotalSamples);
    }

    [Fact]
    public void Invalid_zoom_factor_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Timeline().ZoomAt(0, 0));
    }
}
