using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PracticeMate.Core.Audio;

namespace PracticeMate.Core.IO.Wav;

/// <summary>
/// WAV 讀取器。支援 PCM 8/16/24/32 位元整數、IEEE 32/64 位元浮點、WAVE_FORMAT_EXTENSIBLE
/// （含有效位元數與聲道遮罩）；單聲道與立體聲；8–192 kHz。略過未知區塊（含奇數長度補位）。
/// <para>
/// 檔頭損毀回復：data 長度為 0 或超過檔案剩餘長度時，改以實際檔案長度讀取；
/// 檔案截在取樣（框）中間時捨棄最後不完整的框。上述情況都會在 <see cref="WavReadResult.Warnings"/> 提示。
/// </para>
/// </summary>
public static class WavReader
{
    public const int MinSampleRate = 8000;
    public const int MaxSampleRate = 192000;

    private const ushort TagPcm = 1;
    private const ushort TagAdpcm = 2;
    private const ushort TagFloat = 3;
    private const ushort TagALaw = 6;
    private const ushort TagMuLaw = 7;
    private const ushort TagImaAdpcm = 0x11;
    private const ushort TagMp3 = 0x55;
    private const ushort TagExtensible = 0xFFFE;

    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubTypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static WavReadResult Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return Read(fs);
    }

    public static WavReadResult Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("WAV 讀取需要可搜尋的串流。", nameof(stream));
        }

        var header = new byte[12];
        if (ReadFully(stream, header) < 12
            || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            if (header.AsSpan(0, 4).SequenceEqual("RF64"u8))
            {
                throw new UnsupportedWavFormatException("不支援 RF64 格式（超過 4 GB 的 WAV 檔）。");
            }
            throw new UnsupportedWavFormatException("這不是 WAV 檔：檔案開頭缺少 RIFF/WAVE 標記。");
        }

        WavFormatInfo? format = null;
        var chunkHeader = new byte[8];
        while (true)
        {
            if (ReadFully(stream, chunkHeader) < 8)
            {
                throw new UnsupportedWavFormatException(format is null
                    ? "檔案中找不到 fmt 區塊（格式描述），無法判斷音訊格式。"
                    : "檔案中找不到 data 區塊（音訊資料）。");
            }
            var id = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));

            if (id == "fmt ")
            {
                var fmt = new byte[Math.Min(size, 1024)];
                if (ReadFully(stream, fmt) < Math.Min(size, 16))
                {
                    throw new UnsupportedWavFormatException("fmt 區塊不完整，檔案可能已損毀。");
                }
                format = ParseFormat(fmt);
                SkipTo(stream, stream.Position + (size - fmt.Length) + (size % 2));
            }
            else if (id == "data")
            {
                if (format is null)
                {
                    throw new UnsupportedWavFormatException("檔案中找不到 fmt 區塊（格式描述），無法判斷音訊格式。");
                }
                return ReadData(stream, format, size);
            }
            else
            {
                SkipTo(stream, stream.Position + size + (size % 2));
            }
        }
    }

    private static WavReadResult ReadData(Stream stream, WavFormatInfo format, uint declaredSize)
    {
        var warnings = new List<string>();
        var recovered = false;
        var remaining = stream.Length - stream.Position;
        long dataBytes = declaredSize;

        if (declaredSize == 0)
        {
            dataBytes = remaining;
            recovered = true;
            warnings.Add(Invariant($"檔頭記載的資料長度為 0（常見於錄音途中當機），已改用實際檔案長度讀取（{remaining} 位元組）。"));
        }
        else if (declaredSize > remaining)
        {
            dataBytes = remaining;
            recovered = true;
            warnings.Add(Invariant($"檔頭記載的資料長度（{declaredSize} 位元組）大於實際檔案剩餘長度（{remaining} 位元組），可能是錄音途中當機，已依實際長度讀取。"));
        }

        var blockAlign = format.BlockAlign;
        var partial = dataBytes % blockAlign;
        if (partial != 0)
        {
            dataBytes -= partial;
            recovered = true;
            warnings.Add(Invariant($"檔案在取樣中間被截斷，已捨棄最後不完整的 {partial} 位元組。"));
        }

        var frames = dataBytes / blockAlign;
        if (frames > Array.MaxLength)
        {
            throw new UnsupportedWavFormatException("檔案太長，超過單一分析檔的上限。");
        }

        var channels = new float[format.Channels][];
        for (var c = 0; c < format.Channels; c++)
        {
            channels[c] = new float[frames];
        }

        var sampleFormat = new PcmSampleFormat(format.Encoding, format.ValidBitsPerSample);
        const int framesPerBlock = 16384;
        var byteBuffer = ArrayPool<byte>.Shared.Rent(framesPerBlock * blockAlign);
        var floatBuffer = ArrayPool<float>.Shared.Rent(framesPerBlock * format.Channels);
        try
        {
            long frame = 0;
            while (frame < frames)
            {
                var n = (int)Math.Min(framesPerBlock, frames - frame);
                var bytes = byteBuffer.AsSpan(0, n * blockAlign);
                if (ReadFully(stream, bytes) < bytes.Length)
                {
                    throw new IOException("讀取 WAV 資料時檔案意外結束。");
                }
                var samples = floatBuffer.AsSpan(0, n * format.Channels);
                PcmConverter.ToFloat(bytes, sampleFormat, samples);
                Deinterleave(samples, channels, frame);
                frame += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
            ArrayPool<float>.Shared.Return(floatBuffer);
        }

        return new WavReadResult(new AudioData(format.SampleRate, channels), format, warnings, recovered);
    }

    private static void Deinterleave(ReadOnlySpan<float> samples, float[][] channels, long frameOffset)
    {
        var ch = channels.Length;
        if (ch == 1)
        {
            samples.CopyTo(channels[0].AsSpan((int)frameOffset));
            return;
        }
        var frames = samples.Length / ch;
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < ch; c++)
            {
                channels[c][frameOffset + i] = samples[(i * ch) + c];
            }
        }
    }

    private static WavFormatInfo ParseFormat(ReadOnlySpan<byte> fmt)
    {
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        var validBits = bits;
        uint mask = 0;
        var isExtensible = tag == TagExtensible;
        var isFloat = tag == TagFloat;

        if (isExtensible)
        {
            if (fmt.Length < 40)
            {
                throw new UnsupportedWavFormatException("WAVE_FORMAT_EXTENSIBLE 的 fmt 區塊長度不足，檔案可能已損毀。");
            }
            validBits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]);
            mask = BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]);
            var subType = new Guid(fmt.Slice(24, 16));
            if (subType == SubTypeFloat)
            {
                isFloat = true;
            }
            else if (subType != SubTypePcm)
            {
                throw new UnsupportedWavFormatException($"不支援的 EXTENSIBLE 子格式（{subType}），僅支援 PCM 整數與 IEEE 浮點。");
            }
            if (validBits == 0)
            {
                validBits = bits;
            }
        }
        else if (tag != TagPcm && tag != TagFloat)
        {
            throw new UnsupportedWavFormatException($"不支援的編碼格式：{TagName(tag)}（格式代碼 {tag}）。僅支援 PCM 整數與 IEEE 浮點。");
        }

        if (channels is < 1 or > 2)
        {
            throw new UnsupportedWavFormatException($"不支援 {channels} 聲道的檔案，僅支援單聲道與立體聲。");
        }
        if (rate is < MinSampleRate or > MaxSampleRate)
        {
            throw new UnsupportedWavFormatException(Invariant($"取樣率 {rate} Hz 超出支援範圍（{MinSampleRate}–{MaxSampleRate} Hz）。"));
        }

        WavSampleEncoding encoding;
        if (isFloat)
        {
            encoding = bits switch
            {
                32 => WavSampleEncoding.Float32,
                64 => WavSampleEncoding.Float64,
                _ => throw new UnsupportedWavFormatException($"不支援 {bits} 位元的浮點格式，僅支援 32 與 64 位元。"),
            };
            validBits = bits;
        }
        else
        {
            encoding = bits switch
            {
                8 => WavSampleEncoding.UInt8,
                16 => WavSampleEncoding.Int16,
                24 => WavSampleEncoding.Int24,
                32 => WavSampleEncoding.Int32,
                _ => throw new UnsupportedWavFormatException($"不支援 {bits} 位元的 PCM 格式，僅支援 8、16、24、32 位元。"),
            };
            if (validBits > bits || validBits < 1)
            {
                throw new UnsupportedWavFormatException($"有效位元數 {validBits} 不合理（容器為 {bits} 位元）。");
            }
        }

        var info = new WavFormatInfo(tag, isExtensible, channels, rate, bits, validBits, mask, encoding);
        if (blockAlign != info.BlockAlign)
        {
            throw new UnsupportedWavFormatException($"區塊對齊值 {blockAlign} 與格式不符（應為 {info.BlockAlign}），檔案可能已損毀。");
        }
        return info;
    }

    private static string TagName(ushort tag) => tag switch
    {
        TagAdpcm => "Microsoft ADPCM",
        TagImaAdpcm => "IMA ADPCM",
        TagALaw => "A-law",
        TagMuLaw => "μ-law",
        TagMp3 => "MP3",
        _ => "未知格式",
    };

    private static void SkipTo(Stream stream, long position) => stream.Position = Math.Min(position, stream.Length);

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
