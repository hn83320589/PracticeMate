namespace PracticeMate.Testing.Reference;

public enum Articulation
{
    /// <summary>管樂：線性起音（預設 30 ms）、持續、線性收尾。</summary>
    Wind,

    /// <summary>管樂連音：單一連續聲部，音與音之間以滑音連接，包絡不中斷。</summary>
    WindLegato,

    /// <summary>撥弦：2 ms 起音後指數衰減。</summary>
    Plucked,

    /// <summary>打擊：白雜訊爆發，1 ms 起音後快速指數衰減。</summary>
    Percussive,
}

/// <param name="Midi">目標音高（MIDI，A4 = 69）。</param>
/// <param name="CentsOffset">刻意的走音（音分）。</param>
public sealed record SynthNote(double Midi, double OnsetSeconds, double DurationSeconds, double CentsOffset = 0);

public sealed record PerformanceOptions
{
    public int SampleRate { get; init; } = 48000;
    public Articulation Articulation { get; init; } = Articulation.Wind;
    public HarmonicSpectrum Spectrum { get; init; } = HarmonicSpectrum.Sawtooth;
    public double A4 { get; init; } = 440;
    public double AttackSeconds { get; init; } = 0.030;
    public double ReleaseSeconds { get; init; } = 0.030;
    public double VibratoDepthCents { get; init; }
    public double VibratoRateHz { get; init; } = 5.0;
    public double GlideSeconds { get; init; } = 0.040;
    public double DecaySeconds { get; init; } = 0.25;
    public double Amplitude { get; init; } = 0.4;
    public double TailSeconds { get; init; } = 0.3;
    public double? SnrDb { get; init; }
    public int Seed { get; init; } = 1;
}

/// <summary>合成結果與真值：起音取樣索引與每個音的實際（含走音）MIDI 音高。</summary>
public sealed record PerformanceRender(double[] Signal, long[] OnsetSamples, double[] ActualMidi, int SampleRate);

/// <summary>
/// 依音符序列合成練習錄音並提供真值（起音時刻 = 起音包絡的起點）。與正式程式碼完全獨立。
/// </summary>
public static class PerformanceSynth
{
    public static PerformanceRender Render(IReadOnlyList<SynthNote> notes, PerformanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var o = options ?? new PerformanceOptions();
        var fs = o.SampleRate;
        var last = notes.Max(n => n.OnsetSeconds + n.DurationSeconds);
        var length = (int)Math.Ceiling((last + o.TailSeconds) * fs);
        var x = new double[length];
        var onsets = notes.Select(n => (long)Math.Round(n.OnsetSeconds * fs)).ToArray();
        var actual = notes.Select(n => n.Midi + (n.CentsOffset / 100.0)).ToArray();
        var rng = new Random(o.Seed);

        if (o.Articulation == Articulation.WindLegato)
        {
            RenderLegato(x, notes, actual, o);
        }
        else
        {
            for (var i = 0; i < notes.Count; i++)
            {
                RenderNote(x, notes[i], actual[i], onsets[i], o, rng);
            }
        }

        var peak = x.Max(Math.Abs);
        if (peak > 0)
        {
            for (var n = 0; n < x.Length; n++)
            {
                x[n] *= o.Amplitude / peak;
            }
        }
        if (o.SnrDb is { } snr)
        {
            x = SignalGenerator.AddNoise(x, snr, rng);
        }
        return new PerformanceRender(x, onsets, actual, fs);
    }

