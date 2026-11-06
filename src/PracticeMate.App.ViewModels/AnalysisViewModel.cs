using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Analysis;
using PracticeMate.Core.Analysis.Beats;
using PracticeMate.Core.Analysis.Loudness;
using PracticeMate.Core.Audio;
using PracticeMate.Core.IO.Wav;
using PracticeMate.Core.Reports;

namespace PracticeMate.App.ViewModels;

public sealed record NoteRow(int Number, double StartSeconds, double EndSeconds, string Name, double Cents)
{
    public string CentsText => TunerViewModel.FormatCents(Cents);
}

public sealed record BeatRow(long BeatNumber, double BeatSeconds, double DeviationMs)
{
    public string Kind => DeviationMs > 0 ? "拖拍" : DeviationMs < 0 ? "搶拍" : "準拍";
}

/// <summary>
/// 分析結果頁：載入 WAV（含檔頭回復提示）→ 背景分析（進度、取消）→ 音準報告、節拍報告、響度報告、
/// 波形與頻譜圖（共用 <see cref="Timeline"/>）。報告可匯出 CSV 與 PNG；可正規化並輸出新檔。
/// 取消或失敗時保留上一次的完整結果，不顯示半套結果。
/// </summary>
public sealed partial class AnalysisViewModel : ObservableObject, IDisposable
{
    private readonly IFileDialogService _dialogs;
    private readonly IChartImageExporter _images;
    private readonly SettingsViewModel _settings;
    private CancellationTokenSource? _cts;
    private AudioData? _original;

