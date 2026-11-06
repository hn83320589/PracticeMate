using PracticeMate.Core.Analysis.Loudness;
using PracticeMate.Core.Audio;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Analysis;

/// <summary>驗收 #13、#14。</summary>
public class LoudnessTests
{
    /// <summary>EBU 測試訊號：1 kHz 正弦，各段（峰值 dBFS, 秒數）串接，可選立體聲（兩聲道相同）。</summary>
    private static AudioData Sine1k(int fs, bool stereo, params (double Dbfs, double Seconds)[] segments)
    {
        var parts = new List<float>();
        var phase = 0.0;
        foreach (var (db, sec) in segments)
        {
            var amp = Math.Pow(10, db / 20);
            var n = (int)Math.Round(sec * fs);
            for (var i = 0; i < n; i++)
            {
                parts.Add((float)(amp * Math.Sin(phase)));
                phase += 2 * Math.PI * 1000 / fs;
            }
        }
        var ch = parts.ToArray();
        return new AudioData(fs, stereo ? [ch, (float[])ch.Clone()] : [ch]);
    }

    public static TheoryData<int> Rates => [44100, 48000, 96000];

    [Theory]
    [MemberData(nameof(Rates))]
    public void Ebu3341_case1_stereo_sine_minus_23_dbfs_is_minus_23_lufs(int fs)
    {
        var r = LoudnessMeter.Measure(Sine1k(fs, true, (-23, 20)));
        Assert.Equal(-23.0, r.IntegratedLufs, 1);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Ebu3341_case2_minus_33(int fs)
    {
        Assert.Equal(-33.0, LoudnessMeter.Measure(Sine1k(fs, true, (-33, 20))).IntegratedLufs, 1);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Ebu3341_case3_relative_gate(int fs)
    {
        var r = LoudnessMeter.Measure(Sine1k(fs, true, (-36, 10), (-23, 60), (-36, 10)));
        Assert.InRange(r.IntegratedLufs, -23.1, -22.9);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Ebu3341_case4_absolute_and_relative_gate(int fs)
    {
        var r = LoudnessMeter.Measure(Sine1k(fs, true, (-72, 10), (-36, 10), (-23, 60), (-36, 10), (-72, 10)));
        Assert.InRange(r.IntegratedLufs, -23.1, -22.9);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Ebu3341_case5(int fs)
    {
        var r = LoudnessMeter.Measure(Sine1k(fs, true, (-26, 20), (-20, 20.1), (-26, 20)));
        Assert.InRange(r.IntegratedLufs, -23.1, -22.9);
    }

    [Fact]
    public void Same_signal_at_44k_and_96k_agrees()
    {
        var a = LoudnessMeter.Measure(Sine1k(44100, true, (-18, 10))).IntegratedLufs;
        var b = LoudnessMeter.Measure(Sine1k(96000, true, (-18, 10))).IntegratedLufs;
        Assert.True(Math.Abs(a - b) <= 0.1, $"{a} vs {b}");
    }

    [Fact]
    public void Mono_counts_once_without_plus_3_db()
    {
        Assert.Equal(-26.0, LoudnessMeter.Measure(Sine1k(48000, false, (-23, 10))).IntegratedLufs, 1);
    }

    [Fact]
    public void Silence_is_minus_infinity()
    {
        var r = LoudnessMeter.Measure(new AudioData(48000, [new float[48000 * 5]]));
        Assert.Equal(double.NegativeInfinity, r.IntegratedLufs);
        Assert.True(double.IsNaN(r.LoudnessRangeLu));
    }

    [Fact]
    public void Long_silence_is_gated_out()
    {
        // 交界處部分含訊號的區塊會（正確地）通過相對閘，訊號段越長影響越小
        var withSilence = LoudnessMeter.Measure(Sine1k(48000, true, (-120, 60), (-20, 30), (-120, 60))).IntegratedLufs;
        var alone = LoudnessMeter.Measure(Sine1k(48000, true, (-20, 30))).IntegratedLufs;
        Assert.True(Math.Abs(withSilence - alone) <= 0.1, $"{withSilence} vs {alone}");
    }

    [Fact]
    public void Kweighting_design_reproduces_standard_48k_coefficients()
    {
        var (shelf, hp) = KWeighting.Design(48000);
        var s = KWeighting.StandardShelf48k;
        var h = KWeighting.StandardHighPass48k;
        Assert.Equal(s.B0, shelf.B0, 6);
        Assert.Equal(s.B1, shelf.B1, 6);
        Assert.Equal(s.B2, shelf.B2, 6);
        Assert.Equal(s.A1, shelf.A1, 6);
        Assert.Equal(s.A2, shelf.A2, 6);
        Assert.Equal(h.A1, hp.A1, 6);
        Assert.Equal(h.A2, hp.A2, 6);
    }

    /// <summary>
    /// 響度只在分析取樣率（44.1/48/96 kHz）計算；其他取樣率先轉成 48 kHz，
    /// 因此不驗證低取樣率（雙線性轉換在接近 Nyquist 時的頻率扭曲）。
    /// </summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(96000)]
    public void Kweighting_response_matches_48k_at_audible_frequencies(int fs)
    {
        var (s48, h48) = KWeighting.For(48000);
        var (s, h) = KWeighting.For(fs);
        foreach (var f in new[] { 30.0, 100, 1000, 3000, 10000 })
        {
            var a = s48.MagnitudeDb(f, 48000) + h48.MagnitudeDb(f, 48000);
            var b = s.MagnitudeDb(f, fs) + h.MagnitudeDb(f, fs);
            Assert.True(Math.Abs(a - b) < 0.03, $"{fs} Hz 取樣率在 {f} Hz：{b:F3} vs 48k {a:F3} dB");
        }
    }

    [Fact]
    public void Property_integrated_matches_reference_implementation()
    {
        var rates = new[] { 44100, 48000, 96000 };
        Prop.ForAll(baseSeed: 8001, iterations: 10, rng =>
        {
            var fs = rates[rng.Next(rates.Length)];
            var stereo = rng.Next(2) == 0;
            var seconds = 3 + rng.Next(4);
            var channels = Enumerable.Range(0, stereo ? 2 : 1).Select(_ =>
            {
                // 隨機音量的雜訊段落與靜音段落交錯（觸發兩種閘）
                var x = new double[seconds * fs];
                var pos = 0;
                while (pos < x.Length)
                {
                    var len = Math.Min(x.Length - pos, (int)(fs * (0.2 + rng.NextDouble())));
                    var rms = rng.Next(4) == 0 ? 0 : Math.Pow(10, (-50 + (rng.NextDouble() * 45)) / 20);
                    var seg = SignalGenerator.WhiteNoise(rng, len, rms);
                    Array.Copy(seg, 0, x, pos, len);
                    pos += len;
                }
                return x;
            }).ToArray();
            var floats = channels.Select(c => SignalGenerator.ToFloat(c)).ToArray();
            var expected = ReferenceLoudness.Integrated(floats.Select(f => (IReadOnlyList<double>)f.Select(v => (double)v).ToArray()).ToArray(), fs);
            var actual = LoudnessMeter.Measure(new AudioData(fs, floats)).IntegratedLufs;
            Assert.True(Math.Abs(actual - expected) <= 0.1, $"fs={fs} stereo={stereo}：{actual:F3} vs 參考 {expected:F3}");
        });
    }

    // ---- 瞬時/短期時間對齊 ----

    [Fact]
    public void Momentary_and_short_term_timestamps_are_window_ends_every_100ms()
    {
        const int fs = 48000;
        var r = LoudnessMeter.Measure(Sine1k(fs, true, (-20, 5)));
        Assert.Equal(19200, r.Momentary[0].EndSample);            // 第一點：400 ms
        Assert.Equal(144000, r.ShortTerm[0].EndSample);           // 第一點：3 s
        Assert.Equal(4800, r.Momentary[1].EndSample - r.Momentary[0].EndSample);
        Assert.Equal(4800, r.ShortTerm[1].EndSample - r.ShortTerm[0].EndSample);
        Assert.Equal(47, r.Momentary.Count);                      // (5 s − 0.4 s)/0.1 + 1
        Assert.Equal(21, r.ShortTerm.Count);
    }

    [Fact]
    public void Momentary_reacts_to_step_exactly_when_window_is_filled()
    {
        const int fs = 48000;
        // 5 s 靜音後 5 s 的 −20 dBFS 立體聲正弦；步階在 240000
        var r = LoudnessMeter.Measure(Sine1k(fs, true, (-200, 5), (-20, 5)));
        var full = r.Momentary.First(p => p.EndSample == 240000 + 19200); // 視窗剛好完全落在新段落
        Assert.Equal(-20.0, full.Lufs, 1);
        var before = r.Momentary.First(p => p.EndSample == 240000);
        Assert.True(before.Lufs < -100);
        var shortFull = r.ShortTerm.First(p => p.EndSample == 240000 + 144000);
        Assert.Equal(-20.0, shortFull.Lufs, 1);
        var shortPartial = r.ShortTerm.First(p => p.EndSample == 240000 + 72000); // 一半落在新段落 → 約 −23
        Assert.Equal(-23.0, shortPartial.Lufs, 1);
    }

    // ---- 驗收 #14：EBU Tech 3342 ----

    [Theory]
    [InlineData(10.0, new[] { -20.0, -30.0 })]
    [InlineData(5.0, new[] { -20.0, -15.0 })]
    [InlineData(20.0, new[] { -40.0, -20.0 })]
    [InlineData(15.0, new[] { -50.0, -35.0, -20.0, -35.0, -50.0 })]
    public void Ebu3342_loudness_range(double expectedLra, double[] levels)
    {
        var r = LoudnessMeter.Measure(Sine1k(48000, true, levels.Select(l => (l, 20.0)).ToArray()));
        Assert.True(Math.Abs(r.LoudnessRangeLu - expectedLra) <= 1, $"LRA = {r.LoudnessRangeLu:F2}，預期 {expectedLra}");
    }

    [Fact]
    public void Constant_level_has_zero_lra()
    {
        Assert.Equal(0, LoudnessMeter.Measure(Sine1k(48000, true, (-20, 20))).LoudnessRangeLu, 1);
    }

    [Fact]
    public void Percentile_is_linearly_interpolated()
    {
        double[] v = [0, 10, 20, 30, 40];
        Assert.Equal(4, LoudnessMeter.Percentile(v, 0.10), 9);
        Assert.Equal(38, LoudnessMeter.Percentile(v, 0.95), 9);
    }
}
