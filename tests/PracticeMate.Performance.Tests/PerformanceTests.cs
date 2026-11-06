using System.Globalization;
using PracticeMate.Core.Analysis;
using PracticeMate.Core.Audio;
using PracticeMate.Core.Tuner;
using PracticeMate.Testing.Reference;
using Xunit.Abstractions;

namespace PracticeMate.Performance.Tests;

/// <summary>
/// 驗收 #21。以 TimeProvider.System 的時間戳計時。CI 上僅供參考（硬體不固定），README 記錄本機量測。
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests(ITestOutputHelper output)
{
    private static AudioData TenMinutesStereo48k()
    {
        // 合成 30 秒的練習段落後重複 20 次 = 10 分鐘
        var rng = new Random(21);
        var notes = new List<SynthNote>();
        var t = 0.1;
        while (t < 29.5)
        {
            var dur = 0.2 + (rng.NextDouble() * 0.5);
            notes.Add(new SynthNote(55 + rng.Next(30), t, dur, (rng.NextDouble() * 40) - 20));
            t += dur + 0.05;
        }
        var segment = SignalGenerator.ToFloat(PerformanceSynth.Render(notes, new PerformanceOptions { SnrDb = 40, TailSeconds = 0.4 }).Signal);
        const int total = 48000 * 600;
        var left = new float[total];
        var right = new float[total];
        for (var i = 0; i < total; i++)
        {
            left[i] = segment[i % segment.Length];
            right[i] = segment[(i + 777) % segment.Length] * 0.8f;
        }
        return new AudioData(48000, [left, right]);
    }

    [Fact]
    public void Full_analysis_of_10_minutes_stereo_48k_takes_at_most_10_seconds()
    {
        var audio = TenMinutesStereo48k();
        AnalysisPipeline.Run(new AudioData(48000, [audio.Channel(0)[..480000], audio.Channel(1)[..480000]]), new AnalysisOptions(), null, default); // 暖身（JIT）

        var stages = new List<(string Stage, long Timestamp)>();
        var progress = new SyncProgress(p =>
        {
            if (stages.Count == 0 || stages[^1].Stage != p.Stage)
            {
                stages.Add((p.Stage, TimeProvider.System.GetTimestamp()));
            }
        });
        var start = TimeProvider.System.GetTimestamp();
        var result = AnalysisPipeline.Run(audio, new AnalysisOptions(), progress, default);
        var end = TimeProvider.System.GetTimestamp();
        var elapsed = TimeProvider.System.GetElapsedTime(start, end);

        for (var i = 0; i < stages.Count; i++)
        {
            var stageEnd = i + 1 < stages.Count ? stages[i + 1].Timestamp : end;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{stages[i].Stage}: {TimeProvider.System.GetElapsedTime(stages[i].Timestamp, stageEnd).TotalSeconds:F2} s"));
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"總計 {elapsed.TotalSeconds:F2} s（{Environment.ProcessorCount} 個邏輯處理器）；音符 {result.Notes.Count}、起音 {result.Onsets.Count}"));
        Assert.True(elapsed.TotalSeconds <= 10, $"完整分析耗時 {elapsed.TotalSeconds:F2} s");
    }

    [Fact]
    public void Tuner_processes_a_10ms_block_within_1ms()
    {
        const int fs = 48000;
        var engine = new TunerEngine(fs);
        var x = SignalGenerator.ToFloat(SignalGenerator.Tone(fs, 233.08, fs * 5, HarmonicSpectrum.Sawtooth, 0.3));
        const int block = fs / 100;
        var times = new List<double>();
        for (var pos = 0; pos + block <= x.Length; pos += block)
        {
            var t0 = TimeProvider.System.GetTimestamp();
            engine.Consume(x.AsSpan(pos, block), 1, pos);
            times.Add(TimeProvider.System.GetElapsedTime(t0).TotalMilliseconds);
        }
        var measured = times.Skip(50).Order().ToArray(); // 排除暖身
        var median = measured[measured.Length / 2];
        var p99 = measured[(int)(measured.Length * 0.99)];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"調音器 10 ms 區塊：中位數 {median:F3} ms、p99 {p99:F3} ms、最大 {measured[^1]:F3} ms"));
        Assert.True(p99 <= 1.0, $"p99 = {p99:F3} ms");
    }

    private sealed class SyncProgress(Action<AnalysisProgress> report) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => report(value);
    }
}
