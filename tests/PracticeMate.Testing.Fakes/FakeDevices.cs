using PracticeMate.Core.Devices;

namespace PracticeMate.Testing.Fakes;

/// <summary>假輸出裝置：測試呼叫 <see cref="RenderBlock"/> 模擬輸出執行緒取資料。</summary>
public sealed class FakeRenderDevice(int sampleRate = 48000, int channels = 2) : IAudioRenderDevice
{
    private IAudioBlockSource? _source;

    public AudioStreamFormat Format { get; } = new(sampleRate, channels);

    public bool IsStarted => _source is not null;

    public bool IsDisposed { get; private set; }

    public event EventHandler<StreamStoppedEventArgs>? Stopped;

    public void Start(IAudioBlockSource source) => _source = source;

    public float[] RenderBlock(int frames)
    {
        var buffer = new float[frames * Format.Channels];
        _source?.Render(buffer, Format.Channels);
        return buffer;
    }

    public void Stop()
    {
        _source = null;
        Stopped?.Invoke(this, new StreamStoppedEventArgs(StreamStopReason.Requested));
    }

    public void SimulateDeviceRemoved()
    {
        _source = null;
        Stopped?.Invoke(this, new StreamStoppedEventArgs(StreamStopReason.DeviceRemoved));
    }

    public void Dispose() => IsDisposed = true;
}

/// <summary>假裝置工廠與列舉：記錄建立的裝置，供測試操作。</summary>
public sealed class FakeAudioSystem : IAudioDeviceFactory, IAudioDeviceCatalog
{
    public int CaptureSampleRate { get; set; } = 48000;

    public int CaptureChannels { get; set; } = 1;

    public List<FakeCaptureDevice> CaptureDevices { get; } = [];

    public List<FakeRenderDevice> RenderDevices { get; } = [];

    public List<string> RequestedIds { get; } = [];

    public FakeCaptureDevice? LastCapture => CaptureDevices.LastOrDefault();

    public FakeRenderDevice? LastRender => RenderDevices.LastOrDefault();

    public IReadOnlyList<AudioDeviceInfo> Devices { get; set; } =
    [
        new("mic-1", "USB 麥克風", AudioDeviceKind.Capture, false),
        new("spk-1", "喇叭", AudioDeviceKind.Render, true),
    ];

    public IAudioCaptureDevice CreateCapture(string deviceId)
    {
        RequestedIds.Add(deviceId);
        var d = new FakeCaptureDevice(CaptureSampleRate, CaptureChannels);
        CaptureDevices.Add(d);
        return d;
    }

    public IAudioRenderDevice CreateRender(string deviceId)
    {
        RequestedIds.Add(deviceId);
        var d = new FakeRenderDevice();
        RenderDevices.Add(d);
        return d;
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind) => Devices.Where(d => d.Kind == kind).ToList();
}
