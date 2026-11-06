using PracticeMate.Core.Dsp.Filters;

namespace PracticeMate.Core.Analysis.Loudness;

/// <summary>
/// ITU-R BS.1770-4 的 K 加權（第一級高架 + 第二級高通）。
/// 48 kHz 直接使用標準中的係數；其他取樣率由類比原型以預扭曲雙線性轉換重新設計：
/// <list type="bullet">
/// <item>高架：H(s') = (V_h·s'² + (V_b/Q)·s' + 1) / (s'² + s'/Q + 1)，V_h = 10^(G/20)，V_b = V_h^0.4996667741545416
///（V_b 的指數取自 B. De Man 對標準係數的反推，與 libebur128 相同）。</item>
/// <item>高通：分子取 [1, −2, 1]（不除以 a0，與標準 48 kHz 係數的寫法一致），分母為 s'² + s'/Q + 1 的雙線性轉換。</item>
/// <item>K = tan(π·f₀/fs)。參數：高架 f₀ = 1681.974450955533 Hz、G = 3.999843853973347 dB、Q = 0.7071752369554196；
/// 高通 f₀ = 38.13547087602444 Hz、Q = 0.5003270373238773。</item>
/// </list>
/// </summary>
public static class KWeighting
{
    public const double ShelfFrequency = 1681.974450955533;
    public const double ShelfGainDb = 3.999843853973347;
    public const double ShelfQ = 0.7071752369554196;
    public const double HighPassFrequency = 38.13547087602444;
    public const double HighPassQ = 0.5003270373238773;
    public const double ShelfVbExponent = 0.4996667741545416;

    /// <summary>BS.1770-4 表 1（48 kHz）。</summary>
    public static BiquadCoefficients StandardShelf48k { get; } =
        new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);

    /// <summary>BS.1770-4 表 2（48 kHz）。</summary>
    public static BiquadCoefficients StandardHighPass48k { get; } =
        new(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621);

    public static (BiquadCoefficients Shelf, BiquadCoefficients HighPass) For(int sampleRate) =>
        sampleRate == 48000 ? (StandardShelf48k, StandardHighPass48k) : Design(sampleRate);

    /// <summary>由類比原型設計（任何取樣率；48 kHz 時用於驗證設計與標準係數一致）。</summary>
    public static (BiquadCoefficients Shelf, BiquadCoefficients HighPass) Design(int sampleRate)
    {
        var vh = Math.Pow(10, ShelfGainDb / 20);
        var vb = Math.Pow(vh, ShelfVbExponent);
        var shelf = BiquadDesign.FromAnalogPrototype(vh, vb / ShelfQ, 1, 1, 1 / ShelfQ, 1, ShelfFrequency, sampleRate);

        var k = Math.Tan(Math.PI * HighPassFrequency / sampleRate);
        var a0 = 1 + (k / HighPassQ) + (k * k);
        var hp = new BiquadCoefficients(
            1.0, -2.0, 1.0,
            2 * ((k * k) - 1) / a0,
            (1 - (k / HighPassQ) + (k * k)) / a0);
        return (shelf, hp);
    }
}
