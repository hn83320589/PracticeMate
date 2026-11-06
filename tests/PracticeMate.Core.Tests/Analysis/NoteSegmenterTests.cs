using PracticeMate.Core.Analysis.Notes;
using PracticeMate.Core.Music;
using PracticeMate.Core.Pitch;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Analysis;

public class NoteSegmenterTests
{
    private const int Fs = 48000;
    private const int Hop = 480;

    /// <summary>以「每點的 MIDI（NaN = 無聲）」建立軌跡。</summary>
    private static PitchTrack Track(params double[] midiPerPoint) => new(Fs, Hop,
        midiPerPoint.Select((m, i) => double.IsNaN(m)
            ? new PitchPoint(i * (long)Hop, false, double.NaN, 0, -90)
            : new PitchPoint(i * (long)Hop, true, NoteMath.MidiToFrequency(m), 0.99, -20)).ToArray());

    private static double[] Repeat(double value, int count) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void Ten_points_make_a_note_nine_do_not()
    {
        Assert.Single(NoteSegmenter.Segment(Track(Repeat(60, 10))));
        Assert.Empty(NoteSegmenter.Segment(Track(Repeat(60, 9))));
    }

    [Fact]
    public void Gap_shorter_than_30ms_is_merged()
    {
        var points = Repeat(60, 8).Concat(Repeat(double.NaN, 2)).Concat(Repeat(60, 8)).ToArray();
        var notes = NoteSegmenter.Segment(Track(points));
        var n = Assert.Single(notes);
        Assert.Equal(18 * Hop, n.EndSample - n.StartSample);
    }

    [Fact]
    public void Gap_of_30ms_splits_notes()
    {
        var points = Repeat(60, 12).Concat(Repeat(double.NaN, 3)).Concat(Repeat(60, 12)).ToArray();
        Assert.Equal(2, NoteSegmenter.Segment(Track(points)).Count);
    }

    [Fact]
    public void Short_note_name_jump_is_noise_of_same_note()
    {
        var points = Repeat(60, 8).Concat(Repeat(61, 2)).Concat(Repeat(60, 8)).ToArray();
        var n = Assert.Single(NoteSegmenter.Segment(Track(points)));
        Assert.Equal(60, n.Midi);
    }

    [Fact]
    public void Multiple_short_gaps_merge_repeatedly()
    {
        var points = Repeat(60, 4).Concat(Repeat(double.NaN, 1)).Concat(Repeat(60, 4)).Concat(Repeat(62, 2)).Concat(Repeat(60, 4)).ToArray();
        Assert.Single(NoteSegmenter.Segment(Track(points)));
    }

    [Fact]
    public void Cents_is_median_of_central_60_percent()
    {
        // 20 點：頭尾各 4 點（20%）為起音/收尾的極端值，中間 12 點為 +10 音分
        var points = Repeat(60.4, 4).Concat(Repeat(60.1, 12)).Concat(Repeat(59.6, 4)).ToArray();
        var n = Assert.Single(NoteSegmenter.Segment(Track(points)));
        Assert.Equal(10, n.Cents, 6);
    }

    [Fact]
    public void Plus_50_cents_belongs_to_higher_note()
    {
        var n = Assert.Single(NoteSegmenter.Segment(Track(Repeat(60.5, 12))));
        Assert.Equal(61, n.Midi);
        Assert.Equal(-50, n.Cents, 6);
    }

    [Fact]
    public void Times_are_sample_indices()
    {
        var points = Repeat(double.NaN, 5).Concat(Repeat(64, 20)).ToArray();
        var n = Assert.Single(NoteSegmenter.Segment(Track(points)));
        Assert.Equal((5 * Hop) - (Hop / 2), n.StartSample);
        Assert.Equal((25 * Hop) - (Hop / 2), n.EndSample);
        Assert.Equal("E4", n.Name);
    }

    [Fact]
    public void A4_setting_is_used()
    {
        // 442 Hz 在 A4 = 442 時為 0 音分
        var pts = Enumerable.Range(0, 12).Select(i => new PitchPoint(i * (long)Hop, true, 442, 0.99, -20)).ToArray();
        var n = Assert.Single(NoteSegmenter.Segment(new PitchTrack(Fs, Hop, pts), new NoteSegmentationOptions { A4 = 442 }));
        Assert.Equal(0, n.Cents, 6);
    }

    /// <summary>
    /// 驗收 #10：刻意走音的音階（含 30 ms 起音、輕微顫音、連音），音符數正確、偏差誤差 ≤ 2 音分。
    /// 「輕微顫音」取 ±5 音分、5.5 Hz。規格以「中間 60% 的中位數」計算音準，當這段時間不是整數個顫音週期時，
    /// 中位數會偏離中心約 0.3 × 顫音深度（無顫音時誤差為 0；README 已知限制有說明）。
    /// </summary>
    [Theory]
    [InlineData(Articulation.WindLegato, 48000)]
    [InlineData(Articulation.Wind, 48000)]
    [InlineData(Articulation.WindLegato, 44100)]
    public void Detuned_scale_report_is_accurate(Articulation articulation, int fs)
    {
        int[] scale = [60, 62, 64, 65, 67, 69, 71, 72];
        double[] offsets = [-30, 25, -12, 30, 5, -25, 18, -8];
        var notes = scale.Select((m, i) => new SynthNote(m, 0.2 + (i * 0.5), articulation == Articulation.Wind ? 0.45 : 0.5, offsets[i])).ToArray();
        var render = PerformanceSynth.Render(notes, new PerformanceOptions
        {
            SampleRate = fs, Articulation = articulation, VibratoDepthCents = 5, VibratoRateHz = 5.5, SnrDb = 40,
        });

        var track = PitchTracker.Analyze(SignalGenerator.ToFloat(render.Signal), fs);
        var report = NoteSegmenter.Segment(track);

        Assert.Equal(scale.Length, report.Count);
        for (var i = 0; i < scale.Length; i++)
        {
            Assert.Equal(scale[i], report[i].Midi);
            Assert.True(Math.Abs(report[i].Cents - offsets[i]) <= 2.0,
                $"第 {i + 1} 個音 {report[i].Name}：報告 {report[i].Cents:F2}，實際 {offsets[i]}");
        }
    }
}
