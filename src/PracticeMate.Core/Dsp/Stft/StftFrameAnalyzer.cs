using System.Numerics;
using PracticeMate.Core.Dsp.Fft;
using PracticeMate.Core.Dsp.Windows;

namespace PracticeMate.Core.Dsp.Stft;

/// <summary>
/// 計算單一 STFT 訊框的幅度譜。訊框以 <c>centerSample</c> 為中心，超出訊號邊界補零。
/// 幅度以視窗總和正規化：振幅 A 的正弦（位於 bin 中心）在對應 bin 的幅度為 A/2。
/// 每個執行個體持有暫存區，非執行緒安全。
/// </summary>
public sealed class StftFrameAnalyzer
{
    private readonly double[] _window;
    private readonly double[] _frame;
    private readonly Complex[] _spectrum;
    private readonly RealFft _fft;
    private readonly double _scale;

    public StftFrameAnalyzer(int windowSize, WindowType windowType = WindowType.Hann)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 2);
        WindowSize = windowSize;
        _window = WindowFunction.Create(windowType, windowSize, periodic: true);
        _frame = new double[windowSize];
        _fft = RealFft.Get(windowSize);
        _spectrum = new Complex[_fft.SpectrumLength];
        _scale = 1.0 / _window.Sum();
    }

    public int WindowSize { get; }

    public int BinCount => _fft.SpectrumLength;

    /// <summary>第 k 個 bin 的中心頻率。</summary>
    public static double BinFrequency(int bin, int windowSize, int sampleRate) => (double)bin * sampleRate / windowSize;

    public void Magnitudes(ReadOnlySpan<float> signal, long centerSample, Span<double> magnitudes)
    {
        if (magnitudes.Length != BinCount)
        {
            throw new ArgumentException($"幅度緩衝區長度應為 {BinCount}。", nameof(magnitudes));
        }
        var start = centerSample - (WindowSize / 2);
        for (var j = 0; j < WindowSize; j++)
        {
            var s = start + j;
            _frame[j] = s >= 0 && s < signal.Length ? signal[(int)s] * _window[j] : 0;
        }
        _fft.Forward(_frame, _spectrum);
        for (var k = 0; k < _spectrum.Length; k++)
        {
            magnitudes[k] = _spectrum[k].Magnitude * _scale;
        }
    }
}
