using PracticeMate.Core.IO.Wav;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.IO;

public class WavReaderTests
{
    private static WavReadResult Read(byte[] bytes) => WavReader.Read(new MemoryStream(bytes));

    /// <summary>參考正規化：有號整數 / 2^(位元數−1)，以 double 計算後轉 float。</summary>
    private static float Norm(long v, int bits) => (float)(v / Math.Pow(2, bits - 1));

    private static void AssertSamples(WavReadResult r, IReadOnlyList<float> expectedInterleaved)
    {
        var ch = r.Audio.ChannelCount;
        Assert.Equal(expectedInterleaved.Count / ch, r.Audio.FrameCount);
        for (var i = 0; i < expectedInterleaved.Count; i++)
        {
            Assert.Equal(expectedInterleaved[i], r.Audio.Channel(i % ch)[i / ch]);
        }
    }

    [Fact]
    public void Reads_8bit_unsigned_with_offset()
    {
        long[] raw = [0, 64, 128, 192, 255];
        var r = Read(RefWavBuilder.Pcm(8000, 1, 8).WithIntSamples(raw).Build());
        AssertSamples(r, raw.Select(v => Norm(v - 128, 8)).ToArray());
        Assert.Equal(-1f, r.Audio.Channel(0)[0]);
        Assert.Equal(0f, r.Audio.Channel(0)[2]);
    }

    [Fact]
    public void Reads_16bit_stereo()
    {
        long[] raw = [short.MinValue, short.MaxValue, -1, 1, 0, 12345];
        var r = Read(RefWavBuilder.Pcm(44100, 2, 16).WithIntSamples(raw).Build());
        Assert.Equal(2, r.Audio.ChannelCount);
        AssertSamples(r, raw.Select(v => Norm(v, 16)).ToArray());
    }

    [Fact]
    public void Reads_24bit_with_sign_extension()
    {
        long[] raw = [-8388608, 8388607, -1, -2, 1, 0x123456, -0x123456];
        var r = Read(RefWavBuilder.Pcm(48000, 1, 24).WithIntSamples(raw).Build());
        AssertSamples(r, raw.Select(v => Norm(v, 24)).ToArray());
        Assert.True(r.Audio.Channel(0)[2] < 0, "0xFFFFFF 應為 −1（符號延伸）");
    }

    [Fact]
    public void Reads_32bit_integer()
    {
        long[] raw = [int.MinValue, int.MaxValue, -1, 7, 1 << 20];
        var r = Read(RefWavBuilder.Pcm(96000, 1, 32).WithIntSamples(raw).Build());
        AssertSamples(r, raw.Select(v => Norm(v, 32)).ToArray());
    }

    [Fact]
    public void Reads_32bit_float()
    {
        float[] raw = [0f, 0.5f, -1f, 1.5f, -0.000123f, float.Epsilon];
        var r = Read(RefWavBuilder.IeeeFloat(48000, 1, 32).WithFloat32Samples(raw).Build());
        AssertSamples(r, raw);
    }

    [Fact]
    public void Reads_64bit_float()
    {
        double[] raw = [0.0, 0.25, -0.75, 1.0 / 3.0];
        var r = Read(RefWavBuilder.IeeeFloat(48000, 2, 64).WithFloat64Samples(raw).Build());
        AssertSamples(r, raw.Select(v => (float)v).ToArray());
    }

    [Theory]
    [InlineData(24, 20)]
    [InlineData(32, 24)]
    [InlineData(16, 12)]
    [InlineData(24, 24)]
    public void Extensible_uses_valid_bits(int container, int valid)
    {
        // 有效位元靠左對齊，低位補零；此處刻意在低位放垃圾位元，必須被忽略
        var garbage = (1L << (container - valid)) - 1;
        long[] values = [-(1L << (valid - 1)), (1L << (valid - 1)) - 1, -1, 3];
        var raw = values.Select(v => (v << (container - valid)) | garbage).ToArray();
        var r = Read(RefWavBuilder.Extensible(48000, 1, container, valid, 0x4).WithIntSamples(raw).Build());
        AssertSamples(r, values.Select(v => Norm(v, valid)).ToArray());
        Assert.Equal(valid, r.Format.ValidBitsPerSample);
        Assert.True(r.Format.IsExtensible);
    }

    [Fact]
    public void Extensible_float_and_channel_mask()
    {
        float[] raw = [0.1f, -0.2f, 0.3f, -0.4f];
        var r = Read(RefWavBuilder.Extensible(48000, 2, 32, 32, 0x3, isFloat: true).WithFloat32Samples(raw).Build());
        AssertSamples(r, raw);
        Assert.Equal(0x3u, r.Format.ChannelMask);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(192000)]
    [InlineData(22050)]
    public void Accepts_sample_rate_range(int rate)
    {
        var r = Read(RefWavBuilder.Pcm(rate, 1, 16).WithIntSamples([1, 2]).Build());
        Assert.Equal(rate, r.Audio.SampleRate);
    }

