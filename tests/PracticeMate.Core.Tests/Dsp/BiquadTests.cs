using PracticeMate.Core.Dsp.Filters;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Dsp;

public class BiquadTests
{
    [Theory]
    [InlineData(1000, 48000)]
    [InlineData(100, 44100)]
    [InlineData(5000, 96000)]
    public void Butterworth_lowpass_is_minus_3dB_at_f0_and_unity_at_dc(double f0, double fs)
    {
        var c = BiquadDesign.LowPass(f0, 1 / Math.Sqrt(2), fs);
        Assert.Equal(-3.0103, c.MagnitudeDb(f0, fs), 3);
        Assert.Equal(0, c.MagnitudeDb(1e-6, fs), 6);
    }

    [Fact]
    public void Highpass_blocks_dc_and_passes_nyquist()
    {
        var c = BiquadDesign.HighPass(40, 0.5, 48000);
        Assert.True(c.Response(0, 48000).Magnitude < 1e-12);
        Assert.Equal(1.0, c.Response(24000, 48000).Magnitude, 9);
    }

    [Fact]
    public void Filter_matches_direct_difference_equation()
    {
        var c = BiquadDesign.LowPass(2000, 0.9, 48000);
        var rng = new Random(11);
        var x = Enumerable.Range(0, 500).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var expected = new double[x.Length];
        for (var n = 0; n < x.Length; n++)
        {
            double Xm(int k) => n - k >= 0 ? x[n - k] : 0;
            double Ym(int k) => n - k >= 0 ? expected[n - k] : 0;
            expected[n] = (c.B0 * x[n]) + (c.B1 * Xm(1)) + (c.B2 * Xm(2)) - (c.A1 * Ym(1)) - (c.A2 * Ym(2));
        }
        var actual = (double[])x.Clone();
        new BiquadFilter(c).Process(actual);
        for (var n = 0; n < x.Length; n++)
        {
            Assert.Equal(expected[n], actual[n], 12);
        }
    }

    [Fact]
    public void Steady_state_sine_amplitude_matches_frequency_response()
    {
        const int fs = 48000;
        var c = BiquadDesign.HighPass(500, 0.7, fs);
        var x = SignalGenerator.Sine(fs, 300, fs);
        new BiquadFilter(c).Process(x);
        var peak = x.Skip(fs / 2).Max(Math.Abs);
        Assert.Equal(c.Response(300, fs).Magnitude, peak, 3);
    }

    [Fact]
    public void Reset_clears_state()
    {
        var f = new BiquadFilter(BiquadDesign.LowPass(100, 0.7, 8000));
        var first = f.Process(1.0);
        f.Process(0.3);
        f.Reset();
        Assert.Equal(first, f.Process(1.0));
    }

    [Fact]
    public void Design_rejects_f0_at_or_above_nyquist()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BiquadDesign.LowPass(24000, 0.7, 48000));
    }
}
