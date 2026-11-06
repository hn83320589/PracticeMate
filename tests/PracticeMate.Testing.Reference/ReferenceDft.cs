using System.Numerics;

namespace PracticeMate.Testing.Reference;

/// <summary>
/// 直接計算的 O(N²) 離散傅立葉轉換，作為 FFT 的參考答案。
/// 角度以 (k·n) mod N 先降到 [0, N) 再計算，避免大角度造成的 sin/cos 精度損失。
/// 與正式程式碼完全獨立。
/// </summary>
public static class ReferenceDft
{
    /// <summary>X[k] = Σ x[n]·e^(−2πi·kn/N)</summary>
    public static Complex[] Forward(IReadOnlyList<Complex> x) => Transform(x, -1);

    /// <summary>x[n] = (1/N)·Σ X[k]·e^(+2πi·kn/N)</summary>
    public static Complex[] Inverse(IReadOnlyList<Complex> x)
    {
        var y = Transform(x, +1);
        for (var i = 0; i < y.Length; i++)
        {
            y[i] /= x.Count;
        }
        return y;
    }

    private static Complex[] Transform(IReadOnlyList<Complex> x, int sign)
    {
        ArgumentNullException.ThrowIfNull(x);
        var n = x.Count;
        var cos = new double[n];
        var sin = new double[n];
        for (var i = 0; i < n; i++)
        {
            var angle = 2.0 * Math.PI * i / n;
            cos[i] = Math.Cos(angle);
            sin[i] = sign * Math.Sin(angle);
        }

        var result = new Complex[n];
        for (var k = 0; k < n; k++)
        {
            double re = 0, im = 0;
            for (var j = 0; j < n; j++)
            {
                var idx = (int)((long)k * j % n);
                var xr = x[j].Real;
                var xi = x[j].Imaginary;
                re += (xr * cos[idx]) - (xi * sin[idx]);
                im += (xr * sin[idx]) + (xi * cos[idx]);
            }
            result[k] = new Complex(re, im);
        }
        return result;
    }

    /// <summary>相對誤差 ‖a − b‖₂ / ‖b‖₂（b 為零向量時回傳 ‖a‖₂）。</summary>
    public static double RelativeError(IReadOnlyList<Complex> actual, IReadOnlyList<Complex> expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        if (actual.Count != expected.Count)
        {
            throw new ArgumentException($"長度不同：{actual.Count} vs {expected.Count}");
        }
        double diff = 0, norm = 0;
        for (var i = 0; i < actual.Count; i++)
        {
            var d = actual[i] - expected[i];
            diff += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
            norm += (expected[i].Real * expected[i].Real) + (expected[i].Imaginary * expected[i].Imaginary);
        }
        return norm == 0 ? Math.Sqrt(diff) : Math.Sqrt(diff / norm);
    }
}
