namespace PracticeMate.Core.Metronome;

/// <summary>
/// 以取樣索引排程的節拍器。
/// 第 k 拍（區段內 0 起算）的起點 = 區段起點 + round(k·60·fs / BPM)，每拍以 k 直接計算，沒有累積誤差。
/// 速度或拍號變更先記為「待生效」，於下一小節第一拍生效：以該拍（用舊速度算出的）起點作為新區段起點、k 歸零。
/// 非執行緒安全（由輸出執行緒單獨使用）。
/// </summary>
public sealed class MetronomeScheduler
{
    private MetronomeSettings _settings;
    private MetronomeSettings? _pending;
    private long _segmentStart;
    private long _beatInSegment;
    private long _globalBeat;
    private int _beatInBar;

    public MetronomeScheduler(int sampleRate, MetronomeSettings settings, long startSample = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(settings);
        SampleRate = sampleRate;
        _settings = settings;
        _segmentStart = startSample;
    }

    public int SampleRate { get; }

    public MetronomeSettings CurrentSettings => _settings;

    /// <summary>round(k·60·fs / BPM)，以整數運算實作四捨五入（k ≥ 0）。</summary>
    public static long BeatOffset(long k, int bpm, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var numerator = k * 60L * sampleRate;
        return ((2 * numerator) + bpm) / (2L * bpm);
    }

    /// <summary>要求變更設定；於下一小節第一拍生效。</summary>
    public void RequestSettings(MetronomeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _pending = settings;
    }

    /// <summary>下一拍（不前進）。</summary>
    public BeatEvent Peek() => new(
        _globalBeat,
        _segmentStart + BeatOffset(_beatInSegment, _settings.Bpm, SampleRate),
        _beatInBar,
        _settings.Accents[_beatInBar],
        _settings.Bpm);

    /// <summary>取出下一拍並前進。</summary>
    public BeatEvent Next()
    {
        var beat = Peek();
        _globalBeat++;
        _beatInSegment++;
        _beatInBar++;
        if (_beatInBar >= _settings.BeatsPerBar)
        {
            _beatInBar = 0;
            if (_pending is not null)
            {
                // 新小節第一拍的起點仍以舊速度計算，之後以新速度從 k = 0 重新起算
                _segmentStart += BeatOffset(_beatInSegment, _settings.Bpm, SampleRate);
                _beatInSegment = 0;
                _settings = _pending;
                _pending = null;
            }
        }
        return beat;
    }
}
