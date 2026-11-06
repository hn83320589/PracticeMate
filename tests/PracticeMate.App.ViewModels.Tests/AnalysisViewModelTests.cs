using System.Text;
using PracticeMate.App.ViewModels.Services;
using PracticeMate.Testing.Reference;

namespace PracticeMate.App.ViewModels.Tests;

public sealed class AnalysisViewModelTests : IDisposable
{
    private readonly TestEnvironment _env = new(new AppSettings { MetronomeBpm = 120 });
    private readonly AnalysisViewModel _vm;

    public AnalysisViewModelTests() => _vm = new AnalysisViewModel(_env.Dialogs, _env.Images, _env.Settings);

    public void Dispose()
    {
        _vm.Dispose();
        _env.Dispose();
    }

    /// <summary>120 BPM、第一拍 0.5 s 的 8 個音（音階、刻意走音），寫成 32 位元浮點 WAV。</summary>
    private string WriteExercise(string name, double seconds = 0)
    {
        var notes = Enumerable.Range(0, 8).Select(i => new SynthNote(60 + (2 * i), 0.5 + (i * 0.5), 0.4, i * 3)).ToList();
        if (seconds > 0)
        {
            for (var t = 4.5; t < seconds; t += 0.5)
            {
                notes.Add(new SynthNote(67, t, 0.4));
            }
        }
        var x = SignalGenerator.ToFloat(PerformanceSynth.Render(notes, new PerformanceOptions { SnrDb = 40 }).Signal);
        var path = Path.Combine(_env.Folder, name);
        File.WriteAllBytes(path, RefWavBuilder.IeeeFloat(48000, 1, 32).WithFloat32Samples(x).Build());
        return path;
    }

