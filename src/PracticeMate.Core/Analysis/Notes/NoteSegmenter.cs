using PracticeMate.Core.Music;
using PracticeMate.Core.Pitch;

namespace PracticeMate.Core.Analysis.Notes;

/// <param name="StartSample">音符第一點的時間區間起點（含）。</param>
/// <param name="EndSample">音符最後一點的時間區間終點（不含）。</param>
/// <param name="Midi">音名所對應的整數 MIDI（實際音高）。</param>
/// <param name="Cents">中間 60% 時間內各點音分偏差的中位數。</param>
public sealed record NoteEvent(long StartSample, long EndSample, int Midi, double Cents)
{
    public string Name => NoteMath.NameOf(Midi);
}

public sealed record NoteSegmentationOptions
{
    public double A4 { get; init; } = NoteMath.DefaultA4;

    /// <summary>音符最短長度。</summary>
    public double MinNoteSeconds { get; init; } = 0.100;

    /// <summary>短於此長度的無聲或音名跳動視為同一音符的雜訊。</summary>
    public double MaxGapSeconds { get; init; } = 0.030;

    /// <summary>計算音準時使用的中間比例（排除起音與收尾）。</summary>
    public double CentralFraction { get; init; } = 0.6;
}

/// <summary>
/// 音符切分（依假設事項）：
/// <list type="number">
/// <item>每點標記為「無聲」或 round(m)（剛好 ±50 取較高）。</item>
/// <item>相同標記的連續點成為區段；夾在兩個相同音名區段之間、短於 30 ms 的其他區段（無聲或音名跳動）併入該音符。</item>
/// <item>持續 ≥ 100 ms 的音名區段成為音符；音準 = 中間 60% 時間內、標記為該音名的點之音分中位數。</item>
/// </list>
/// 時間以點序換算取樣索引：第 i 點涵蓋 [i·hop − hop/2, i·hop + hop/2)。
/// </summary>
public static class NoteSegmenter
{
    private const int Unvoiced = int.MinValue;

    public static IReadOnlyList<NoteEvent> Segment(PitchTrack track, NoteSegmentationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        var o = options ?? new NoteSegmentationOptions();
        var pts = track.Points;
        var hop = track.HopSize;
        var pointSeconds = (double)hop / track.SampleRate;
        var maxGapPoints = (int)Math.Ceiling((o.MaxGapSeconds / pointSeconds) - 1e-9) - 1;   // 30 ms → 2 點
        var minNotePoints = (int)Math.Ceiling((o.MinNoteSeconds / pointSeconds) - 1e-9);    // 100 ms → 10 點

        var labels = new int[pts.Count];
        var midi = new double[pts.Count];
        for (var i = 0; i < pts.Count; i++)
        {
            if (pts[i].Voiced)
            {
                midi[i] = NoteMath.FrequencyToMidi(pts[i].FrequencyHz, o.A4);
                labels[i] = NoteMath.FromMidi(midi[i]).NearestMidi;
            }
            else
            {
                labels[i] = Unvoiced;
            }
        }

        var runs = BuildRuns(labels);
        var merged = MergeShortGaps(runs, maxGapPoints);

        var notes = new List<NoteEvent>();
        foreach (var (label, start, length) in merged)
        {
            if (label == Unvoiced || length < minNotePoints)
            {
                continue;
            }
            var trim = (int)Math.Floor(length * (1 - o.CentralFraction) / 2);
            var cents = new List<double>();
            for (var i = start + trim; i < start + length - trim; i++)
            {
                if (labels[i] == label)
                {
                    cents.Add(100 * (midi[i] - label));
                }
            }
            if (cents.Count == 0)
            {
                continue;
            }
            notes.Add(new NoteEvent(
                (start * (long)hop) - (hop / 2),
                ((start + length) * (long)hop) - (hop / 2),
                label,
                Median(cents)));
        }
        return notes;
    }

    private static List<(int Label, int Start, int Length)> BuildRuns(int[] labels)
    {
        var runs = new List<(int, int, int)>();
        var i = 0;
        while (i < labels.Length)
        {
            var j = i;
            while (j < labels.Length && labels[j] == labels[i])
            {
                j++;
            }
            runs.Add((labels[i], i, j - i));
            i = j;
        }
        return runs;
    }

    /// <summary>把「音名 L、短雜訊、音名 L」合併為一段（可連續多次）。</summary>
    private static List<(int Label, int Start, int Length)> MergeShortGaps(List<(int Label, int Start, int Length)> runs, int maxGapPoints)
    {
        var result = new List<(int Label, int Start, int Length)>();
        var k = 0;
        while (k < runs.Count)
        {
            var current = runs[k];
            if (current.Label != Unvoiced)
            {
                // 跳過中間短於門檻的雜訊區段（可能是多段），只要後面接回同一音名就合併
                var m = k + 1;
                while (m < runs.Count)
                {
                    var gapPoints = 0;
                    var n = m;
                    while (n < runs.Count && runs[n].Label != current.Label && gapPoints + runs[n].Length <= maxGapPoints)
                    {
                        gapPoints += runs[n].Length;
                        n++;
                    }
                    if (n > m && n < runs.Count && runs[n].Label == current.Label)
                    {
                        current = (current.Label, current.Start, runs[n].Start + runs[n].Length - current.Start);
                        m = n + 1;
                    }
                    else
                    {
                        break;
                    }
                }
                result.Add(current);
                k = m;
            }
            else
            {
                result.Add(current);
                k++;
            }
        }
        return result;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) / 2;
    }
}
