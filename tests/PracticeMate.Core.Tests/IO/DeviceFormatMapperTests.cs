using PracticeMate.Core.Audio;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.IO;

public class DeviceFormatMapperTests
{
    [Theory]
    [InlineData(DeviceFormatMapper.TagFloat, 32, 32, WavSampleEncoding.Float32)]
    [InlineData(DeviceFormatMapper.TagPcm, 16, 16, WavSampleEncoding.Int16)]
    [InlineData(DeviceFormatMapper.TagPcm, 24, 24, WavSampleEncoding.Int24)]
    public void Maps_basic_formats(ushort tag, int bits, int valid, WavSampleEncoding expected)
    {
        Assert.Equal(expected, DeviceFormatMapper.Map(tag, bits, valid, null).Encoding);
    }

    [Fact]
    public void Maps_extensible_float_mix_format()
    {
        var f = DeviceFormatMapper.Map(DeviceFormatMapper.TagExtensible, 32, 32, DeviceFormatMapper.SubTypeFloat);
        Assert.Equal(WavSampleEncoding.Float32, f.Encoding);
    }

    [Fact]
    public void Maps_extensible_pcm_with_valid_bits()
    {
        var f = DeviceFormatMapper.Map(DeviceFormatMapper.TagExtensible, 32, 24, DeviceFormatMapper.SubTypePcm);
        Assert.Equal(new PcmSampleFormat(WavSampleEncoding.Int32, 24), f);
    }

    [Fact]
    public void Unsupported_subformat_is_rejected_in_chinese()
    {
        var ex = Assert.Throws<NotSupportedException>(() => DeviceFormatMapper.Map(DeviceFormatMapper.TagExtensible, 16, 16, Guid.NewGuid()));
        Assert.Contains("子格式", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WavSampleEncoding.Float32, 32)]
    [InlineData(WavSampleEncoding.Int16, 16)]
    [InlineData(WavSampleEncoding.Int24, 24)]
    [InlineData(WavSampleEncoding.Int32, 24)]
    [InlineData(WavSampleEncoding.UInt8, 8)]
    public void Float_round_trips_through_device_format(WavSampleEncoding encoding, int valid)
    {
        var format = new PcmSampleFormat(encoding, valid);
        var x = SignalGenerator.ToFloat(SignalGenerator.Sine(48000, 1000, 480, 0.9));
        var bytes = new byte[x.Length * format.ContainerBytes];
        DeviceFormatMapper.FromFloat(x, format, bytes);
        var back = new float[x.Length];
        PcmConverter.ToFloat(bytes, format, back);
        var tolerance = encoding is WavSampleEncoding.Float32 ? 0 : 1.0 / Math.Pow(2, valid - 1);
        for (var i = 0; i < x.Length; i++)
        {
            Assert.True(Math.Abs(x[i] - back[i]) <= tolerance, $"{encoding}: {x[i]} → {back[i]}");
        }
    }

    [Fact]
    public void Integer_output_is_clipped()
    {
        var bytes = new byte[4];
        DeviceFormatMapper.FromFloat([2f, -2f], PcmSampleFormat.Of(WavSampleEncoding.Int16), bytes);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(bytes, 2));
    }
}
