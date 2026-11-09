using System.Numerics;
using PracticeMate.Core.Dsp.Fft;
using PracticeMate.Core.Dsp.Windows;

namespace PracticeMate.Core.Pitch;

/// <summary>
/// 由粗估基頻精修頻率：對 Hann 視窗化的訊框，在每個主要泛音附近以 Newton 法求
/// 離散時間傅立葉轉換（DTFT）功率 |X(θ)|² 的極大值（單一弦波於白雜訊中的最大概似估計），
/// 再以功率加權最小平方合併：f0 = Σ P_h·h·f_h / Σ P_h·h²。
/// 缺少基頻時 P_1 ≈ 0，自然不影響結果。
/// <para>MPM 負責「選對週期」（避免八度錯誤），本類別負責「量得準」：MPM 的自相關峰值
/// 在 20 dB 白雜訊下對低音純音有數音分的誤差，而 DTFT 峰值接近 Cramér–Rao 下界。</para>
/// 非執行緒安全。
/// </summary>
internal sealed class HarmonicFrequencyRefiner
{
    private const double MaxDeviation = 0.03; // 泛音峰值與 h·f0 的最大相對偏差（約 ±50 音分）

    private readonly int _sampleRate;
    private readonly int _windowSize;
    private readonly int _fftSize;
    private readonly double _maxHarmonicFrequency;
    private readonly int _maxHarmonics;
    private readonly int _newtonIterations;
    private readonly double[] _hann;
    private readonly double[] _weighted;
    private readonly double[] _padded;
    private readonly Complex[] _spectrum;
    private readonly double[] _power;
    private readonly RealFft _fft;
    private readonly List<(int Harmonic, int Bin, double Power)> _peaks = new(32);

    public HarmonicFrequencyRefiner(int sampleRate, int windowSize, int maxHarmonics, int newtonIterations, double maxHarmonicFrequency = 6000)
    {
        _sampleRate = sampleRate;
        _windowSize = windowSize;
        _fftSize = (int)BitOperations.RoundUpToPowerOf2((uint)(2 * windowSize));
        _maxHarmonicFrequency = Math.Min(maxHarmonicFrequency, 0.45 * sampleRate);
        _maxHarmonics = maxHarmonics;
        _newtonIterations = newtonIterations;
        _hann = WindowFunction.Create(WindowType.Hann, windowSize, periodic: false);
        _weighted = new double[windowSize];
        _padded = new double[_fftSize];
        _fft = RealFft.Get(_fftSize);
        _spectrum = new Complex[_fft.SpectrumLength];
        _power = new double[_fft.SpectrumLength];
    }

    /// <summary>回傳精修後的基頻；無法可靠精修時回傳原估計。</summary>
    public double Refine(ReadOnlySpan<float> frame, double coarseF0)
    {
        for (var n = 0; n < _windowSize; n++)
        {
            _weighted[n] = frame[n] * _hann[n];
        }
        _weighted.CopyTo(_padded, 0);
        _padded.AsSpan(_windowSize).Clear();
        _fft.Forward(_padded, _spectrum);
        for (var k = 0; k < _spectrum.Length; k++)
        {
            _power[k] = (_spectrum[k].Real * _spectrum[k].Real) + (_spectrum[k].Imaginary * _spectrum[k].Imaginary);
        }

        FindHarmonicPeaks(coarseF0);
        if (_peaks.Count == 0)
        {
            return coarseF0;
        }

        double num = 0, den = 0;
        foreach (var (h, bin, power) in _peaks)
        {
            var theta = NewtonPeak(InterpolatedTheta(bin));
            var fh = theta * _sampleRate / (2 * Math.PI);
            if (Math.Abs((fh / h) - coarseF0) > MaxDeviation * coarseF0)
            {
                continue;
            }
            num += power * h * fh;
            den += power * h * h;
        }
        return den > 0 ? num / den : coarseF0;
    }

    private void FindHarmonicPeaks(double f0)
    {
        _peaks.Clear();
        var binPerHz = (double)_fftSize / _sampleRate;
        var maxPower = 0.0;
        for (var h = 1; h * f0 <= _maxHarmonicFrequency; h++)
        {
            var center = h * f0 * binPerHz;
            var radius = Math.Max(2.0, MaxDeviation * center);
            var lo = Math.Max(1, (int)Math.Floor(center - radius));
            var hi = Math.Min(_power.Length - 2, (int)Math.Ceiling(center + radius));
            var best = -1;
            for (var k = lo; k <= hi; k++)
            {
                if (best < 0 || _power[k] > _power[best])
                {
                    best = k;
                }
            }
            if (best > 0 && _power[best] >= _power[best - 1] && _power[best] >= _power[best + 1])
            {
                _peaks.Add((h, best, _power[best]));
                maxPower = Math.Max(maxPower, _power[best]);
            }
        }

        // 只保留最強的幾個泛音（且不低於最強者 −20 dB），降低雜訊與運算量
        _peaks.RemoveAll(p => p.Power < 0.01 * maxPower);
        if (_peaks.Count > _maxHarmonics)
        {
            _peaks.Sort((a, b) => b.Power.CompareTo(a.Power));
            _peaks.RemoveRange(_maxHarmonics, _peaks.Count - _maxHarmonics);
        }
    }

    /// <summary>以對數幅度的拋物線內插取得初始角頻率。</summary>
    private double InterpolatedTheta(int bin)
    {
        var a = Math.Log(_power[bin - 1] + 1e-300);
        var b = Math.Log(_power[bin] + 1e-300);
        var c = Math.Log(_power[bin + 1] + 1e-300);
        var denom = a - (2 * b) + c;
        var delta = denom < 0 ? 0.5 * (a - c) / denom : 0;
        return 2 * Math.PI * (bin + Math.Clamp(delta, -0.5, 0.5)) / _fftSize;
    }

    /// <summary>
    /// Newton 法求 P(θ) = |X(θ)|² 的極大值，X(θ) = Σ a_n·e^(−iθu_n)，u_n = n − (W−1)/2（以視窗中心為時間原點，
    /// 讓導數數值較小）。P' = 2Re(X̄·X')、P'' = 2(|X'|² + Re(X̄·X''))。
    /// </summary>
    private double NewtonPeak(double theta)
    {
        var start = theta;
        var maxStep = 2 * Math.PI / _fftSize;
        var c = (_windowSize - 1) / 2.0;
        for (var iter = 0; iter < _newtonIterations; iter++)
        {
            Complex x = 0, x1 = 0, x2 = 0;
            var rot = Complex.FromPolarCoordinates(1, -theta);
            var z = Complex.FromPolarCoordinates(1, theta * c); // e^(−iθ·u_0)，u_0 = −c
            for (var n = 0; n < _windowSize; n++)
            {
                var u = n - c;
                var t = _weighted[n] * z;
                x += t;
                x1 += t * u;
                x2 += t * (u * u);
                z *= rot;
            }
            // X' = −i·Σ u·a·e，X'' = −Σ u²·a·e
            x1 *= -Complex.ImaginaryOne;
            x2 = -x2;
            var p1 = 2 * ((Complex.Conjugate(x) * x1).Real);
            var p2 = 2 * ((x1.Magnitude * x1.Magnitude) + (Complex.Conjugate(x) * x2).Real);
            if (p2 >= 0)
            {
                return start;
            }
            var step = Math.Clamp(-p1 / p2, -maxStep, maxStep);
            theta += step;
            if (Math.Abs(step) < 1e-12)
            {
                break;
            }
        }
        return Math.Abs(theta - start) <= 2 * maxStep ? theta : start;
    }
}
