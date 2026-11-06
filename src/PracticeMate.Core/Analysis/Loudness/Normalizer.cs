using System.Globalization;
using PracticeMate.Core.Audio;
using PracticeMate.Core.IO.Wav;

namespace PracticeMate.Core.Analysis.Loudness;

public sealed record NormalizationRequest(double TargetLufs, double TruePeakCeilingDbtp);

/// <param name="LimitedByTruePeak">為避免真峰值超過上限而改以峰值為準。</param>
/// <param name="ExpectedLufs">套用增益後的預期整合響度。</param>
/// <param name="Message">給使用者看的中文說明。</param>
public sealed record NormalizationPlan(
    bool CanNormalize,
    double GainDb,
    double ExpectedLufs,
    double ExpectedTruePeakDbtp,
    bool LimitedByTruePeak,
    string Message);

/// <summary>
/// 響度正規化：增益 = 目標 − 整合響度；若套用後真峰值會超過上限，改以「上限 − 真峰值」為增益並說明。
/// 只乘上固定增益（不壓縮、不限幅），因此響度與真峰值都精確平移同樣的 dB 數。
/// 輸出 32 位元浮點 WAV（原取樣率、原聲道），先寫暫存檔，完成後才更名，取消或失敗不會留下半套檔案。
/// </summary>
public static class Normalizer
{
    public static NormalizationPlan Plan(double integratedLufs, double truePeakDbtp, NormalizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (double.IsNegativeInfinity(integratedLufs) || double.IsNaN(integratedLufs))
        {
            return new NormalizationPlan(false, 0, integratedLufs, truePeakDbtp, false, "錄音全為靜音（整合響度無法定義），無法正規化。");
        }

        var gain = request.TargetLufs - integratedLufs;
        var limited = false;
        if (truePeakDbtp + gain > request.TruePeakCeilingDbtp)
        {
            gain = request.TruePeakCeilingDbtp - truePeakDbtp;
            limited = true;
        }
        var lufs = integratedLufs + gain;
        var tp = truePeakDbtp + gain;
        var message = limited
            ? Invariant($"若調到 {request.TargetLufs:F1} LUFS，真峰值會超過上限 {request.TruePeakCeilingDbtp:F1} dBTP；已改以峰值為準，增益 {gain:+0.0;-0.0} dB，結果響度 {lufs:F1} LUFS、真峰值 {tp:F1} dBTP。")
            : Invariant($"增益 {gain:+0.0;-0.0} dB，結果響度 {lufs:F1} LUFS、真峰值 {tp:F1} dBTP。");
        return new NormalizationPlan(true, gain, lufs, tp, limited, message);
    }

    /// <summary>把增益套用到原始音訊並寫出新檔。</summary>
    public static void Write(AudioData original, NormalizationPlan plan, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanNormalize)
        {
            throw new InvalidOperationException(plan.Message);
        }
        var temp = outputPath + ".tmp";
        try
        {
            var gain = (float)Math.Pow(10, plan.GainDb / 20);
            var channels = original.ChannelCount;
            const int block = 65536;
            var buffer = new float[block * channels];
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new StreamingWavWriter(stream, original.SampleRate, channels, original.SampleRate * 10))
            {
                for (long start = 0; start < original.FrameCount; start += block)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(block, original.FrameCount - start);
                    for (var i = 0; i < n; i++)
                    {
                        for (var c = 0; c < channels; c++)
                        {
                            buffer[(i * channels) + c] = original.Channel(c)[start + i] * gain;
                        }
                    }
                    writer.Write(buffer.AsSpan(0, n * channels));
                    progress?.Report((double)(start + n) / Math.Max(1, original.FrameCount));
                }
                writer.Complete();
            }
            File.Move(temp, outputPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
            throw;
        }
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
