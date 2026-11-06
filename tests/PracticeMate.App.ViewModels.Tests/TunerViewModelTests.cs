using PracticeMate.App.ViewModels.Services;
using PracticeMate.Core.Music;
using PracticeMate.Core.Tuner;
using PracticeMate.Testing.Reference;

namespace PracticeMate.App.ViewModels.Tests;

public sealed class TunerViewModelTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly TunerViewModel _vm;

    public TunerViewModelTests() => _vm = new TunerViewModel(_env.Input, _env.Settings, _env.Dispatcher, _env.Time);

    public void Dispose()
    {
        _vm.Dispose();
        _env.Dispose();
    }

    private static TunerReading Reading(double midi, Transposition t = Transposition.C)
    {
        var concert = NoteMath.FromMidi(midi);
        return new TunerReading(true, NoteMath.MidiToFrequency(midi), concert, NoteMath.ToWritten(concert, t), 0.95, -20, 4800);
    }

    /// <summary>以 10 ms 區塊送入 seconds 秒的訊號，每區塊 Pump 並推進假時間 10 ms。</summary>
    private void Play(float[] signal)
    {
        const int block = 480;
        for (var pos = 0; pos + block <= signal.Length; pos += block)
        {
            _env.Audio.LastCapture!.Push(signal.AsSpan(pos, block));
            _env.Input.Pump();
            _env.Time.Advance(TimeSpan.FromMilliseconds(10));
        }
    }

    [Fact]
    public void Initially_shows_dash()
    {
        Assert.Equal("—", _vm.NoteText);
        Assert.Equal("—", _vm.CentsText);
        Assert.False(_vm.HasPitch);
    }

    [Fact]
    public void Shows_note_cents_and_needle()
    {
        _vm.Show(Reading(69.12));
        Assert.Equal("A4", _vm.NoteText);
        Assert.Equal("+12", _vm.CentsText);
        Assert.Equal(12 / 50.0 * 45, _vm.NeedleAngle, 6);
        Assert.False(_vm.IsInTune);
        _vm.Show(Reading(68.97));
        Assert.Equal("−3", _vm.CentsText);
        Assert.True(_vm.IsInTune);
    }

    [Fact]
    public void Transposed_reading_shows_written_and_concert_pitch()
    {
        _vm.Show(Reading(58, Transposition.BFlat));
        Assert.Equal("C4", _vm.NoteText);
        Assert.Equal("實際音高 A#3", _vm.ConcertText);
    }

    [Fact]
    public void No_pitch_resets_to_dash()
    {
        _vm.Show(Reading(60));
        _vm.Show(TunerReading.Empty);
        Assert.Equal("—", _vm.NoteText);
        Assert.Equal(0, _vm.NeedleAngle);
    }

    [Theory]
    [InlineData(0.4, "0")]
    [InlineData(-0.5, "−1")]
    [InlineData(49.6, "+50")]
    [InlineData(-12.0, "−12")]
    public void Cents_formatting(double cents, string text)
    {
        Assert.Equal(text, TunerViewModel.FormatCents(cents));
    }

    [Fact]
    public async Task Listening_updates_display_from_microphone()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.True(_vm.IsListening);
        Assert.False(_vm.StartCommand.CanExecute(null));
        Play(SignalGenerator.ToFloat(SignalGenerator.Tone(48000, 440, 24000, HarmonicSpectrum.Sawtooth, 0.3)));
        Assert.Equal("A4", _vm.NoteText);
        Assert.Equal("0", _vm.CentsText);
    }

    [Fact]
    public async Task Display_refreshes_at_least_20_times_per_second()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Play(SignalGenerator.ToFloat(SignalGenerator.Tone(48000, 330, 48000, HarmonicSpectrum.Sawtooth, 0.3)));
        Assert.True(_vm.DisplayUpdates >= 20, $"一秒內更新 {_vm.DisplayUpdates} 次");
    }

    [Fact]
    public async Task Silence_shows_dash_while_listening()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Play(new float[24000]);
        Assert.Equal("—", _vm.NoteText);
    }

    [Fact]
    public async Task Transposition_setting_changes_display_while_listening()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        _env.Settings.Transposition = Transposition.EFlat;
        _env.Settings.SaveCommand.Execute(null);
        Play(SignalGenerator.ToFloat(SignalGenerator.Tone(48000, NoteMath.MidiToFrequency(63), 24000, HarmonicSpectrum.Sawtooth, 0.3)));
        Assert.Equal("C5", _vm.NoteText);
    }

    [Fact]
    public async Task Device_removal_stops_listening_with_message()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        _env.Audio.LastCapture!.SimulateDeviceRemoved();
        _env.Input.Pump();
        Assert.False(_vm.IsListening);
        Assert.Contains("移除", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(_vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_returns_to_dash()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Play(SignalGenerator.ToFloat(SignalGenerator.Tone(48000, 440, 24000, HarmonicSpectrum.Sawtooth, 0.3)));
        await _vm.StopCommand.ExecuteAsync(null);
        Assert.False(_vm.IsListening);
        Assert.Equal("—", _vm.NoteText);
    }

    [Fact]
    public async Task Uses_capture_device_from_settings()
    {
        _env.Settings.CaptureDeviceId = "mic-1";
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("mic-1", _env.Audio.RequestedIds.Single());
    }
}
