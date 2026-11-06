using PracticeMate.Core.Devices;
using PracticeMate.Core.Tuner;

namespace PracticeMate.Core.Recording;

/// <summary>
/// 管理目前唯一的輸入工作階段（調音器監聽或錄音）：建立裝置與 <see cref="CaptureSession"/>，
/// 掛上調音器與電平表，並在背景定期 Pump。同時間只會有一個輸入工作階段。
/// <para><c>backgroundPump = false</c> 時不啟動背景迴圈，由呼叫端（測試）呼叫 <see cref="Pump"/>。</para>
/// </summary>
public sealed class AudioInputController : IAsyncDisposable
{
    private readonly IAudioDeviceFactory _factory;
    private readonly TimeProvider _time;
    private readonly bool _backgroundPump;
    private CapturePumpRunner? _runner;

    public AudioInputController(IAudioDeviceFactory factory, TimeProvider time, bool backgroundPump = true)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _backgroundPump = backgroundPump;
    }

    public CaptureSession? Session { get; private set; }

    public TunerEngine? Tuner { get; private set; }

    public LevelMeter? Level { get; private set; }

    public string? RecordingPath { get; private set; }

    public bool IsActive => Session?.State == CaptureState.Running;

    public bool IsRecording => IsActive && RecordingPath is not null;

    /// <summary>工作階段狀態改變（可能在背景執行緒觸發）。</summary>
    public event EventHandler? StateChanged;

    /// <param name="recordingPath">null 表示只監聽（調音器）。</param>
    public async Task StartAsync(string deviceId, string? recordingPath, TunerSettings tunerSettings)
    {
        await StopAsync().ConfigureAwait(false);
        Session?.Dispose();
        Session = null;
        var device = _factory.CreateCapture(deviceId);
        try
        {
            Tuner = new TunerEngine(device.Format.SampleRate, tunerSettings);
            Level = LevelMeter.ForSampleRate(device.Format.SampleRate);
            var options = new CaptureSessionOptions
            {
                OpenOutput = recordingPath is null ? null : _ => OpenRecordingFile(recordingPath),
            };
            var session = new CaptureSession(device, options, [Tuner, Level]);
            session.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
            Session = session;
            RecordingPath = recordingPath;
            session.Start();
        }
        catch
        {
            Session?.Dispose();
            Session = null;
            device.Dispose();
            throw;
        }
        if (_backgroundPump)
        {
            _runner = new CapturePumpRunner(Session, _time);
            _runner.Start();
        }
    }

    /// <summary>停止並保存錄音（若有）。工作階段已因裝置問題結束時只做清理。</summary>
    public async Task StopAsync()
    {
        var session = Session;
        if (session is null)
        {
            return;
        }
        session.RequestStop();
        if (_runner is not null)
        {
            await _runner.Completion.WaitAsync(TimeSpan.FromSeconds(5), _time).ConfigureAwait(false);
            await _runner.DisposeAsync().ConfigureAwait(false);
            _runner = null;
        }
        else
        {
            session.Pump();
        }
    }

    /// <summary>手動 Pump（只在 backgroundPump = false 時使用）。</summary>
    public int Pump() => Session?.Pump() ?? 0;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Session?.Dispose();
        Session = null;
    }

    private static FileStream OpenRecordingFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
    }
}
