using PracticeMate.Core.Pitch;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Pitch;

/// <summary>
/// 驗收 #6：純音、鋸齒波、方波、缺少基頻（2–6 次泛音），55–1760 Hz，SNR 20 dB。
/// 判定（設計文件 Q1，已確認）：穩定段逐幀 |誤差| 的中位數 ≤ 1 音分、95% 幀 ≤ 3 音分；
/// 八度（粗大）錯誤率 &lt; 1%（|誤差| &gt; 50 音分或無音高即計為錯誤）。
/// </summary>
public class PitchAccuracyTests
{
    private static readonly HarmonicSpectrum[] Spectra =
    [
        HarmonicSpectrum.Pure, HarmonicSpectrum.Sawtooth, HarmonicSpectrum.Square, HarmonicSpectrum.MissingFundamental,
    ];

    public static TheoryData<int, double> Grid()
    {
        var data = new TheoryData<int, double>();
        foreach (var s in Enumerable.Range(0, Spectra.Length))
        {
            foreach (var f in new[] { 55.0, 82.41, 110.0, 196.0, 440.0, 880.0, 1318.51, 1760.0 })
            {
                data.Add(s, f);
            }
        }
        return data;
    }

    private sealed record Stats(double MedianAbs, double P95Abs, double GrossRate, int Frames);

    private static Stats Measure(int fs, double f, HarmonicSpectrum spectrum, Random rng, double phase = 0)
    {
        var length = fs / 2;
        var tone = SignalGenerator.Tone(fs, length, _ => f, spectrum, null, 0.5, phase);
        var x = SignalGenerator.ToFloat(SignalGenerator.AddNoise(tone, 20, rng));
        var win = McLeodPitchDetector.AnalysisWindowSize(fs);
        var det = new McLeodPitchDetector(fs, win);
        var abs = new List<double>();
        var gross = 0;
        var frames = 0;
        // 穩定段：去掉頭尾各 50 ms，每 10 ms 一點
        for (var c = (fs / 20) + (win / 2); c + (win / 2) <= length - (fs / 20); c += fs / 100)
        {
            frames++;
            var e = det.Detect(x.AsSpan(c - (win / 2), win));
            var cents = e.HasPitch ? 1200 * Math.Log2(e.FrequencyHz / f) : double.PositiveInfinity;
            if (Math.Abs(cents) > 50)
            {
                gross++;
                continue;
            }
            abs.Add(Math.Abs(cents));
        }
        abs.Sort();
        return abs.Count == 0
            ? new Stats(double.PositiveInfinity, double.PositiveInfinity, 1, frames)
            : new Stats(abs[abs.Count / 2], abs[(int)(abs.Count * 0.95)], (double)gross / frames, frames);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Stable_segment_error_is_within_one_cent(int spectrumIndex, double frequency)
    {
        var spectrum = Spectra[spectrumIndex];
        var s = Measure(48000, frequency, spectrum, new Random((int)frequency + spectrumIndex));
        Assert.True(s.Frames >= 20);
        Assert.True(s.MedianAbs <= 1.0, $"{spectrum} {frequency} Hz：中位數誤差 {s.MedianAbs:F2} 音分");
        Assert.True(s.P95Abs <= 3.0, $"{spectrum} {frequency} Hz：95% 誤差 {s.P95Abs:F2} 音分");
        Assert.True(s.GrossRate < 0.01, $"{spectrum} {frequency} Hz：八度錯誤率 {s.GrossRate:P1}");
    }

    [Fact]
    public void Property_random_pitch_timbre_phase_and_rate()
    {
        var rates = new[] { 44100, 48000, 96000 };
        var totalFrames = 0;
        var totalGross = 0.0;
        Prop.ForAll(baseSeed: 6001, iterations: 24, rng =>
        {
            var fs = rates[rng.Next(rates.Length)];
            var f = 55 * Math.Pow(2, rng.NextDouble() * 5); // 55–1760 Hz 對數均勻
            var spectrum = Spectra[rng.Next(Spectra.Length)];
            var s = Measure(fs, f, spectrum, rng, rng.NextDouble() * 2 * Math.PI);
            Assert.True(s.MedianAbs <= 1.0, $"fs={fs} {spectrum} {f:F2} Hz：中位數誤差 {s.MedianAbs:F2} 音分");
            Assert.True(s.P95Abs <= 3.0, $"fs={fs} {spectrum} {f:F2} Hz：95% 誤差 {s.P95Abs:F2} 音分");
            totalFrames += s.Frames;
            totalGross += s.GrossRate * s.Frames;
        });
        Assert.True(totalGross / totalFrames < 0.01, $"整體八度錯誤率 {totalGross / totalFrames:P2}");
    }

    [Fact]
    public void Silence_has_no_pitch()
    {
        var det = new McLeodPitchDetector(48000, 4096);
        var e = det.Detect(new float[4096]);
        Assert.False(e.HasPitch);
        Assert.Equal(double.NegativeInfinity, e.RmsDbfs);
    }

    [Fact]
    public void White_noise_has_low_clarity()
    {
        var det = new McLeodPitchDetector(48000, 4096);
        var noise = SignalGenerator.ToFloat(SignalGenerator.WhiteNoise(new Random(9), 4096, 0.1));
        var e = det.Detect(noise);
        Assert.True(!e.HasPitch || e.Clarity < 0.5, $"白雜訊 clarity = {e.Clarity:F2}");
    }

    [Fact]
    public void Clean_tone_has_high_clarity_and_rms()
    {
        var det = new McLeodPitchDetector(48000, 4096);
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(48000, 440, 4096, 0.5));
        var e = det.Detect(x);
        Assert.True(e.Clarity > 0.95);
        Assert.Equal(20 * Math.Log10(0.5 / Math.Sqrt(2)), e.RmsDbfs, 1);
    }

    [Fact]
    public void Wrong_window_length_throws()
    {
        Assert.Throws<ArgumentException>(() => new McLeodPitchDetector(48000, 4096).Detect(new float[100]));
    }

    [Theory]
    [InlineData(44100, 4096)]
    [InlineData(48000, 4096)]
    [InlineData(96000, 8192)]
    public void Analysis_window_is_about_80_ms(int fs, int expected)
    {
        Assert.Equal(expected, McLeodPitchDetector.AnalysisWindowSize(fs));
    }
}
