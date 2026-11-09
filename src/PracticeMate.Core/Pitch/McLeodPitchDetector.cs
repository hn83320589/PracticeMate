using System.Numerics;
using PracticeMate.Core.Dsp.Fft;

namespace PracticeMate.Core.Pitch;

public sealed record PitchDetectorOptions
{
    public double MinFrequency { get; init; } = 50.0;
    public double MaxFrequency { get; init; } = 1800.0;

    /// <summary>MPM 的 k：取第一個高於 k·最大峰值的關鍵極大值（抑制八度錯誤）。</summary>
    public double PeakThreshold { get; init; } = 0.9;

    /// <summary>以泛音的 DTFT 峰值精修頻率（見 <see cref="HarmonicFrequencyRefiner"/>）。</summary>
    public bool HarmonicRefinement { get; init; } = true;

    /// <summary>精修時最多使用的泛音數（取最強者）。越少越快，但雜訊中的精度略降。</summary>
    public int RefinementHarmonics { get; init; } = 6;

    /// <summary>每個泛音峰值的 Newton 迭代次數。</summary>
    public int RefinementIterations { get; init; } = 3;
}

/// <summary>單一視窗的音高估計。Clarity 為 NSDF 峰值（0–1），越接近 1 週期性越明確。</summary>
public readonly record struct PitchEstimate(double FrequencyHz, double Clarity, double RmsDbfs)
{
    public static PitchEstimate None(double rmsDbfs) => new(double.NaN, 0, rmsDbfs);

    public bool HasPitch => !double.IsNaN(FrequencyHz);
}

/// <summary>
/// 音高偵測：McLeod Pitch Method（NSDF）選週期 + 泛音 DTFT 峰值精修頻率。
/// <list type="number">
/// <item>NSDF n(τ) = 2·r(τ)/m(τ)，r 為截斷視窗自相關（零填充到 ≥ 2W 的實數 FFT 計算），
/// m(τ) = Σ(x[j]² + x[j+τ]²) 遞推計算。</item>
/// <item>各正值區段取最大值（關鍵極大值），選第一個 ≥ k·全域最大者——抑制八度錯誤，
/// 且以週期性判斷，對缺少基頻的音色有效。</item>
/// <item>拋物線內插得到粗估基頻，再由 <see cref="HarmonicFrequencyRefiner"/> 精修。</item>
/// </list>
/// 每個執行個體持有暫存區，非執行緒安全；平行分析時每個執行緒各建一個。
/// </summary>
public sealed class McLeodPitchDetector
{
    private readonly PitchDetectorOptions _options;
    private readonly RealFft _fft;
    private readonly double[] _padded;
    private readonly Complex[] _spectrum;
    private readonly double[] _acf;
    private readonly double[] _nsdf;
    private readonly double[] _m;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly HarmonicFrequencyRefiner? _refiner;

