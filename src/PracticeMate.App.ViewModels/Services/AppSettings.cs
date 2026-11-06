using System.Text.Json;
using PracticeMate.Core.Devices;
using PracticeMate.Core.Music;

namespace PracticeMate.App.ViewModels.Services;

/// <summary>使用者設定（存於 %LocalAppData%\PracticeMate\settings.json）。</summary>
public sealed record AppSettings
{
    public double A4 { get; init; } = NoteMath.DefaultA4;
    public Transposition Transposition { get; init; } = Transposition.C;
    public double NoiseGateDbfs { get; init; } = -50;
    public string CaptureDeviceId { get; init; } = AudioDeviceInfo.DefaultDeviceId;
    public string RenderDeviceId { get; init; } = AudioDeviceInfo.DefaultDeviceId;
    public int MetronomeBpm { get; init; } = 90;
    public int MetronomeBeatsPerBar { get; init; } = 4;
    public int SpectrogramWindow { get; init; } = 2048;
    public double SpectrogramOverlap { get; init; } = 0.75;
    public bool SpectrogramLogFrequency { get; init; } = true;
    public double NormalizationTargetLufs { get; init; } = -16;
    public double TruePeakCeilingDbtp { get; init; } = -1;
    public string? RecordingFolder { get; init; }
}

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>JSON 設定檔。讀取失敗（不存在或損毀）時回傳預設值並記錄原因，不讓程式無法啟動。</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>最近一次載入失敗的原因（給 UI 顯示）。</summary>
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            LastLoadError = $"設定檔格式錯誤，已改用預設值：{ex.Message}";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}
