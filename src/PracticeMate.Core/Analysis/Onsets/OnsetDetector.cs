using System.Numerics;
using PracticeMate.Core.Dsp.Stft;

namespace PracticeMate.Core.Analysis.Onsets;

public sealed record OnsetDetectorOptions
{
    /// <summary>自適應門檻：區域平均之上再加 δ × 全段最大通量。</summary>
    public double Delta { get; init; } = 0.06;

    /// <summary>兩個起音的最小間隔。</summary>
    public double MinIntervalSeconds { get; init; } = 0.05;

    /// <summary>精修：包絡從前段底噪上升到「底噪 + 此比例 × 峰值差」的時刻視為起音。</summary>
    public double RiseFraction { get; init; } = 0.1;

    /// <summary>通量只計算到此頻率（管樂主要能量所在）。</summary>
    public double MaxFrequency { get; init; } = 10000;
}

/// <summary>
/// 起音偵測：SuperFlux 頻譜通量 + 自適應門檻 + 包絡回溯精修。
/// <list type="number">
/// <item>STFT：約 21 ms Hann 視窗（48 kHz → 1024），hop = round(fs/200)（5 ms），訊框以 n·hop 為中心。</item>
/// <item>對數壓縮 L = log(1 + 1000·|X|)；與 2 幀前（10 ms）比較，比較前先沿頻率做 3 bin 最大值濾波（抑制顫音造成的假起音）。
/// 通量 = Σ max(0, L_n − maxfilt(L_{n−2}))。</item>
/// <item>峰值挑選：±30 ms 內的局部最大，且高於 [−100 ms, +70 ms] 區域平均 + δ·全段最大；間隔至少 50 ms。</item>
/// <item>時間精修：通量峰值落在起音中段（30 ms 起音會偏晚 10–15 ms），因此在 1 ms 解析度的振幅包絡上，
/// 從峰值往回找「首次超過 底噪 + 10%×(峰值 − 底噪)」的位置（細節見 <c>Refine</c>）。</item>
/// </list>
/// 所有時間以取樣索引表示。
/// </summary>
public static class OnsetDetector
{
    private const int Lag = 2;

    /// <summary>包絡至少上升 3 dB（振幅比 √2）才視為可靠的起點。</summary>
    private const double MinRiseRatio = 1.4125375446227544;

