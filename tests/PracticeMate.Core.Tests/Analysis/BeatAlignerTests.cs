using PracticeMate.Core.Analysis.Beats;
using PracticeMate.Core.Analysis.Onsets;
using PracticeMate.Core.Analysis.Statistics;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Analysis;

public class BeatAlignerTests
{
    private const int Fs = 48000;

    [Fact]
    public void Deviation_sign_positive_means_dragging()
    {
        // 120 BPM：拍長 24000；起點 1000
        var r = BeatAligner.Align([1000 + 480, 25000 - 960, 49000], Fs, 120, 1000);
        Assert.Equal([10.0, -20.0, 0.0], r.Beats.Select(b => b.DeviationMs));
        Assert.Equal([0L, 1L, 2L], r.Beats.Select(b => b.BeatIndex));
    }

    [Fact]
    public void Onsets_beyond_quarter_beat_are_off_beat()
    {
        // 拍長 24000 → 1/4 拍 = 6000
        var r = BeatAligner.Align([6001, 24000 + 5999, 12000], Fs, 120, 0);
        Assert.Single(r.Beats);
        Assert.Equal(2, r.OffBeatOnsets.Count);
    }

    [Fact]
    public void Onsets_before_first_beat_are_off_beat()
    {
        var r = BeatAligner.Align([100, 24000], Fs, 120, 24000);
        Assert.Equal([100L], r.OffBeatOnsets);
    }

    [Fact]
    public void Mean_and_sample_standard_deviation()
    {
        var r = BeatAligner.Align([0 + 480, 24000 - 480, 48000 + 960, 72000], Fs, 120, 0);
        Assert.Equal(new[] { 10.0, -10.0, 20.0, 0.0 }.Average(), r.MeanMs, 9);
        Assert.Equal(Math.Sqrt(((5 * 5) + (15 * 15) + (15 * 15) + (5 * 5)) / 3.0), r.StandardDeviationMs, 9);
    }

    [Fact]
    public void Non_integer_beat_length_uses_rounded_sample_positions()
    {
        // 44.1 kHz、97 BPM：拍長 27278.35… 取樣，第 10 拍 = round(272783.5…)
        var beat10 = (long)Math.Round(10 * 60.0 * 44100 / 97, MidpointRounding.AwayFromZero);
        var r = BeatAligner.Align([beat10], 44100, 97, 0);
        Assert.Equal(0, r.Beats[0].DeviationMs);
    }

    [Fact]
    public void Few_beats_give_insufficient_data()
    {
        var r = BeatAligner.Align([0, 24000, 48000], Fs, 120, 0);
        Assert.Equal(TempoTrend.InsufficientData, r.Trend.Trend);
        Assert.Equal("資料不足", r.Trend.Description);
    }

    [Fact]
    public void Linear_rush_is_accelerating()
    {
        var onsets = Enumerable.Range(0, 16).Select(j => (j * 24000L) - (j * 48)).ToArray(); // 每拍提早 1 ms
        var r = BeatAligner.Align(onsets, Fs, 120, 0);
        Assert.Equal(TempoTrend.Accelerating, r.Trend.Trend);
        Assert.Equal(-1.0, r.Trend.SlopeMsPerBeat, 9);
    }

    [Fact]
    public void Slope_below_threshold_is_steady_even_if_significant()
    {
        var onsets = Enumerable.Range(0, 16).Select(j => (j * 24000L) + (j * 19)).ToArray(); // 0.396 ms/拍
        Assert.Equal(TempoTrend.Steady, BeatAligner.Align(onsets, Fs, 120, 0).Trend.Trend);
    }

    [Fact]
    public void Large_but_noisy_slope_is_not_significant()
    {
        long[] jitterMs = [30, -40, 25, -35, 40, -30, 20, -45, 35, -20];
        var onsets = jitterMs.Select((d, j) => (j * 24000L) + (d * 48) + (j * 48)).ToArray(); // 斜率約 +1 但雜訊大
        var r = BeatAligner.Align(onsets, Fs, 120, 0);
        Assert.True(r.Trend.PValue > 0.05);
        Assert.Equal(TempoTrend.Steady, r.Trend.Trend);
    }

