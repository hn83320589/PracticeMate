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

    /// <summary>
    /// 前音餘音延續到下一個音、且新音不比餘音大（漸弱、同音連續吐音）：
    /// 起音不可被拉到餘音上（曾因此提前約 40 ms）。
    /// </summary>
    [Theory]
    [InlineData(Articulation.Plucked, false)]
    [InlineData(Articulation.Percussive, false)]
    [InlineData(Articulation.Plucked, true)]
    [InlineData(Articulation.Wind, true)]
    public void Onsets_over_a_ringing_previous_note_are_not_pulled_early(Articulation articulation, bool sameNote)
    {
        var rng = new Random(4242 + (int)articulation + (sameNote ? 100 : 0));
        var notes = new List<SynthNote>();
        var t = 0.3;
        for (var i = 0; i < 30; i++)
        {
            // 音量在 −10 到 0 dB 間隨機：新音常比前音餘音小或相近
            var gain = Math.Pow(10, -rng.NextDouble() * 10 / 20);
            notes.Add(new SynthNote(sameNote ? 67 : 55 + rng.Next(0, 30), t, 0.2, Gain: gain));
            t += 0.2 + 0.05 + (rng.NextDouble() * 0.2);
        }
        var render = PerformanceSynth.Render(notes, new PerformanceOptions
        {
            Articulation = articulation, RingOut = true, DecaySeconds = 0.5, SnrDb = 40, Seed = 9,
        });
        var detected = OnsetDetector.Detect(SignalGenerator.ToFloat(render.Signal), 48000);
        var (f, maxErr, matched) = Evaluate(render.OnsetSamples, detected, 48000);
        Assert.True(f >= 0.95, $"{articulation}：F = {f:F3}（命中 {matched}/{notes.Count}，偵測 {detected.Count}）");
        Assert.True(maxErr <= 10, $"{articulation}：最大起音誤差 {maxErr:F1} ms");
    }

    /// <summary>房間殘響（RT60 0.3 s）讓每個音的尾巴延續到下一個音，搭配漸弱樂句。</summary>
    [Theory]
    [InlineData(Articulation.Wind)]
    [InlineData(Articulation.Plucked)]
    [InlineData(Articulation.Percussive)]
    public void Onsets_with_room_reverb_and_diminuendo_stay_within_10ms(Articulation articulation)
    {
        var notes = new List<SynthNote>();
        var t = 0.3;
        var rng = new Random(77 + (int)articulation);
        for (var i = 0; i < 24; i++)
        {
            // 每 8 個音一段漸弱：0 dB → −14 dB
            var gain = Math.Pow(10, -2.0 * (i % 8) / 20);
            notes.Add(new SynthNote(60 + rng.Next(0, 12), t, 0.2, Gain: gain));
            t += 0.25 + (rng.NextDouble() * 0.15);
        }
        var render = PerformanceSynth.Render(notes, new PerformanceOptions
        {
            Articulation = articulation, ReverbSeconds = 0.3, SnrDb = 40, Seed = 5,
        });
        var detected = OnsetDetector.Detect(SignalGenerator.ToFloat(render.Signal), 48000);
        var (f, maxErr, matched) = Evaluate(render.OnsetSamples, detected, 48000);
        Assert.True(f >= 0.95, $"{articulation}：F = {f:F3}（命中 {matched}/{notes.Count}，偵測 {detected.Count}）");
        Assert.True(maxErr <= 10, $"{articulation}：最大起音誤差 {maxErr:F1} ms");
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
