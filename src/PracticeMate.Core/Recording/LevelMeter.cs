namespace PracticeMate.Core.Recording;

/// <summary>電平讀數（dBFS；無訊號時為 −∞）。</summary>
public sealed record LevelReading(double PeakDbfs, double RmsDbfs, long EndFrame)
{
    public static LevelReading Silent { get; } = new(double.NegativeInfinity, double.NegativeInfinity, 0);
}

/// <summary>
/// 輸入電平表：每 <c>windowFrames</c> 框（所有聲道合計）計算一次峰值與 RMS，
/// 以不可變快照發布，UI 執行緒可隨時讀取最新值。
/// </summary>
public sealed class LevelMeter : ICaptureConsumer
{
    private readonly int _windowFrames;
    private double _peak;
    private double _sumSquares;
    private long _samples;
    private int _framesInWindow;
    private LevelReading _latest = LevelReading.Silent;

    public LevelMeter(int windowFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowFrames);
        _windowFrames = windowFrames;
    }

    /// <summary>50 ms 視窗。</summary>
    public static LevelMeter ForSampleRate(int sampleRate) => new(Math.Max(1, sampleRate / 20));

    public LevelReading Latest => Volatile.Read(ref _latest);

    public void Consume(ReadOnlySpan<float> interleaved, int channels, long firstFrame)
    {
        var frames = interleaved.Length / channels;
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                double v = interleaved[(f * channels) + c];
                _peak = Math.Max(_peak, Math.Abs(v));
                _sumSquares += v * v;
                _samples++;
            }
            if (++_framesInWindow >= _windowFrames)
            {
                Volatile.Write(ref _latest, new LevelReading(ToDb(_peak), ToDb(Math.Sqrt(_sumSquares / _samples)), firstFrame + f + 1));
                _peak = 0;
                _sumSquares = 0;
                _samples = 0;
                _framesInWindow = 0;
            }
        }
    }

    private static double ToDb(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;
}
