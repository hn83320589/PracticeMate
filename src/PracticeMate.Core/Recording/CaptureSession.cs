using System.Globalization;
using PracticeMate.Core.Buffers;
using PracticeMate.Core.Devices;
using PracticeMate.Core.IO.Wav;

namespace PracticeMate.Core.Recording;

/// <summary>擷取資料的消費者（調音器、電平表等）。在消費者執行緒上呼叫。</summary>
public interface ICaptureConsumer
{
    /// <param name="firstFrame">此區塊第一框自擷取開始起算的取樣索引。</param>
    void Consume(ReadOnlySpan<float> interleaved, int channels, long firstFrame);
}

public enum CaptureState
{
    Idle,
    Running,
    Stopped,
    Faulted,
}

public sealed record CaptureSessionOptions
{
    public const double MinimumRingBufferSeconds = 2.0;

    /// <summary>環狀緩衝區容量（秒），至少 2 秒。</summary>
    public double RingBufferSeconds { get; init; } = MinimumRingBufferSeconds;

    /// <summary>建立錄音檔串流；null 表示只擷取不錄音（例如調音器）。</summary>
    public Func<AudioStreamFormat, Stream>? OpenOutput { get; init; }

    /// <summary>錄音檔頭更新間隔（框數）；null 表示每秒（= 取樣率）。</summary>
    public int? HeaderUpdateIntervalFrames { get; init; }
}

/// <summary>
/// 擷取工作階段：擷取回呼（生產者）只把資料寫進無鎖環狀緩衝區；
/// <see cref="Pump"/>（消費者，單一執行緒）取出後寫入錄音檔並交給各消費者。
/// 裝置停止（使用者要求、拔除、預設變更、錯誤）時，下一次 Pump 會排空緩衝、寫入最終檔頭並結束。
/// <para>正式環境由 <see cref="CapturePumpRunner"/> 在背景定期呼叫 Pump；測試直接呼叫 Pump，行為完全確定。</para>
/// </summary>
public sealed class CaptureSession : IAudioBlockSink, IDisposable
{
    private readonly IAudioCaptureDevice _device;
    private readonly CaptureSessionOptions _options;
    private readonly IReadOnlyList<ICaptureConsumer> _consumers;
    private readonly SpscRingBuffer _ring;
    private readonly float[] _pumpBuffer;
    private StreamingWavWriter? _writer;
    private StreamStoppedEventArgs? _deviceStopped;
    private int _state = (int)CaptureState.Idle;

