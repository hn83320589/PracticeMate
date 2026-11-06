using System.Buffers.Binary;
using System.Text;

namespace PracticeMate.Testing.Reference;

/// <summary>
/// 手刻 WAV 位元組的建構器，用來產生各種格式（含刻意損毀）的測試檔。
/// 取樣以「容器中的原始整數值」或 IEEE 浮點提供，與正式程式碼的讀取器互相獨立。
/// </summary>
public sealed class RefWavBuilder
{
    public const ushort FormatPcm = 1;
    public const ushort FormatIeeeFloat = 3;
    public const ushort FormatALaw = 6;
    public const ushort FormatMuLaw = 7;
    public const ushort FormatExtensible = 0xFFFE;

    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubTypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly List<(string Id, byte[] Payload)> _chunksBeforeFmt = [];
    private readonly List<(string Id, byte[] Payload)> _chunksBeforeData = [];
    private readonly List<(string Id, byte[] Payload)> _chunksAfterData = [];

    public ushort FormatTag { get; set; } = FormatPcm;
    public ushort Channels { get; set; } = 1;
    public int SampleRate { get; set; } = 48000;
    public ushort BitsPerSample { get; set; } = 16;

    /// <summary>EXTENSIBLE 用：子格式是否為浮點。</summary>
    public bool ExtensibleIsFloat { get; set; }
    public ushort ValidBitsPerSample { get; set; }
    public uint ChannelMask { get; set; }

    /// <summary>覆寫 data 區塊宣告的長度（模擬損毀）。null 表示正確長度。</summary>
    public uint? DeclaredDataSize { get; set; }

    /// <summary>覆寫 RIFF 宣告的長度。null 表示正確長度。</summary>
    public uint? DeclaredRiffSize { get; set; }

    /// <summary>data 區塊的原始位元組。</summary>
    public byte[] Data { get; set; } = [];

    public RefWavBuilder AddChunkBeforeFmt(string id, byte[] payload) { _chunksBeforeFmt.Add((id, payload)); return this; }
    public RefWavBuilder AddChunkBeforeData(string id, byte[] payload) { _chunksBeforeData.Add((id, payload)); return this; }
    public RefWavBuilder AddChunkAfterData(string id, byte[] payload) { _chunksAfterData.Add((id, payload)); return this; }

    public static RefWavBuilder Pcm(int sampleRate, int channels, int bits) => new()
    {
        FormatTag = FormatPcm, SampleRate = sampleRate, Channels = (ushort)channels, BitsPerSample = (ushort)bits,
    };

    public static RefWavBuilder IeeeFloat(int sampleRate, int channels, int bits) => new()
    {
        FormatTag = FormatIeeeFloat, SampleRate = sampleRate, Channels = (ushort)channels, BitsPerSample = (ushort)bits,
    };

    public static RefWavBuilder Extensible(int sampleRate, int channels, int containerBits, int validBits, uint channelMask, bool isFloat = false) => new()
    {
        FormatTag = FormatExtensible, SampleRate = sampleRate, Channels = (ushort)channels, BitsPerSample = (ushort)containerBits,
        ValidBitsPerSample = (ushort)validBits, ChannelMask = channelMask, ExtensibleIsFloat = isFloat,
    };

    /// <summary>以容器整數值（已含符號；8 位元請給 0–255 的無號值）編碼交錯取樣。</summary>
    public RefWavBuilder WithIntSamples(IReadOnlyList<long> interleaved)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        var bytesPer = BitsPerSample / 8;
        var data = new byte[interleaved.Count * bytesPer];
        for (var i = 0; i < interleaved.Count; i++)
        {
            var v = interleaved[i];
            for (var b = 0; b < bytesPer; b++)
            {
                data[(i * bytesPer) + b] = (byte)((v >> (8 * b)) & 0xFF);
            }
        }
        Data = data;
        return this;
    }

    public RefWavBuilder WithFloat32Samples(IReadOnlyList<float> interleaved)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        var data = new byte[interleaved.Count * 4];
        for (var i = 0; i < interleaved.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), interleaved[i]);
        }
        Data = data;
        return this;
    }

    public RefWavBuilder WithFloat64Samples(IReadOnlyList<double> interleaved)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        var data = new byte[interleaved.Count * 8];
        for (var i = 0; i < interleaved.Count; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(i * 8), interleaved[i]);
        }
        Data = data;
        return this;
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0u); // 稍後回填
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        foreach (var (id, payload) in _chunksBeforeFmt)
        {
            WriteChunk(w, id, payload);
        }

        WriteChunk(w, "fmt ", BuildFmt());

        foreach (var (id, payload) in _chunksBeforeData)
        {
            WriteChunk(w, id, payload);
        }

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(DeclaredDataSize ?? (uint)Data.Length);
        w.Write(Data);
        if (Data.Length % 2 == 1)
        {
            w.Write((byte)0);
        }

        foreach (var (id, payload) in _chunksAfterData)
        {
            WriteChunk(w, id, payload);
        }

        w.Flush();
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), DeclaredRiffSize ?? (uint)(bytes.Length - 8));
        return bytes;
    }

    private byte[] BuildFmt()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var blockAlign = (ushort)(Channels * (BitsPerSample / 8));
        w.Write(FormatTag);
        w.Write(Channels);
        w.Write(SampleRate);
        w.Write(SampleRate * blockAlign);
        w.Write(blockAlign);
        w.Write(BitsPerSample);
        if (FormatTag == FormatExtensible)
        {
            w.Write((ushort)22);
            w.Write(ValidBitsPerSample);
            w.Write(ChannelMask);
            w.Write((ExtensibleIsFloat ? SubTypeFloat : SubTypePcm).ToByteArray());
        }
        else if (FormatTag != FormatPcm)
        {
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>寫入區塊；奇數長度時補一個位元組（RIFF 規定）。</summary>
    private static void WriteChunk(BinaryWriter w, string id, byte[] payload)
    {
        w.Write(Encoding.ASCII.GetBytes(id));
        w.Write((uint)payload.Length);
        w.Write(payload);
        if (payload.Length % 2 == 1)
        {
            w.Write((byte)0);
        }
    }
}
