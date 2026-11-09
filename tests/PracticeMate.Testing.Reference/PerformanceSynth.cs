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
/// <param name="Gain">此音的相對音量（線性），用於漸強/漸弱。</param>
public sealed record SynthNote(double Midi, double OnsetSeconds, double DurationSeconds, double CentsOffset = 0, double Gain = 1.0);

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

    /// <summary>撥弦/打擊的餘音不截斷，自然衰減到訊號結束（與後面的音重疊）。</summary>
    public bool RingOut { get; init; }

    /// <summary>房間殘響的 RT60（秒）；0 為無殘響。以 Schroeder 殘響器（4 組並聯梳狀 + 2 組全通）加在乾聲上。</summary>
    public double ReverbSeconds { get; init; }
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

        if (o.ReverbSeconds > 0)
        {
            x = Reverb(x, fs, o.ReverbSeconds);
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
            : o.RingOut ? x.Length : Math.Min(x.Length, onset + duration + (int)(o.TailSeconds * fs));
        var attack = o.Articulation switch
        {
            Articulation.Wind => o.AttackSeconds,
            Articulation.Plucked => 0.002,
            _ => 0.001,
        } * fs;
        var release = o.ReleaseSeconds * fs;
        var cutFade = 0.010 * fs; // 撥弦/打擊在截斷處淡出 10 ms，避免截斷本身產生喀聲（假起音）
        var phase = 0.0;
        for (var n = onset; n < end; n++)
        {
            var t = n - onset;
            double env = note.Gain * o.Articulation switch
            {
                Articulation.Wind => Math.Min(1.0, t / attack) * Math.Min(1.0, (end - n) / release),
                Articulation.Plucked => Math.Min(1.0, t / attack) * Math.Exp(-t / (o.DecaySeconds * fs)) * Math.Min(1.0, (end - n) / cutFade),
                _ => Math.Min(1.0, t / attack) * Math.Exp(-t / (0.05 * fs)) * Math.Min(1.0, (end - n) / cutFade),
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

    /// <summary>
    /// Schroeder 殘響：4 組並聯回授梳狀濾波器（延遲互質、增益依 RT60 設定）後串接 2 組全通濾波器，
    /// 與乾聲 1:1 混合。只用於產生「前音殘響延續到下一個音」的測試情境。
    /// </summary>
    private static double[] Reverb(double[] dry, int fs, double rt60)
    {
        var y = new double[dry.Length];
        foreach (var ms in new[] { 29.7, 37.1, 41.1, 43.7 })
        {
            var d = (int)(ms / 1000 * fs);
            var g = Math.Pow(10, -3.0 * d / (rt60 * fs));
            var buf = new double[dry.Length];
            for (var n = 0; n < dry.Length; n++)
            {
                buf[n] = dry[n] + (n >= d ? g * buf[n - d] : 0);
                y[n] += 0.25 * buf[n];
            }
        }
        foreach (var ms in new[] { 5.0, 1.7 })
        {
            var d = (int)(ms / 1000 * fs);
            const double g = 0.7;
            var outp = new double[y.Length];
            for (var n = 0; n < y.Length; n++)
            {
                var xd = n >= d ? y[n - d] : 0;
                var yd = n >= d ? outp[n - d] : 0;
                outp[n] = (-g * y[n]) + xd + (g * yd);
            }
            y = outp;
        }
        for (var n = 0; n < y.Length; n++)
        {
            y[n] += dry[n];
        }
        return y;
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