    public CaptureSession(IAudioCaptureDevice device, CaptureSessionOptions options, IEnumerable<ICaptureConsumer>? consumers = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RingBufferSeconds < CaptureSessionOptions.MinimumRingBufferSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RingBufferSeconds, "環狀緩衝區至少需容納 2 秒的音訊。");
        }
        _device = device;
        _options = options;
        _consumers = consumers?.ToArray() ?? [];
        Format = device.Format;
        var capacity = (int)Math.Ceiling(options.RingBufferSeconds * Format.SampleRate * Format.Channels);
        _ring = new SpscRingBuffer(capacity);
        _pumpBuffer = new float[(Format.SampleRate / 10) * Format.Channels]; // 每次最多取 100 ms
        _device.Stopped += OnDeviceStopped;
    }

    public AudioStreamFormat Format { get; }

    public CaptureState State => (CaptureState)Volatile.Read(ref _state);

    public StreamStopReason? StopReason { get; private set; }

    /// <summary>結束原因的中文說明。</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>已交給消費者（並寫入檔案）的框數。</summary>
    public long FramesProcessed { get; private set; }

    public long FramesRecorded => _writer?.FramesWritten ?? FramesProcessed;

    public long OverflowCount => _ring.OverflowCount;

    public long DroppedFrames => _ring.DroppedSamples / Format.Channels;

    public int RingBufferCapacitySamples => _ring.Capacity;

    /// <summary>狀態改變（在消費者執行緒上觸發）。</summary>
    public event EventHandler? StateChanged;

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, (int)CaptureState.Running, (int)CaptureState.Idle) != (int)CaptureState.Idle)
        {
            throw new InvalidOperationException("擷取工作階段只能啟動一次。");
        }
        if (_options.OpenOutput is { } open)
        {
            var stream = open(Format);
            _writer = new StreamingWavWriter(stream, Format.SampleRate, Format.Channels,
                _options.HeaderUpdateIntervalFrames ?? Format.SampleRate);
        }
        _device.Start(this);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>擷取執行緒呼叫：只寫入環狀緩衝區，滿了就丟棄並計數。不阻塞、不配置。</summary>
    public void OnCaptured(ReadOnlySpan<float> interleaved) => _ring.TryWrite(interleaved);

    /// <summary>要求停止。實際收尾在裝置回報停止後的下一次 <see cref="Pump"/>。</summary>
    public void RequestStop()
    {
        if (State == CaptureState.Running)
        {
            _device.Stop();
        }
    }

    /// <summary>消費者：排空緩衝區並處理裝置停止。回傳本次處理的框數。只能由單一執行緒呼叫。</summary>
    public int Pump()
    {
        if (State != CaptureState.Running)
        {
            return 0;
        }

        // 先讀停止旗標再排空：確保停止前送達的資料都會被寫入
        var stopped = Volatile.Read(ref _deviceStopped);
        try
        {
            var processed = Drain();
            if (stopped is not null)
            {
                processed += Drain();
                Finish(stopped);
            }
            return processed;
        }
        catch (IOException ex)
        {
            Fault($"寫入錄音檔失敗：{ex.Message}");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Fault(ex.Message);
            return 0;
        }
    }

    public void Dispose()
    {
        _device.Stopped -= OnDeviceStopped;
        if (State == CaptureState.Running)
        {
            _device.Stop();
            Drain();
            _writer?.Complete();
        }
        _writer?.Dispose();
        _device.Dispose();
    }

    private int Drain()
    {
        var total = 0;
        int n;
        while ((n = _ring.Read(_pumpBuffer)) > 0)
        {
            var block = _pumpBuffer.AsSpan(0, n);
            _writer?.Write(block);
            foreach (var c in _consumers)
            {
                c.Consume(block, Format.Channels, FramesProcessed);
            }
            var frames = n / Format.Channels;
            FramesProcessed += frames;
            total += frames;
        }
        return total;
    }

    private void Finish(StreamStoppedEventArgs e)
    {
        _writer?.Complete();
        _writer?.Dispose();
        StopReason = e.Reason;
        var duration = FormatDuration(FramesRecorded);
        var saved = _writer is null ? string.Empty : $"已保存 {duration} 的錄音。";
        StatusMessage = e.Reason switch
        {
            StreamStopReason.Requested => _writer is null ? "已停止。" : $"錄音已停止，{saved}",
            StreamStopReason.DeviceRemoved => $"錄音裝置已被移除，{(_writer is null ? "已停止。" : saved)}",
            StreamStopReason.DefaultDeviceChanged => $"系統預設錄音裝置已變更，{(_writer is null ? "已停止。" : saved)}",
            _ => $"錄音裝置發生錯誤{(e.Detail is null ? string.Empty : $"（{e.Detail}）")}，{(_writer is null ? "已停止。" : saved)}",
        };
        Volatile.Write(ref _state, (int)CaptureState.Stopped);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Fault(string message)
    {
        StatusMessage = message;
        Volatile.Write(ref _state, (int)CaptureState.Faulted);
        _device.Stop();
        try
        {
            _writer?.Dispose();
        }
        catch (IOException ex)
        {
            StatusMessage += $"（關閉錄音檔時也失敗：{ex.Message}）";
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeviceStopped(object? sender, StreamStoppedEventArgs e) =>
        Interlocked.CompareExchange(ref _deviceStopped, e, null);

    private string FormatDuration(long frames)
    {
        var seconds = frames / Format.SampleRate;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} 分 {seconds % 60:00} 秒");
    }
}
