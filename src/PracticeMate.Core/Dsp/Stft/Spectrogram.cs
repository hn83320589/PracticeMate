using PracticeMate.Core.Dsp.Windows;

namespace PracticeMate.Core.Dsp.Stft;

public sealed record SpectrogramOptions
{
    public const int MinWindow = 64;
    public const int MaxWindow = 16384;

    public int WindowSize { get; init; } = 2048;

    /// <summary>重疊比例 [0, 0.95]。hop = max(1, round(W·(1 − overlap)))。</summary>
    public double Overlap { get; init; } = 0.75;

    public WindowType Window { get; init; } = WindowType.Hann;

    public int HopSize => Math.Max(1, (int)Math.Round(WindowSize * (1 - Overlap)));

    public void Validate()
    {
        if (WindowSize is < MinWindow or > MaxWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(WindowSize), WindowSize, $"視窗大小必須介於 {MinWindow} 與 {MaxWindow} 之間。");
        }
        if (Overlap is < 0 or > 0.95 || double.IsNaN(Overlap))
        {
            throw new ArgumentOutOfRangeException(nameof(Overlap), Overlap, "重疊比例必須介於 0 與 95% 之間。");
        }
    }
}

/// <summary>
/// 頻譜圖（以 8 位元量化的 dB 儲存以節省記憶體：dB = −120 + 0.5·v，v ∈ [0, 240]）。
/// 第 f 個訊框以取樣 f·hop 為中心；第 k 個 bin 中心頻率 k·fs/W。
/// </summary>
public sealed class Spectrogram
{
    public const double FloorDb = -120.0;
    public const double StepDb = 0.5;

    private readonly byte[] _cells;

    internal Spectrogram(int sampleRate, int windowSize, int hopSize, int frameCount, byte[] cells)
    {
        SampleRate = sampleRate;
        WindowSize = windowSize;
        HopSize = hopSize;
        FrameCount = frameCount;
        BinCount = (windowSize / 2) + 1;
        _cells = cells;
    }

    public int SampleRate { get; }
    public int WindowSize { get; }
    public int HopSize { get; }
    public int FrameCount { get; }
    public int BinCount { get; }

    public long FrameCenterSample(int frame) => (long)frame * HopSize;

    public double BinFrequency(int bin) => (double)bin * SampleRate / WindowSize;

    public double MagnitudeDb(int frame, int bin) => FloorDb + (StepDb * _cells[((long)frame * BinCount) + bin]);

    /// <summary>在任意頻率取值（相鄰 bin 間線性內插 dB）。</summary>
    public double MagnitudeDbAt(int frame, double frequency)
    {
        var pos = Math.Clamp(frequency * WindowSize / SampleRate, 0, BinCount - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(BinCount - 1, lo + 1);
        var t = pos - lo;
        return ((1 - t) * MagnitudeDb(frame, lo)) + (t * MagnitudeDb(frame, hi));
    }

    public int PeakBin(int frame)
    {
        var best = 0;
        var row = (long)frame * BinCount;
        for (var k = 1; k < BinCount; k++)
        {
            if (_cells[row + k] > _cells[row + best])
            {
                best = k;
            }
        }
        return best;
    }

    public static byte Quantize(double db) =>
        (byte)Math.Clamp(Math.Round((db - FloorDb) / StepDb), 0, 240);
}

public static class SpectrogramBuilder
{
    public static Spectrogram Compute(float[] mono, int sampleRate, SpectrogramOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mono);
        var o = options ?? new SpectrogramOptions();
        o.Validate();
        var hop = o.HopSize;
        var frames = mono.Length == 0 ? 0 : ((mono.Length - 1) / hop) + 1;
        var bins = (o.WindowSize / 2) + 1;
        var cells = new byte[(long)frames * bins];
        const int chunk = 256;
        var chunks = (frames + chunk - 1) / chunk;
        var done = 0;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellationToken },
            () => (Stft: new StftFrameAnalyzer(o.WindowSize, o.Window), Mag: new double[bins]),
            (c, _, local) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(frames, (c + 1) * chunk);
                for (var f = c * chunk; f < end; f++)
                {
                    local.Stft.Magnitudes(mono, (long)f * hop, local.Mag);
                    var row = (long)f * bins;
                    for (var k = 0; k < bins; k++)
                    {
                        var m = local.Mag[k];
                        cells[row + k] = Spectrogram.Quantize(m > 0 ? 20 * Math.Log10(m) : Spectrogram.FloorDb);
                    }
                }
                progress?.Report((double)Interlocked.Increment(ref done) / chunks);
                return local;
            },
            _ => { });
        return new Spectrogram(sampleRate, o.WindowSize, hop, frames, cells);
    }
}

/// <summary>頻譜圖顯示的頻率軸映射（列 0 為最低頻）。</summary>
public static class FrequencyAxis
{
    /// <summary>線性：f = fmin + (fmax − fmin)·y/H。</summary>
    public static double LinearFrequency(double row, int height, double fmin, double fmax) => fmin + ((fmax - fmin) * row / height);

    /// <summary>對數：f = fmin·(fmax/fmin)^(y/H)。</summary>
    public static double LogFrequency(double row, int height, double fmin, double fmax)
    {
        if (fmin <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fmin), fmin, "對數頻率軸的最低頻率必須大於 0。");
        }
        return fmin * Math.Pow(fmax / fmin, row / height);
    }

    /// <summary>對數軸的反映射：y = H·log(f/fmin)/log(fmax/fmin)。</summary>
    public static double LogRow(double frequency, int height, double fmin, double fmax) =>
        height * Math.Log(frequency / fmin) / Math.Log(fmax / fmin);

    public static double LinearRow(double frequency, int height, double fmin, double fmax) =>
        height * (frequency - fmin) / (fmax - fmin);
}
