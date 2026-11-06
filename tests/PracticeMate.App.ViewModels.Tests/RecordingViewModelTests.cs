using PracticeMate.Core.IO.Wav;

namespace PracticeMate.App.ViewModels.Tests;

public sealed class RecordingViewModelTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly RecordingViewModel _vm;

    public RecordingViewModelTests() => _vm = new RecordingViewModel(_env.Input, _env.Settings, _env.Dispatcher, _env.Time);

    public void Dispose()
    {
        _vm.Dispose();
        _env.Dispose();
    }

    private void Push(int blocks, float value = 0.25f)
    {
        var block = Enumerable.Repeat(value, 480).ToArray();
        for (var i = 0; i < blocks; i++)
        {
            _env.Audio.LastCapture!.Push(block);
            _env.Input.Pump();
            _env.Time.Advance(TimeSpan.FromMilliseconds(10));
        }
    }

    [Fact]
    public async Task Start_switches_to_recording_with_timestamped_file_name()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(RecordingState.Recording, _vm.State);
        Assert.True(_vm.IsRecording);
        Assert.False(_vm.StartCommand.CanExecute(null));
        Assert.True(_vm.StopCommand.CanExecute(null));
        Assert.Equal(Path.Combine(_env.Folder, "20261106-193005.wav"), _vm.LastRecordingPath);
    }

    [Fact]
    public async Task Meters_and_elapsed_time_update()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Push(150); // 1.5 秒
        Assert.Equal("0:01", _vm.ElapsedText);
        Assert.Equal(20 * Math.Log10(0.25), _vm.PeakDbfs, 3);
        Assert.Equal((20 * Math.Log10(0.25) + 60) / 60, _vm.PeakMeterFraction, 3);
    }

    [Fact]
    public async Task Overflow_is_counted_and_displayed()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        var block = new float[480];
        for (var i = 0; i < 400; i++) // 4 秒資料、消費者不 Pump → 超過 2 秒緩衝
        {
            _env.Audio.LastCapture!.Push(block);
        }
        _env.Time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(_vm.OverflowCount > 0);
        Assert.Equal(_env.Input.Session!.OverflowCount, _vm.OverflowCount);
        Assert.Equal(_vm.OverflowCount * 480, _vm.DroppedFrames);
    }

    [Fact]
    public async Task Stop_saves_a_readable_file()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Push(100);
        await _vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(RecordingState.Stopped, _vm.State);
        Assert.Contains("已保存", _vm.StatusMessage, StringComparison.Ordinal);
        var r = WavReader.Read(_vm.LastRecordingPath!);
        Assert.Empty(r.Warnings);
        Assert.Equal(48000, r.Audio.FrameCount);
        Assert.True(_vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Device_removed_stops_and_keeps_recording()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Push(50);
        _env.Audio.LastCapture!.SimulateDeviceRemoved();
        _env.Input.Pump();
        Assert.Equal(RecordingState.Stopped, _vm.State);
        Assert.Contains("移除", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(24000, WavReader.Read(_vm.LastRecordingPath!).Audio.FrameCount);
    }

    [Fact]
    public async Task Default_device_change_stops_and_explains()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Push(10);
        _env.Audio.LastCapture!.SimulateDefaultDeviceChanged();
        _env.Input.Pump();
        Assert.Equal(RecordingState.Stopped, _vm.State);
        Assert.Contains("預設", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_request_carries_last_recording_path()
    {
        string? requested = null;
        _vm.AnalyzeRequested += (_, p) => requested = p;
        await _vm.StartCommand.ExecuteAsync(null);
        Push(10);
        _vm.AnalyzeCommand.Execute(null);
        Assert.Null(requested); // 錄音中不可分析
        await _vm.StopCommand.ExecuteAsync(null);
        _vm.AnalyzeCommand.Execute(null);
        Assert.Equal(_vm.LastRecordingPath, requested);
    }

    [Fact]
    public async Task Start_failure_is_reported()
    {
        _env.Settings.RecordingFolder = Path.Combine(_env.Folder, "existing.wav");
        await File.WriteAllBytesAsync(Path.Combine(_env.Folder, "existing.wav"), [1]); // 資料夾路徑其實是檔案
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(RecordingState.Failed, _vm.State);
        Assert.Contains("無法開始錄音", _vm.StatusMessage, StringComparison.Ordinal);
    }
}
