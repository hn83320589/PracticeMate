using PracticeMate.Core.Devices;
using PracticeMate.Core.IO.Wav;
using PracticeMate.Core.Recording;
using PracticeMate.Testing.Fakes;

namespace PracticeMate.Core.Tests.Recording;

/// <summary>驗收 #3、#19（假裝置）。</summary>
public sealed class CaptureSessionTests : IDisposable
{
    private const int Fs = 8000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-rec-" + Guid.NewGuid().ToString("N"));

    public CaptureSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static CaptureSessionOptions RecordTo(string path) => new()
    {
        OpenOutput = _ => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read),
    };

    /// <summary>每個取樣的值 = 全域框索引 / 1e6（可驗證順序與完整性）。</summary>
    private static float[] Block(long firstFrame, int frames, int channels = 1)
    {
        var b = new float[frames * channels];
        for (var i = 0; i < b.Length; i++)
        {
            b[i] = (firstFrame + (i / channels)) / 1e6f;
        }
        return b;
    }

    private static WavReadResult ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return WavReader.Read(fs);
    }

    [Fact]
    public void Records_every_delivered_frame_and_completes_header_on_stop()
    {
        var path = PathOf("a.wav");
        var device = new FakeCaptureDevice(Fs, 2);
        using var session = new CaptureSession(device, RecordTo(path));
        session.Start();
        for (var i = 0; i < 30; i++)
        {
            device.Push(Block(i * 80L, 80, 2));
            if (i % 7 == 0)
            {
                session.Pump();
            }
        }
        session.RequestStop();
        session.Pump();

        Assert.Equal(CaptureState.Stopped, session.State);
        Assert.Equal(StreamStopReason.Requested, session.StopReason);
        var r = ReadShared(path);
        Assert.Empty(r.Warnings);
        Assert.Equal(2400, r.Audio.FrameCount);
        Assert.Equal(1234 / 1e6f, r.Audio.Channel(1)[1234]);
    }

    [Fact]
    public void Crash_without_stop_loses_at_most_data_after_last_header_update()
    {
        var path = PathOf("crash.wav");
        var device = new FakeCaptureDevice(Fs, 1);
        FileStream? stream = null;
        var session = new CaptureSession(device, new CaptureSessionOptions
        {
            OpenOutput = _ => stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read),
        }); // 檔頭每秒（8000 框）更新
        session.Start();
        for (var i = 0; i < 25; i++) // 2.5 秒
        {
            device.Push(Block(i * 800L, 800));
            session.Pump();
        }

        // 不呼叫 RequestStop / Dispose：模擬當機
        var r = ReadShared(path);
        Assert.InRange(r.Audio.FrameCount, 2 * Fs, 25 * 800);
        for (var i = 0; i < r.Audio.FrameCount; i += 997)
        {
            Assert.Equal(i / 1e6f, r.Audio.Channel(0)[i]);
        }

        // 測試結束：只關閉檔案控制代碼（不經過 session，不會補寫檔頭）
        stream!.Dispose();
    }

    [Theory]
    [InlineData(StreamStopReason.DeviceRemoved, "移除")]
    [InlineData(StreamStopReason.DefaultDeviceChanged, "預設")]
    [InlineData(StreamStopReason.DeviceError, "錯誤")]
    public void Device_loss_stops_cleanly_and_keeps_recording(StreamStopReason reason, string keyword)
    {
        var path = PathOf($"{reason}.wav");
        var device = new FakeCaptureDevice(Fs, 1);
        using var session = new CaptureSession(device, RecordTo(path));
        session.Start();
        for (var i = 0; i < 12; i++)
        {
            device.Push(Block(i * 400L, 400));
        }
        session.Pump();
        device.Push(Block(4800, 400));
        switch (reason)
        {
            case StreamStopReason.DeviceRemoved: device.SimulateDeviceRemoved(); break;
            case StreamStopReason.DefaultDeviceChanged: device.SimulateDefaultDeviceChanged(); break;
            default: device.SimulateError("AUDCLNT_E_DEVICE_INVALIDATED"); break;
        }
        device.Push(Block(5200, 400)); // 停止後才到的資料被忽略
        session.Pump();

        Assert.Equal(CaptureState.Stopped, session.State);
        Assert.Equal(reason, session.StopReason);
        Assert.Contains(keyword, session.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("已保存", session.StatusMessage, StringComparison.Ordinal);
        var r = ReadShared(path);
        Assert.Empty(r.Warnings); // 檔頭完整
        Assert.Equal(5200, r.Audio.FrameCount);
    }

    [Fact]
    public void Capture_callback_never_blocks_with_slow_consumer_and_counts_overflow()
    {
        var device = new FakeCaptureDevice(Fs, 1);
        using var session = new CaptureSession(device, new CaptureSessionOptions());
        session.Start();
        var block = Block(0, Fs / 100); // 10 ms
        var durations = new long[1000];

        // 擷取執行緒以遠快於即時的速度送資料；消費者完全不 Pump（極慢）
        var producer = new Thread(() =>
        {
            for (var i = 0; i < durations.Length; i++)
            {
                var t0 = TimeProvider.System.GetTimestamp();
                device.Push(block);
                durations[i] = TimeProvider.System.GetTimestamp() - t0;
            }
        });
        producer.Start();
        Assert.True(producer.Join(TimeSpan.FromSeconds(10)), "擷取回呼被阻塞");

        Array.Sort(durations);
        var median = TimeProvider.System.GetElapsedTime(0, durations[durations.Length / 2]);
        Assert.True(median < TimeSpan.FromMilliseconds(0.2), $"回呼中位數耗時 {median.TotalMilliseconds} ms");

        // 緩衝區容量 ≥ 2 秒 = 200 個 10 ms 區塊（進位到 2 的冪次後可多放一些），其餘溢位
        var accepted = session.RingBufferCapacitySamples / block.Length;
        Assert.Equal(1000 - accepted, session.OverflowCount);
        Assert.Equal((1000 - accepted) * block.Length, session.DroppedFrames);
        Assert.True(session.RingBufferCapacitySamples >= 2 * Fs);

        // 消費者恢復後取得的資料完整
        Assert.Equal(accepted * block.Length, session.Pump());
    }

    [Fact]
    public void Ring_buffer_holds_at_least_two_seconds_for_stereo_48k()
    {
        using var session = new CaptureSession(new FakeCaptureDevice(48000, 2), new CaptureSessionOptions());
        Assert.True(session.RingBufferCapacitySamples >= 2 * 48000 * 2);
    }

    [Fact]
    public void Rejects_ring_buffer_shorter_than_two_seconds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureSession(new FakeCaptureDevice(), new CaptureSessionOptions { RingBufferSeconds = 1 }));
    }

    [Fact]
    public void Consumers_receive_frames_with_sample_indices()
    {
        var device = new FakeCaptureDevice(Fs, 2);
        var probe = new ProbeConsumer();
        using var session = new CaptureSession(device, new CaptureSessionOptions(), [probe]);
        session.Start();
        device.Push(Block(0, 100, 2));
        device.Push(Block(100, 50, 2));
        session.Pump();
        device.Push(Block(150, 10, 2));
        session.Pump();
        Assert.Equal([0L, 150L], probe.FirstFrames);
        Assert.Equal(160, probe.TotalFrames);
        Assert.Equal(160, session.FramesProcessed);
    }

    [Fact]
    public void Tuner_only_session_without_output_reports_stopped()
    {
        var device = new FakeCaptureDevice(Fs, 1);
        using var session = new CaptureSession(device, new CaptureSessionOptions());
        session.Start();
        device.Push(Block(0, 10));
        session.RequestStop();
        session.Pump();
        Assert.Equal(CaptureState.Stopped, session.State);
        Assert.Equal("已停止。", session.StatusMessage);
    }

    [Fact]
    public void Write_failure_faults_with_message()
    {
        var device = new FakeCaptureDevice(Fs, 1);
        using var session = new CaptureSession(device, new CaptureSessionOptions { OpenOutput = _ => new FailingStream() });
        session.Start();
        device.Push(Block(0, 10));
        session.Pump();
        Assert.Equal(CaptureState.Faulted, session.State);
        Assert.Contains("寫入錄音檔失敗", session.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, device.StopCalls);
    }

    [Fact]
    public void Start_twice_throws()
    {
        using var session = new CaptureSession(new FakeCaptureDevice(), new CaptureSessionOptions());
        session.Start();
        Assert.Throws<InvalidOperationException>(session.Start);
    }

    [Fact]
    public void State_changes_are_raised()
    {
        var device = new FakeCaptureDevice();
        using var session = new CaptureSession(device, new CaptureSessionOptions());
        var states = new List<CaptureState>();
        session.StateChanged += (_, _) => states.Add(session.State);
        session.Start();
        session.RequestStop();
        session.Pump();
        Assert.Equal([CaptureState.Running, CaptureState.Stopped], states);
    }

    [Fact]
    public void Level_meter_reports_peak_and_rms()
    {
        var meter = new LevelMeter(4);
        meter.Consume([0.5f, -0.5f, 0.5f, -0.5f], 1, 0);
        Assert.Equal(20 * Math.Log10(0.5), meter.Latest.PeakDbfs, 9);
        Assert.Equal(20 * Math.Log10(0.5), meter.Latest.RmsDbfs, 9);
        Assert.Equal(4, meter.Latest.EndFrame);
        meter.Consume([0f, 0f, 0f, 0f], 1, 4);
        Assert.Equal(double.NegativeInfinity, meter.Latest.PeakDbfs);
    }

    private sealed class ProbeConsumer : ICaptureConsumer
    {
        public List<long> FirstFrames { get; } = [];

        public long TotalFrames { get; private set; }

        public void Consume(ReadOnlySpan<float> interleaved, int channels, long firstFrame)
        {
            FirstFrames.Add(firstFrame);
            TotalFrames += interleaved.Length / channels;
        }
    }

    /// <summary>檔頭（58 位元組）之後的寫入一律失敗，模擬磁碟已滿。</summary>
    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position >= 58)
            {
                throw new IOException("磁碟已滿");
            }
            base.Write(buffer, offset, count);
        }
    }
}