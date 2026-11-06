using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Metronome;
using PracticeMate.Core.Music;

namespace PracticeMate.App.ViewModels.Tests;

public sealed class MetronomeViewModelTests : IDisposable
{
    private readonly TestEnvironment _env = new(new AppSettings { MetronomeBpm = 120, MetronomeBeatsPerBar = 4 });
    private readonly MetronomeViewModel _vm;

    public MetronomeViewModelTests() => _vm = new MetronomeViewModel(_env.Audio, _env.Settings, _env.Dispatcher, _env.Time);

    public void Dispose()
    {
        _vm.Dispose();
        _env.Dispose();
    }

    /// <summary>從輸出中找出點擊聲起點（前一個取樣為 0、之後連續非 0）。</summary>
    private static List<long> ClickStarts(float[] interleaved, int channels, long offset)
    {
        var starts = new List<long>();
        var frames = interleaved.Length / channels;
        for (var i = 1; i < frames - 1; i++)
        {
            if (interleaved[(i - 1) * channels] == 0 && interleaved[i * channels] == 0 && interleaved[(i + 1) * channels] != 0)
            {
                starts.Add(offset + i);
            }
        }
        return starts;
    }

    [Fact]
    public void Loads_defaults_from_settings()
    {
        Assert.Equal(120, _vm.Bpm);
        Assert.Equal(4, _vm.Accents.Count);
        Assert.Equal([true, false, false, false], _vm.Accents.Select(a => a.IsAccent));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(301)]
    public void Invalid_bpm_shows_error_and_has_no_settings(int bpm)
    {
        _vm.Bpm = bpm;
        Assert.NotNull(_vm.BpmError);
        Assert.Null(_vm.CurrentSettings);
    }

    [Fact]
    public void Changing_beats_per_bar_resizes_accents_and_keeps_existing()
    {
        _vm.Accents[2].IsAccent = true;
        _vm.BeatsPerBar = 6;
        Assert.Equal([true, false, true, false, false, false], _vm.Accents.Select(a => a.IsAccent));
        _vm.BeatsPerBar = 2;
        Assert.Equal([true, false], _vm.Accents.Select(a => a.IsAccent));
        _vm.BeatsPerBar = 13;
        Assert.NotNull(_vm.BeatsPerBarError);
        Assert.Equal(2, _vm.Accents.Count);
    }

    [Fact]
    public void Start_opens_render_device_and_produces_clicks()
    {
        _vm.StartCommand.Execute(null);
        Assert.True(_vm.IsRunning);
        Assert.False(_vm.StartCommand.CanExecute(null));
        Assert.True(_vm.StopCommand.CanExecute(null));
        var out1 = _env.Audio.LastRender!.RenderBlock(48000);
        Assert.Equal([24000L], ClickStarts(out1, 2, 0)); // 第 0 拍在取樣 0（不計入），第 1 拍在 0.5 s
        Assert.NotEqual(0f, out1[2]);
    }

    [Fact]
    public void Tempo_change_while_running_applies_on_next_bar()
    {
        _vm.StartCommand.Execute(null);
        var device = _env.Audio.LastRender!;
        device.RenderBlock(12000);           // 第 0 拍期間
        _vm.Bpm = 60;
        var rest = device.RenderBlock(48000 * 4);
        var starts = ClickStarts(rest, 2, 12000);
        // 120 BPM 的第 1–3 拍（24000、48000、72000），第 4 拍（新小節）96000，之後每 48000
        Assert.Equal([24000L, 48000L, 72000L, 96000L, 144000L, 192000L], starts.Take(6));
    }

    [Fact]
    public void Current_beat_follows_renderer()
    {
        _vm.StartCommand.Execute(null);
        _env.Audio.LastRender!.RenderBlock(30000); // 已排入第 1 拍
        _env.Time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, _vm.CurrentBeat);
        Assert.True(_vm.Accents[1].IsCurrent);
    }

    [Fact]
    public void Stop_releases_device()
    {
        _vm.StartCommand.Execute(null);
        var device = _env.Audio.LastRender!;
        _vm.StopCommand.Execute(null);
        Assert.False(_vm.IsRunning);
        Assert.True(device.IsDisposed);
        Assert.Equal(-1, _vm.CurrentBeat);
    }

    [Fact]
    public void Device_removal_stops_with_message()
    {
        _vm.StartCommand.Execute(null);
        _env.Audio.LastRender!.SimulateDeviceRemoved();
        Assert.False(_vm.IsRunning);
        Assert.Contains("移除", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_settings_reflect_accent_pattern()
    {
        _vm.Accents[3].IsAccent = true;
        Assert.Equal(new MetronomeSettings(120, 4, [true, false, false, true]), _vm.CurrentSettings);
    }
}

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Theory]
    [InlineData(414.0)]
    [InlineData(467.0)]
    public void Invalid_a4_blocks_save(double a4)
    {
        _env.Settings.A4 = a4;
        Assert.NotNull(_env.Settings.A4Error);
        Assert.False(_env.Settings.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Save_persists_and_notifies()
    {
        var changed = 0;
        _env.Settings.Changed += (_, _) => changed++;
        _env.Settings.A4 = 442;
        _env.Settings.Transposition = Transposition.F;
        _env.Settings.SaveCommand.Execute(null);
        Assert.Equal(1, changed);
        Assert.Equal(442, _env.Store.Current.A4);
        Assert.Equal(Transposition.F, _env.Store.Current.Transposition);
        Assert.Equal(442, _env.Settings.TunerSettings.A4);
    }

    [Fact]
    public void Device_lists_start_with_system_default()
    {
        Assert.Equal("系統預設裝置", _env.Settings.CaptureDevices[0].Name);
        Assert.Equal("USB 麥克風", _env.Settings.CaptureDevices[1].Name);
        Assert.Equal(2, _env.Settings.RenderDevices.Count);
    }

    [Fact]
    public void Overlap_and_normalization_are_validated()
    {
        _env.Settings.SpectrogramOverlap = 0.99;
        Assert.NotNull(_env.Settings.OverlapError);
        _env.Settings.SpectrogramOverlap = 0.5;
        _env.Settings.TruePeakCeilingDbtp = 3;
        Assert.NotNull(_env.Settings.NormalizationError);
        Assert.False(_env.Settings.IsValid);
    }

    [Fact]
    public void Empty_recording_folder_uses_default()
    {
        _env.Settings.RecordingFolder = "";
        Assert.Equal(_env.Folder, _env.Settings.EffectiveRecordingFolder);
        _env.Settings.RecordingFolder = @"D:\錄音";
        Assert.Equal(@"D:\錄音", _env.Settings.EffectiveRecordingFolder);
    }

    [Fact]
    public void Json_store_round_trips_and_survives_corruption()
    {
        var path = Path.Combine(_env.Folder, "sub", "settings.json");
        var store = new JsonSettingsStore(path);
        Assert.Equal(new AppSettings(), store.Load());
        store.Save(new AppSettings { A4 = 443, SpectrogramLogFrequency = false });
        Assert.Equal(443, store.Load().A4);
        File.WriteAllText(path, "{ 這不是 JSON");
        Assert.Equal(new AppSettings(), store.Load());
        Assert.Contains("格式錯誤", store.LastLoadError, StringComparison.Ordinal);
    }
}
