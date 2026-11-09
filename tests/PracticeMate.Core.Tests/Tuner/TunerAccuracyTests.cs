using PracticeMate.Core.Tuner;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Tuner;

/// <summary>
/// 調音器的精度：與錄音分析相同的訊號格（4 種音色 × 低到高音域、20 dB SNR），
/// 逐 10 ms 區塊餵入，穩定段讀數的誤差中位數 ≤ 1 音分、95% ≤ 3 音分。
/// 調音器為了即時性使用較短視窗（40 ms）與較少的泛音精修，此測試確保精度未因此退化。
/// 例外：低於 60 Hz 的純音在 40 ms 內只有約兩個週期且沒有泛音可借力，
/// 誤差約 2.4 音分（中位數）／5.5 音分（95%），放寬為 3 / 6 音分。
/// </summary>
public class TunerAccuracyTests
{
    private const int Fs = 48000;

    private static readonly HarmonicSpectrum[] Spectra =
    [
        HarmonicSpectrum.Pure, HarmonicSpectrum.Sawtooth, HarmonicSpectrum.Square, HarmonicSpectrum.MissingFundamental,
    ];

    public static TheoryData<int, double> Grid()
    {
        var data = new TheoryData<int, double>();
        foreach (var s in Enumerable.Range(0, Spectra.Length))
        {
            foreach (var f in new[] { 55.0, 82.41, 110.0, 196.0, 440.0, 880.0, 1318.51, 1760.0 })
            {
                data.Add(s, f);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Steady_tone_reading_error_is_within_one_cent(int spectrumIndex, double frequency)
    {
        var spectrum = Spectra[spectrumIndex];
        var rng = new Random((int)frequency + spectrumIndex);
        var tone = SignalGenerator.Tone(Fs, Fs / 2, _ => frequency, spectrum, null, 0.5, 0);
        var x = SignalGenerator.ToFloat(SignalGenerator.AddNoise(tone, 20, rng));

        var engine = new TunerEngine(Fs);
        var errors = new List<double>();
        var block = Fs / 100;
        for (var pos = 0; pos + block <= x.Length; pos += block)
        {
            engine.Consume(x.AsSpan(pos, block), 1, pos);
            var r = engine.Latest;
            // 穩定段：視窗完全落在 50 ms 之後
            if (pos >= Fs / 20 + engine.WindowSize && r.HasPitch)
            {
                errors.Add(Math.Abs(1200 * Math.Log2(r.FrequencyHz / frequency)));
            }
        }

        Assert.True(errors.Count >= 20, $"{spectrum} {frequency} Hz：只有 {errors.Count} 個讀數");
        errors.Sort();
        var median = errors[errors.Count / 2];
        var p95 = errors[(int)(errors.Count * 0.95)];
        var lowPureTone = spectrum == HarmonicSpectrum.Pure && frequency < 60;
        var (maxMedian, maxP95) = lowPureTone ? (3.0, 6.0) : (1.0, 3.0);
        Assert.True(median <= maxMedian, $"{spectrum} {frequency} Hz：中位數誤差 {median:F2} 音分");
        Assert.True(p95 <= maxP95, $"{spectrum} {frequency} Hz：95% 誤差 {p95:F2} 音分");
    }
}