    [Theory]
    [InlineData(2.228138852, 10, 0.05)]
    [InlineData(1.0, 1, 0.5)]
    [InlineData(0.0, 5, 1.0)]
    [InlineData(1.959963985, 1e7, 0.05)]
    [InlineData(4.302652730, 2, 0.05)]
    [InlineData(2.763262442, 28, 0.01)]
    public void Student_t_two_sided_p_values(double t, double df, double expected)
    {
        Assert.Equal(expected, StudentT.TwoSidedPValue(t, df), 6);
        Assert.Equal(expected, StudentT.TwoSidedPValue(-t, df), 6);
    }

    [Theory]
    [InlineData(5.0, 24.0)]
    [InlineData(0.5, 1.7724538509055159)]
    [InlineData(10.0, 362880.0)]
    public void Log_gamma_matches_known_values(double x, double gamma)
    {
        Assert.Equal(Math.Log(gamma), StudentT.LogGamma(x), 10);
    }

    // ---- 驗收 #12：與合成時刻意加入的偏差比較 ----

    private static BeatReport AnalyzeRhythm(IReadOnlyList<SynthNote> notes, Articulation articulation, double bpm, double firstBeatSeconds)
    {
        var render = PerformanceSynth.Render(notes, new PerformanceOptions { Articulation = articulation, SnrDb = 40 });
        var onsets = OnsetDetector.Detect(SignalGenerator.ToFloat(render.Signal), Fs);
        return BeatAligner.Align(onsets, Fs, bpm, (long)Math.Round(firstBeatSeconds * Fs));
    }

    [Theory]
    [InlineData(Articulation.Wind)]
    [InlineData(Articulation.Percussive)]
    [InlineData(Articulation.Plucked)]
    public void Per_beat_deviation_matches_injected_within_5ms(Articulation articulation)
    {
        var rng = new Random(12 + (int)articulation);
        var injected = Enumerable.Range(0, 24).Select(_ => Math.Round((rng.NextDouble() * 120) - 60)).ToArray();
        var notes = PerformanceSynth.RhythmExercise(120, 0.5, injected);
        var report = AnalyzeRhythm(notes, articulation, 120, 0.5);

        Assert.Equal(injected.Length, report.Beats.Count);
        Assert.Empty(report.OffBeatOnsets);
        for (var j = 0; j < injected.Length; j++)
        {
            var b = report.Beats.Single(x => x.BeatIndex == j);
            Assert.True(Math.Abs(b.DeviationMs - injected[j]) <= 5,
                $"第 {j} 拍：報告 {b.DeviationMs:F1} ms，實際 {injected[j]} ms");
        }
        Assert.True(Math.Abs(report.MeanMs - injected.Average()) <= 5);
    }

    [Fact]
    public void Gradual_two_percent_acceleration_is_detected()
    {
        var notes = PerformanceSynth.RhythmExercise(120, 0.5, new double[20], tempoRampPercent: 2);
        var report = AnalyzeRhythm(notes, Articulation.Wind, 120, 0.5);
        Assert.Equal(TempoTrend.Accelerating, report.Trend.Trend);
        Assert.Equal("漸快", report.Trend.Description);
    }

    [Fact]
    public void Gradual_two_percent_deceleration_is_detected()
    {
        var notes = PerformanceSynth.RhythmExercise(100, 0.4, new double[20], tempoRampPercent: -2);
        var report = AnalyzeRhythm(notes, Articulation.Wind, 100, 0.4);
        Assert.Equal(TempoTrend.Decelerating, report.Trend.Trend);
    }

    [Fact]
    public void Steady_tempo_with_jitter_is_steady()
    {
        var rng = new Random(3);
        var jitter = Enumerable.Range(0, 24).Select(_ => (rng.NextDouble() * 20) - 10).ToArray();
        var report = AnalyzeRhythm(PerformanceSynth.RhythmExercise(120, 0.5, jitter), Articulation.Wind, 120, 0.5);
        Assert.Equal(TempoTrend.Steady, report.Trend.Trend);
    }
}
