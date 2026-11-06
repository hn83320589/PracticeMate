using Microsoft.Extensions.Time.Testing;
using PracticeMate.App.ViewModels;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Recording;
using PracticeMate.Testing.Fakes;

namespace PracticeMate.App.ViewModels.Tests;

internal sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public int SaveCount { get; private set; }

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        SaveCount++;
    }
}

internal sealed class FakeDialogs : IFileDialogService
{
    public string? OpenResult { get; set; }

    public Func<string, string?> SaveResult { get; set; } = _ => null;

    public List<string> SaveRequests { get; } = [];

    public string? OpenWav() => OpenResult;

    public string? Save(string defaultFileName, string filter)
    {
        SaveRequests.Add(defaultFileName);
        return SaveResult(defaultFileName);
    }
}

internal sealed class FakeImageExporter : IChartImageExporter
{
    public List<(ChartKind Chart, string Path)> Exports { get; } = [];

    public void ExportPng(ChartKind chart, string path)
    {
        Exports.Add((chart, path));
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
    }
}

/// <summary>組好一套可測試的 ViewModel 環境（假裝置、假時間、同步 dispatcher、手動 Pump）。</summary>
internal sealed class TestEnvironment : IDisposable
{
    public TestEnvironment(AppSettings? settings = null)
    {
        Folder = Path.Combine(Path.GetTempPath(), "pm-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Store = new InMemorySettingsStore(settings);
        Settings = new SettingsViewModel(Store, Audio, Folder);
        Input = new AudioInputController(Audio, Time, backgroundPump: false);
    }

    public string Folder { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 6, 19, 30, 5, TimeSpan.FromHours(8)));

    public FakeAudioSystem Audio { get; } = new();

    public ImmediateDispatcher Dispatcher { get; } = new();

    public InMemorySettingsStore Store { get; }

    public SettingsViewModel Settings { get; }

    public AudioInputController Input { get; }

    public FakeDialogs Dialogs { get; } = new();

    public FakeImageExporter Images { get; } = new();

    public void Dispose()
    {
        Input.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // 測試結束時檔案仍被系統短暫鎖住；暫存資料夾會由系統清理，不影響測試結果
        }
    }
}
