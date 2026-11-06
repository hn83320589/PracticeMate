using PracticeMate.Core.Analysis.Loudness;
using PracticeMate.Core.Analysis.Notes;
using PracticeMate.Core.Analysis.Onsets;
using PracticeMate.Core.Audio;
using PracticeMate.Core.Buffers;
using PracticeMate.Core.Dsp.Resampling;
using PracticeMate.Core.Dsp.Stft;
using PracticeMate.Core.Music;
using PracticeMate.Core.Pitch;

namespace PracticeMate.Core.Analysis;

public sealed record AnalysisOptions
{
    public double A4 { get; init; } = NoteMath.DefaultA4;
    public PitchTrackOptions Pitch { get; init; } = new();
    public OnsetDetectorOptions Onsets { get; init; } = new();
    public SpectrogramOptions Spectrogram { get; init; } = new();
}

/// <param name="Fraction">0–1，單調遞增。</param>
/// <param name="Stage">目前階段的中文名稱。</param>
public sealed record AnalysisProgress(double Fraction, string Stage);

/// <summary>完整分析結果。所有時間皆為「分析取樣率」下的取樣索引（真峰值除外，以原始取樣率計算）。</summary>
public sealed record AnalysisResult(
    int OriginalSampleRate,
    int AnalysisSampleRate,
    int Channels,
    long AnalysisFrameCount,
    float[] Mono,
    MinMaxIndex Waveform,
    PitchTrack PitchTrack,
    IReadOnlyList<NoteEvent> Notes,
    IReadOnlyList<long> Onsets,
    LoudnessResult Loudness,
    TruePeakResult TruePeak,
    Spectrogram Spectrogram,
    IReadOnlyList<string> Warnings)
{
    public bool Resampled => OriginalSampleRate != AnalysisSampleRate;
}

/// <summary>
/// 完整分析流程：（必要時）取樣率轉換 → 真峰值（原始取樣率）→ 響度/LRA → 音高軌跡 → 音符切分 → 起音 → 頻譜圖 → 波形索引。
/// 每一階段內部平行化；階段依序執行，進度以各階段估計成本加權，並保證單調遞增。
/// 取消時拋出 <see cref="OperationCanceledException"/>，只有全部完成才會回傳結果——不會留下半套結果。
/// </summary>
public static class AnalysisPipeline
{
    public const int MaxSeconds = 60 * 60;

    private static readonly (string Name, double Weight)[] Stages =
    [
        ("取樣率轉換", 0.05),
        ("真峰值", 0.10),
        ("響度", 0.08),
        ("音高軌跡", 0.45),
        ("音符切分", 0.02),
        ("起音偵測", 0.12),
        ("頻譜圖", 0.15),
        ("波形", 0.03),
    ];

    /// <summary>分析取樣率：44.1/48/96 kHz 保持不變，其他轉成 48 kHz。</summary>
    public static int AnalysisRateFor(int sampleRate) => sampleRate is 44100 or 48000 or 96000 ? sampleRate : 48000;

    public static Task<AnalysisResult> RunAsync(AudioData audio, AnalysisOptions? options = null,
        IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return Task.Run(() => Run(audio, options ?? new AnalysisOptions(), progress, cancellationToken), cancellationToken);
    }

    public static AnalysisResult Run(AudioData audio, AnalysisOptions options, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        if (audio.FrameCount > (long)MaxSeconds * audio.SampleRate)
        {
            throw new ArgumentException("單一分析檔最長 60 分鐘。", nameof(audio));
        }
        var reporter = new StageReporter(progress);
        var warnings = new List<string>();

        reporter.Begin(0);
        var rate = AnalysisRateFor(audio.SampleRate);
        AudioData analysisAudio;
        if (rate != audio.SampleRate)
        {
            var resampler = new RationalResampler(audio.SampleRate, rate);
            var channels = new float[audio.ChannelCount][];
            Parallel.For(0, audio.ChannelCount, new ParallelOptions { CancellationToken = ct }, c =>
                channels[c] = ResampleParallel(resampler, audio.Channel(c), ct));
            analysisAudio = new AudioData(rate, channels);
            warnings.Add($"取樣率 {audio.SampleRate} Hz 已轉換為 {rate} Hz 進行分析（真峰值仍以原始取樣率計算）。");
        }
        else
        {
            analysisAudio = audio;
        }
        ct.ThrowIfCancellationRequested();

        reporter.Begin(1);
        var truePeak = TruePeakMeter.Measure(audio, ct);

        reporter.Begin(2);
        var loudness = LoudnessMeter.Measure(analysisAudio, reporter.For(2), ct);

        reporter.Begin(3);
        var mono = analysisAudio.MixToMono();
        var track = PitchTracker.Analyze(mono, rate, options.Pitch, reporter.For(3), ct);

        reporter.Begin(4);
        var notes = NoteSegmenter.Segment(track, new NoteSegmentationOptions { A4 = options.A4 });

        reporter.Begin(5);
        var onsets = OnsetDetector.Detect(mono, rate, options.Onsets, ct);

        reporter.Begin(6);
        var spectrogram = SpectrogramBuilder.Compute(mono, rate, options.Spectrogram, reporter.For(6), ct);

        reporter.Begin(7);
        var waveform = new MinMaxIndex(mono);
        ct.ThrowIfCancellationRequested();
        reporter.Complete();

        return new AnalysisResult(audio.SampleRate, rate, audio.ChannelCount, analysisAudio.FrameCount, mono, waveform,
            track, notes, onsets, loudness, truePeak, spectrogram, warnings);
    }

    private static float[] ResampleParallel(RationalResampler r, float[] input, CancellationToken ct)
    {
        var output = new float[r.OutputLength(input.Length)];
        const int chunk = 1 << 16;
        var chunks = (output.Length + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            var start = (long)c * chunk;
            var n = (int)Math.Min(chunk, output.Length - start);
            r.Process(input, start, output.AsSpan((int)start, n));
        });
        return output;
    }

    /// <summary>把各階段的 0–1 進度換算成整體進度，並保證只回報遞增的值（可多執行緒呼叫）。</summary>
    private sealed class StageReporter(IProgress<AnalysisProgress>? sink)
    {
        private readonly object _gate = new();
        private double _last = -1;
        private int _stage;

        public void Begin(int stage)
        {
            _stage = stage;
            Report(Offset(stage));
        }

        public InlineProgress For(int stage) => new InlineProgress(f => Report(Offset(stage) + (Stages[stage].Weight * Math.Clamp(f, 0, 1))));

        public void Complete() => Report(1.0);

        private static double Offset(int stage) => Stages.Take(stage).Sum(s => s.Weight);

        private void Report(double value)
        {
            if (sink is null)
            {
                return;
            }
            lock (_gate)
            {
                if (value <= _last)
                {
                    return;
                }
                _last = value;
                sink.Report(new AnalysisProgress(Math.Min(1.0, value), Stages[Math.Min(_stage, Stages.Length - 1)].Name));
            }
        }
    }

    /// <summary>同步回報（不經 SynchronizationContext），由呼叫端的 IProgress 負責切換執行緒。</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
