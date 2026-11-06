using PracticeMate.Core.Devices;

namespace PracticeMate.Testing.Fakes;

/// <summary>假擷取裝置：測試以 <see cref="Push"/> 送出區塊，並可模擬拔除、預設裝置變更、錯誤。</summary>
public sealed class FakeCaptureDevice(int sampleRate = 48000, int channels = 1) : IAudioCaptureDevice
{
    private IAudioBlockSink? _sink;
    private int _stopped;

    public AudioStreamFormat Format { get; } = new(sampleRate, channels);

    public bool IsStarted => _sink is not null;

    public bool IsDisposed { get; private set; }

    public int StopCalls { get; private set; }

    public event EventHandler<StreamStoppedEventArgs>? Stopped;

    public void Start(IAudioBlockSink sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>以擷取回呼送出一個區塊（呼叫端所在執行緒即「擷取執行緒」）。</summary>
    public void Push(ReadOnlySpan<float> interleaved)
    {
        if (Volatile.Read(ref _stopped) == 0)
        {
            _sink?.OnCaptured(interleaved);
        }
    }

    public void Stop()
    {
        StopCalls++;
        RaiseStopped(StreamStopReason.Requested);
    }

    public void SimulateDeviceRemoved() => RaiseStopped(StreamStopReason.DeviceRemoved);

    public void SimulateDefaultDeviceChanged() => RaiseStopped(StreamStopReason.DefaultDeviceChanged);

    public void SimulateError(string detail) => RaiseStopped(StreamStopReason.DeviceError, detail);

    public void Dispose() => IsDisposed = true;

    private void RaiseStopped(StreamStopReason reason, string? detail = null)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            Stopped?.Invoke(this, new StreamStoppedEventArgs(reason, detail));
        }
    }
}
