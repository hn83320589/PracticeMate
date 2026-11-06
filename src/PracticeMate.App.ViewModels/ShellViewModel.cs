using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PracticeMate.App.ViewModels;

public enum Page
{
    Tuner,
    Metronome,
    Recording,
    Analysis,
    Loudness,
    Settings,
}

/// <summary>主視窗：頁面導覽，並把錄音頁的「分析這段錄音」轉到分析頁。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(TunerViewModel tuner, MetronomeViewModel metronome, RecordingViewModel recording,
        AnalysisViewModel analysis, SettingsViewModel settings)
    {
        Tuner = tuner ?? throw new ArgumentNullException(nameof(tuner));
        Metronome = metronome ?? throw new ArgumentNullException(nameof(metronome));
        Recording = recording ?? throw new ArgumentNullException(nameof(recording));
        Analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Recording.AnalyzeRequested += async (_, path) =>
        {
            CurrentPage = Page.Analysis;
            await Analysis.LoadAsync(path).ConfigureAwait(true);
        };
    }

    public TunerViewModel Tuner { get; }
    public MetronomeViewModel Metronome { get; }
    public RecordingViewModel Recording { get; }
    public AnalysisViewModel Analysis { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private Page _currentPage = Page.Tuner;

    [RelayCommand]
    private void Navigate(Page page) => CurrentPage = page;
}
