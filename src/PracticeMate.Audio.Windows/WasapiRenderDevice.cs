using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PracticeMate.Core.Audio;
using PracticeMate.Core.Devices;

namespace PracticeMate.Audio.Windows;

/// <summary>
/// WASAPI 共用模式輸出（節拍器）。輸出格式直接使用裝置的共用模式格式（MixFormat），
/// 因此 NAudio 不會插入任何重新取樣；float→裝置格式由 Core 的 <see cref="DeviceFormatMapper"/> 轉換。
/// </summary>
public sealed class WasapiRenderDevice : IAudioRenderDevice
{
    private readonly MMDevice _device;
    private readonly bool _followsDefault;
    private readonly EndpointNotifications _notifications;
    private readonly WaveFormat _mixFormat;
    private WasapiOut? _output;
    private int _pendingReason = -1;
    private int _stoppedRaised;

    internal WasapiRenderDevice(MMDevice device, bool followsDefault, EndpointNotifications notifications)
    {
        _device = device;
        _followsDefault = followsDefault;
        _notifications = notifications;
        _mixFormat = device.AudioClient.MixFormat;
        Format = new AudioStreamFormat(_mixFormat.SampleRate, _mixFormat.Channels);
    }

    public AudioStreamFormat Format { get; }

    public event EventHandler<StreamStoppedEventArgs>? Stopped;

    public void Start(IAudioBlockSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sampleFormat = DeviceFormatMapper.Map((ushort)_mixFormat.Encoding, _mixFormat.BitsPerSample, _mixFormat.BitsPerSample,
            _mixFormat is WaveFormatExtensible ext ? ext.SubFormat : null);
        _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 30);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(new SourceAdapter(source, _mixFormat, sampleFormat));
        _notifications.DeviceLost += OnDeviceLost;
        _notifications.DefaultChanged += OnDefaultChanged;
        _output.Play();
    }

    public void Stop() => _output?.Stop();

    public void Dispose()
    {
        _notifications.DeviceLost -= OnDeviceLost;
        _notifications.DefaultChanged -= OnDefaultChanged;
        _output?.Dispose();
        _device.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) == 1)
        {
            return;
        }
        var pending = Volatile.Read(ref _pendingReason);
        var reason = pending >= 0 ? (StreamStopReason)pending
            : e.Exception is COMException { HResult: WasapiCaptureDevice.DeviceInvalidated } ? StreamStopReason.DeviceRemoved
            : e.Exception is not null ? StreamStopReason.DeviceError
            : StreamStopReason.Requested;
        Stopped?.Invoke(this, new StreamStoppedEventArgs(reason, e.Exception?.Message));
    }

    private void OnDeviceLost(string id)
    {
        if (id == _device.ID)
        {
            RequestStop(StreamStopReason.DeviceRemoved);
        }
    }

    private void OnDefaultChanged(DataFlow flow, string id)
    {
        if (_followsDefault && flow == DataFlow.Render && id != _device.ID)
        {
            RequestStop(StreamStopReason.DefaultDeviceChanged);
        }
    }

    private void RequestStop(StreamStopReason reason)
    {
        Interlocked.CompareExchange(ref _pendingReason, (int)reason, -1);
        ThreadPool.QueueUserWorkItem(_ => _output?.Stop());
    }

    /// <summary>NAudio 要求的 IWaveProvider：向 Core 的音源要 float，再轉成裝置格式。</summary>
    private sealed class SourceAdapter(IAudioBlockSource source, WaveFormat format, PcmSampleFormat sampleFormat) : IWaveProvider
    {
        private float[] _floats = new float[format.SampleRate * format.Channels / 10];

        public WaveFormat WaveFormat => format;

        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / format.BlockAlign;
            var samples = frames * format.Channels;
            if (samples > _floats.Length)
            {
                _floats = new float[samples];
            }
            var span = _floats.AsSpan(0, samples);
            source.Render(span, format.Channels);
            DeviceFormatMapper.FromFloat(span, sampleFormat, buffer.AsSpan(offset, frames * format.BlockAlign));
            return frames * format.BlockAlign;
        }
    }
}