    public McLeodPitchDetector(int sampleRate, int windowSize, PitchDetectorOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 32);
        _options = options ?? new PitchDetectorOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.RefinementHarmonics, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.RefinementIterations, nameof(options));
        SampleRate = sampleRate;
        WindowSize = windowSize;
        var fftSize = (int)BitOperations.RoundUpToPowerOf2((uint)(2 * windowSize));
        _fft = RealFft.Get(fftSize);
        _padded = new double[fftSize];
        _spectrum = new Complex[_fft.SpectrumLength];
        _acf = new double[fftSize];
        _minLag = Math.Max(2, (int)Math.Floor(sampleRate / _options.MaxFrequency));
        _maxLag = Math.Min(windowSize - 2, (int)Math.Ceiling(sampleRate / _options.MinFrequency));
        _nsdf = new double[_maxLag + 2];
        _m = new double[_maxLag + 2];
        _refiner = _options.HarmonicRefinement ? new HarmonicFrequencyRefiner(sampleRate, windowSize, _options.RefinementHarmonics, _options.RefinementIterations) : null;
    }

    public int SampleRate { get; }

    public int WindowSize { get; }

    /// <summary>分析用的建議視窗：約 80 ms 進位到 2 的冪次（48 kHz → 4096，96 kHz → 8192）。</summary>
    public static int AnalysisWindowSize(int sampleRate) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Ceiling(0.08 * sampleRate));

    public PitchEstimate Detect(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException($"視窗長度 {window.Length} 與設定 {WindowSize} 不符。", nameof(window));
        }

        double energy = 0;
        for (var i = 0; i < WindowSize; i++)
        {
            double v = window[i];
            _padded[i] = v;
            energy += v * v;
        }
        var rmsDb = energy > 0 ? 10 * Math.Log10(energy / WindowSize) : double.NegativeInfinity;
        if (energy <= 0)
        {
            return PitchEstimate.None(rmsDb);
        }
        _padded.AsSpan(WindowSize).Clear();

        // r(τ) = IFFT(|X|²)
        _fft.Forward(_padded, _spectrum);
        for (var k = 0; k < _spectrum.Length; k++)
        {
            var c = _spectrum[k];
            _spectrum[k] = (c.Real * c.Real) + (c.Imaginary * c.Imaginary);
        }
        _fft.Inverse(_spectrum, _acf);

        // m(τ) 遞推：m(0) = 2Σx²，m(τ) = m(τ−1) − x[τ−1]² − x[W−τ]²
        var limit = _maxLag + 1;
        _m[0] = 2 * energy;
        for (var tau = 1; tau <= limit; tau++)
        {
            var a = _padded[tau - 1];
            var b = _padded[WindowSize - tau];
            _m[tau] = _m[tau - 1] - (a * a) - (b * b);
        }
        for (var tau = 0; tau <= limit; tau++)
        {
            _nsdf[tau] = _m[tau] > 0 ? 2 * _acf[tau] / _m[tau] : 0;
        }

        var peakLag = PickPeak();
        if (peakLag < 0)
        {
            return PitchEstimate.None(rmsDb);
        }

        var (lag, clarity) = Parabolic(peakLag);
        var frequency = SampleRate / lag;
        if (_refiner is not null)
        {
            frequency = _refiner.Refine(window, frequency);
        }
        return new PitchEstimate(frequency, Math.Clamp(clarity, 0, 1), rmsDb);
    }

    /// <summary>MPM 關鍵極大值選取。回傳整數延遲，找不到回傳 −1。</summary>
    private int PickPeak()
    {
        Span<int> candidates = stackalloc int[64];
        var count = 0;
        var tau = 1;
        // 跳過從 τ=0 開始的第一個正值區段
        while (tau <= _maxLag && _nsdf[tau] > 0)
        {
            tau++;
        }
        var globalMax = 0.0;
        while (tau <= _maxLag && count < candidates.Length)
        {
            while (tau <= _maxLag && _nsdf[tau] <= 0)
            {
                tau++;
            }
            var best = -1;
            while (tau <= _maxLag && _nsdf[tau] > 0)
            {
                if (best < 0 || _nsdf[tau] > _nsdf[best])
                {
                    best = tau;
                }
                tau++;
            }
            if (best >= _minLag && best < _maxLag)
            {
                candidates[count++] = best;
                globalMax = Math.Max(globalMax, _nsdf[best]);
            }
        }
        if (count == 0 || globalMax <= 0)
        {
            return -1;
        }
        var threshold = _options.PeakThreshold * globalMax;
        for (var i = 0; i < count; i++)
        {
            if (_nsdf[candidates[i]] >= threshold)
            {
                return candidates[i];
            }
        }
        return -1;
    }

    private (double Lag, double Value) Parabolic(int t)
    {
        double a = _nsdf[t - 1], b = _nsdf[t], c = _nsdf[t + 1];
        var denom = a - (2 * b) + c;
        if (denom >= 0)
        {
            return (t, b);
        }
        var delta = 0.5 * (a - c) / denom;
        return (t + delta, b - (0.25 * (a - c) * delta));
    }
}
