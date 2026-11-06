using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Analysis.Loudness;
using PracticeMate.Core.Devices;
using PracticeMate.Core.Dsp.Stft;
using PracticeMate.Core.Music;
using PracticeMate.Core.Tuner;

namespace PracticeMate.App.ViewModels;

/// <summary>設定頁：所有欄位即時驗證，錯誤以中文顯示；儲存時寫入設定檔並通知其他頁面。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IAudioDeviceCatalog _devices;

    public SettingsViewModel(ISettingsStore store, IAudioDeviceCatalog devices, string defaultRecordingFolder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        DefaultRecordingFolder = defaultRecordingFolder;
        Apply(store.Load());
        RefreshDevices();
    }

    public string DefaultRecordingFolder { get; }

    public static IReadOnlyList<Transposition> Transpositions { get; } = Enum.GetValues<Transposition>();

    public static IReadOnlyList<int> SpectrogramWindows { get; } = [256, 512, 1024, 2048, 4096, 8192, 16384];

    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = [];

    /// <summary>設定已儲存並生效。</summary>
    public event EventHandler? Changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(A4Error))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private double _a4;

    [ObservableProperty]
    private Transposition _transposition;

    [ObservableProperty]
    private double _noiseGateDbfs;

    [ObservableProperty]
    private string _captureDeviceId = AudioDeviceInfo.DefaultDeviceId;

    [ObservableProperty]
    private string _renderDeviceId = AudioDeviceInfo.DefaultDeviceId;

    [ObservableProperty]
    private int _spectrogramWindow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlapError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private double _spectrogramOverlap;

    [ObservableProperty]
    private bool _spectrogramLogFrequency;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NormalizationError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private double _normalizationTargetLufs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NormalizationError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private double _truePeakCeilingDbtp;

    [ObservableProperty]
    private int _metronomeBpm;

    [ObservableProperty]
    private int _metronomeBeatsPerBar;

    [ObservableProperty]
    private string _recordingFolder = string.Empty;

    public string? A4Error => A4 is >= NoteMath.MinA4 and <= NoteMath.MaxA4 ? null : $"標準音 A4 必須介於 {NoteMath.MinA4} 與 {NoteMath.MaxA4} Hz 之間。";

    public string? OverlapError => SpectrogramOverlap is >= 0 and <= 0.95 ? null : "重疊比例必須介於 0% 與 95% 之間。";

    public string? NormalizationError =>
        NormalizationTargetLufs is < -70 or > 0 ? "目標響度必須介於 −70 與 0 LUFS 之間。"
        : TruePeakCeilingDbtp is < -20 or > 0 ? "真峰值上限必須介於 −20 與 0 dBTP 之間。"
        : null;

    public bool IsValid => A4Error is null && OverlapError is null && NormalizationError is null;

    public TunerSettings TunerSettings => new() { A4 = A4, Transposition = Transposition, NoiseGateDbfs = NoiseGateDbfs };

    public SpectrogramOptions SpectrogramOptions => new() { WindowSize = SpectrogramWindow, Overlap = SpectrogramOverlap };

    public NormalizationRequest NormalizationRequest => new(NormalizationTargetLufs, TruePeakCeilingDbtp);

    public string EffectiveRecordingFolder => string.IsNullOrWhiteSpace(RecordingFolder) ? DefaultRecordingFolder : RecordingFolder;

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Save()
    {
        _store.Save(Snapshot());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        Fill(CaptureDevices, AudioDeviceKind.Capture);
        Fill(RenderDevices, AudioDeviceKind.Render);
    }

    public AppSettings Snapshot() => new()
    {
        A4 = A4,
        Transposition = Transposition,
        NoiseGateDbfs = NoiseGateDbfs,
        CaptureDeviceId = CaptureDeviceId,
        RenderDeviceId = RenderDeviceId,
        MetronomeBpm = MetronomeBpm,
        MetronomeBeatsPerBar = MetronomeBeatsPerBar,
        SpectrogramWindow = SpectrogramWindow,
        SpectrogramOverlap = SpectrogramOverlap,
        SpectrogramLogFrequency = SpectrogramLogFrequency,
        NormalizationTargetLufs = NormalizationTargetLufs,
        TruePeakCeilingDbtp = TruePeakCeilingDbtp,
        RecordingFolder = string.IsNullOrWhiteSpace(RecordingFolder) ? null : RecordingFolder,
    };

    private void Apply(AppSettings s)
    {
        A4 = s.A4;
        Transposition = s.Transposition;
        NoiseGateDbfs = s.NoiseGateDbfs;
        CaptureDeviceId = s.CaptureDeviceId;
        RenderDeviceId = s.RenderDeviceId;
        MetronomeBpm = s.MetronomeBpm;
        MetronomeBeatsPerBar = s.MetronomeBeatsPerBar;
        SpectrogramWindow = s.SpectrogramWindow;
        SpectrogramOverlap = s.SpectrogramOverlap;
        SpectrogramLogFrequency = s.SpectrogramLogFrequency;
        NormalizationTargetLufs = s.NormalizationTargetLufs;
        TruePeakCeilingDbtp = s.TruePeakCeilingDbtp;
        RecordingFolder = s.RecordingFolder ?? string.Empty;
    }

    private void Fill(ObservableCollection<AudioDeviceInfo> target, AudioDeviceKind kind)
    {
        target.Clear();
        target.Add(new AudioDeviceInfo(AudioDeviceInfo.DefaultDeviceId, "系統預設裝置", kind, true));
        foreach (var d in _devices.GetDevices(kind))
        {
            target.Add(d);
        }
    }
}
