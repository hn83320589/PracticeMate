using PracticeMate.Core.Analysis;
using PracticeMate.Core.Audio;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Analysis;

/// <summary>驗收 #20：取消與進度。</summary>
public class AnalysisPipelineTests
{
    private static AudioData Sample(int fs, double seconds = 4)
    {
        var notes = Enumerable.Range(0, (int)(seconds / 0.5) - 1).Select(i => new SynthNote(60 + (i % 8), 0.2 + (i * 0.5), 0.4)).ToArray();
        var x = SignalGenerator.ToFloat(PerformanceSynth.Render(notes, new PerformanceOptions { SampleRate = fs, SnrDb = 40 }).Signal);
        return new AudioData(fs, [x, x]);
    }

    /// <summary>同步收集進度（不經 SynchronizationContext）。</summary>
    private sealed class Collector(Action<AnalysisProgress>? onReport = null) : IProgress<AnalysisProgress>
    {
        public List<AnalysisProgress> Reports { get; } = [];

        public void Report(AnalysisProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
            onReport?.Invoke(value);
        }
    }

    [Fact]
    public async Task Produces_complete_result_with_monotonic_progress_ending_at_one()
    {
        var progress = new Collector();
        var r = await AnalysisPipeline.RunAsync(Sample(48000), null, progress);
        var fractions = progress.Reports.Select(p => p.Fraction).ToArray();
        Assert.True(fractions.Length > 5);
        for (var i = 1; i < fractions.Length; i++)
        {
            Assert.True(fractions[i] > fractions[i - 1], $"進度在第 {i} 筆沒有遞增：{fractions[i - 1]} → {fractions[i]}");
        }
        Assert.Equal(1.0, fractions[^1]);
        Assert.Equal(7, r.Notes.Count);
        Assert.Equal(7, r.Onsets.Count);
        Assert.False(r.Resampled);
        Assert.True(r.Spectrogram.FrameCount > 0);
        Assert.Equal(r.AnalysisFrameCount, r.Waveform.Length);
    }

    [Fact]
    public void Property_cancel_at_any_point_leaves_no_partial_result()
    {
        Prop.ForAll(baseSeed: 11001, iterations: 12, rng =>
        {
            // 最後一個階段從 0.97 開始且沒有中間進度；在回報 1.0（已完成）時才取消屬於「完成後取消」，結果本來就完整
            var cancelAt = rng.NextDouble() * 0.97;
            using var cts = new CancellationTokenSource();
            var progress = new Collector(p =>
            {
                if (p.Fraction >= cancelAt)
                {
                    cts.Cancel();
                }
            });
            AnalysisResult? result = null;
            var ex = Record.Exception(() => result = AnalysisPipeline.Run(Sample(48000, 2), new AnalysisOptions(), progress, cts.Token));
            Assert.IsAssignableFrom<OperationCanceledException>(ex);
            Assert.Null(result);
            var fractions = progress.Reports.Select(p => p.Fraction).ToArray();
            for (var i = 1; i < fractions.Length; i++)
            {
                Assert.True(fractions[i] > fractions[i - 1]);
            }
            Assert.True(fractions[^1] < 1.0);
        });
    }

    [Fact]
    public async Task Pre_cancelled_token_throws_immediately()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnalysisPipeline.RunAsync(Sample(48000, 1), null, null, cts.Token));
    }

    [Theory]
    [InlineData(44100, 44100)]
    [InlineData(48000, 48000)]
    [InlineData(96000, 96000)]
    [InlineData(22050, 48000)]
    [InlineData(192000, 48000)]
    [InlineData(8000, 48000)]
    public void Analysis_rate_rule(int fs, int expected)
    {
        Assert.Equal(expected, AnalysisPipeline.AnalysisRateFor(fs));
    }

    [Fact]
    public async Task Unusual_rate_is_resampled_and_noted()
    {
        var r = await AnalysisPipeline.RunAsync(Sample(32000, 2));
        Assert.True(r.Resampled);
        Assert.Equal(48000, r.AnalysisSampleRate);
        Assert.Contains(r.Warnings, w => w.Contains("轉換為 48000", StringComparison.Ordinal));
        Assert.Equal(3, r.Notes.Count);
        Assert.Equal(4, r.TruePeak.Oversampling); // 真峰值仍以原始 32 kHz 計算
    }

    [Fact]
    public void Rejects_more_than_60_minutes()
    {
        var audio = new AudioData(8000, [new float[(8000 * 3600) + 1]]);
        Assert.Throws<ArgumentException>(() => AnalysisPipeline.Run(audio, new AnalysisOptions(), null, default));
    }
}
