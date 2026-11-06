namespace PracticeMate.Core.Dsp.Windows;

public enum WindowType
{
    Rectangular,
    Hann,
    Hamming,
    BlackmanHarris,
}

/// <summary>
/// 視窗函數。廣義餘弦視窗 w[n] = Σ (−1)^k·a_k·cos(2πkn/D)，
/// periodic（頻譜分析/STFT 用）D = N，symmetric（濾波器設計用）D = N − 1。
/// </summary>
public static class WindowFunction
{
    private static readonly double[] HannCoefficients = [0.5, 0.5];
    private static readonly double[] HammingCoefficients = [0.54, 0.46];

    /// <summary>4 項 Blackman–Harris（旁瓣 −92 dB）。</summary>
    private static readonly double[] BlackmanHarrisCoefficients = [0.35875, 0.48829, 0.14128, 0.01168];

    public static double[] Create(WindowType type, int length, bool periodic = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var coefficients = type switch
        {
            WindowType.Rectangular => [1.0],
            WindowType.Hann => HannCoefficients,
            WindowType.Hamming => HammingCoefficients,
            WindowType.BlackmanHarris => BlackmanHarrisCoefficients,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知的視窗類型"),
        };

        var w = new double[length];
        if (length == 1)
        {
            w[0] = 1.0;
            return w;
        }

        var denominator = periodic ? length : length - 1;
        for (var n = 0; n < length; n++)
        {
            // cos(2πk·n/D) = cos(2πk·(D−n)/D)：以較小的鏡像索引計算，讓視窗位元級對稱
            var m = Math.Min(n, denominator - n);
            double sum = 0;
            for (var k = 0; k < coefficients.Length; k++)
            {
                var sign = k % 2 == 0 ? 1.0 : -1.0;
                sum += sign * coefficients[k] * Math.Cos(2.0 * Math.PI * k * m / denominator);
            }
            w[n] = sum;
        }
        return w;
    }

    /// <summary>Kaiser 視窗在連續位置 t ∈ [−halfWidth, halfWidth] 的值。</summary>
    public static double Kaiser(double t, double halfWidth, double beta)
    {
        if (Math.Abs(t) > halfWidth)
        {
            return 0;
        }
        var r = t / halfWidth;
        return BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - (r * r)))) / BesselI0(beta);
    }

    /// <summary>第一類修正 Bessel 函數 I0（級數展開，收斂至相對 1e-17）。</summary>
    public static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var halfX = x / 2.0;
        for (var k = 1; k < 500; k++)
        {
            term *= halfX / k;
            var t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-17)
            {
                break;
            }
        }
        return sum;
    }

    /// <summary>依目標阻帶衰減（dB）計算 Kaiser β（Kaiser 經驗公式）。</summary>
    public static double KaiserBeta(double attenuationDb) => attenuationDb switch
    {
        > 50 => 0.1102 * (attenuationDb - 8.7),
        >= 21 => (0.5842 * Math.Pow(attenuationDb - 21, 0.4)) + (0.07886 * (attenuationDb - 21)),
        _ => 0,
    };
}