    public static IReadOnlyList<long> Detect(float[] mono, int sampleRate, OnsetDetectorOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mono);
        var o = options ?? new OnsetDetectorOptions();
        var window = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Ceiling(0.02 * sampleRate));
        var hop = (int)Math.Round(sampleRate / 200.0);
        var frames = mono.Length == 0 ? 0 : ((mono.Length - 1) / hop) + 1;
        var flux = ComputeFlux(mono, sampleRate, window, hop, frames, o, cancellationToken);
        var peaks = PickPeaks(flux, sampleRate, hop, o);
        var envelope = Envelope(mono, sampleRate, out var block);
        var onsets = new List<long>(peaks.Count);
        for (var i = 0; i < peaks.Count; i++)
        {
            var previous = i > 0 ? peaks[i - 1] * (long)hop : -1;
            onsets.Add(Refine(envelope, block, peaks[i] * (long)hop, previous, sampleRate, o));
        }
        return onsets.Distinct().Order().ToList();
    }

    private static double[] ComputeFlux(float[] mono, int fs, int window, int hop, int frames, OnsetDetectorOptions o, CancellationToken ct)
    {
        var maxBin = Math.Min((window / 2) + 1, (int)(o.MaxFrequency * window / fs) + 1);
        var logMag = new double[frames][];
        const int chunk = 512;
        var chunks = (frames + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, () => new StftFrameAnalyzer(window), (c, _, stft) =>
        {
            var mag = new double[stft.BinCount];
            var end = Math.Min(frames, (c + 1) * chunk);
            for (var n = c * chunk; n < end; n++)
            {
                stft.Magnitudes(mono, (long)n * hop, mag);
                var row = new double[maxBin];
                for (var k = 0; k < maxBin; k++)
                {
                    row[k] = Math.Log(1 + (1000 * mag[k]));
                }
                logMag[n] = row;
            }
            return stft;
        }, _ => { });

        var flux = new double[frames];
        for (var n = Lag; n < frames; n++)
        {
            var cur = logMag[n];
            var prev = logMag[n - Lag];
            double sum = 0;
            for (var k = 0; k < maxBin; k++)
            {
                var p = prev[k];
                if (k > 0) p = Math.Max(p, prev[k - 1]);
                if (k + 1 < maxBin) p = Math.Max(p, prev[k + 1]);
                var d = cur[k] - p;
                if (d > 0) sum += d;
            }
            flux[n] = sum;
        }
        return flux;
    }

    private static List<int> PickPeaks(double[] flux, int fs, int hop, OnsetDetectorOptions o)
    {
        var framesPerSecond = (double)fs / hop;
        var localRadius = (int)Math.Round(0.03 * framesPerSecond);
        var meanBefore = (int)Math.Round(0.10 * framesPerSecond);
        var meanAfter = (int)Math.Round(0.07 * framesPerSecond);
        var minGap = (int)Math.Round(o.MinIntervalSeconds * framesPerSecond);
        var max = flux.Length == 0 ? 0 : flux.Max();
        var peaks = new List<int>();
        if (max <= 0)
        {
            return peaks;
        }

        // 區域平均以前綴和計算
        var prefix = new double[flux.Length + 1];
        for (var i = 0; i < flux.Length; i++)
        {
            prefix[i + 1] = prefix[i] + flux[i];
        }

        for (var n = 1; n < flux.Length; n++)
        {
            var v = flux[n];
            var lo = Math.Max(0, n - localRadius);
            var hi = Math.Min(flux.Length - 1, n + localRadius);
            var isMax = true;
            for (var j = lo; j <= hi && isMax; j++)
            {
                // 平台時取最早的一點
                if (flux[j] > v || (j < n && flux[j] == v))
                {
                    isMax = false;
                }
            }
            if (!isMax)
            {
                continue;
            }
            var a = Math.Max(0, n - meanBefore);
            var b = Math.Min(flux.Length, n + meanAfter + 1);
            var mean = (prefix[b] - prefix[a]) / (b - a);
            if (v <= mean + (o.Delta * max))
            {
                continue;
            }
            if (peaks.Count > 0 && n - peaks[^1] < minGap)
            {
                if (v > flux[peaks[^1]])
                {
                    peaks[^1] = n;
                }
                continue;
            }
            peaks.Add(n);
        }
        return peaks;
    }

    /// <summary>
    /// 每 1 ms 一格的 RMS 包絡（以該格為中心的 10 ms 視窗）。
    /// 不用逐格峰值：低音的週期長於 1 ms 時，許多格抓不到波峰，包絡起伏會讓門檻交會點偏晚數毫秒。
    /// 10 ms 視窗對 ≥ 100 Hz 的音平滑（更低的音仍有些許起伏，見已知限制）。
    /// </summary>
    private static float[] Envelope(float[] mono, int fs, out int block)
    {
        block = Math.Max(1, fs / 1000);
        var blocks = (mono.Length + block - 1) / block;
        var energy = new double[blocks + 1]; // 前綴和
        for (var i = 0; i < blocks; i++)
        {
            var end = Math.Min(mono.Length, (i + 1) * block);
            double e = 0;
            for (var j = i * block; j < end; j++)
            {
                e += mono[j] * (double)mono[j];
            }
            energy[i + 1] = energy[i] + e;
        }
        const int half = 5;
        var env = new float[blocks];
        for (var i = 0; i < blocks; i++)
        {
            var a = Math.Max(0, i - half);
            var b = Math.Min(blocks, i + half);
            var n = Math.Max(1, (b - a) * block);
            env[i] = (float)Math.Sqrt((energy[b] - energy[a]) / n);
        }
        return env;
    }
    /// <summary>
    /// 把通量峰值精修到起音起點。前一個音的餘音（撥弦、殘響、漸弱）可能和新音一樣大，且因拍頻而起伏，因此：
    /// <list type="bullet">
    /// <item>谷底：通量峰值前 20 ms（不越過上一個起音）到峰值後 40 ms 之間的最小值，視為新音開始前的底。</item>
    /// <item>包絡峰值：從通量峰值往後找第一個比谷底高 3 dB 以上的局部最大（不可落到前面的餘音上，
    /// 也不跳到後面拍頻造成的第二個峰）；找不到則取 40 ms 內最大值。</item>
    /// <item>門檻：底 + 10%×(峰 − 底)，但至少要高於谷底前 20 ms 內餘音的最大值（避免把餘音的起伏當起音），
    /// 最多不超過 50% 上升量。回溯不越過谷底。</item>
    /// <item>谷底到峰值上升不足 3 dB（連音、餘音完全掩蓋）時沒有可靠的包絡起點，保留通量峰值的時間。</item>
    /// </list>
    /// </summary>
    private static long Refine(float[] env, int block, long peakSample, long previousPeakSample, int fs, OnsetDetectorOptions o)
    {
        if (env.Length == 0)
        {
            return peakSample;
        }
        var ms = fs / 1000.0 / block; // 每毫秒的格數（≈ 1）
        var center = Math.Min(env.Length - 1, (int)(peakSample / block));
        var searchEnd = Math.Min(env.Length - 1, center + (int)(40 * ms));
        var lowerBound = Math.Max(0, center - (int)(20 * ms));
        if (previousPeakSample >= 0)
        {
            lowerBound = Math.Max(lowerBound, Math.Min(center, (int)(previousPeakSample / block)));
        }

        // 谷底：通量峰值前後最低處（在 40 ms 最大值之前）
        var maxIdx = center;
        for (var i = center; i <= searchEnd; i++)
        {
            if (env[i] > env[maxIdx])
            {
                maxIdx = i;
            }
        }
        var floorIdx = lowerBound;
        for (var i = lowerBound; i <= maxIdx; i++)
        {
            if (env[i] < env[floorIdx])
            {
                floorIdx = i;
            }
        }
        var floor = env[floorIdx];
        if (env[maxIdx] < MinRiseRatio * floor)
        {
            return peakSample;
        }

        // 第一個足夠高的局部最大
        var peakIdx = maxIdx;
        for (var i = Math.Max(center, floorIdx + 1); i < searchEnd; i++)
        {
            if (env[i] >= MinRiseRatio * floor && env[i] >= env[i - 1] && env[i] >= env[i + 1])
            {
                peakIdx = i;
                break;
            }
        }
        var peak = env[peakIdx];

        var residual = 0f;
        for (var i = Math.Max(0, floorIdx - (int)(20 * ms)); i < floorIdx; i++)
        {
            residual = Math.Max(residual, env[i]);
        }
        var level = floor + (o.RiseFraction * (peak - floor));
        level = Math.Min(Math.Max(level, residual), floor + (0.5f * (peak - floor)));

        for (var i = peakIdx; i > floorIdx; i--)
        {
            if (env[i - 1] <= level)
            {
                return (long)i * block;
            }
        }
        return (long)floorIdx * block;
    }
}
