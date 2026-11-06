using PracticeMate.Core.Analysis;
using PracticeMate.Core.Analysis.Beats;
using PracticeMate.Core.Analysis.Loudness;
using PracticeMate.Core.IO.Wav;

namespace PracticeMate.Core.Tests.Analysis;

/// <summary>交付的範例錄音（samples/）以正式分析流程檢查，結果須符合各自的「預期結果」說明。</summary>
public class SampleRecordingsTests
{
    private static string SamplePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PracticeMate.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "samples", name);
    }

    private static AnalysisResult Analyze(string name) =>
        AnalysisPipeline.Run(WavReader.Read(SamplePath(name)).Audio, new AnalysisOptions(), null, default);

    [Fact]
    public void Detuned_scale_matches_expected_notes()
    {
        var r = Analyze("01-走音音階.wav");
        double[] cents = [-30, 25, -12, 30, 5, -25, 18, -8];
        string[] names = ["C4", "D4", "E4", "F4", "G4", "A4", "B4", "C5"];
        Assert.Equal(names, r.Notes.Select(n => n.Name));
        for (var i = 0; i < cents.Length; i++)
        {
            Assert.True(Math.Abs(r.Notes[i].Cents - cents[i]) <= 2, $"{names[i]}：{r.Notes[i].Cents:F2} vs {cents[i]}");
        }
    }

    [Fact]
    public void Rushing_and_dragging_matches_expected_deviations()
    {
        var r = Analyze("02-搶拍與拖拍.wav");
        double[] expected = [0, -30, 20, 0, 45, -15, -40, 10, 25, -20, 0, 35, -50, 15, -10, 30];
        var report = BeatAligner.Align(r.Onsets, r.AnalysisSampleRate, 100, 48000);
        Assert.Equal(expected.Length, report.Beats.Count);
        for (var j = 0; j < expected.Length; j++)
        {
            Assert.True(Math.Abs(report.Beats[j].DeviationMs - expected[j]) <= 5, $"第 {j + 1} 拍：{report.Beats[j].DeviationMs:F1} vs {expected[j]}");
        }
        Assert.Equal(TempoTrend.Steady, report.Trend.Trend);
    }

    [Fact]
    public void Accelerando_is_detected()
    {
        var r = Analyze("03-漸快.wav");
        Assert.Equal(TempoTrend.Accelerating, BeatAligner.Align(r.Onsets, r.AnalysisSampleRate, 120, 24000).Trend.Trend);
    }

    [Theory]
    [InlineData("04-響度偏小.wav", -33.0)]
    [InlineData("05-響度偏大.wav", -10.1)]
    public void Loudness_matches_expected_and_normalizes_to_target(string file, double expectedLufs)
    {
        var r = Analyze(file);
        Assert.True(Math.Abs(r.Loudness.IntegratedLufs - expectedLufs) <= 0.1, $"{r.Loudness.IntegratedLufs:F2} vs {expectedLufs}");
        var plan = Normalizer.Plan(r.Loudness.IntegratedLufs, r.TruePeak.Dbtp, new NormalizationRequest(-16, -1));
        Assert.True(plan.CanNormalize);
        Assert.Equal(-16, plan.ExpectedLufs, 1);
    }
}
