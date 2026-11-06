using PracticeMate.Core.Music;
using PracticeMate.Core.Tuner;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Tuner;

/// <summary>驗收 #8：以合成訊號逐區塊（10 ms）餵入。</summary>
public class TunerEngineTests
{
    private const int Fs = 48000;
    private const int Block = Fs / 100;

    private static List<TunerReading> Feed(TunerEngine engine, float[] signal, int block = Block)
    {
        var readings = new List<TunerReading>();
        engine.ReadingProduced += (_, r) => readings.Add(r);
        for (var pos = 0; pos + block <= signal.Length; pos += block)
        {
            engine.Consume(signal.AsSpan(pos, block), 1, pos);
        }
        return readings;
    }

    private static float[] Tone(double f, double seconds, HarmonicSpectrum? spectrum = null, double amp = 0.3) =>
        SignalGenerator.ToFloat(SignalGenerator.Tone(Fs, f, (int)(Fs * seconds), spectrum ?? HarmonicSpectrum.Sawtooth, amp));

    [Fact]
    public void Shows_note_and_cents_for_steady_tone()
    {
        var engine = new TunerEngine(Fs);
        var f = 440 * Math.Pow(2, 12.0 / 1200); // A4 + 12 音分
        var r = Feed(engine, Tone(f, 0.5)).Last();
        Assert.True(r.HasPitch);
        Assert.Equal("A4", r.Written.Name);
        Assert.Equal(12, r.Cents, 0);
    }

    [Fact]
    public void Silence_shows_dash()
    {
        var readings = Feed(new TunerEngine(Fs), new float[Fs]);
        Assert.NotEmpty(readings);
        Assert.All(readings, r => Assert.False(r.HasPitch));
    }

    [Fact]
    public void Tone_below_noise_gate_shows_dash()
    {
        var quiet = Tone(440, 0.5, HarmonicSpectrum.Pure, amp: 0.001); // 約 −63 dBFS RMS
        Assert.All(Feed(new TunerEngine(Fs), quiet), r => Assert.False(r.HasPitch));
    }

    [Fact]
    public void White_noise_never_shows_a_pitch()
    {
        var noise = SignalGenerator.ToFloat(SignalGenerator.WhiteNoise(new Random(5), 2 * Fs, 0.1));
        var readings = Feed(new TunerEngine(Fs), noise);
        Assert.True(readings.Count(r => r.HasPitch) == 0, $"白雜訊中有 {readings.Count(r => r.HasPitch)} 筆讀數顯示音高");
    }

    [Theory]
    [InlineData(440.0, 493.88)]
    [InlineData(220.0, 207.65)]
    [InlineData(110.0, 164.81)]
    [InlineData(880.0, 1760.0)]
    public void Latency_from_pitch_change_to_display_change_is_at_most_100ms(double from, double to)
    {
        var a = Tone(from, 0.5);
        var b = Tone(to, 0.5);
        var signal = a.Concat(b).ToArray();
        var changeFrame = a.Length;
        var target = NoteMath.FromFrequency(to).NearestMidi;
        var readings = Feed(new TunerEngine(Fs), signal);
        var first = readings.First(r => r.EndFrame > changeFrame && r.HasPitch && r.Concert.NearestMidi == target);
        var latencyMs = (first.EndFrame - changeFrame) * 1000.0 / Fs;
        Assert.True(latencyMs <= 100, $"延遲 {latencyMs} ms");
        // 改變之後不得再跳回舊音
        Assert.DoesNotContain(readings, r => r.EndFrame > first.EndFrame && r.HasPitch && r.Concert.NearestMidi != target);
    }

    [Fact]
    public void Latency_from_silence_to_note_is_at_most_100ms()
    {
        var signal = new float[Fs / 2].Concat(Tone(330, 0.5)).ToArray();
        var readings = Feed(new TunerEngine(Fs), signal);
        var first = readings.First(r => r.HasPitch);
        Assert.True((first.EndFrame - (Fs / 2)) * 1000.0 / Fs <= 100);
    }

    [Fact]
    public void Update_rate_is_at_least_20_per_second()
    {
        var engine = new TunerEngine(Fs);
        var readings = Feed(engine, Tone(440, 2.0));
        var perSecond = readings.Count / 2.0;
        Assert.True(perSecond >= 20, $"更新率 {perSecond}/s");
        Assert.Equal(readings.Count, engine.ReadingCount);
    }

    [Fact]
    public void Block_size_does_not_change_reading_times()
    {
        var x = Tone(440, 0.5);
        var a = Feed(new TunerEngine(Fs), x, 480).Select(r => r.EndFrame).ToArray();
        var b = Feed(new TunerEngine(Fs), x, 160).Select(r => r.EndFrame).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Transposition_shows_written_pitch()
    {
        var engine = new TunerEngine(Fs, new TunerSettings { Transposition = Transposition.BFlat });
        var r = Feed(engine, Tone(NoteMath.MidiToFrequency(58), 0.5)).Last(); // 實際 B♭3
        Assert.Equal("A#3", r.Concert.Name);
        Assert.Equal("C4", r.Written.Name);
    }

    [Fact]
    public void A4_setting_changes_cents()
    {
        var engine = new TunerEngine(Fs, new TunerSettings { A4 = 442 });
        var r = Feed(engine, Tone(440, 0.5)).Last();
        Assert.Equal("A4", r.Concert.Name);
        Assert.Equal(1200 * Math.Log2(440.0 / 442.0), r.Cents, 0);
    }

    [Fact]
    public void Settings_can_change_while_running()
    {
        var engine = new TunerEngine(Fs);
        var x = Tone(440, 0.5);
        Feed(engine, x[..(Fs / 4)]);
        engine.UpdateSettings(new TunerSettings { Transposition = Transposition.F });
        engine.Consume(x.AsSpan(Fs / 4, Block), 1, Fs / 4);
        Assert.Equal("E5", engine.Latest.Written.Name);
    }

    [Fact]
    public void Short_dropout_does_not_flicker_to_dash()
    {
        var x = Tone(440, 1.0);
        Array.Clear(x, Fs / 2, Fs / 100); // 10 ms 斷音
        var readings = Feed(new TunerEngine(Fs), x);
        var afterStart = readings.SkipWhile(r => !r.HasPitch).ToList();
        Assert.All(afterStart, r => Assert.True(r.HasPitch));
    }

    [Fact]
    public void Stereo_input_is_mixed()
    {
        var mono = Tone(330, 0.3);
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = mono[i];
            stereo[(2 * i) + 1] = mono[i];
        }
        var engine = new TunerEngine(Fs);
        for (var p = 0; p + (2 * Block) <= stereo.Length; p += 2 * Block)
        {
            engine.Consume(stereo.AsSpan(p, 2 * Block), 2, p / 2);
        }
        Assert.Equal("E4", engine.Latest.Concert.Name);
    }

    [Fact]
    public void Invalid_a4_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TunerEngine(Fs, new TunerSettings { A4 = 400 }));
    }

    [Theory]
    [InlineData(44100, 2048, 441)]
    [InlineData(48000, 2048, 480)]
    [InlineData(96000, 4096, 960)]
    public void Window_and_hop_scale_with_rate(int fs, int window, int hop)
    {
        var e = new TunerEngine(fs);
        Assert.Equal(window, e.WindowSize);
        Assert.Equal(hop, e.HopSize);
    }
}