    [Fact]
    public void Skips_unknown_chunks_including_odd_length_padding()
    {
        long[] raw = [100, -100, 200];
        var bytes = RefWavBuilder.Pcm(48000, 1, 16)
            .AddChunkBeforeFmt("JUNK", [1, 2, 3, 4, 5])
            .AddChunkBeforeData("LIST", [9, 9, 9])
            .AddChunkBeforeData("abc ", [7])
            .AddChunkAfterData("id3 ", [1, 2, 3])
            .WithIntSamples(raw)
            .Build();
        var r = Read(bytes);
        AssertSamples(r, raw.Select(v => Norm(v, 16)).ToArray());
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Odd_length_8bit_data_chunk_is_padded()
    {
        long[] raw = [1, 2, 3];
        var r = Read(RefWavBuilder.Pcm(8000, 1, 8).WithIntSamples(raw).AddChunkAfterData("LIST", [1, 2]).Build());
        Assert.Equal(3, r.Audio.FrameCount);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Wrong_riff_size_is_ignored()
    {
        var b = RefWavBuilder.Pcm(8000, 1, 16).WithIntSamples([5, 6]);
        b.DeclaredRiffSize = 12345678;
        Assert.Equal(2, Read(b.Build()).Audio.FrameCount);
    }

    public static TheoryData<string, Func<byte[]>, string> UnsupportedCases => new()
    {
        { "A-law", () => new RefWavBuilder { FormatTag = RefWavBuilder.FormatALaw, BitsPerSample = 8 }.WithIntSamples([1]).Build(), "A-law" },
        { "μ-law", () => new RefWavBuilder { FormatTag = RefWavBuilder.FormatMuLaw, BitsPerSample = 8 }.WithIntSamples([1]).Build(), "μ-law" },
        { "ADPCM", () => new RefWavBuilder { FormatTag = 2, BitsPerSample = 4, Data = [1, 2] }.Build(), "ADPCM" },
        { "3 聲道", () => RefWavBuilder.Pcm(48000, 3, 16).WithIntSamples([1, 2, 3]).Build(), "聲道" },
        { "6 聲道", () => RefWavBuilder.Pcm(48000, 6, 16).WithIntSamples([1, 2, 3, 4, 5, 6]).Build(), "聲道" },
        { "12 位元 PCM", () => new RefWavBuilder { BitsPerSample = 12, Data = [0, 0, 0] }.Build(), "位元" },
        { "16 位元浮點", () => RefWavBuilder.IeeeFloat(48000, 1, 16).WithIntSamples([1]).Build(), "位元" },
        { "4 kHz", () => RefWavBuilder.Pcm(4000, 1, 16).WithIntSamples([1]).Build(), "取樣率" },
        { "384 kHz", () => RefWavBuilder.Pcm(384000, 1, 16).WithIntSamples([1]).Build(), "取樣率" },
        { "有效位元數大於容器", () => RefWavBuilder.Extensible(48000, 1, 16, 20, 4).WithIntSamples([1]).Build(), "有效位元" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedCases))]
    public void Unsupported_formats_have_chinese_reason(string name, Func<byte[]> build, string keyword)
    {
        ArgumentNullException.ThrowIfNull(build);
        var ex = Assert.Throws<UnsupportedWavFormatException>(() => Read(build()));
        Assert.Contains(keyword, ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void Not_a_riff_file_is_rejected_in_chinese()
    {
        var ex = Assert.Throws<UnsupportedWavFormatException>(() => Read("OggS0000000000000000000000"u8.ToArray()));
        Assert.Contains("不是 WAV", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_fmt_chunk_is_rejected()
    {
        var bytes = "RIFF\x0c\0\0\0WAVEdata\0\0\0\0"u8.ToArray();
        var ex = Assert.Throws<UnsupportedWavFormatException>(() => Read(bytes));
        Assert.Contains("fmt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_data_chunk_is_rejected()
    {
        var full = RefWavBuilder.Pcm(8000, 1, 16).WithIntSamples([1]).Build();
        var bytes = full.AsSpan(0, 36).ToArray(); // 只有 RIFF 與 fmt
        var ex = Assert.Throws<UnsupportedWavFormatException>(() => Read(bytes));
        Assert.Contains("data", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extensible_with_unknown_subformat_is_rejected()
    {
        var bytes = RefWavBuilder.Extensible(48000, 1, 16, 16, 4).WithIntSamples([1]).Build();
        bytes[44 + 0] = 0x55; // 竄改子格式 GUID 的第一個位元組（fmt 起點 20 + 24）
        var ex = Assert.Throws<UnsupportedWavFormatException>(() => Read(bytes));
        Assert.Contains("子格式", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_description_is_chinese()
    {
        var r = Read(RefWavBuilder.Pcm(44100, 2, 24).WithIntSamples([1, 2]).Build());
        Assert.Equal("PCM 24 位元整數，立體聲，44100 Hz", r.Format.Description);
    }
}
