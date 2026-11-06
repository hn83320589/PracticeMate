using PracticeMate.Core.Buffers;
using PracticeMate.Core.Dsp.Stft;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Dsp;

/// <summary>驗收 #17、#18。</summary>
public class SpectrogramAndMinMaxTests
{
    [Theory]
    [InlineData(48000, 2048, 1000.0)]
    [InlineData(48000, 1024, 5000.0)]
    [InlineData(44100, 4096, 440.0)]
    [InlineData(96000, 512, 20000.0)]
    public void Sine_appears_in_correct_frequency_bin(int fs, int window, double f)
    {
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(fs, f, fs, 0.5));
        var s = SpectrogramBuilder.Compute(x, fs, new SpectrogramOptions { WindowSize = window });
        var expectedBin = (int)Math.Round(f * window / fs);
        var mid = s.FrameCount / 2;
        Assert.Equal(expectedBin, s.PeakBin(mid));
        Assert.True(Math.Abs(s.BinFrequency(expectedBin) - f) <= (double)fs / window / 2);
    }

    [Fact]
    public void Bin_centered_sine_has_expected_level()
    {
        // bin 中心的正弦，振幅 A → 幅度 A/2（視窗總和正規化）→ 20·log10(0.25) ≈ −12 dB
        const int fs = 48000;
        var f = 48 * (double)fs / 2048;
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(fs, f, fs, 0.5));
        var s = SpectrogramBuilder.Compute(x, fs, new SpectrogramOptions { WindowSize = 2048 });
        Assert.Equal(20 * Math.Log10(0.25), s.MagnitudeDb(s.FrameCount / 2, 48), 0);
    }

    [Theory]
    [InlineData(2048, 0.75, 512)]
    [InlineData(1024, 0.5, 512)]
    [InlineData(4096, 0.0, 4096)]
    [InlineData(256, 0.9, 26)]
    public void Window_and_overlap_settings_take_effect(int window, double overlap, int hop)
    {
        var opts = new SpectrogramOptions { WindowSize = window, Overlap = overlap };
        var s = SpectrogramBuilder.Compute(new float[48000], 48000, opts);
        Assert.Equal(hop, s.HopSize);
        Assert.Equal(window, s.WindowSize);
        Assert.Equal((window / 2) + 1, s.BinCount);
        Assert.Equal(((48000 - 1) / hop) + 1, s.FrameCount);
        Assert.Equal(3L * hop, s.FrameCenterSample(3));
    }

    [Fact]
    public void Two_tones_separate_in_time()
    {
        const int fs = 48000;
        var x = SignalGenerator.Sine(fs, 500, fs / 2, 0.5).Concat(SignalGenerator.Sine(fs, 3000, fs / 2, 0.5)).ToArray();
        var s = SpectrogramBuilder.Compute(SignalGenerator.ToFloat(x), fs);
        Assert.Equal(Math.Round(500.0 * 2048 / fs), s.PeakBin(10));
        Assert.Equal(Math.Round(3000.0 * 2048 / fs), s.PeakBin(s.FrameCount - 10));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(20000)]
    [InlineData(-0.1)]
    [InlineData(0.96)]
    public void Invalid_options_are_rejected(double value)
    {
        var opts = value > 1 || value == 32 ? new SpectrogramOptions { WindowSize = (int)value } : new SpectrogramOptions { Overlap = value };
        Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
    }

    [Fact]
    public void Log_frequency_axis_mapping()
    {
        Assert.Equal(50, FrequencyAxis.LogFrequency(0, 400, 50, 12800), 9);
        Assert.Equal(12800, FrequencyAxis.LogFrequency(400, 400, 50, 12800), 9);
        Assert.Equal(800, FrequencyAxis.LogFrequency(200, 400, 50, 12800), 9); // 幾何中點：8 個八度的一半
        Assert.Equal(100, FrequencyAxis.LogFrequency(50, 400, 50, 12800), 9);  // 每個八度 50 列
        Assert.Equal(123.4, FrequencyAxis.LogRow(FrequencyAxis.LogFrequency(123.4, 400, 50, 12800), 400, 50, 12800), 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyAxis.LogFrequency(1, 10, 0, 100));
    }

    [Fact]
    public void Linear_frequency_axis_mapping()
    {
        Assert.Equal(12000, FrequencyAxis.LinearFrequency(250, 500, 0, 24000), 9);
        Assert.Equal(250, FrequencyAxis.LinearRow(12000, 500, 0, 24000), 9);
    }

    [Fact]
    public void Interpolated_magnitude_between_bins()
    {
        const int fs = 48000;
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(fs, 1000, fs / 4, 0.5));
        var s = SpectrogramBuilder.Compute(x, fs);
        var f = s.FrameCount / 2;
        var a = s.MagnitudeDb(f, 42);
        var b = s.MagnitudeDb(f, 43);
        Assert.Equal((a + b) / 2, s.MagnitudeDbAt(f, s.BinFrequency(42) + (fs / 2048.0 / 2)), 9);
    }

    // ---- 驗收 #18：最小最大抽樣 ----

    private static MinMax Brute(float[] x, long a, long b)
    {
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        for (var i = a; i < b; i++)
        {
            min = Math.Min(min, x[i]);
            max = Math.Max(max, x[i]);
        }
        return new MinMax(min, max);
    }

    [Fact]
    public void Property_columns_equal_brute_force()
    {
        Prop.ForAll(baseSeed: 10001, iterations: 60, rng =>
        {
            var n = rng.Next(1, rng.Next(2) == 0 ? 3000 : 2_000_000);
            var x = new float[n];
            for (var i = 0; i < n; i++)
            {
                x[i] = (float)((rng.NextDouble() * 2) - 1);
            }
            var index = new MinMaxIndex(x);
            for (var q = 0; q < 5; q++)
            {
                var start = rng.Next(0, n);
                var end = rng.Next(start + 1, n + 1);
                var width = rng.Next(1, 3000);
                var cols = index.Columns(start, end, width);
                var length = (long)end - start;
                for (var c = 0; c < width; c++)
                {
                    // 獨立計算欄位區間（整數 floor），空區間取單一取樣
                    var a = start + (c * length / width);
                    var b = start + ((c + 1) * length / width);
                    if (b <= a)
                    {
                        b = a + 1;
                    }
                    Assert.Equal(Brute(x, a, b), cols[c]);
                }
            }
        });
    }

    [Fact]
    public void Property_query_equals_brute_force()
    {
        Prop.ForAll(baseSeed: 10002, iterations: 30, rng =>
        {
            var n = rng.Next(1, 600_000);
            var x = Enumerable.Range(0, n).Select(_ => (float)rng.NextDouble()).ToArray();
            var index = new MinMaxIndex(x);
            for (var q = 0; q < 50; q++)
            {
                var a = rng.Next(0, n);
                var b = rng.Next(a + 1, n + 1);
                Assert.Equal(Brute(x, a, b), index.Query(a, b));
            }
        });
    }

    [Fact]
    public void Zoomed_in_columns_take_single_samples()
    {
        var x = new float[] { 1, 2, 3 };
        var cols = new MinMaxIndex(x).Columns(0, 3, 6);
        Assert.Equal([1f, 1f, 2f, 2f, 3f, 3f], cols.Select(c => c.Max));
    }

    [Fact]
    public void Invalid_range_throws()
    {
        var index = new MinMaxIndex(new float[10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Query(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Columns(0, 11, 4));
    }
}
