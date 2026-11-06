using PracticeMate.Core.Dsp.Resampling;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Dsp;

public class RationalResamplerTests
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000];

    [Theory]
    [InlineData(44100, 48000, 160, 147)]
    [InlineData(192000, 48000, 1, 4)]
    [InlineData(8000, 48000, 6, 1)]
    [InlineData(22050, 48000, 320, 147)]
    public void Ratio_is_reduced(int fin, int fout, int up, int down)
    {
        var r = new RationalResampler(fin, fout);
        Assert.Equal(up, r.UpFactor);
        Assert.Equal(down, r.DownFactor);
    }

    [Theory]
    [InlineData(44100, 48000, 44100, 48000)]
    [InlineData(192000, 48000, 10, 3)]
    [InlineData(8000, 48000, 7, 42)]
    public void Output_length_is_ceiling(int fin, int fout, long inLen, long outLen)
    {
        Assert.Equal(outLen, new RationalResampler(fin, fout).OutputLength(inLen));
    }

    [Fact]
    public void Same_rate_is_identity()
    {
        var x = new float[] { 1, -2, 3 };
        Assert.Equal(x, new RationalResampler(48000, 48000).Process(x));
    }

    [Theory]
    [InlineData(96000, 48000)]
    [InlineData(192000, 48000)]
    [InlineData(32000, 48000)]
    public void Impulse_maps_to_same_time(int fin, int fout)
    {
        var r = new RationalResampler(fin, fout);
        var x = new float[fin / 10];
        var j = (x.Length / 2 / r.DownFactor) * r.DownFactor; // j·L/M 為整數
        x[j] = 1;
        var y = r.Process(x);
        var expectedIndex = j * r.UpFactor / r.DownFactor;
        var argMax = Array.IndexOf(y, y.Max());
        Assert.Equal(expectedIndex, argMax);
    }

    [Fact]
    public void Stopband_tone_is_attenuated()
    {
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(96000, 30000, 96000, 0.9));
        var y = new RationalResampler(96000, 48000).Process(x);
        var rms = SignalGenerator.Rms(y.Skip(4800).Take(38400).Select(v => (double)v).ToArray());
        Assert.True(20 * Math.Log10(rms) < -80, $"阻帶殘留 {20 * Math.Log10(rms):F1} dB");
    }

    [Fact]
    public void Property_passband_sine_is_preserved()
    {
        Prop.ForAll(baseSeed: 2001, iterations: 25, rng =>
        {
            var fin = Rates[rng.Next(Rates.Length)];
            var fout = 48000;
            var minRate = Math.Min(fin, fout);
            var f = (0.02 + (rng.NextDouble() * 0.38)) * minRate; // 0.02–0.40·min
            var phase = rng.NextDouble() * 2 * Math.PI;
            var x = SignalGenerator.ToFloat(SignalGenerator.Sine(fin, f, fin / 2, 0.8, phase));
            var r = new RationalResampler(fin, fout);
            var y = r.Process(x);

            var margin = fout / 20;
            var maxErr = 0.0;
            for (var m = margin; m < y.Length - margin; m++)
            {
                var ideal = 0.8 * Math.Sin((2 * Math.PI * f * m / fout) + phase);
                maxErr = Math.Max(maxErr, Math.Abs(y[m] - ideal));
            }
            Assert.True(maxErr < 2e-3, $"{fin}→{fout} f={f:F1} Hz 最大誤差 {maxErr:E2}");
        });
    }

    [Fact]
    public void Chunked_processing_equals_whole_processing()
    {
        var x = SignalGenerator.ToFloat(SignalGenerator.WhiteNoise(new Random(4), 4410, 0.3));
        var r = new RationalResampler(44100, 48000);
        var whole = r.Process(x);
        var chunked = new float[whole.Length];
        for (var start = 0; start < whole.Length; start += 777)
        {
            var count = Math.Min(777, whole.Length - start);
            r.Process(x, start, chunked.AsSpan(start, count));
        }
        Assert.Equal(whole, chunked);
    }
}
