using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Recording;
using PracticeMate.Core.Tuner;

namespace PracticeMate.App.ViewModels;

/// <summary>
/// 調音器畫面：以 30 Hz 讀取調音器引擎的最新讀數並更新顯示（引擎本身每秒產生 100 筆）。
/// 沒有明確音高時音名、音分都顯示「—」，指針歸零。
/// </summary>
public sealed partial class TunerViewModel : ObservableObject, IDisposable
{
    public const string NoValue = "—";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>指針滿刻度角度（對應 ±50 音分）。</summary>
    public const double MaxNeedleAngle = 45.0;

    /// <summary>視為「準」的範圍（音分）。</summary>
    public const double InTuneCents = 5.0;

    private readonly AudioInputController _input;
    private readonly SettingsViewModel _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ITimer _timer;
    private long _lastReadingFrame = -1;

    public TunerViewModel(AudioInputController input, SettingsViewModel settings, IUiDispatcher dispatcher, TimeProvider time)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(time);
        _timer = time.CreateTimer(_ => _dispatcher.Post(Refresh), null, RefreshInterval, RefreshInterval);
        _input.StateChanged += (_, _) => _dispatcher.Post(OnInputStateChanged);
        _settings.Changed += (_, _) => _input.Tuner?.UpdateSettings(_settings.TunerSettings);
    }

    [ObservableProperty]
    private string _noteText = NoValue;

    [ObservableProperty]
    private string _centsText = NoValue;

    [ObservableProperty]
    private string _frequencyText = NoValue;

    [ObservableProperty]
    private string _concertText = string.Empty;

    [ObservableProperty]
    private double _needleAngle;

    [ObservableProperty]
    private bool _hasPitch;

    [ObservableProperty]
    private bool _isInTune;

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>顯示更新次數（測試用來驗證更新率）。</summary>
    public int DisplayUpdates { get; private set; }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await _input.StartAsync(_settings.CaptureDeviceId, null, _settings.TunerSettings).ConfigureAwait(true);
            StatusMessage = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            StatusMessage = $"無法開啟錄音裝置：{ex.Message}";
        }
        UpdateListening();
    }

    private bool CanStart() => !IsListening;

    [RelayCommand(CanExecute = nameof(IsListening))]
    private async Task StopAsync()
    {
        await _input.StopAsync().ConfigureAwait(true);
        UpdateListening();
        Show(TunerReading.Empty);
    }

    /// <summary>以最新讀數更新顯示（由計時器在 UI 執行緒呼叫）。</summary>
    public void Refresh()
    {
        var reading = _input.Tuner?.Latest;
        if (reading is null || reading.EndFrame == _lastReadingFrame)
        {
            return;
        }
        _lastReadingFrame = reading.EndFrame;
        Show(reading);
    }

    public void Dispose() => _timer.Dispose();

    internal void Show(TunerReading r)
    {
        DisplayUpdates++;
        HasPitch = r.HasPitch;
        if (!r.HasPitch)
        {
            NoteText = NoValue;
            CentsText = NoValue;
            FrequencyText = NoValue;
            ConcertText = string.Empty;
            NeedleAngle = 0;
            IsInTune = false;
            return;
        }
        NoteText = r.Written.Name;
        CentsText = FormatCents(r.Cents);
        FrequencyText = r.FrequencyHz.ToString("F1", CultureInfo.InvariantCulture) + " Hz";
        ConcertText = r.Written.NearestMidi == r.Concert.NearestMidi ? string.Empty : $"實際音高 {r.Concert.Name}";
        NeedleAngle = Math.Clamp(r.Cents / 50.0, -1, 1) * MaxNeedleAngle;
        IsInTune = Math.Abs(r.Cents) <= InTuneCents;
    }

    /// <summary>四捨五入到整數音分，正值加「+」，負值用減號「−」。</summary>
    public static string FormatCents(double cents)
    {
        var rounded = (int)Math.Round(cents, MidpointRounding.AwayFromZero);
        return rounded switch
        {
            > 0 => "+" + rounded.ToString(CultureInfo.InvariantCulture),
            < 0 => "−" + (-rounded).ToString(CultureInfo.InvariantCulture),
            _ => "0",
        };
    }

    private void OnInputStateChanged()
    {
        UpdateListening();
        if (!_input.IsActive && _input.Session?.StatusMessage is { } message && _input.Session.StopReason != Core.Devices.StreamStopReason.Requested)
        {
            StatusMessage = message;
        }
    }

    private void UpdateListening()
    {
        IsListening = _input.IsActive && !_input.IsRecording;
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
