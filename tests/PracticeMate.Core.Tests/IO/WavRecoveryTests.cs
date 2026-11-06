using System.Buffers.Binary;
using PracticeMate.Core.IO.Wav;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.IO;

/// <summary>驗收 #2：標頭損毀回復。</summary>
public class WavRecoveryTests
{
    private static readonly long[] Raw16 = [10, -20, 30, -40, 50, -60, 70, -80];

    private static WavReadResult Read(byte[] bytes) => WavReader.Read(new MemoryStream(bytes));

    private static void AssertFirstSamples(WavReadResult r, IReadOnlyList<long> raw, int bits, int channels)
    {
        for (var i = 0; i < r.Audio.FrameCount * channels; i++)
        {
            Assert.Equal((float)(raw[i] / Math.Pow(2, bits - 1)), r.Audio.Channel(i % channels)[i / channels]);
        }
    }

    [Fact]
    public void Data_size_zero_reads_to_end_of_file_with_warning()
    {
        var b = RefWavBuilder.Pcm(48000, 1, 16).WithIntSamples(Raw16);
        b.DeclaredDataSize = 0;
        var r = Read(b.Build());
        Assert.Equal(Raw16.Length, r.Audio.FrameCount);
        AssertFirstSamples(r, Raw16, 16, 1);
        Assert.Contains(r.Warnings, w => w.Contains("為 0", StringComparison.Ordinal));
        Assert.True(r.HeaderRecovered);
    }

    [Theory]
    [InlineData(1000u)]
    [InlineData(0xFFFFFFFFu)]
    public void Data_size_larger_than_file_reads_actual_length(uint declared)
    {
        var b = RefWavBuilder.Pcm(48000, 2, 16).WithIntSamples(Raw16);
        b.DeclaredDataSize = declared;
        var r = Read(b.Build());
        Assert.Equal(Raw16.Length / 2, r.Audio.FrameCount);
        AssertFirstSamples(r, Raw16, 16, 2);
        Assert.Contains(r.Warnings, w => w.Contains("大於", StringComparison.Ordinal));
    }

    [Fact]
    public void File_truncated_mid_sample_keeps_complete_frames()
    {
        long[] raw = [1000, -1000, 2000, -2000, 3000];
        var full = RefWavBuilder.Pcm(48000, 1, 24).WithIntSamples(raw).Build();
        var truncated = full.AsSpan(0, full.Length - 2).ToArray(); // 最後一個 24 位元取樣只剩 1 位元組
        var r = Read(truncated);
        Assert.Equal(4, r.Audio.FrameCount);
        AssertFirstSamples(r, raw, 24, 1);
        Assert.Contains(r.Warnings, w => w.Contains("截斷", StringComparison.Ordinal));
    }

    [Fact]
    public void Stereo_file_truncated_mid_frame_keeps_complete_frames()
    {
        var full = RefWavBuilder.Pcm(48000, 2, 16).WithIntSamples(Raw16).Build();
        var truncated = full.AsSpan(0, full.Length - 2).ToArray(); // 最後一框只剩左聲道
        var r = Read(truncated);
        Assert.Equal(3, r.Audio.FrameCount);
        AssertFirstSamples(r, Raw16, 16, 2);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void Size_zero_and_truncated_mid_sample_combined()
    {
        var b = RefWavBuilder.Pcm(48000, 1, 16).WithIntSamples(Raw16);
        b.DeclaredDataSize = 0;
        var bytes = b.Build();
        var truncated = bytes.AsSpan(0, bytes.Length - 1).ToArray();
        var r = Read(truncated);
        Assert.Equal(Raw16.Length - 1, r.Audio.FrameCount);
        AssertFirstSamples(r, Raw16, 16, 1);
    }

    [Fact]
    public void Healthy_file_has_no_warnings()
    {
        var r = Read(RefWavBuilder.Pcm(48000, 1, 16).WithIntSamples(Raw16).Build());
        Assert.Empty(r.Warnings);
        Assert.False(r.HeaderRecovered);
    }

    [Fact]
    public void Truncated_float_recording_header_is_recovered()
    {
        // 模擬錄音中途當機：檔頭仍是初始的 0，資料已寫入
        float[] samples = [0.1f, 0.2f, 0.3f, 0.4f];
        var bytes = RefWavBuilder.IeeeFloat(48000, 1, 32).WithFloat32Samples(samples).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0);
        var dataSizeOffset = bytes.Length - (samples.Length * 4) - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(dataSizeOffset), 0);
        var r = Read(bytes);
        Assert.Equal(samples, r.Audio.Channel(0));
    }
}
