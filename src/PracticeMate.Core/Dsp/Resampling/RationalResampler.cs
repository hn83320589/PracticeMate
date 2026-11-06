using PracticeMate.Core.Dsp.Windows;

namespace PracticeMate.Core.Dsp.Resampling;

/// <summary>
/// 有理數比例 L/M 的取樣率轉換（多相 windowed-sinc，Kaiser 視窗）。
/// <list type="bullet">
/// <item>L/M = 輸出率/輸入率，以最大公因數約分（44.1k→48k = 160/147）。</item>
/// <item>原型低通設計於上取樣率 L·fin：通帶到 0.43·min(fin, fout)，阻帶自 0.5·min(fin, fout) 起，阻帶衰減 90 dB。</item>
/// <item>濾波器以中心對稱設計（零相位）：輸出第 m 點精確對應輸入時間 m·M/L，不引入延遲。</item>
/// <item>邊界之外視為 0。</item>
/// </list>
/// 計算以整數取樣位置進行，無浮點時間累積。
/// </summary>
public sealed class RationalResampler
{
    public const double PassbandEdge = 0.43;
    public const double StopbandEdge = 0.50;
    public const double StopbandAttenuationDb = 90.0;

    private readonly float[] _table;   // h[t + T]，t ∈ [−T, T]（上取樣域的整數位置）
    private readonly long _halfWidth;  // T

    public RationalResampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        InputRate = inputRate;
        OutputRate = outputRate;
        var g = Gcd(inputRate, outputRate);
        UpFactor = outputRate / g;
        DownFactor = inputRate / g;

        if (IsIdentity)
        {
            _table = [];
            return;
        }

        double l = UpFactor;
        double upRate = (double)inputRate * UpFactor;
        double minRate = Math.Min(inputRate, outputRate);
        var cutoff = (PassbandEdge + StopbandEdge) / 2.0 * minRate;            // Hz
        var transition = 2.0 * Math.PI * (StopbandEdge - PassbandEdge) * minRate / upRate; // rad/sample（上取樣域）
        var length = ((StopbandAttenuationDb - 7.95) / (2.285 * transition)) + 1;
        var halfTapsPerPhase = (long)Math.Ceiling(length / (2.0 * l));
        _halfWidth = halfTapsPerPhase * UpFactor;
        var beta = WindowFunction.KaiserBeta(StopbandAttenuationDb);

        var fc = cutoff / upRate; // 正規化截止（cycles/sample）
        _table = new float[(2 * _halfWidth) + 1];
        for (var t = -_halfWidth; t <= _halfWidth; t++)
        {
            var sinc = t == 0 ? 1.0 : Math.Sin(2.0 * Math.PI * fc * t) / (2.0 * Math.PI * fc * t);
            var h = 2.0 * fc * sinc * WindowFunction.Kaiser(t, _halfWidth, beta) * l;
            _table[t + _halfWidth] = (float)h;
        }
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    public int UpFactor { get; }
    public int DownFactor { get; }
    public bool IsIdentity => UpFactor == DownFactor;

    /// <summary>每個輸出取樣使用的輸入取樣數（約）。</summary>
    public int TapsPerOutput => IsIdentity ? 1 : (int)(2 * _halfWidth / UpFactor) + 1;

    /// <summary>輸出長度 = ceil(輸入長度·L/M)。</summary>
    public long OutputLength(long inputLength) => ((inputLength * UpFactor) + DownFactor - 1) / DownFactor;

    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new float[OutputLength(input.Length)];
        Process(input, 0, output);
        return output;
    }

    /// <summary>計算輸出索引 [firstOutput, firstOutput + destination.Length) 的取樣（可分段/平行呼叫）。</summary>
    public void Process(ReadOnlySpan<float> input, long firstOutput, Span<float> destination)
    {
        if (IsIdentity)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                var j = firstOutput + i;
                destination[i] = j < input.Length ? input[(int)j] : 0f;
            }
            return;
        }

        var l = UpFactor;
        var table = _table.AsSpan();
        for (var i = 0; i < destination.Length; i++)
        {
            var q = (firstOutput + i) * DownFactor;        // 上取樣域位置
            var jStart = CeilDiv(q - _halfWidth, l);
            var jEnd = FloorDiv(q + _halfWidth, l);
            jStart = Math.Max(jStart, 0);
            jEnd = Math.Min(jEnd, input.Length - 1);
            double acc = 0;
            var idx = q - (jStart * l) + _halfWidth;
            for (var j = jStart; j <= jEnd; j++, idx -= l)
            {
                acc += input[(int)j] * table[(int)idx];
            }
            destination[i] = (float)acc;
        }
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }
}
