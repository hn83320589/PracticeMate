using System.Globalization;
using PracticeMate.Core.Audio;

namespace PracticeMate.Core.IO.Wav;

/// <summary>WAV 檔的格式資訊（讀取後的結果）。</summary>
public sealed record WavFormatInfo(
    ushort FormatTag,
    bool IsExtensible,
    int Channels,
    int SampleRate,
    int ContainerBitsPerSample,
    int ValidBitsPerSample,
    uint ChannelMask,
    WavSampleEncoding Encoding)
{
    public int BlockAlign => Channels * (ContainerBitsPerSample / 8);

    public string Description
    {
        get
        {
            var kind = Encoding is WavSampleEncoding.Float32 or WavSampleEncoding.Float64
                ? $"IEEE 浮點 {ContainerBitsPerSample} 位元"
                : ValidBitsPerSample == ContainerBitsPerSample
                    ? $"PCM {ContainerBitsPerSample} 位元整數"
                    : $"PCM {ValidBitsPerSample} 位元整數（{ContainerBitsPerSample} 位元容器）";
            var channels = Channels == 1 ? "單聲道" : "立體聲";
            return string.Create(CultureInfo.InvariantCulture, $"{kind}，{channels}，{SampleRate} Hz");
        }
    }
}

/// <summary>讀取結果。<see cref="Warnings"/> 為給使用者看的中文提示（例如檔頭損毀已回復）。</summary>
public sealed record WavReadResult(AudioData Audio, WavFormatInfo Format, IReadOnlyList<string> Warnings, bool HeaderRecovered);

/// <summary>檔案不是可支援的 WAV 格式。<see cref="Exception.Message"/> 為中文原因。</summary>
public sealed class UnsupportedWavFormatException : Exception
{
    public UnsupportedWavFormatException() { }
    public UnsupportedWavFormatException(string message) : base(message) { }
    public UnsupportedWavFormatException(string message, Exception inner) : base(message, inner) { }
}
