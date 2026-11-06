namespace PracticeMate.Core.Devices;

public enum AudioDeviceKind
{
    Capture,
    Render,
}

/// <param name="Id">裝置識別碼；<see cref="DefaultDeviceId"/> 代表「跟隨系統預設裝置」。</param>
public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault)
{
    public const string DefaultDeviceId = "default";
}

/// <summary>裝置共用模式的串流格式（取樣一律轉成交錯 float 交給 Core）。</summary>
public readonly record struct AudioStreamFormat(int SampleRate, int Channels);

public enum StreamStopReason
{
    Requested,
    DeviceRemoved,
    DefaultDeviceChanged,
    DeviceError,
}

public sealed class StreamStoppedEventArgs(StreamStopReason reason, string? detail = null) : EventArgs
{
    public StreamStopReason Reason { get; } = reason;

    /// <summary>給使用者看的中文說明（可為 null，由上層補上一般性說明）。</summary>
    public string? Detail { get; } = detail;
}

/// <summary>接收擷取到的音訊。由擷取執行緒呼叫：實作不得阻塞、不得配置記憶體。</summary>
public interface IAudioBlockSink
{
    void OnCaptured(ReadOnlySpan<float> interleaved);
}

/// <summary>提供要輸出的音訊。由輸出執行緒呼叫：實作不得阻塞。</summary>
public interface IAudioBlockSource
{
    void Render(Span<float> interleaved, int channels);
}

public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind);
}

public interface IAudioCaptureDevice : IDisposable
{
    AudioStreamFormat Format { get; }

    void Start(IAudioBlockSink sink);

    void Stop();

    /// <summary>串流停止（含使用者要求、裝置拔除、預設裝置變更、裝置錯誤）。可能在任意執行緒觸發。</summary>
    event EventHandler<StreamStoppedEventArgs>? Stopped;
}

public interface IAudioRenderDevice : IDisposable
{
    AudioStreamFormat Format { get; }

    void Start(IAudioBlockSource source);

    void Stop();

    event EventHandler<StreamStoppedEventArgs>? Stopped;
}

/// <summary>依裝置識別碼建立擷取/輸出裝置（Audio.Windows 實作）。</summary>
public interface IAudioDeviceFactory
{
    IAudioCaptureDevice CreateCapture(string deviceId);

    IAudioRenderDevice CreateRender(string deviceId);
}
