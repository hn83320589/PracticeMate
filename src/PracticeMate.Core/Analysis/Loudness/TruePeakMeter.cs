using System.Numerics;
using PracticeMate.Core.Audio;
using PracticeMate.Core.Dsp.Windows;

namespace PracticeMate.Core.Analysis.Loudness;

/// <param name="Dbtp">20·log10(|峰值|)，滿刻度為 0 dBTP；全零為 −∞。</param>
/// <param name="Oversampling">實際使用的過取樣倍數（1 表示未過取樣）。</param>
public sealed record TruePeakResult(double Dbtp, double LinearPeak, IReadOnlyList<double> ChannelDbtp, int Oversampling);

/// <summary>
/// 真峰值（dBTP）。
/// <list type="bullet">
/// <item>fs &lt; 176.4 kHz：4 倍多相 FIR 過取樣。原型為 Kaiser 視窗 sinc（每相 32 taps、阻帶 70 dB），
/// 截止頻率在原取樣率的 Nyquist，使第 0 相恰為單位脈衝——原取樣點原封不動，真峰值永遠 ≥ 取樣峰值。</item>
/// <item>4 倍過取樣對接近 Nyquist 的正弦最多低估約 0.7 dB，因此對接近目前最大值（−0.9 dB 內）的過取樣局部極值
/// 再以三點拋物線內插精修。</item>
/// <item>fs ≥ 176.4 kHz：直接取樣峰值。</item>
/// </list>
/// 定義：全零訊號 → −∞ dBTP；單一取樣脈衝 a → 20·log10|a|（第 0 相為 δ，sinc 主瓣其餘相位皆小於 |a|）。
/// </summary>
public static class TruePeakMeter
{
    public const int Factor = 4;
    public const int TapsPerPhase = 32;
    public const double StopbandDb = 70;
    public const int NoOversamplingRate = 176400;

    private static readonly float[][] Phases = DesignPhases();

    public static TruePeakResult Measure(AudioData audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var oversample = audio.SampleRate < NoOversamplingRate;
        var peaks = new double[audio.ChannelCount];
        Parallel.For(0, audio.ChannelCount, new ParallelOptions { CancellationToken = cancellationToken }, c =>
        {
            peaks[c] = oversample ? OversampledPeak(audio.Channel(c), cancellationToken) : SamplePeak(audio.Channel(c));
        });
        var max = peaks.Length == 0 ? 0 : peaks.Max();
        return new TruePeakResult(ToDb(max), max, peaks.Select(ToDb).ToArray(), oversample ? Factor : 1);
    }

    public static double ToDb(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;

    private static double SamplePeak(float[] x)
    {
        var max = 0.0;
        foreach (var v in x)
        {
            max = Math.Max(max, Math.Abs(v));
        }
        return max;
    }

    /// <summary>
    /// 第 p 相（p = 1..3）的輸出 y[4m+p] = Σ_k h_p[k]·x[m − H + 1 + k]；第 0 相即 x[m]。
    /// 以區段平行計算，最後合併各區段最大值。
    /// </summary>
    private static double OversampledPeak(float[] x, CancellationToken ct)
    {
        const int chunk = 1 << 16;
        var chunks = (x.Length + chunk - 1) / chunk;
        var best = SamplePeak(x);
        var lockObj = new object();
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, ci =>
        {
            var start = ci * chunk;
            var end = Math.Min(x.Length, start + chunk);
            var local = ChunkPeak(x, start, end, best);
            lock (lockObj)
            {
                best = Math.Max(best, local);
            }
        });
        return best;
    }

    private static double ChunkPeak(float[] x, int start, int end, double samplePeak)
    {
        const int half = TapsPerPhase / 2;
        var best = samplePeak;
        var window = new float[TapsPerPhase];
        // 前一個與目前的過取樣值，用於找局部極值並做拋物線精修
        double prev2 = 0, prev1 = 0;
        var haveHistory = 0;
        for (var m = start; m < end; m++)
        {
            // 取 x[m − half + 1 .. m + half]（邊界外為 0）
            var baseIndex = m - half + 1;
            if (baseIndex >= 0 && baseIndex + TapsPerPhase <= x.Length)
            {
                x.AsSpan(baseIndex, TapsPerPhase).CopyTo(window);
            }
            else
            {
                for (var k = 0; k < TapsPerPhase; k++)
                {
                    var i = baseIndex + k;
                    window[k] = i >= 0 && i < x.Length ? x[i] : 0f;
                }
            }
            for (var p = 0; p < Factor; p++)
            {
                double y = p == 0 ? x[m] : Dot(window, Phases[p]);
                if (haveHistory >= 2)
                {
                    var a = Math.Abs(prev1);
                    if (a >= best * 0.9 && a >= Math.Abs(prev2) && a >= Math.Abs(y) && Math.Sign(prev1) == Math.Sign(prev2) && Math.Sign(prev1) == Math.Sign(y))
                    {
                        best = Math.Max(best, ParabolicPeak(Math.Abs(prev2), a, Math.Abs(y)));
                    }
                    best = Math.Max(best, a);
                }
                prev2 = prev1;
                prev1 = y;
                haveHistory++;
            }
        }
        return Math.Max(best, Math.Abs(prev1));
    }

    private static double ParabolicPeak(double a, double b, double c)
    {
        var denom = a - (2 * b) + c;
        if (denom >= 0)
        {
            return b;
        }
        var d = 0.5 * (a - c) / denom;
        return b - (0.25 * (a - c) * d);
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(a, i) * new Vector<float>(b, i);
        }
        var s = Vector.Sum(sum);
        for (; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }
        return s;
    }

    /// <summary>
    /// 原型 h(t) = sinc(t/4)·w(t)，t 為過取樣域的整數位置；第 p 相、第 k 個係數對應
    /// 輸入 x[m − H + 1 + k] 與輸出時間 4m + p 的距離 t = 4(m − (m − H + 1 + k)) + p = 4(H − 1 − k) + p。
    /// </summary>
    private static float[][] DesignPhases()
    {
        const int half = TapsPerPhase / 2;
        var beta = WindowFunction.KaiserBeta(StopbandDb);
        var halfWidth = (double)half * Factor;
        var phases = new float[Factor][];
        for (var p = 0; p < Factor; p++)
        {
            phases[p] = new float[TapsPerPhase];
            for (var k = 0; k < TapsPerPhase; k++)
            {
                var t = (Factor * (half - 1 - k)) + p; // 輸出相對輸入的距離（過取樣取樣數）
                var u = t / (double)Factor;
                var sinc = u == 0 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
                phases[p][k] = (float)(sinc * WindowFunction.Kaiser(t, halfWidth, beta));
            }
        }
        return phases;
    }
}
