using PracticeMate.Core.Analysis.Statistics;

namespace PracticeMate.Core.Analysis.Beats;

public enum TempoTrend
{
    Steady,
    Accelerating,
    Decelerating,
    InsufficientData,
}

/// <param name="DeviationMs">起音 − 拍點（正值為拖拍）。</param>
public sealed record BeatDeviation(long OnsetSample, long BeatIndex, long BeatSample, double DeviationMs);

/// <param name="SlopeMsPerBeat">偏差對拍序的迴歸斜率（負值 = 越來越早到 = 漸快）。</param>
public sealed record TempoTrendResult(TempoTrend Trend, double SlopeMsPerBeat, double PValue, int Count)
{
    public string Description => Trend switch
    {
        TempoTrend.Accelerating => "漸快",
        TempoTrend.Decelerating => "漸慢",
        TempoTrend.Steady => "速度穩定",
        _ => "資料不足",
    };
}

public sealed record BeatReport(
    IReadOnlyList<BeatDeviation> Beats,
    IReadOnlyList<long> OffBeatOnsets,
    double MeanMs,
    double StandardDeviationMs,
    TempoTrendResult Trend);

public sealed record BeatAlignmentOptions
{
    /// <summary>判定漸快/漸慢的最小斜率（毫秒/拍）。</summary>
    public double SlopeThresholdMsPerBeat { get; init; } = 0.5;

    /// <summary>斜率 t 檢定的顯著水準（雙尾）。</summary>
    public double SignificanceLevel { get; init; } = 0.05;

    /// <summary>做趨勢判定所需的最少拍數。</summary>
    public int MinimumBeatsForTrend { get; init; } = 8;
}

/// <summary>
/// 節拍對照（依假設事項）：拍點 b_j = 起點 + round(j·60·fs/BPM)（j ≥ 0）；
/// 每個起音對應最近的拍點，偏差 = 起音 − 拍點；相距超過 1/4 拍者列為拍點外，不計入統計。
/// 漸快/漸慢：偏差（ms）對拍序 j 做最小平方迴歸，斜率 t 檢定（df = n − 2）雙尾 p &lt; 0.05
/// 且 |斜率| &gt; 0.5 ms/拍時判定；斜率 &lt; 0 為漸快。
/// </summary>
public static class BeatAligner
{
    public static BeatReport Align(IReadOnlyList<long> onsetSamples, int sampleRate, double bpm, long firstBeatSample, BeatAlignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(onsetSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (bpm <= 0 || double.IsNaN(bpm))
        {
            throw new ArgumentOutOfRangeException(nameof(bpm), bpm, "速度必須為正數。");
        }
        var o = options ?? new BeatAlignmentOptions();
        var beatSamples = 60.0 * sampleRate / bpm;
        var beats = new List<BeatDeviation>();
        var off = new List<long>();

        foreach (var onset in onsetSamples)
        {
            var j = (long)Math.Round((onset - firstBeatSample) / beatSamples, MidpointRounding.AwayFromZero);
            if (j < 0)
            {
                off.Add(onset);
                continue;
            }
            var beat = firstBeatSample + (long)Math.Round(j * beatSamples, MidpointRounding.AwayFromZero);
            var dev = onset - beat;
            if (Math.Abs(dev) > beatSamples / 4)
            {
                off.Add(onset);
                continue;
            }
            beats.Add(new BeatDeviation(onset, j, beat, dev * 1000.0 / sampleRate));
        }

        var devs = beats.Select(b => b.DeviationMs).ToArray();
        var mean = devs.Length == 0 ? double.NaN : devs.Average();
        var sd = devs.Length < 2 ? 0 : Math.Sqrt(devs.Sum(d => (d - mean) * (d - mean)) / (devs.Length - 1));
        return new BeatReport(beats, off, mean, sd, Trend(beats, o));
    }

    private static TempoTrendResult Trend(List<BeatDeviation> beats, BeatAlignmentOptions o)
    {
        var n = beats.Count;
        if (n < Math.Max(3, o.MinimumBeatsForTrend))
        {
            return new TempoTrendResult(TempoTrend.InsufficientData, double.NaN, double.NaN, n);
        }
        var xMean = beats.Average(b => (double)b.BeatIndex);
        var yMean = beats.Average(b => b.DeviationMs);
        double sxx = 0, sxy = 0;
        foreach (var b in beats)
        {
            var dx = b.BeatIndex - xMean;
            sxx += dx * dx;
            sxy += dx * (b.DeviationMs - yMean);
        }
        if (sxx == 0)
        {
            return new TempoTrendResult(TempoTrend.InsufficientData, double.NaN, double.NaN, n);
        }
        var slope = sxy / sxx;
        var intercept = yMean - (slope * xMean);
        var sse = beats.Sum(b => Math.Pow(b.DeviationMs - (intercept + (slope * b.BeatIndex)), 2));
        var se = Math.Sqrt(sse / (n - 2) / sxx);
        var p = se == 0 ? (slope == 0 ? 1.0 : 0.0) : StudentT.TwoSidedPValue(slope / se, n - 2);

        var trend = Math.Abs(slope) > o.SlopeThresholdMsPerBeat && p < o.SignificanceLevel
            ? (slope < 0 ? TempoTrend.Accelerating : TempoTrend.Decelerating)
            : TempoTrend.Steady;
        return new TempoTrendResult(trend, slope, p, n);
    }
}
