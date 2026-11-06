using System.Buffers.Binary;

namespace PracticeMate.Core.Audio;

/// <summary>
/// 把裝置共用模式格式（WAVEFORMATEX / EXTENSIBLE 的欄位）對應到 <see cref="PcmSampleFormat"/>，
/// 並提供 float → 裝置格式的轉換（節拍器輸出用）。Audio.Windows 只負責把 NAudio 的欄位傳進來。
/// </summary>
public static class DeviceFormatMapper
{
    public const ushort TagPcm = 1;
    public const ushort TagFloat = 3;
    public const ushort TagExtensible = 0xFFFE;

    public static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubTypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>不支援時拋出 <see cref="NotSupportedException"/>（中文原因）。</summary>
    public static PcmSampleFormat Map(ushort formatTag, int bitsPerSample, int validBitsPerSample, Guid? subFormat)
    {
        var isFloat = formatTag == TagFloat;
        if (formatTag == TagExtensible)
        {
            if (subFormat == SubTypeFloat)
            {
                isFloat = true;
            }
            else if (subFormat != SubTypePcm)
            {
                throw new NotSupportedException($"裝置使用不支援的子格式（{subFormat}）。");
            }
        }
        else if (formatTag != TagPcm && formatTag != TagFloat)
        {
            throw new NotSupportedException($"裝置使用不支援的格式代碼 {formatTag}。");
        }

        if (isFloat)
        {
            return bitsPerSample switch
            {
                32 => PcmSampleFormat.Of(WavSampleEncoding.Float32),
                64 => PcmSampleFormat.Of(WavSampleEncoding.Float64),
                _ => throw new NotSupportedException($"裝置使用不支援的 {bitsPerSample} 位元浮點格式。"),
            };
        }
        var encoding = bitsPerSample switch
        {
            8 => WavSampleEncoding.UInt8,
            16 => WavSampleEncoding.Int16,
            24 => WavSampleEncoding.Int24,
            32 => WavSampleEncoding.Int32,
            _ => throw new NotSupportedException($"裝置使用不支援的 {bitsPerSample} 位元整數格式。"),
        };
        var valid = validBitsPerSample is > 0 and <= 32 && validBitsPerSample <= bitsPerSample ? validBitsPerSample : bitsPerSample;
        return new PcmSampleFormat(encoding, valid);
    }

    /// <summary>float（±1）→ 裝置位元組。整數格式會限幅並四捨五入；有效位元數以外的低位補 0。</summary>
    public static void FromFloat(ReadOnlySpan<float> source, PcmSampleFormat format, Span<byte> destination)
    {
        var bytes = format.ContainerBytes;
        if (destination.Length < source.Length * bytes)
        {
            throw new ArgumentException("目的緩衝區太小。", nameof(destination));
        }
        var shift = (bytes * 8) - format.ValidBits;
        var scale = Math.Pow(2, format.ValidBits - 1);
        for (var i = 0; i < source.Length; i++)
        {
            var dst = destination.Slice(i * bytes, bytes);
            double v = source[i];
            switch (format.Encoding)
            {
                case WavSampleEncoding.Float32:
                    BinaryPrimitives.WriteSingleLittleEndian(dst, (float)v);
                    break;
                case WavSampleEncoding.Float64:
                    BinaryPrimitives.WriteDoubleLittleEndian(dst, v);
                    break;
                default:
                    var q = (long)Math.Round(Math.Clamp(v * scale, -scale, scale - 1)) << shift;
                    if (format.Encoding == WavSampleEncoding.UInt8)
                    {
                        dst[0] = (byte)(q + 128);
                    }
                    else
                    {
                        for (var b = 0; b < bytes; b++)
                        {
                            dst[b] = (byte)((q >> (8 * b)) & 0xFF);
                        }
                    }
                    break;
            }
        }
    }
}
