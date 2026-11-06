using System.Buffers.Binary;

namespace PracticeMate.Core.Audio;

/// <summary>取樣在位元組中的編碼方式（皆為 little-endian）。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "成員名稱即為 WAV 規格中的取樣編碼名稱，改名反而失去精確性。")]
public enum WavSampleEncoding
{
    /// <summary>8 位元無號整數（128 為零點）。</summary>
    UInt8,
    Int16,
    Int24,
    Int32,
    Float32,
    Float64,
}

/// <summary>取樣格式：編碼與有效位元數（整數格式才有意義，有效位元靠左對齊於容器）。</summary>
public readonly record struct PcmSampleFormat(WavSampleEncoding Encoding, int ValidBits)
{
    public int ContainerBytes => Encoding switch
    {
        WavSampleEncoding.UInt8 => 1,
        WavSampleEncoding.Int16 => 2,
        WavSampleEncoding.Int24 => 3,
        WavSampleEncoding.Int32 or WavSampleEncoding.Float32 => 4,
        WavSampleEncoding.Float64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(Encoding), Encoding, "未知的取樣編碼"),
    };

    public static PcmSampleFormat Of(WavSampleEncoding encoding) =>
        new(encoding, new PcmSampleFormat(encoding, 0).ContainerBytes * 8);
}

/// <summary>
/// 原始位元組 → float 取樣（WAV 讀取與 WASAPI 擷取共用）。
/// 整數：先右移捨棄無效的低位元，再除以 2^(有效位元−1)，以 double 計算後轉 float；
/// 8 位元為無號，先減 128。
/// </summary>
public static class PcmConverter
{
    public static void ToFloat(ReadOnlySpan<byte> source, PcmSampleFormat format, Span<float> destination)
    {
        var bytes = format.ContainerBytes;
        var count = source.Length / bytes;
        if (destination.Length < count)
        {
            throw new ArgumentException($"目的緩衝區太小：需要 {count}，實際 {destination.Length}。", nameof(destination));
        }

        var containerBits = bytes * 8;
        var shift = containerBits - format.ValidBits;
        var scale = 1.0 / Math.Pow(2, format.ValidBits - 1);

        switch (format.Encoding)
        {
            case WavSampleEncoding.UInt8:
                for (var i = 0; i < count; i++)
                {
                    var v = (source[i] - 128) >> shift;
                    destination[i] = (float)(v * scale);
                }
                break;
            case WavSampleEncoding.Int16:
                for (var i = 0; i < count; i++)
                {
                    int v = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]);
                    destination[i] = (float)((v >> shift) * scale);
                }
                break;
            case WavSampleEncoding.Int24:
                for (var i = 0; i < count; i++)
                {
                    var o = i * 3;
                    // 放到 int 的高 24 位元後算術右移，完成符號延伸
                    var v = (source[o] << 8) | (source[o + 1] << 16) | (source[o + 2] << 24);
                    v >>= 8;
                    destination[i] = (float)((v >> shift) * scale);
                }
                break;
            case WavSampleEncoding.Int32:
                for (var i = 0; i < count; i++)
                {
                    var v = BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]);
                    destination[i] = (float)((v >> shift) * scale);
                }
                break;
            case WavSampleEncoding.Float32:
                for (var i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source[(i * 4)..]);
                }
                break;
            case WavSampleEncoding.Float64:
                for (var i = 0; i < count; i++)
                {
                    destination[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(source[(i * 8)..]);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format.Encoding, "未知的取樣編碼");
        }
    }
}
