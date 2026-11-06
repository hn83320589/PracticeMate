using PracticeMate.Core.Devices;

namespace PracticeMate.Core.Metronome;

/// <summary>
/// 把排程好的拍點渲染成音訊（離線或在輸出執行緒上）。
/// 以內部取樣計數器追蹤位置；跨區塊邊界的點擊聲會在下一區塊接續。
/// 設定變更由任意執行緒呼叫 <see cref="RequestSettings"/>，在下一次 Render 時交給排程器（下一小節生效）。
/// </summary>
public sealed class MetronomeRenderer : IAudioBlockSource
{
    private readonly MetronomeScheduler _scheduler;
    private readonly float[] _normalClick;
    private readonly float[] _accentClick;
    private readonly List<(long Start, float[] Wave)> _active = new(4);
    private MetronomeSettings? _requested;
    private BeatBox? _lastBeat;

    public MetronomeRenderer(int sampleRate, MetronomeSettings settings)
    {
        _scheduler = new MetronomeScheduler(sampleRate, settings);
        _normalClick = ClickSynth.Create(sampleRate, accent: false);
        _accentClick = ClickSynth.Create(sampleRate, accent: true);
    }

    public int SampleRate => _scheduler.SampleRate;

    /// <summary>下一個要渲染的取樣索引。</summary>
    public long Position { get; private set; }

    /// <summary>最近一個已排入的拍（給 UI 顯示目前拍序；可跨執行緒讀取）。</summary>
    public BeatEvent? LastBeat => Volatile.Read(ref _lastBeat)?.Value;

    /// <summary>要求變更設定（任意執行緒）；於下一小節第一拍生效。</summary>
    public void RequestSettings(MetronomeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _requested, settings);
    }

    /// <summary>渲染下一段（交錯、各聲道相同）。輸出會覆寫 destination。</summary>
    public void Render(Span<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var requested = Interlocked.Exchange(ref _requested, null);
        if (requested is not null)
        {
            _scheduler.RequestSettings(requested);
        }

        interleaved.Clear();
        var frames = interleaved.Length / channels;
        var blockStart = Position;
        var blockEnd = blockStart + frames;

        BeatEvent? latest = null;
        while (_scheduler.Peek().StartSample < blockEnd)
        {
            var beat = _scheduler.Next();
            _active.Add((beat.StartSample, beat.IsAccent ? _accentClick : _normalClick));
            latest = beat;
        }
        if (latest is { } lb)
        {
            Volatile.Write(ref _lastBeat, new BeatBox(lb));
        }

        for (var a = _active.Count - 1; a >= 0; a--)
        {
            var (start, wave) = _active[a];
            var from = Math.Max(start, blockStart);
            var to = Math.Min(start + wave.Length, blockEnd);
            for (var t = from; t < to; t++)
            {
                var v = wave[t - start];
                var baseIndex = (int)(t - blockStart) * channels;
                for (var c = 0; c < channels; c++)
                {
                    interleaved[baseIndex + c] += v;
                }
            }
            if (start + wave.Length <= blockEnd)
            {
                _active.RemoveAt(a);
            }
        }
        Position = blockEnd;
    }

    private sealed record BeatBox(BeatEvent Value);
}
