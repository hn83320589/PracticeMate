namespace PracticeMate.Core.Buffers;

public readonly record struct MinMax(float Min, float Max);

/// <summary>
/// 波形的最小最大抽樣。以分層區塊（每層 ×64）金字塔加速任意區間查詢：
/// 區間 = 頭尾原始取樣 + 中間整塊（遞迴到更高層），結果與暴力計算完全相同。
/// <para>欄位切分：畫面第 c 欄涵蓋 [s + ⌊c·L/W⌋, s + ⌊(c+1)·L/W⌋)；放大到一欄不足一個取樣時，
/// 該欄取單一取樣 s + ⌊c·L/W⌋。</para>
/// </summary>
public sealed class MinMaxIndex
{
    private const int Fanout = 64;
    private const int BruteForceLimit = 2 * Fanout;

    private readonly float[] _samples;
    private readonly List<(float[] Min, float[] Max)> _levels = [];

    public MinMaxIndex(float[] samples)
    {
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
        // 第 0 層：每 64 個取樣一格
        var (min, max) = Reduce(samples, samples);
        while (min.Length > 0)
        {
            _levels.Add((min, max));
            if (min.Length <= Fanout)
            {
                break;
            }
            (min, max) = Reduce(min, max);
        }
    }

    public long Length => _samples.Length;

    /// <summary>[start, end) 的最小與最大值（end &gt; start）。</summary>
    public MinMax Query(long start, long end)
    {
        if (start < 0 || end > _samples.Length || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"區間 [{start}, {end}) 不合法（長度 {_samples.Length}）。");
        }
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        QueryLevel(-1, start, end, ref min, ref max);
        return new MinMax(min, max);
    }

    /// <summary>把 [start, end) 分成 width 欄，回傳每欄的最小與最大值。</summary>
    public MinMax[] Columns(long start, long end, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (start < 0 || end > _samples.Length || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"區間 [{start}, {end}) 不合法（長度 {_samples.Length}）。");
        }
        var length = end - start;
        var result = new MinMax[width];
        for (var c = 0; c < width; c++)
        {
            var (a, b) = ColumnRange(start, length, width, c);
            result[c] = Query(a, b);
        }
        return result;
    }

    /// <summary>欄位 c 的取樣區間（保證非空）。</summary>
    public static (long Start, long End) ColumnRange(long start, long length, int width, int column)
    {
        // 整數除法即 floor（皆非負），避免浮點在大數值時把 k − ε 進位成 k
        var a = start + (column * length / width);
        var b = start + ((column + 1) * length / width);
        a = Math.Min(a, start + length - 1);
        return (a, Math.Max(b, a + 1));
    }

    /// <summary>level = −1 表示原始取樣；level ≥ 0 為金字塔第 level 層的格索引。</summary>
    private void QueryLevel(int level, long i0, long i1, ref float min, ref float max)
    {
        var next = level + 1;
        if (i1 - i0 <= BruteForceLimit || next >= _levels.Count)
        {
            Scan(level, i0, i1, ref min, ref max);
            return;
        }
        var j0 = (i0 + Fanout - 1) / Fanout;
        var j1 = i1 / Fanout;
        if (j1 <= j0)
        {
            Scan(level, i0, i1, ref min, ref max);
            return;
        }
        Scan(level, i0, j0 * Fanout, ref min, ref max);
        QueryLevel(next, j0, j1, ref min, ref max);
        Scan(level, j1 * Fanout, i1, ref min, ref max);
    }

    private void Scan(int level, long i0, long i1, ref float min, ref float max)
    {
        if (level < 0)
        {
            for (var i = i0; i < i1; i++)
            {
                var v = _samples[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return;
        }
        var (mins, maxs) = _levels[level];
        for (var i = i0; i < i1; i++)
        {
            if (mins[i] < min) min = mins[i];
            if (maxs[i] > max) max = maxs[i];
        }
    }

    /// <summary>每 64 格合併成一格（只保留完整的格，尾端不足 64 的部分由查詢時直接掃描）。</summary>
    private static (float[] Min, float[] Max) Reduce(float[] mins, float[] maxs)
    {
        var n = mins.Length / Fanout;
        var rMin = new float[n];
        var rMax = new float[n];
        for (var i = 0; i < n; i++)
        {
            var lo = float.PositiveInfinity;
            var hi = float.NegativeInfinity;
            for (var j = i * Fanout; j < (i + 1) * Fanout; j++)
            {
                if (mins[j] < lo) lo = mins[j];
                if (maxs[j] > hi) hi = maxs[j];
            }
            rMin[i] = lo;
            rMax[i] = hi;
        }
        return (rMin, rMax);
    }
}
