using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Devices;
using PracticeMate.Core.Metronome;

namespace PracticeMate.App.ViewModels;

/// <summary>小節內一拍的重音開關。</summary>
public sealed partial class AccentSlot(int index, bool isAccent) : ObservableObject
{
    public int Index { get; } = index;

    public string Label => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isAccent = isAccent;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// 節拍器：BPM 30–300、每小節 1–12 拍、重音拍可個別切換。
/// 執行中變更設定會交給渲染器，於下一小節第一拍生效。
/// </summary>
public sealed partial class MetronomeViewModel : ObservableObject, IDisposable
{
    private readonly IAudioDeviceFactory _factory;
    private readonly SettingsViewModel _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ITimer _timer;
    private IAudioRenderDevice? _device;
    private MetronomeRenderer? _renderer;

    public MetronomeViewModel(IAudioDeviceFactory factory, SettingsViewModel settings, IUiDispatcher dispatcher, TimeProvider time)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(time);
        _bpm = Math.Clamp(settings.MetronomeBpm, MetronomeSettings.MinBpm, MetronomeSettings.MaxBpm);
        _beatsPerBar = Math.Clamp(settings.MetronomeBeatsPerBar, MetronomeSettings.MinBeatsPerBar, MetronomeSettings.MaxBeatsPerBar);
        RebuildAccents();
        _timer = time.CreateTimer(_ => _dispatcher.Post(RefreshBeat), null, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
    }

    public ObservableCollection<AccentSlot> Accents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmError))]
    private int _bpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BeatsPerBarError))]
    private int _beatsPerBar;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private int _currentBeat = -1;

    [ObservableProperty]
    private string? _statusMessage;

    public string? BpmError => Bpm is >= MetronomeSettings.MinBpm and <= MetronomeSettings.MaxBpm ? null : "速度必須介於 30 與 300 BPM 之間。";

    public string? BeatsPerBarError => BeatsPerBar is >= MetronomeSettings.MinBeatsPerBar and <= MetronomeSettings.MaxBeatsPerBar ? null : "每小節拍數必須介於 1 與 12 之間。";

    /// <summary>目前的設定（無效時為 null）。</summary>
    public MetronomeSettings? CurrentSettings =>
        BpmError is null && BeatsPerBarError is null ? new MetronomeSettings(Bpm, BeatsPerBar, Accents.Select(a => a.IsAccent).ToArray()) : null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (CurrentSettings is not { } settings)
        {
            return;
        }
        try
        {
            _device = _factory.CreateRender(_settings.RenderDeviceId);
            _renderer = new MetronomeRenderer(_device.Format.SampleRate, settings);
            _device.Stopped += OnDeviceStopped;
            _device.Start(_renderer);
            IsRunning = true;
            StatusMessage = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            StatusMessage = $"無法開啟輸出裝置：{ex.Message}";
            ReleaseDevice();
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _device?.Stop();
        ReleaseDevice();
        IsRunning = false;
        CurrentBeat = -1;
        UpdateCurrentSlot();
    }

    public void RefreshBeat()
    {
        if (_renderer?.LastBeat is { } beat && beat.BeatInBar != CurrentBeat)
        {
            CurrentBeat = beat.BeatInBar;
            UpdateCurrentSlot();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        ReleaseDevice();
    }

    partial void OnBpmChanged(int value) => PushSettings();

    partial void OnBeatsPerBarChanged(int value)
    {
        if (BeatsPerBarError is null)
        {
            RebuildAccents();
        }
        PushSettings();
    }

    private void RebuildAccents()
    {
        var old = Accents.Select(a => a.IsAccent).ToArray();
        foreach (var a in Accents)
        {
            a.PropertyChanged -= OnAccentChanged;
        }
        Accents.Clear();
        for (var i = 0; i < BeatsPerBar; i++)
        {
            var slot = new AccentSlot(i, i < old.Length ? old[i] : i == 0);
            slot.PropertyChanged += OnAccentChanged;
            Accents.Add(slot);
        }
    }

    private void OnAccentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccentSlot.IsAccent))
        {
            PushSettings();
        }
    }

    private void PushSettings()
    {
        StartCommand.NotifyCanExecuteChanged();
        if (_renderer is not null && CurrentSettings is { } s)
        {
            _renderer.RequestSettings(s);
        }
    }

    private void UpdateCurrentSlot()
    {
        foreach (var a in Accents)
        {
            a.IsCurrent = a.Index == CurrentBeat;
        }
    }

    private void OnDeviceStopped(object? sender, StreamStoppedEventArgs e)
    {
        if (e.Reason == StreamStopReason.Requested)
        {
            return;
        }
        _dispatcher.Post(() =>
        {
            ReleaseDevice();
            IsRunning = false;
            CurrentBeat = -1;
            StatusMessage = e.Reason == StreamStopReason.DeviceRemoved ? "輸出裝置已被移除，節拍器已停止。"
                : e.Reason == StreamStopReason.DefaultDeviceChanged ? "系統預設輸出裝置已變更，節拍器已停止。"
                : $"輸出裝置發生錯誤，節拍器已停止。{e.Detail}";
        });
    }

    private void ReleaseDevice()
    {
        if (_device is not null)
        {
            _device.Stopped -= OnDeviceStopped;
            _device.Dispose();
        }
        _device = null;
        _renderer = null;
    }
}
