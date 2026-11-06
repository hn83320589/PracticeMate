namespace PracticeMate.Core.Metronome;

/// <summary>節拍器設定。BPM 為整數，讓拍點公式可以全整數運算。</summary>
public sealed record MetronomeSettings
{
    public const int MinBpm = 30;
    public const int MaxBpm = 300;
    public const int MinBeatsPerBar = 1;
    public const int MaxBeatsPerBar = 12;

    public MetronomeSettings(int bpm, int beatsPerBar, IReadOnlyList<bool>? accents = null)
    {
        if (bpm is < MinBpm or > MaxBpm)
        {
            throw new ArgumentOutOfRangeException(nameof(bpm), bpm, $"速度必須介於 {MinBpm} 與 {MaxBpm} BPM 之間。");
        }
        if (beatsPerBar is < MinBeatsPerBar or > MaxBeatsPerBar)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar), beatsPerBar, $"每小節拍數必須介於 {MinBeatsPerBar} 與 {MaxBeatsPerBar} 之間。");
        }
        if (accents is not null && accents.Count != beatsPerBar)
        {
            throw new ArgumentException($"重音設定的長度（{accents.Count}）必須等於每小節拍數（{beatsPerBar}）。", nameof(accents));
        }
        Bpm = bpm;
        BeatsPerBar = beatsPerBar;
        // 預設：每小節第一拍為重音
        Accents = accents?.ToArray() ?? Enumerable.Range(0, beatsPerBar).Select(i => i == 0).ToArray();
    }

    public int Bpm { get; }

    public int BeatsPerBar { get; }

    /// <summary>小節內第 i 拍是否為重音。</summary>
    public IReadOnlyList<bool> Accents { get; }

    public bool Equals(MetronomeSettings? other) =>
        other is not null && Bpm == other.Bpm && BeatsPerBar == other.BeatsPerBar && Accents.SequenceEqual(other.Accents);

    public override int GetHashCode() => HashCode.Combine(Bpm, BeatsPerBar, Accents.Count(a => a));
}

/// <param name="BeatIndex">從節拍器啟動起算的全域拍序（0 起算）。</param>
/// <param name="StartSample">此拍點擊聲的起點取樣索引。</param>
/// <param name="BeatInBar">小節內拍序（0 起算）。</param>
public readonly record struct BeatEvent(long BeatIndex, long StartSample, int BeatInBar, bool IsAccent, int Bpm);