    public AnalysisViewModel(IFileDialogService dialogs, IChartImageExporter images, SettingsViewModel settings)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bpm = settings.MetronomeBpm;
    }

    public TimelineViewModel Timeline { get; } = new();

    public void Dispose() => _cts?.Dispose();

    public ObservableCollection<NoteRow> Notes { get; } = [];

    public ObservableCollection<BeatRow> Beats { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(ExportNotesCsvCommand), nameof(ExportBeatsCsvCommand), nameof(ExportLoudnessCsvCommand), nameof(ExportPngCommand), nameof(NormalizeCommand))]
    private AnalysisResult? _result;

    [ObservableProperty]
    private BeatReport? _beatReport;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(CancelCommand), nameof(NormalizeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string? _progressStage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _fileName;

    [ObservableProperty]
    private double _bpm;

    [ObservableProperty]
    private double _firstBeatSeconds;

    public bool HasResult => Result is not null;

    // ---- 響度報告 ----
    public string IntegratedText => Result is null ? TunerViewModel.NoValue : Format(Result.Loudness.IntegratedLufs, "LUFS");

    public string LraText => Result is null ? TunerViewModel.NoValue : Format(Result.Loudness.LoudnessRangeLu, "LU");

    public string MaxMomentaryText => Result is null ? TunerViewModel.NoValue : Format(Result.Loudness.MaxMomentaryLufs, "LUFS");

    public string MaxShortTermText => Result is null ? TunerViewModel.NoValue : Format(Result.Loudness.MaxShortTermLufs, "LUFS");

    public string TruePeakText => Result is null ? TunerViewModel.NoValue : Format(Result.TruePeak.Dbtp, "dBTP");

    public string NormalizationPreview =>
        Result is null ? string.Empty : Normalizer.Plan(Result.Loudness.IntegratedLufs, Result.TruePeak.Dbtp, _settings.NormalizationRequest).Message;

    // ---- 節拍報告摘要 ----
    public string BeatSummary => BeatReport is null
        ? TunerViewModel.NoValue
        : string.Create(CultureInfo.InvariantCulture,
            $"平均 {CsvReport.Number(BeatReport.MeanMs, "+0.0;-0.0;0.0")} ms、標準差 {CsvReport.Number(BeatReport.StandardDeviationMs, "F1")} ms、{BeatReport.Trend.Description}（拍點外 {BeatReport.OffBeatOnsets.Count} 個）");

    public static string Format(double value, string unit) =>
        double.IsNaN(value) || double.IsInfinity(value) ? TunerViewModel.NoValue : value.ToString("F1", CultureInfo.InvariantCulture) + " " + unit;

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task OpenAsync()
    {
        if (_dialogs.OpenWav() is { } path)
        {
            await LoadAsync(path).ConfigureAwait(true);
        }
    }

    private bool IsNotBusy() => !IsBusy;

    /// <summary>載入並分析。可從錄音頁直接呼叫。</summary>
    public async Task LoadAsync(string path)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        ProgressPercent = 0;
        ProgressStage = "讀取檔案";
        StatusMessage = null;
        try
        {
            var read = await Task.Run(() => WavReader.Read(path), token).ConfigureAwait(true);
            var progress = new Progress<AnalysisProgress>(p =>
            {
                ProgressPercent = p.Fraction * 100;
                ProgressStage = p.Stage;
            });
            var options = new AnalysisOptions { A4 = _settings.A4, Spectrogram = _settings.SpectrogramOptions };
            var result = await AnalysisPipeline.RunAsync(read.Audio, options, progress, token).ConfigureAwait(true);

            // 全部完成才替換畫面上的結果
            _original = read.Audio;
            FileName = Path.GetFileName(path);
            Warnings.Clear();
            foreach (var w in read.Warnings.Concat(result.Warnings))
            {
                Warnings.Add(w);
            }
            Show(result);
            StatusMessage = $"分析完成：{FileName}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消分析，畫面保留先前的結果。";
        }
        catch (UnsupportedWavFormatException ex)
        {
            StatusMessage = $"無法開啟檔案：{ex.Message}";
        }
        catch (IOException ex)
        {
            StatusMessage = $"讀取檔案失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressStage = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>依目前的 BPM 與第一拍時刻重算節拍報告（不需重新分析）。</summary>
    public void RecomputeBeats()
    {
        Beats.Clear();
        if (Result is null || Bpm <= 0)
        {
            BeatReport = null;
            return;
        }
        var first = (long)Math.Round(FirstBeatSeconds * Result.AnalysisSampleRate);
        BeatReport = BeatAligner.Align(Result.Onsets, Result.AnalysisSampleRate, Bpm, first);
        foreach (var b in BeatReport.Beats)
        {
            Beats.Add(new BeatRow(b.BeatIndex + 1, (double)b.BeatSample / Result.AnalysisSampleRate, b.DeviationMs));
        }
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void ExportNotesCsv() => ExportCsv("音準報告.csv", () => CsvReport.Notes(Result!.Notes, Result.AnalysisSampleRate));

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void ExportBeatsCsv() => ExportCsv("節拍報告.csv", () => CsvReport.Beats(BeatReport!, Result!.AnalysisSampleRate));

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void ExportLoudnessCsv() => ExportCsv("響度報告.csv", () => CsvReport.Loudness(Result!.Loudness, Result.TruePeak));

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void ExportPng(ChartKind chart)
    {
        if (_dialogs.Save(ChartFileName(chart), "PNG 圖片 (*.png)|*.png") is not { } path)
        {
            return;
        }
        try
        {
            _images.ExportPng(chart, path);
            StatusMessage = $"已匯出 {Path.GetFileName(path)}";
        }
        catch (IOException ex)
        {
            StatusMessage = $"匯出失敗：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanNormalize))]
    private async Task NormalizeAsync()
    {
        var result = Result!;
        var plan = Normalizer.Plan(result.Loudness.IntegratedLufs, result.TruePeak.Dbtp, _settings.NormalizationRequest);
        if (!plan.CanNormalize)
        {
            StatusMessage = plan.Message;
            return;
        }
        var defaultName = Path.GetFileNameWithoutExtension(FileName ?? "錄音") + "-正規化.wav";
        if (_dialogs.Save(defaultName, "WAV 音訊 (*.wav)|*.wav") is not { } path)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var original = _original!;
            await Task.Run(() => Normalizer.Write(original, plan, path)).ConfigureAwait(true);
            StatusMessage = $"已輸出 {Path.GetFileName(path)}。{plan.Message}";
        }
        catch (IOException ex)
        {
            StatusMessage = $"輸出失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanNormalize() => HasResult && !IsBusy;

    partial void OnBpmChanged(double value) => RecomputeBeats();

    partial void OnFirstBeatSecondsChanged(double value) => RecomputeBeats();

    partial void OnResultChanged(AnalysisResult? value)
    {
        foreach (var name in new[] { nameof(IntegratedText), nameof(LraText), nameof(MaxMomentaryText), nameof(MaxShortTermText), nameof(TruePeakText), nameof(NormalizationPreview) })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnBeatReportChanged(BeatReport? value) => OnPropertyChanged(nameof(BeatSummary));

    private void Show(AnalysisResult result)
    {
        Result = result;
        Timeline.Reset(result.AnalysisFrameCount, result.AnalysisSampleRate);
        Notes.Clear();
        var i = 1;
        foreach (var n in result.Notes)
        {
            Notes.Add(new NoteRow(i++, (double)n.StartSample / result.AnalysisSampleRate, (double)n.EndSample / result.AnalysisSampleRate, n.Name, n.Cents));
        }
        // 第一拍預設為第一個起音
        var first = result.Onsets.Count > 0 ? (double)result.Onsets[0] / result.AnalysisSampleRate : 0;
        if (Math.Abs(first - FirstBeatSeconds) < 1e-12)
        {
            RecomputeBeats();
        }
        else
        {
            FirstBeatSeconds = first;
        }
    }

    private void ExportCsv(string defaultName, Func<byte[]> build)
    {
        if (_dialogs.Save(defaultName, "CSV 檔 (*.csv)|*.csv") is not { } path)
        {
            return;
        }
        try
        {
            File.WriteAllBytes(path, build());
            StatusMessage = $"已匯出 {Path.GetFileName(path)}";
        }
        catch (IOException ex)
        {
            StatusMessage = $"匯出失敗：{ex.Message}";
        }
    }

    private static string ChartFileName(ChartKind chart) => chart switch
    {
        ChartKind.Waveform => "波形.png",
        ChartKind.Spectrogram => "頻譜圖.png",
        ChartKind.NoteDeviation => "音準報告.png",
        ChartKind.BeatDeviation => "節拍報告.png",
        _ => "響度報告.png",
    };
}
