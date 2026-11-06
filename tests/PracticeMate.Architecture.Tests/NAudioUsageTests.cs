using PracticeMate.Audio.Windows;

namespace PracticeMate.Architecture.Tests;

/// <summary>Audio.Windows 只能使用 NAudio 的裝置列舉、擷取與輸出功能（不得使用 DSP、重新取樣、檔案解碼）。</summary>
public class NAudioUsageTests
{
    private static readonly HashSet<string> AllowedNAudioTypes = new(StringComparer.Ordinal)
    {
        // 裝置列舉與通知
        "NAudio.CoreAudioApi.MMDeviceEnumerator",
        "NAudio.CoreAudioApi.MMDevice",
        "NAudio.CoreAudioApi.MMDeviceCollection",
        "NAudio.CoreAudioApi.DataFlow",
        "NAudio.CoreAudioApi.Role",
        "NAudio.CoreAudioApi.DeviceState",
        "NAudio.CoreAudioApi.AudioClientShareMode",
        "NAudio.CoreAudioApi.AudioClient",
        "NAudio.CoreAudioApi.PropertyKey",
        "NAudio.CoreAudioApi.Interfaces.IMMNotificationClient",
        // 擷取與輸出
        "NAudio.CoreAudioApi.WasapiCapture",
        "NAudio.Wave.WasapiOut",
        "NAudio.Wave.WaveInEventArgs",
        "NAudio.Wave.StoppedEventArgs",
        "NAudio.Wave.PlaybackState",
        "NAudio.CoreAudioApi.CaptureState",
        "NAudio.Wave.IWaveProvider",
        "NAudio.Wave.IWavePlayer",
        "NAudio.Wave.IWaveIn",
        // 格式描述（只讀欄位，不做轉換）
        "NAudio.Wave.WaveFormat",
        "NAudio.Wave.WaveFormatExtensible",
        "NAudio.Wave.WaveFormatEncoding",
    };

    [Fact]
    public void Audio_Windows_uses_only_allowed_NAudio_types()
    {
        var types = AssemblyInspector.ReferencedTypes(typeof(AudioWindowsAssembly).Assembly);
        var violations = types
            .Where(t => t.Assembly.StartsWith("NAudio", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Where(n => !AllowedNAudioTypes.Contains(n))
            .Distinct()
            .ToList();
        Assert.True(violations.Count == 0, $"使用了不允許的 NAudio 型別：{string.Join(", ", violations)}");
    }
}