    [Fact]
    public async Task Load_shows_note_beat_and_loudness_reports()
    {
        await _vm.LoadAsync(WriteExercise("a.wav"));
        Assert.True(_vm.HasResult);
        Assert.Equal(8, _vm.Notes.Count);
        Assert.Equal("C4", _vm.Notes[0].Name);
        Assert.Equal(8, _vm.Beats.Count);
        Assert.Equal(0.5, _vm.FirstBeatSeconds, 2);
        Assert.Contains("速度穩定", _vm.BeatSummary, StringComparison.Ordinal);
        Assert.EndsWith("LUFS", _vm.IntegratedText, StringComparison.Ordinal);
        Assert.EndsWith("dBTP", _vm.TruePeakText, StringComparison.Ordinal);
        Assert.Equal(_vm.Result!.AnalysisFrameCount, _vm.Timeline.TotalSamples);
        Assert.Equal(0, _vm.Timeline.StartSample);
        Assert.Contains("分析完成", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task Changing_bpm_recomputes_beat_report_without_reanalysis()
    {
        await _vm.LoadAsync(WriteExercise("b.wav"));
        var result = _vm.Result;
        _vm.Bpm = 60; // 只有每隔一個音落在拍上
        Assert.Same(result, _vm.Result);
        Assert.Equal(4, _vm.Beats.Count);
        Assert.Equal(4, _vm.BeatReport!.OffBeatOnsets.Count);
    }

    [Fact]
    public async Task Cancel_keeps_previous_complete_result()
    {
        await _vm.LoadAsync(WriteExercise("first.wav"));
        var previous = _vm.Result;
        var second = WriteExercise("long.wav", seconds: 60);
        var task = _vm.LoadAsync(second);
        _vm.CancelCommand.Execute(null);
        await task;
        Assert.Same(previous, _vm.Result);
        Assert.Equal("first.wav", _vm.FileName);
        Assert.Contains("已取消", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task Unsupported_file_shows_chinese_reason()
    {
        var path = Path.Combine(_env.Folder, "bad.wav");
        await File.WriteAllBytesAsync(path, new RefWavBuilder { FormatTag = RefWavBuilder.FormatMuLaw, BitsPerSample = 8 }.WithIntSamples([1, 2]).Build());
        await _vm.LoadAsync(path);
        Assert.False(_vm.HasResult);
        Assert.Contains("μ-law", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovered_header_warning_is_shown()
    {
        var path = WriteExercise("crash.wav");
        var bytes = await File.ReadAllBytesAsync(path);
        var dataChunk = bytes.AsSpan().IndexOf("data"u8);
        BitConverter.GetBytes(0u).CopyTo(bytes, dataChunk + 4); // data 長度清 0（模擬錄音當機）
        await File.WriteAllBytesAsync(path, bytes);
        await _vm.LoadAsync(path);
        Assert.Contains(_vm.Warnings, w => w.Contains("為 0", StringComparison.Ordinal));
        Assert.Equal(8, _vm.Notes.Count);
    }

    [Fact]
    public async Task Export_notes_csv_is_utf8_with_bom()
    {
        await _vm.LoadAsync(WriteExercise("c.wav"));
        var target = Path.Combine(_env.Folder, "notes.csv");
        _env.Dialogs.SaveResult = _ => target;
        _vm.ExportNotesCsvCommand.Execute(null);
        var bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("序號,開始(秒),結束(秒),音名,MIDI,音準偏差(音分)", lines[0]);
        Assert.Equal(9, lines.Length);
        Assert.Contains("已匯出", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_beat_and_loudness_csv()
    {
        await _vm.LoadAsync(WriteExercise("d.wav"));
        _env.Dialogs.SaveResult = n => Path.Combine(_env.Folder, n);
        _vm.ExportBeatsCsvCommand.Execute(null);
        _vm.ExportLoudnessCsvCommand.Execute(null);
        var beats = await File.ReadAllTextAsync(Path.Combine(_env.Folder, "節拍報告.csv"), Encoding.UTF8);
        Assert.StartsWith("拍序,", beats.TrimStart('﻿'), StringComparison.Ordinal);
        Assert.Contains("趨勢,速度穩定", beats, StringComparison.Ordinal);
        var loud = await File.ReadAllTextAsync(Path.Combine(_env.Folder, "響度報告.csv"), Encoding.UTF8);
        Assert.Contains("整合響度(LUFS),", loud, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChartKind.Waveform, "波形.png")]
    [InlineData(ChartKind.Spectrogram, "頻譜圖.png")]
    [InlineData(ChartKind.NoteDeviation, "音準報告.png")]
    [InlineData(ChartKind.BeatDeviation, "節拍報告.png")]
    public async Task Export_png_uses_chart_exporter(ChartKind chart, string defaultName)
    {
        await _vm.LoadAsync(WriteExercise("e.wav"));
        _env.Dialogs.SaveResult = n => Path.Combine(_env.Folder, n);
        _vm.ExportPngCommand.Execute(chart);
        Assert.Equal(defaultName, _env.Dialogs.SaveRequests.Last());
        Assert.Equal((chart, Path.Combine(_env.Folder, defaultName)), _env.Images.Exports.Single());
    }

    [Fact]
    public void Exports_are_disabled_without_result()
    {
        Assert.False(_vm.ExportNotesCsvCommand.CanExecute(null));
        Assert.False(_vm.ExportPngCommand.CanExecute(ChartKind.Waveform));
        Assert.False(_vm.NormalizeCommand.CanExecute(null));
        Assert.Equal("—", _vm.IntegratedText);
    }

    [Fact]
    public async Task Cancelled_save_dialog_writes_nothing()
    {
        await _vm.LoadAsync(WriteExercise("f.wav"));
        _env.Dialogs.SaveResult = _ => null;
        _vm.ExportNotesCsvCommand.Execute(null);
        Assert.DoesNotContain(Directory.GetFiles(_env.Folder), f => f.EndsWith(".csv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Normalize_writes_new_file_and_reports_plan()
    {
        await _vm.LoadAsync(WriteExercise("g.wav"));
        Assert.Contains("增益", _vm.NormalizationPreview, StringComparison.Ordinal);
        var target = Path.Combine(_env.Folder, "out.wav");
        _env.Dialogs.SaveResult = _ => target;
        await _vm.NormalizeCommand.ExecuteAsync(null);
        Assert.True(File.Exists(target));
        Assert.Contains("已輸出", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_command_uses_dialog()
    {
        _env.Dialogs.OpenResult = WriteExercise("h.wav");
        await _vm.OpenCommand.ExecuteAsync(null);
        Assert.Equal("h.wav", _vm.FileName);
    }
}
