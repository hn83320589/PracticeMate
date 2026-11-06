using PracticeMate.Core.Analysis.Onsets;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Analysis;

/// <summary>驗收 #11：打擊、撥弦、管樂（30 ms 起音），起音誤差 ≤ 10 ms，F 值 ≥ 0.95（±50 ms 內視為命中）。</summary>
public class OnsetDetectorTests
{
    private static (double F, double MaxErrorMs, int Matched) Evaluate(IReadOnlyList<long> truth, IReadOnlyList<long> detected, int fs)
    {
        var tol = (long)(0.05 * fs);
        var used = new bool[detected.Count];
        var matched = 0;
        var maxErr = 0.0;
        foreach (var t in truth)
        {
            var best = -1;
            for (var i = 0; i < detected.Count; i++)
            {
                if (!used[i] && Math.Abs(detected[i] - t) <= tol && (best < 0 || Math.Abs(detected[i] - t) < Math.Abs(detected[best] - t)))
                {
                    best = i;
                }
            }
            if (best >= 0)
            {
                used[best] = true;
                matched++;
                maxErr = Math.Max(maxErr, Math.Abs(detected[best] - t) * 1000.0 / fs);
            }
        }
        var precision = detected.Count == 0 ? 0 : (double)matched / detected.Count;
        var recall = (double)matched / truth.Count;
        var f = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return (f, maxErr, matched);
    }

    /// <summary>不規則間隔、不同音高與音量的序列。</summary>
    private static SynthNote[] IrregularSequence(Random rng, int count, double noteSeconds)
    {
        var notes = new List<SynthNote>();
        var t = 0.3;
        for (var i = 0; i < count; i++)
        {
            notes.Add(new SynthNote(55 + rng.Next(0, 30), t, noteSeconds));
            t += noteSeconds + 0.08 + (rng.NextDouble() * 0.3);
        }
        return [.. notes];
    }

    [Theory]
    [InlineData(Articulation.Percussive, 48000)]
    [InlineData(Articulation.Plucked, 48000)]
    [InlineData(Articulation.Wind, 48000)]
    [InlineData(Articulation.Percussive, 44100)]
    [InlineData(Articulation.Plucked, 96000)]
    [InlineData(Articulation.Wind, 44100)]
    public void Detects_onsets_within_10ms_with_high_f_measure(Articulation articulation, int fs)
    {
        var rng = new Random((int)articulation + fs);
        var notes = IrregularSequence(rng, 30, articulation == Articulation.Wind ? 0.25 : 0.2);
        var render = PerformanceSynth.Render(notes, new PerformanceOptions
        {
            SampleRate = fs, Articulation = articulation, SnrDb = 40, Seed = fs,
        });
        var detected = OnsetDetector.Detect(SignalGenerator.ToFloat(render.Signal), fs);
        var (f, maxErr, matched) = Evaluate(render.OnsetSamples, detected, fs);
        Assert.True(f >= 0.95, $"{articulation}@{fs}：F = {f:F3}（命中 {matched}/{render.OnsetSamples.Length}，偵測 {detected.Count}）");
        Assert.True(maxErr <= 10, $"{articulation}@{fs}：最大起音誤差 {maxErr:F1} ms");
    }

    [Fact]
    public void Property_random_sequences_meet_criteria()
    {
        var articulations = new[] { Articulation.Percussive, Articulation.Plucked, Articulation.Wind };
        Prop.ForAll(baseSeed: 7001, iterations: 10, rng =>
        {
            var a = articulations[rng.Next(3)];
            var notes = IrregularSequence(rng, 15, 0.22);
            var render = PerformanceSynth.Render(notes, new PerformanceOptions
            {
                Articulation = a, SnrDb = 35, Seed = rng.Next(), Spectrum = rng.Next(2) == 0 ? HarmonicSpectrum.Sawtooth : HarmonicSpectrum.Square,
            });
            var detected = OnsetDetector.Detect(SignalGenerator.ToFloat(render.Signal), 48000);
            var (f, maxErr, _) = Evaluate(render.OnsetSamples, detected, 48000);
            Assert.True(f >= 0.95, $"{a}：F = {f:F3}");
            Assert.True(maxErr <= 10, $"{a}：最大誤差 {maxErr:F1} ms");
        });
    }

    [Fact]
    public void Steady_tone_with_vibrato_has_no_extra_onsets()
    {
        var render = PerformanceSynth.Render([new SynthNote(67, 0.3, 2.0)], new PerformanceOptions
        {
            VibratoDepthCents = 30, VibratoRateHz = 6, SnrDb = 40,
        });
        var detected = OnsetDetector.Detect(SignalGenerator.ToFloat(render.Signal), 48000);
        Assert.Single(detected);
    }

    [Fact]
    public void Silence_has_no_onsets()
    {
        Assert.Empty(OnsetDetector.Detect(new float[48000], 48000));
        Assert.Empty(OnsetDetector.Detect([], 48000));
    }
}
