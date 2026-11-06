using System.Numerics;

namespace PracticeMate.Core.Dsp.Filters;

/// <summary>
/// 正規化（a0 = 1）的雙二階濾波器係數：
/// H(z) = (b0 + b1·z⁻¹ + b2·z⁻²) / (1 + a1·z⁻¹ + a2·z⁻²)
/// </summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>在頻率 f（Hz）的複數頻率響應。</summary>
    public Complex Response(double frequency, double sampleRate)
    {
        var w = 2.0 * Math.PI * frequency / sampleRate;
        var z1 = Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        return (B0 + (B1 * z1) + (B2 * z2)) / (1 + (A1 * z1) + (A2 * z2));
    }

    public double MagnitudeDb(double frequency, double sampleRate) =>
        20.0 * Math.Log10(Response(frequency, sampleRate).Magnitude);
}

/// <summary>雙二階濾波器（Transposed Direct Form II，狀態以 double 保存）。非執行緒安全。</summary>
public sealed class BiquadFilter
{
    private readonly BiquadCoefficients _c;
    private double _s1;
    private double _s2;

    public BiquadFilter(BiquadCoefficients coefficients) => _c = coefficients;

    public BiquadCoefficients Coefficients => _c;

    public double Process(double x)
    {
        var y = (_c.B0 * x) + _s1;
        _s1 = (_c.B1 * x) - (_c.A1 * y) + _s2;
        _s2 = (_c.B2 * x) - (_c.A2 * y);
        return y;
    }

    public void Process(Span<double> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = Process(buffer[i]);
        }
    }

    public void Reset() => (_s1, _s2) = (0, 0);
}

/// <summary>
/// 由類比原型以「預扭曲雙線性轉換」設計雙二階濾波器。
/// 類比原型以正規化角頻率 s' = s/ω₀ 表示：
/// H(s') = (n2·s'² + n1·s' + n0) / (d2·s'² + d1·s' + d0)。
/// 代入 s' = (1/K)·(1 − z⁻¹)/(1 + z⁻¹)，K = tan(π·f₀/fs)，使 f₀ 在數位域精確對應。
/// </summary>
public static class BiquadDesign
{
    public static BiquadCoefficients FromAnalogPrototype(
        double n2, double n1, double n0,
        double d2, double d1, double d0,
        double f0, double sampleRate)
    {
        if (f0 <= 0 || f0 >= sampleRate / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(f0), f0, $"f0 必須介於 0 與 Nyquist（{sampleRate / 2} Hz）之間。");
        }
        var k = Math.Tan(Math.PI * f0 / sampleRate);
        var kk = k * k;
        var b0 = n2 + (n1 * k) + (n0 * kk);
        var b1 = 2.0 * ((n0 * kk) - n2);
        var b2 = n2 - (n1 * k) + (n0 * kk);
        var a0 = d2 + (d1 * k) + (d0 * kk);
        var a1 = 2.0 * ((d0 * kk) - d2);
        var a2 = d2 - (d1 * k) + (d0 * kk);
        return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>二階低通：H(s') = 1 / (s'² + s'/Q + 1)。</summary>
    public static BiquadCoefficients LowPass(double f0, double q, double sampleRate) =>
        FromAnalogPrototype(0, 0, 1, 1, 1 / q, 1, f0, sampleRate);

    /// <summary>二階高通：H(s') = s'² / (s'² + s'/Q + 1)。</summary>
    public static BiquadCoefficients HighPass(double f0, double q, double sampleRate) =>
        FromAnalogPrototype(1, 0, 0, 1, 1 / q, 1, f0, sampleRate);
}
