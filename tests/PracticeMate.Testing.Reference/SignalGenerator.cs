namespace PracticeMate.Testing.Reference;

/// <summary>泛音結構（第 h 次泛音的振幅，h 從 1 起算）。</summary>
public sealed class HarmonicSpectrum
{
    private readonly Func<int, double> _amplitude;

    private HarmonicSpectrum(string name, int firstHarmonic, int lastHarmonic, Func<int, double> amplitude)
    {
        Name = name;
        FirstHarmonic = firstHarmonic;
        LastHarmonic = lastHarmonic;
        _amplitude = amplitude;
    }

    public string Name { get; }
    public int FirstHarmonic { get; }

    /// <summary>最高泛音次數（實際合成時另會截掉超過 Nyquist 的泛音）。</summary>
    public int LastHarmonic { get; }

    public double Amplitude(int harmonic) =>
        harmonic < FirstHarmonic || harmonic > LastHarmonic ? 0 : _amplitude(harmonic);

    public static HarmonicSpectrum Pure { get; } = new("純音", 1, 1, _ => 1.0);

    /// <summary>帶限鋸齒波：振幅 1/h。</summary>
    public static HarmonicSpectrum Sawtooth { get; } = new("鋸齒波", 1, int.MaxValue, h => 1.0 / h);

    /// <summary>帶限方波：奇次泛音，振幅 1/h。</summary>
    public static HarmonicSpectrum Square { get; } = new("方波", 1, int.MaxValue, h => h % 2 == 1 ? 1.0 / h : 0.0);

    /// <summary>缺少基頻：只有 2 到 6 次泛音，等振幅。</summary>
    public static HarmonicSpectrum MissingFundamental { get; } = new("缺基頻(2–6)", 2, 6, _ => 1.0);

    public override string ToString() => Name;
}

/// <summary>
/// 測試訊號產生器：以相位累加合成已知音高、振幅、包絡的訊號，並提供已知 SNR 的雜訊。
/// 與正式程式碼完全獨立。所有輸出為 double，最大振幅由呼叫端控制。
/// </summary>
public static class SignalGenerator
{
    /// <summary>單一正弦：amplitude·sin(2π·f·n/fs + phase)。</summary>
    public static double[] Sine(int sampleRate, double frequency, int length, double amplitude = 1.0, double phase = 0.0)
    {
        var x = new double[length];
        for (var n = 0; n < length; n++)
        {
            x[n] = amplitude * Math.Sin((2.0 * Math.PI * frequency * n / sampleRate) + phase);
        }
        return x;
    }

    /// <summary>
    /// 合成有泛音的音色。頻率可隨時間變化（以相位累加，因此滑音與顫音的瞬時頻率正確）。
    /// 輸出峰值會正規化為 <paramref name="peakAmplitude"/>（以合成結果的實際峰值計算）。
    /// </summary>
    /// <param name="frequencyAt">取樣索引 → 瞬時基頻（Hz）。</param>
    /// <param name="envelopeAt">取樣索引 → 包絡（0–1），null 表示固定 1。</param>
    public static double[] Tone(
        int sampleRate,
        int length,
        Func<int, double> frequencyAt,
        HarmonicSpectrum spectrum,
        Func<int, double>? envelopeAt = null,
        double peakAmplitude = 0.5,
        double initialPhase = 0.0)
    {
        ArgumentNullException.ThrowIfNull(frequencyAt);
        ArgumentNullException.ThrowIfNull(spectrum);
        var x = new double[length];
        var phase = initialPhase;
        var nyquist = sampleRate / 2.0;
        for (var n = 0; n < length; n++)
        {
            var f0 = frequencyAt(n);
            double sum = 0;
            var maxHarmonic = Math.Min(spectrum.LastHarmonic, (int)Math.Floor((nyquist * 0.95) / f0));
            for (var h = spectrum.FirstHarmonic; h <= maxHarmonic; h++)
            {
                var a = spectrum.Amplitude(h);
                if (a != 0)
                {
                    sum += a * Math.Sin(h * phase);
                }
            }
            x[n] = sum * (envelopeAt?.Invoke(n) ?? 1.0);
            phase += 2.0 * Math.PI * f0 / sampleRate;
            if (phase > 2.0 * Math.PI * 1e6)
            {
                phase %= 2.0 * Math.PI;
            }
        }
        NormalizePeak(x, peakAmplitude);
        return x;
    }

    /// <summary>固定頻率的音色。</summary>
    public static double[] Tone(int sampleRate, double frequency, int length, HarmonicSpectrum spectrum, double peakAmplitude = 0.5) =>
        Tone(sampleRate, length, _ => frequency, spectrum, null, peakAmplitude);

    /// <summary>平均值為 0、指定 RMS 的高斯白雜訊（Box–Muller）。</summary>
    public static double[] WhiteNoise(Random rng, int length, double rms)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var x = new double[length];
        for (var n = 0; n < length; n++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            x[n] = rms * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        return x;
    }

    /// <summary>加入白雜訊，使 訊號 RMS / 雜訊 RMS = 10^(snrDb/20)。回傳新陣列。</summary>
    public static double[] AddNoise(double[] signal, double snrDb, Random rng)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var noiseRms = Rms(signal) / Math.Pow(10, snrDb / 20.0);
        var noise = WhiteNoise(rng, signal.Length, noiseRms);
        var y = new double[signal.Length];
        for (var n = 0; n < y.Length; n++)
        {
            y[n] = signal[n] + noise[n];
        }
        return y;
    }

    public static double Rms(ReadOnlySpan<double> x)
    {
        if (x.Length == 0)
        {
            return 0;
        }
        double sum = 0;
        foreach (var v in x)
        {
            sum += v * v;
        }
        return Math.Sqrt(sum / x.Length);
    }

    public static void NormalizePeak(double[] x, double peak)
    {
        ArgumentNullException.ThrowIfNull(x);
        double max = 0;
        foreach (var v in x)
        {
            max = Math.Max(max, Math.Abs(v));
        }
        if (max == 0)
        {
            return;
        }
        var g = peak / max;
        for (var n = 0; n < x.Length; n++)
        {
            x[n] *= g;
        }
    }

    public static float[] ToFloat(ReadOnlySpan<double> x)
    {
        var y = new float[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            y[i] = (float)x[i];
        }
        return y;
    }

    /// <summary>MIDI 音高 → 頻率（A4 可設定）。</summary>
    public static double MidiToFrequency(double midi, double a4 = 440.0) => a4 * Math.Pow(2.0, (midi - 69.0) / 12.0);
}
