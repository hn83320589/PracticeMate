using PracticeMate.Core.Audio;
using PracticeMate.Core.Dsp.Filters;

namespace PracticeMate.Core.Analysis.Loudness;

/// <param name="EndSample">視窗結束時刻（取樣索引，不含）。</param>
public readonly record struct LoudnessPoint(long EndSample, double Lufs);

/// <summary>響度分析結果。無法定義的值（全被閘掉）為 −∞，LRA 無資料時為 NaN。</summary>
public sealed record LoudnessResult(
    double IntegratedLufs,
    double LoudnessRangeLu,
    IReadOnlyList<LoudnessPoint> Momentary,
    IReadOnlyList<LoudnessPoint> ShortTerm,
    int SampleRate)
{
    public double MaxMomentaryLufs => Momentary.Count == 0 ? double.NegativeInfinity : Momentary.Max(p => p.Lufs);

    public double MaxShortTermLufs => ShortTerm.Count == 0 ? double.NegativeInfinity : ShortTerm.Max(p => p.Lufs);
}

/// <summary>
/// ITU-R BS.1770-4 / EBU Tech 3341、3342 響度計。
/// <list type="bullet">
/// <item>每聲道 K 加權後取平方，累加成 100 ms 子區塊能量（子區塊邊界為 round(i·fs/10)，任何取樣率都是整數取樣位置）。</item>
/// <item>瞬時 = 4 個子區塊（400 ms）、短期 = 30 個子區塊（3 s），皆每 100 ms 一點，時間戳為視窗結束。</item>
/// <item>整合響度：400 ms 區塊、75% 重疊；絕對閘 −70 LUFS、相對閘 −10 LU。</item>
/// <item>LRA：短期響度，絕對閘 −70、相對閘 −20，LRA = P95 − P10（線性內插百分位）。</item>
/// <item>聲道權重 L、R 皆 1.0；單聲道只計一次。</item>
/// </list>
/// </summary>
public static class LoudnessMeter
{
    public const double AbsoluteGateLufs = -70.0;
    public const double RelativeGateLu = -10.0;
    public const double LraRelativeGateLu = -20.0;

    public static LoudnessResult Measure(AudioData audio, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.ChannelCount > 2)
        {
            throw new ArgumentException("響度計算只支援單聲道與立體聲。", nameof(audio));
        }
        var fs = audio.SampleRate;
        var frames = audio.FrameCount;
        var subBlocks = (int)(frames * 10 / fs); // 只計完整的 100 ms 子區塊
        var bounds = new long[subBlocks + 1];
        for (var i = 0; i <= subBlocks; i++)
        {
            bounds[i] = (long)Math.Round(i * fs / 10.0, MidpointRounding.AwayFromZero);
        }

        // 各聲道平行濾波
        var energies = new double[audio.ChannelCount][];
        var done = 0;
        Parallel.For(0, audio.ChannelCount, new ParallelOptions { CancellationToken = cancellationToken }, c =>
        {
            energies[c] = SubBlockEnergies(audio.Channel(c), fs, bounds, cancellationToken);
            progress?.Report((double)Interlocked.Increment(ref done) / audio.ChannelCount);
        });

        // 子區塊能量總和（聲道權重 1.0）
        var sum = new double[subBlocks];
        for (var c = 0; c < energies.Length; c++)
        {
            for (var i = 0; i < subBlocks; i++)
            {
                sum[i] += energies[c][i];
            }
        }

        var momentaryZ = WindowedMeanSquares(sum, bounds, 4);
        var shortTermZ = WindowedMeanSquares(sum, bounds, 30);
        var momentary = momentaryZ.Select(p => new LoudnessPoint(p.End, ToLufs(p.Z))).ToList();
        var shortTerm = shortTermZ.Select(p => new LoudnessPoint(p.End, ToLufs(p.Z))).ToList();

        var integrated = GatedLoudness(momentaryZ.Select(p => p.Z).ToArray(), RelativeGateLu);
        var lra = LoudnessRange(shortTermZ.Select(p => p.Z).ToArray());
        return new LoudnessResult(integrated, lra, momentary, shortTerm, fs);
    }

    /// <summary>BS.1770：L = −0.691 + 10·log10(z)；z = 0 → −∞。</summary>
    public static double ToLufs(double meanSquare) => meanSquare > 0 ? -0.691 + (10 * Math.Log10(meanSquare)) : double.NegativeInfinity;

    private static double[] SubBlockEnergies(float[] samples, int fs, long[] bounds, CancellationToken ct)
    {
        var (shelfC, hpC) = KWeighting.For(fs);
        var shelf = new BiquadFilter(shelfC);
        var hp = new BiquadFilter(hpC);
        var result = new double[bounds.Length - 1];
        for (var i = 0; i < result.Length; i++)
        {
            if ((i & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            double e = 0;
            for (var n = bounds[i]; n < bounds[i + 1]; n++)
            {
                var y = hp.Process(shelf.Process(samples[n]));
                e += y * y;
            }
            result[i] = e;
        }
        return result;
    }

    private static List<(long End, double Z)> WindowedMeanSquares(double[] subBlockSums, long[] bounds, int length)
    {
        var list = new List<(long, double)>();
        double window = 0;
        for (var i = 0; i < subBlockSums.Length; i++)
        {
            window += subBlockSums[i];
            if (i >= length)
            {
                window -= subBlockSums[i - length];
            }
            if (i >= length - 1)
            {
                var start = bounds[i + 1 - length];
                var end = bounds[i + 1];
                list.Add((end, Math.Max(0, window) / (end - start)));
            }
        }
        return list;
    }

    /// <summary>絕對閘 −70 LUFS + 相對閘（相對於絕對閘後的能量平均）。全被閘掉 → −∞。</summary>
    private static double GatedLoudness(double[] blockZ, double relativeGateLu)
    {
        var absGated = blockZ.Where(z => ToLufs(z) > AbsoluteGateLufs).ToArray();
        if (absGated.Length == 0)
        {
            return double.NegativeInfinity;
        }
        var relativeGate = ToLufs(absGated.Average()) + relativeGateLu;
        var gated = absGated.Where(z => ToLufs(z) > relativeGate).ToArray();
        return gated.Length == 0 ? double.NegativeInfinity : ToLufs(gated.Average());
    }

    private static double LoudnessRange(double[] shortTermZ)
    {
        var absGated = shortTermZ.Where(z => ToLufs(z) > AbsoluteGateLufs).ToArray();
        if (absGated.Length == 0)
        {
            return double.NaN;
        }
        var relativeGate = ToLufs(absGated.Average()) + LraRelativeGateLu;
        var values = absGated.Select(ToLufs).Where(l => l > relativeGate).Order().ToArray();
        if (values.Length == 0)
        {
            return double.NaN;
        }
        return Percentile(values, 0.95) - Percentile(values, 0.10);
    }

    /// <summary>線性內插百分位（排序後位置 p·(n−1)）。</summary>
    public static double Percentile(double[] sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Length == 1)
        {
            return sorted[0];
        }
        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(sorted.Length - 1, lo + 1);
        return sorted[lo] + ((pos - lo) * (sorted[hi] - sorted[lo]));
    }
}
