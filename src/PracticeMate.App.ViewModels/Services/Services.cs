namespace PracticeMate.App.ViewModels.Services;

/// <summary>把動作切換到 UI 執行緒（App 以 Dispatcher 實作；測試以同步實作）。</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>同步執行（測試或沒有 UI 執行緒時使用）。</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

public interface IFileDialogService
{
    /// <summary>選擇要開啟的 WAV 檔；取消回傳 null。</summary>
    string? OpenWav();

    /// <param name="filter">例如「CSV 檔 (*.csv)|*.csv」。</param>
    string? Save(string defaultFileName, string filter);
}

/// <summary>可匯出成 PNG 的圖表。</summary>
public enum ChartKind
{
    Waveform,
    Spectrogram,
    NoteDeviation,
    BeatDeviation,
    Loudness,
}

/// <summary>把指定圖表畫成 PNG（App 以 RenderTargetBitmap 實作）。</summary>
public interface IChartImageExporter
{
    void ExportPng(ChartKind chart, string path);
}

/// <summary>錄音檔命名與存放位置。</summary>
public interface IRecordingLocation
{
    string Folder { get; }
}
