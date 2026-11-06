using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Reference;

/// <summary>參考實作本身的健全性檢查（參考答案錯了，所有比對都會失去意義）。</summary>
public class ReferenceSelfTests
{
    [Fact]
    public void Dft_of_impulse_is_flat()
    {
        var x = new Complex[7];
        x[0] = 1;
        var y = ReferenceDft.Forward(x);
        Assert.All(y, v => Assert.Equal(1.0, v.Real, 12));
        Assert.All(y, v => Assert.Equal(0.0, v.Imaginary, 12));
    }

    [Fact]
    public void Dft_of_complex_exponential_has_single_bin()
    {
        const int n = 12;
        const int bin = 5;
        var x = Enumerable.Range(0, n).Select(i => Complex.FromPolarCoordinates(1, 2 * Math.PI * bin * i / n)).ToArray();
        var y = ReferenceDft.Forward(x);
        for (var k = 0; k < n; k++)
        {
            Assert.Equal(k == bin ? n : 0, y[k].Magnitude, 9);
        }
    }

    [Fact]
    public void Dft_inverse_round_trips()
    {
        var rng = new Random(1);
        var x = Enumerable.Range(0, 31).Select(_ => new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)).ToArray();
        var back = ReferenceDft.Inverse(ReferenceDft.Forward(x));
        Assert.True(ReferenceDft.RelativeError(back, x) < 1e-13);
    }

    [Fact]
    public void Prop_failure_reports_reproducible_seed()
    {
        var ex = Assert.Throws<PropertyFailedException>(() =>
            Prop.ForAll(42, 10, rng => Assert.True(rng.Next(100) < 0, "always fails")));
        Assert.Equal(Prop.SeedFor(42, 0), ex.Seed);
        Assert.Contains("PM_PROP_SEED=" + ex.Seed, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prop_seeds_are_deterministic()
    {
        var first = new List<int>();
        var second = new List<int>();
        Prop.ForAll(7, 5, rng => first.Add(rng.Next()));
        Prop.ForAll(7, 5, rng => second.Add(rng.Next()));
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(40.0)]
    public void AddNoise_produces_requested_snr(double snrDb)
    {
        var s = SignalGenerator.Sine(48000, 440, 96000, 0.5);
        var y = SignalGenerator.AddNoise(s, snrDb, new Random(3));
        var noise = y.Zip(s, (a, b) => a - b).ToArray();
        var measured = 20 * Math.Log10(SignalGenerator.Rms(s) / SignalGenerator.Rms(noise));
        Assert.InRange(measured, snrDb - 0.1, snrDb + 0.1);
    }

    [Fact]
    public void Tone_with_missing_fundamental_has_no_energy_at_f0()
    {
        const int fs = 8000;
        const int n = 800; // 10 Hz 解析度
        var x = SignalGenerator.Tone(fs, 200, n, HarmonicSpectrum.MissingFundamental);
        var spec = ReferenceDft.Forward(x.Select(v => new Complex(v, 0)).ToArray());
        Assert.True(spec[20].Magnitude < 1e-6, "基頻應不存在");
        Assert.True(spec[40].Magnitude > 10, "第二泛音應存在");
        Assert.True(spec[120].Magnitude > 10, "第六泛音應存在");
        Assert.True(spec[140].Magnitude < 1e-6, "第七泛音應不存在");
    }

    [Fact]
    public void Tone_peak_is_normalized()
    {
        var x = SignalGenerator.Tone(48000, 110, 4800, HarmonicSpectrum.Sawtooth, peakAmplitude: 0.25);
        Assert.Equal(0.25, x.Max(Math.Abs), 12);
    }

    [Fact]
    public void WavBuilder_pads_odd_length_chunks()
    {
        var bytes = RefWavBuilder.Pcm(8000, 1, 16)
            .AddChunkBeforeData("LIST", [1, 2, 3])
            .WithIntSamples([1, -1])
            .Build();
        // RIFF(12) + fmt(8+16) + LIST(8+3+1 pad) + data(8+4)
        Assert.Equal(12 + 24 + 12 + 12, bytes.Length);
        Assert.Equal("data", Encoding.ASCII.GetString(bytes, 48, 4));
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void WavBuilder_encodes_negative_24bit_little_endian()
    {
        var bytes = RefWavBuilder.Pcm(8000, 1, 24).WithIntSamples([-2]).Build();
        var data = bytes.AsSpan(44, 3).ToArray();
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0xFF }, data);
    }
}