    private static void RenderNote(double[] x, SynthNote note, double midi, long onset, PerformanceOptions o, Random rng)
    {
        var fs = o.SampleRate;
        var f0 = SignalGenerator.MidiToFrequency(midi, o.A4);
        var duration = (int)Math.Round(note.DurationSeconds * fs);
        var end = o.Articulation == Articulation.Wind
            ? Math.Min(x.Length, onset + duration)
            : Math.Min(x.Length, onset + duration + (int)(o.TailSeconds * fs));
        var attack = o.Articulation switch
        {
            Articulation.Wind => o.AttackSeconds,
            Articulation.Plucked => 0.002,
            _ => 0.001,
        } * fs;
        var release = o.ReleaseSeconds * fs;
        var phase = 0.0;
        for (var n = onset; n < end; n++)
        {
            var t = n - onset;
            double env = o.Articulation switch
            {
                Articulation.Wind => Math.Min(1.0, t / attack) * Math.Min(1.0, (end - n) / release),
                Articulation.Plucked => Math.Min(1.0, t / attack) * Math.Exp(-t / (o.DecaySeconds * fs)),
                _ => Math.Min(1.0, t / attack) * Math.Exp(-t / (0.05 * fs)),
            };
            if (o.Articulation == Articulation.Percussive)
            {
                x[n] += env * ((rng.NextDouble() * 2) - 1);
                continue;
            }
            var vib = o.VibratoDepthCents * Math.Sin(2 * Math.PI * o.VibratoRateHz * t / fs);
            var f = f0 * Math.Pow(2, vib / 1200);
            x[n] += env * Harmonics(phase, f, fs, o.Spectrum);
            phase += 2 * Math.PI * f / fs;
        }
    }

    private static void RenderLegato(double[] x, IReadOnlyList<SynthNote> notes, double[] actual, PerformanceOptions o)
    {
        var fs = o.SampleRate;
        var start = (long)Math.Round(notes[0].OnsetSeconds * fs);
        var end = (long)Math.Round((notes[^1].OnsetSeconds + notes[^1].DurationSeconds) * fs);
        var attack = o.AttackSeconds * fs;
        var release = o.ReleaseSeconds * fs;
        var glide = o.GlideSeconds * fs;
        var phase = 0.0;
        var idx = 0;
        for (var n = start; n < Math.Min(end, x.Length); n++)
        {
            while (idx + 1 < notes.Count && n >= (notes[idx + 1].OnsetSeconds * fs) + (glide / 2))
            {
                idx++;
            }
            var midi = actual[idx];
            if (idx + 1 < notes.Count)
            {
                var boundary = notes[idx + 1].OnsetSeconds * fs;
                var g0 = boundary - (glide / 2);
                if (n >= g0)
                {
                    var a = (n - g0) / glide; // 以 MIDI（對數頻率）線性滑音
                    midi = actual[idx] + (a * (actual[idx + 1] - actual[idx]));
                }
            }
            var t = n - start;
            var vib = o.VibratoDepthCents * Math.Sin(2 * Math.PI * o.VibratoRateHz * t / fs);
            var f = SignalGenerator.MidiToFrequency(midi + (vib / 100), o.A4);
            var env = Math.Min(1.0, t / attack) * Math.Min(1.0, (end - n) / release);
            x[n] += env * Harmonics(phase, f, fs, o.Spectrum);
            phase += 2 * Math.PI * f / fs;
        }
    }

    private static double Harmonics(double phase, double f0, int fs, HarmonicSpectrum spectrum)
    {
        double sum = 0;
        var maxH = Math.Min(spectrum.LastHarmonic, (int)Math.Floor(0.475 * fs / f0));
        for (var h = spectrum.FirstHarmonic; h <= maxH; h++)
        {
            var a = spectrum.Amplitude(h);
            if (a != 0)
            {
                sum += a * Math.Sin(h * phase);
            }
        }
        return sum;
    }

    /// <summary>
    /// 節奏練習：在 BPM 拍點上刻意加入偏差（毫秒，正 = 拖拍）。
    /// tempoRampPercent 讓速度從 BPM 線性漸變到 BPM·(1 + ramp/100)（以拍序內插瞬時速度）。
    /// 回傳的音符起音即為真值。
    /// </summary>
    public static IReadOnlyList<SynthNote> RhythmExercise(double bpm, double firstBeatSeconds, IReadOnlyList<double> deviationsMs,
        double midi = 67, double noteSeconds = 0.2, double tempoRampPercent = 0)
    {
        ArgumentNullException.ThrowIfNull(deviationsMs);
        var notes = new List<SynthNote>();
        var t = firstBeatSeconds;
        var count = deviationsMs.Count;
        for (var k = 0; k < count; k++)
        {
            notes.Add(new SynthNote(midi, t + (deviationsMs[k] / 1000.0), noteSeconds));
            var progress = count > 1 ? (double)k / (count - 1) : 0;
            var instantBpm = bpm * (1 + (tempoRampPercent / 100.0 * progress));
            t += 60.0 / instantBpm;
        }
        return notes;
    }
}
