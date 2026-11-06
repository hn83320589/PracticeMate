using PracticeMate.Core.Analysis.Loudness;
using PracticeMate.Core.Audio;
using PracticeMate.Core.IO.Wav;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Analysis;

/// <summary>驗收 #15、#16。</summary>
public sealed class TruePeakAndNormalizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-norm-" + Guid.NewGuid().ToString("N"));

    public TruePeakAndNormalizerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AudioData Mono(float[] x, int fs) => new(fs, [x]);

    [Theory]
    [InlineData(48000, Math.PI / 4)]
    [InlineData(44100, Math.PI / 4)]
    [InlineData(96000, 0.3)]
    public void Quarter_rate_sine_with_phase_offset_reads_continuous_peak(int fs, double phase)
    {
        // fs/4 加 45° 相位：每個取樣都只有 ±0.707（−3 dB），連續訊號峰值為 0 dBFS
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(fs, fs / 4.0, fs, 1.0, phase));
        var sample = x.Max(Math.Abs);
        Assert.True(20 * Math.Log10(sample) < -2.9 || phase != Math.PI / 4);
        var tp = TruePeakMeter.Measure(Mono(x, fs)).Dbtp;
        Assert.True(Math.Abs(tp) <= 0.3, $"{fs} Hz：{tp:F3} dBTP");
    }

    [Fact]
    public void Property_sines_of_many_frequencies_within_0_3_db()
    {
        var rates = new[] { 44100, 48000, 96000 };
        Prop.ForAll(baseSeed: 9001, iterations: 40, rng =>
        {
            var fs = rates[rng.Next(rates.Length)];
            var f = (20 + (rng.NextDouble() * ((0.4 * fs) - 20)));
            var amp = Math.Pow(10, (-20 + (rng.NextDouble() * 19)) / 20);
            // 頭尾淡入淡出：突然開始/結束的正弦，其帶限重建在邊界真的會過衝，不是理論峰值
            var x = SignalGenerator.ToFloat(Faded(SignalGenerator.Sine(fs, f, fs / 5, amp, rng.NextDouble() * 2 * Math.PI), fs / 50));
            var tp = TruePeakMeter.Measure(Mono(x, fs)).Dbtp;
            var theory = 20 * Math.Log10(amp);
            Assert.True(Math.Abs(tp - theory) <= 0.3, $"fs={fs} f={f:F1}：{tp:F3} vs 理論 {theory:F3}");
        });
    }

    private static double[] Faded(double[] x, int fade)
    {
        for (var i = 0; i < fade; i++)
        {
            var g = 0.5 * (1 - Math.Cos(Math.PI * i / fade));
            x[i] *= g;
            x[x.Length - 1 - i] *= g;
        }
        return x;
    }

    [Fact]
    public void All_zero_is_minus_infinity()
    {
        var r = TruePeakMeter.Measure(Mono(new float[1000], 48000));
        Assert.Equal(double.NegativeInfinity, r.Dbtp);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-0.25)]
    public void Single_sample_impulse_reads_its_own_amplitude(double a)
    {
        var x = new float[2000];
        x[1000] = (float)a;
        var tp = TruePeakMeter.Measure(Mono(x, 48000)).Dbtp;
        Assert.Equal(20 * Math.Log10(Math.Abs(a)), tp, 6);
    }

    [Fact]
    public void True_peak_is_never_below_sample_peak()
    {
        var rng = new Random(2);
        var x = SignalGenerator.ToFloat(SignalGenerator.WhiteNoise(rng, 48000, 0.2));
        var r = TruePeakMeter.Measure(Mono(x, 48000));
        Assert.True(r.LinearPeak >= x.Max(Math.Abs));
        Assert.Equal(4, r.Oversampling);
    }

    [Fact]
    public void High_rate_uses_sample_peak_without_oversampling()
    {
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(192000, 1000, 19200, 0.5));
        var r = TruePeakMeter.Measure(Mono(x, 192000));
        Assert.Equal(1, r.Oversampling);
        Assert.Equal(20 * Math.Log10(x.Max(Math.Abs)), r.Dbtp, 9);
    }

    [Fact]
    public void Stereo_reports_per_channel_and_overall()
    {
        var l = SignalGenerator.ToFloat(SignalGenerator.Sine(48000, 1000, 4800, 0.5));
        var r = SignalGenerator.ToFloat(SignalGenerator.Sine(48000, 1000, 4800, 0.25));
        var tp = TruePeakMeter.Measure(new AudioData(48000, [l, r]));
        Assert.Equal(2, tp.ChannelDbtp.Count);
        Assert.Equal(tp.ChannelDbtp[0], tp.Dbtp);
        Assert.True(tp.ChannelDbtp[1] < tp.ChannelDbtp[0] - 5);
    }

    // ---- 驗收 #16：正規化 ----

    private static AudioData Program(int fs, double rmsDb, double crestBoostDb = 0)
    {
        var rng = new Random(77);
        var x = SignalGenerator.WhiteNoise(rng, fs * 8, Math.Pow(10, rmsDb / 20));
        if (crestBoostDb > 0)
        {
            // 加入幾個尖峰，讓真峰值遠高於平均響度
            for (var i = 1; i < 8; i++)
            {
                x[i * fs] = Math.Pow(10, crestBoostDb / 20) * 0.5;
            }
        }
        return new AudioData(fs, [SignalGenerator.ToFloat(x), SignalGenerator.ToFloat(x.Reverse().ToArray())]);
    }

    private (double Lufs, double Tp) NormalizeAndMeasure(AudioData input, NormalizationRequest request, out NormalizationPlan plan)
    {
        var loud = LoudnessMeter.Measure(input).IntegratedLufs;
        var tp = TruePeakMeter.Measure(input).Dbtp;
        plan = Normalizer.Plan(loud, tp, request);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
        Normalizer.Write(input, plan, path);
        var output = WavReader.Read(path).Audio;
        return (LoudnessMeter.Measure(output).IntegratedLufs, TruePeakMeter.Measure(output).Dbtp);
    }

    [Theory]
    [InlineData(48000, -30.0, -16.0)]
    [InlineData(44100, -12.0, -23.0)]
    [InlineData(96000, -40.0, -18.0)]
    public void Output_integrated_loudness_is_within_0_1_lu_of_target(int fs, double rms, double target)
    {
        var (lufs, tp) = NormalizeAndMeasure(Program(fs, rms), new NormalizationRequest(target, -1.0), out var plan);
        Assert.False(plan.LimitedByTruePeak);
        Assert.True(Math.Abs(lufs - target) <= 0.1, $"結果 {lufs:F2} LUFS，目標 {target}");
        Assert.True(tp <= -1.0 + 0.05);
    }

    [Fact]
    public void Peak_ceiling_takes_precedence_and_is_explained()
    {
        var input = Program(48000, -30, crestBoostDb: 0);
        var spiky = new AudioData(48000, [input.Channel(0).Select((v, i) => i % 48000 == 100 ? 0.9f : v).ToArray(), input.Channel(1)]);
        var (lufs, tp) = NormalizeAndMeasure(spiky, new NormalizationRequest(-14, -1.0), out var plan);
        Assert.True(plan.LimitedByTruePeak);
        Assert.Contains("峰值為準", plan.Message, StringComparison.Ordinal);
        Assert.True(tp <= -1.0 + 0.05, $"真峰值 {tp:F2}");
        Assert.True(lufs < -14 - 1, "受峰值限制時響度會低於目標");
        Assert.True(Math.Abs(lufs - plan.ExpectedLufs) <= 0.1);
    }

    [Fact]
    public void Silence_cannot_be_normalized()
    {
        var plan = Normalizer.Plan(double.NegativeInfinity, double.NegativeInfinity, new NormalizationRequest(-16, -1));
        Assert.False(plan.CanNormalize);
        Assert.Contains("靜音", plan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelled_write_leaves_no_files()
    {
        var input = Program(48000, -20);
        var plan = Normalizer.Plan(-20, -5, new NormalizationRequest(-16, -1));
        var path = Path.Combine(_dir, "cancel.wav");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Normalizer.Write(input, plan, path, null, cts.Token));
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
