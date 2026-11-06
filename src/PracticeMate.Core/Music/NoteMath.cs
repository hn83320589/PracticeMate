using System.Globalization;

namespace PracticeMate.Core.Music;

/// <summary>移調樂器：記譜音高 = 實際音高 + <see cref="NoteMath.TranspositionSemitones"/>。</summary>
public enum Transposition
{
    C,
    BFlat,
    EFlat,
    F,
}

/// <param name="Midi">MIDI 音高（含小數）。</param>
/// <param name="NearestMidi">音名所對應的整數 MIDI（剛好 ±50 音分取較高者）。</param>
/// <param name="Cents">相對 NearestMidi 的偏差，範圍 [−50, 50)。</param>
public readonly record struct NoteReading(double Midi, int NearestMidi, double Cents)
{
    public string Name => NoteMath.NameOf(NearestMidi);
}

/// <summary>
/// 音高換算。m = 69 + 12·log₂(f/A4)；音名取 round(m)（剛好 .5 取較高），音分 = 100·(m − round(m))。
/// 使用升記號（C、C#、D…B），八度編號使 C4 = MIDI 60。
/// </summary>
public static class NoteMath
{
    public const double MinA4 = 415.0;
    public const double MaxA4 = 466.0;
    public const double DefaultA4 = 440.0;

    private static readonly string[] PitchClassNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static int TranspositionSemitones(Transposition t) => t switch
    {
        Transposition.C => 0,
        Transposition.BFlat => 2,
        Transposition.EFlat => 9,
        Transposition.F => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "未知的移調"),
    };

    public static void ValidateA4(double a4)
    {
        if (double.IsNaN(a4) || a4 < MinA4 || a4 > MaxA4)
        {
            throw new ArgumentOutOfRangeException(nameof(a4), a4, $"標準音 A4 必須介於 {MinA4} 與 {MaxA4} Hz 之間。");
        }
    }

    public static double FrequencyToMidi(double frequency, double a4 = DefaultA4)
    {
        if (frequency <= 0 || double.IsNaN(frequency))
        {
            throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "頻率必須為正數。");
        }
        return 69.0 + (12.0 * Math.Log2(frequency / a4));
    }

    public static double MidiToFrequency(double midi, double a4 = DefaultA4) => a4 * Math.Pow(2.0, (midi - 69.0) / 12.0);

    /// <summary>
    /// 由頻率求音名與音分。m 先四捨五入到 1e-9 半音，避免 log₂ 的浮點誤差讓「剛好 +50 音分」落到 49.9999999。
    /// </summary>
    public static NoteReading FromFrequency(double frequency, double a4 = DefaultA4) => FromMidi(FrequencyToMidi(frequency, a4));

    public static NoteReading FromMidi(double midi)
    {
        var m = Math.Round(midi, 9, MidpointRounding.AwayFromZero);
        var nearest = (int)Math.Floor(m + 0.5);
        return new NoteReading(midi, nearest, 100.0 * (m - nearest));
    }

    /// <summary>實際音高 → 記譜音高（同樣的音分偏差）。</summary>
    public static NoteReading ToWritten(NoteReading concert, Transposition t)
    {
        var s = TranspositionSemitones(t);
        return new NoteReading(concert.Midi + s, concert.NearestMidi + s, concert.Cents);
    }

    public static string PitchClassOf(int midi) => PitchClassNames[((midi % 12) + 12) % 12];

    /// <summary>八度編號，C4 = 60。</summary>
    public static int OctaveOf(int midi) => (int)Math.Floor(midi / 12.0) - 1;

    public static string NameOf(int midi) => string.Create(CultureInfo.InvariantCulture, $"{PitchClassOf(midi)}{OctaveOf(midi)}");
}
