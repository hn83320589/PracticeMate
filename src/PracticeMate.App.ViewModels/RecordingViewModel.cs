using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Recording;

namespace PracticeMate.App.ViewModels;

public enum RecordingState
{
    Idle,
    Recording,
    Stopped,
    Failed,
}

/// <summary>
/// 錄音控制：開始/停止、輸入電平表、溢位計數、錄音時間。
/// 錄音檔名依 TimeProvider 的本地時間：yyyyMMdd-HHmmss.wav，存放在設定的錄音資料夾。
/// 裝置拔除或預設裝置變更時，錄音會自動停止並保留已錄部分，狀態改為「已停止」並顯示原因。
/// </summary>
public sealed partial class RecordingViewModel : ObservableObject, IDisposable
{
    private readonly AudioInputController _input;
    private readonly SettingsViewModel _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    public RecordingViewModel(AudioInputController input, SettingsViewModel settings, IUiDispatcher dispatcher, TimeProvider time)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _timer = time.CreateTimer(_ => _dispatcher.Post(RefreshMeters), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        _input.StateChanged += (_, _) => _dispatcher.Post(OnInputStateChanged);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    private RecordingState _state = RecordingState.Idle;

    [ObservableProperty]
    private string _elapsedText = "0:00";

    [ObservableProperty]
    private double _peakDbfs = double.NegativeInfinity;

    [ObservableProperty]
    private double _rmsDbfs = double.NegativeInfinity;

    [ObservableProperty]
    private long _overflowCount;

    [ObservableProperty]
    private long _droppedFrames;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _lastRecordingPath;

    public bool IsRecording => State == RecordingState.Recording;

    /// <summary>電平表的 0–1 位置（−60 dBFS 到 0 dBFS）。</summary>
    public double PeakMeterFraction => ToFraction(PeakDbfs);

    public double RmsMeterFraction => ToFraction(RmsDbfs);

    /// <summary>使用者要求開啟最近的錄音進行分析。</summary>
    public event EventHandler<string>? AnalyzeRequested;

    public string NextFileName() => _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".wav";

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var path = Path.Combine(_settings.EffectiveRecordingFolder, NextFileName());
        try
        {
            await _input.StartAsync(_settings.CaptureDeviceId, path, _settings.TunerSettings).ConfigureAwait(true);
            LastRecordingPath = path;
            StatusMessage = $"錄音中：{Path.GetFileName(path)}";
            State = RecordingState.Recording;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = $"無法開始錄音：{ex.Message}";
            State = RecordingState.Failed;
        }
    }

    private bool CanStart() => State != RecordingState.Recording;

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private async Task StopAsync()
    {
        await _input.StopAsync().ConfigureAwait(true);
        OnInputStateChanged();
    }

    [RelayCommand]
    private void Analyze()
    {
        if (LastRecordingPath is { } p && State != RecordingState.Recording)
        {
            AnalyzeRequested?.Invoke(this, p);
        }
    }

    public void RefreshMeters()
    {
        var session = _input.Session;
        if (session is null)
        {
            return;
        }
        OverflowCount = session.OverflowCount;
        DroppedFrames = session.DroppedFrames;
        if (_input.Level?.Latest is { } level)
        {
            PeakDbfs = level.PeakDbfs;
            RmsDbfs = level.RmsDbfs;
        }
        if (State == RecordingState.Recording)
        {
            var seconds = session.FramesRecorded / session.Format.SampleRate;
            ElapsedText = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
        }
    }

    public void Dispose() => _timer.Dispose();

    partial void OnPeakDbfsChanged(double value) => OnPropertyChanged(nameof(PeakMeterFraction));

    partial void OnRmsDbfsChanged(double value) => OnPropertyChanged(nameof(RmsMeterFraction));

    private static double ToFraction(double db) => double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db + 60) / 60, 0, 1);

    private void OnInputStateChanged()
    {
        if (State != RecordingState.Recording || _input.IsRecording)
        {
            return;
        }
        RefreshMeters();
        var session = _input.Session;
        State = session?.State == CaptureState.Faulted ? RecordingState.Failed : RecordingState.Stopped;
        StatusMessage = session?.StatusMessage;
    }
}
