namespace PracticeMate.Core.Audio;

/// <summary>
/// 已解碼的音訊（各聲道分開存放的 float，滿刻度為 ±1）。
/// 時間一律以取樣索引（框索引）表示。
/// </summary>
public sealed class AudioData
{
    private readonly float[][] _channels;

    public AudioData(int sampleRate, float[][] channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length == 0)
        {
            throw new ArgumentException("至少需要一個聲道。", nameof(channels));
        }
        if (channels.Any(c => c.Length != channels[0].Length))
        {
            throw new ArgumentException("各聲道長度必須相同。", nameof(channels));
        }
        SampleRate = sampleRate;
        _channels = channels;
    }

    public int SampleRate { get; }

    public int ChannelCount => _channels.Length;

    public long FrameCount => _channels[0].Length;

    public float[] Channel(int index) => _channels[index];

    /// <summary>各聲道平均成單聲道（單聲道時直接回傳原陣列）。</summary>
    public float[] MixToMono()
    {
        if (ChannelCount == 1)
        {
            return _channels[0];
        }
        var mono = new float[FrameCount];
        var scale = 1.0f / ChannelCount;
        for (var c = 0; c < ChannelCount; c++)
        {
            var ch = _channels[c];
            for (var i = 0; i < mono.Length; i++)
            {
                mono[i] += ch[i] * scale;
            }
        }
        return mono;
    }

    /// <summary>由交錯取樣建立。</summary>
    public static AudioData FromInterleaved(int sampleRate, int channels, ReadOnlySpan<float> interleaved)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var frames = interleaved.Length / channels;
        var data = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
        }
        for (var i = 0; i < frames * channels; i++)
        {
            data[i % channels][i / channels] = interleaved[i];
        }
        return new AudioData(sampleRate, data);
    }
}
