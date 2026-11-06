namespace PracticeMate.Core.Analysis.Statistics;

/// <summary>Student t 分布與所需的特殊函數（不使用外部數學套件）。</summary>
public static class StudentT
{
    /// <summary>雙尾 p 值：P(|T| ≥ |t|) = I_{df/(df+t²)}(df/2, 1/2)。</summary>
    public static double TwoSidedPValue(double t, double degreesOfFreedom)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(degreesOfFreedom);
        if (double.IsNaN(t))
        {
            return double.NaN;
        }
        if (double.IsInfinity(t))
        {
            return 0;
        }
        var x = degreesOfFreedom / (degreesOfFreedom + (t * t));
        return Math.Clamp(RegularizedIncompleteBeta(degreesOfFreedom / 2, 0.5, x), 0, 1);
    }

    /// <summary>正規化不完全 Beta 函數 I_x(a, b)，以連分式（修正 Lentz 法）計算。</summary>
    public static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0)
        {
            return 0;
        }
        if (x >= 1)
        {
            return 1;
        }
        var logFront = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + (a * Math.Log(x)) + (b * Math.Log(1 - x));
        var front = Math.Exp(logFront);
        // 依收斂條件選擇直接計算或使用對稱關係 I_x(a,b) = 1 − I_{1−x}(b,a)
        return x < (a + 1) / (a + b + 2)
            ? front * BetaContinuedFraction(a, b, x) / a
            : 1 - (front * BetaContinuedFraction(b, a, 1 - x) / b);
    }

    /// <summary>ln Γ(x)，Lanczos 近似（g = 7，n = 9），x &gt; 0，相對誤差約 1e-15。</summary>
    public static double LogGamma(double x)
    {
        ReadOnlySpan<double> c =
        [
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
            1.5056327351493116e-7,
        ];
        if (x < 0.5)
        {
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        }
        x -= 1;
        var sum = c[0];
        for (var i = 1; i < c.Length; i++)
        {
            sum += c[i] / (x + i);
        }
        var t = x + 7.5;
        return (0.5 * Math.Log(2 * Math.PI)) + ((x + 0.5) * Math.Log(t)) - t + Math.Log(sum);
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const double tiny = 1e-300;
        const double eps = 1e-15;
        var qab = a + b;
        var qap = a + 1;
        var qam = a - 1;
        var c = 1.0;
        var d = 1 - (qab * x / qap);
        d = Math.Abs(d) < tiny ? tiny : d;
        d = 1 / d;
        var h = d;
        for (var m = 1; m <= 300; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + (aa * d);
            d = Math.Abs(d) < tiny ? tiny : d;
            c = 1 + (aa / c);
            c = Math.Abs(c) < tiny ? tiny : c;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + (aa * d);
            d = Math.Abs(d) < tiny ? tiny : d;
            c = 1 + (aa / c);
            c = Math.Abs(c) < tiny ? tiny : c;
            d = 1 / d;
            var del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < eps)
            {
                break;
            }
        }
        return h;
    }
}
