using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PracticeMate.Core.Audio;
using PracticeMate.Core.Devices;

namespace PracticeMate.Audio.Windows;

/// <summary>
/// WASAPI 共用模式擷取。取樣格式依裝置的共用模式格式（通常為 32 位元浮點），
/// 位元組→float 由 Core 的 <see cref="PcmConverter"/> 轉換（不使用 NAudio 的轉換功能）。
/// 擷取回呼只做格式轉換並交給 sink，不阻塞。
/// </summary>
public sealed class WasapiCaptureDevice : IAudioCaptureDevice
{
    internal const int DeviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED

    private readonly MMDevice _device;
    private readonly bool _followsDefault;
    private readonly EndpointNotifications _notifications;
    private readonly WasapiCapture _capture;
    private readonly PcmSampleFormat _sampleFormat;
    private float[] _buffer;
    private IAudioBlockSink? _sink;
    private int _pendingReason = -1;
    private int _stoppedRaised;

    internal WasapiCaptureDevice(MMDevice device, bool followsDefault, EndpointNotifications notifications)
    {
        _device = device;
        _followsDefault = followsDefault;
        _notifications = notifications;
        _capture = new WasapiCapture(device, true, 20);
        var wf = _capture.WaveFormat;
        _sampleFormat = DeviceFormatMapper.Map((ushort)wf.Encoding, wf.BitsPerSample, wf.BitsPerSample,
            wf is WaveFormatExtensible ext ? ext.SubFormat : null);
        Format = new AudioStreamFormat(wf.SampleRate, wf.Channels);
        _buffer = new float[wf.SampleRate * wf.Channels]; // 1 秒，正常情況不需重新配置
    }

    public AudioStreamFormat Format { get; }

    public event EventHandler<StreamStoppedEventArgs>? Stopped;

    public void Start(IAudioBlockSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _notifications.DeviceLost += OnDeviceLost;
        _notifications.DefaultChanged += OnDefaultChanged;
        _capture.StartRecording();
    }

    public void Stop() => _capture.StopRecording();

    public void Dispose()
    {
        _notifications.DeviceLost -= OnDeviceLost;
        _notifications.DefaultChanged -= OnDefaultChanged;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _device.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var samples = e.BytesRecorded / _sampleFormat.ContainerBytes;
        if (samples > _buffer.Length)
        {
            _buffer = new float[samples]; // 極少發生：裝置一次給超過 1 秒
        }
        PcmConverter.ToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), _sampleFormat, _buffer);
        _sink?.OnCaptured(_buffer.AsSpan(0, samples - (samples % Format.Channels)));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) == 1)
        {
            return;
        }
        var pending = Volatile.Read(ref _pendingReason);
        var reason = pending >= 0 ? (StreamStopReason)pending
            : e.Exception is COMException { HResult: DeviceInvalidated } ? StreamStopReason.DeviceRemoved
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
        if (_followsDefault && flow == DataFlow.Capture && id != _device.ID)
        {
            RequestStop(StreamStopReason.DefaultDeviceChanged);
        }
    }

    /// <summary>通知回呼在 COM 執行緒上，不可在其中直接操作音訊 API，改交給執行緒集區。</summary>
    private void RequestStop(StreamStopReason reason)
    {
        Interlocked.CompareExchange(ref _pendingReason, (int)reason, -1);
        ThreadPool.QueueUserWorkItem(_ => _capture.StopRecording());
    }
}
