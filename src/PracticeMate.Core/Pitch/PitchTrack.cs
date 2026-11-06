namespace PracticeMate.Core.Pitch;

/// <param name="CenterSample">此點視窗中心的取樣索引（= 點序 × hop）。</param>
public readonly record struct PitchPoint(long CenterSample, bool Voiced, double FrequencyHz, double Clarity, double RmsDbfs);

/// <summary>每 10 ms 一點的音高軌跡。</summary>
public sealed class PitchTrack(int sampleRate, int hopSize, IReadOnlyList<PitchPoint> points)
{
    public int SampleRate { get; } = sampleRate;

    public int HopSize { get; } = hopSize;

    public IReadOnlyList<PitchPoint> Points { get; } = points;
}

public sealed record PitchTrackOptions
{
    /// <summary>有聲判定的 clarity 門檻。</summary>
    public double VoicingClarity { get; init; } = 0.8;

    /// <summary>有聲判定的音量門檻（dBFS）。</summary>
    public double SilenceThresholdDbfs { get; init; } = -50.0;

    public PitchDetectorOptions Detector { get; init; } = new() { MinFrequency = 50, MaxFrequency = 1800 };
}

/// <summary>
/// 產生分析用的音高軌跡：hop = round(fs/100)（44.1/48/96 kHz 皆為整數），
/// 第 i 點的視窗以取樣 i·hop 為中心，超出訊號邊界的部分補零。
/// 各點互相獨立，以分段平行計算（每個工作執行緒各自持有偵測器）。
/// </summary>
public static class PitchTracker
{
    private const int ChunkPoints = 256;

    public static PitchTrack Analyze(
        float[] mono,
        int sampleRate,
        PitchTrackOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mono);
        var o = options ?? new PitchTrackOptions();
        var hop = (int)Math.Round(sampleRate / 100.0);
        var window = McLeodPitchDetector.AnalysisWindowSize(sampleRate);
        var count = mono.Length == 0 ? 0 : (int)(((mono.Length - 1) / hop) + 1);
        var points = new PitchPoint[count];
        var chunks = (count + ChunkPoints - 1) / ChunkPoints;
        var done = 0;

        Parallel.For(
            0,
            chunks,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (Detector: new McLeodPitchDetector(sampleRate, window, o.Detector), Frame: new float[window]),
            (chunk, _, local) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(count, (chunk + 1) * ChunkPoints);
                for (var i = chunk * ChunkPoints; i < end; i++)
                {
                    var center = (long)i * hop;
                    CopyCentered(mono, center, local.Frame);
                    var e = local.Detector.Detect(local.Frame);
                    var voiced = e.HasPitch && e.Clarity >= o.VoicingClarity && e.RmsDbfs >= o.SilenceThresholdDbfs;
                    points[i] = new PitchPoint(center, voiced, e.FrequencyHz, e.Clarity, e.RmsDbfs);
                }
                progress?.Report((double)Interlocked.Increment(ref done) / chunks);
                return local;
            },
            _ => { });

        return new PitchTrack(sampleRate, hop, points);
    }

    private static void CopyCentered(float[] source, long center, float[] frame)
    {
        var start = center - (frame.Length / 2);
        for (var j = 0; j < frame.Length; j++)
        {
            var s = start + j;
            frame[j] = s >= 0 && s < source.Length ? source[s] : 0f;
        }
    }
}
