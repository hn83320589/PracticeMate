using PracticeMate.Core.Metronome;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Metronome;

/// <summary>驗收 #9。</summary>
public class MetronomeTests
{
    /// <summary>參考公式：以 decimal 直接計算 round(k·60·fs/BPM)（四捨五入）。</summary>
    private static long ExpectedStart(long k, int bpm, int fs) =>
        (long)Math.Round(k * 60m * fs / bpm, MidpointRounding.AwayFromZero);

    [Fact]
    public void One_hour_at_120_bpm_has_no_drift()
    {
        const int fs = 48000;
        var s = new MetronomeScheduler(fs, new MetronomeSettings(120, 4));
        for (long k = 0; k < 7200; k++)
        {
            var beat = s.Next();
            Assert.Equal(k, beat.BeatIndex);
            Assert.Equal(ExpectedStart(k, 120, fs), beat.StartSample);
        }
    }

    [Fact]
    public void Property_beat_start_equals_direct_formula()
    {
        Prop.ForAll(baseSeed: 5001, iterations: 40, rng =>
        {
            var fs = new[] { 44100, 48000, 96000, 22050, 8000 }[rng.Next(5)];
            var bpm = rng.Next(MetronomeSettings.MinBpm, MetronomeSettings.MaxBpm + 1);
            var k = (long)rng.Next(0, 2_000_000);
            Assert.Equal(ExpectedStart(k, bpm, fs), MetronomeScheduler.BeatOffset(k, bpm, fs));
        });
    }

    [Fact]
    public void Tempo_change_takes_effect_on_next_bar_first_beat()
    {
        const int fs = 48000;
        var s = new MetronomeScheduler(fs, new MetronomeSettings(120, 4));
        Assert.Equal(0, s.Next().StartSample);
        s.RequestSettings(new MetronomeSettings(90, 4)); // 於第 1 拍期間要求
        Assert.Equal(24000, s.Next().StartSample);        // 第 2 拍仍為 120 BPM
        Assert.Equal(48000, s.Next().StartSample);
        Assert.Equal(72000, s.Next().StartSample);
        var barStart = s.Next();                           // 下一小節第一拍：以舊速度算出 96000
        Assert.Equal(96000, barStart.StartSample);
        Assert.Equal(90, barStart.Bpm);
        Assert.Equal(0, barStart.BeatInBar);
        for (long k = 1; k < 100; k++)
        {
            Assert.Equal(96000 + ExpectedStart(k, 90, fs), s.Next().StartSample);
        }
    }

    [Fact]
    public void Time_signature_change_also_waits_for_bar_line()
    {
        var s = new MetronomeScheduler(48000, new MetronomeSettings(60, 3));
        s.Next();
        s.RequestSettings(new MetronomeSettings(60, 2));
        Assert.Equal(1, s.Next().BeatInBar);
        Assert.Equal(2, s.Next().BeatInBar);
        Assert.Equal(0, s.Next().BeatInBar);
        Assert.Equal(1, s.Next().BeatInBar);
        Assert.Equal(0, s.Next().BeatInBar);
    }

    [Fact]
    public void Accents_follow_pattern()
    {
        var s = new MetronomeScheduler(48000, new MetronomeSettings(100, 3, [true, false, true]));
        var accents = Enumerable.Range(0, 6).Select(_ => s.Next().IsAccent).ToArray();
        Assert.Equal([true, false, true, true, false, true], accents);
    }

    [Theory]
    [InlineData(29, 4)]
    [InlineData(301, 4)]
    [InlineData(120, 0)]
    [InlineData(120, 13)]
    public void Settings_validate_ranges(int bpm, int beats)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new MetronomeSettings(bpm, beats));
        Assert.Contains("之間", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(48000, 960, 96)]
    [InlineData(44100, 882, 88)]
    [InlineData(96000, 1920, 192)]
    public void Click_length_and_fade_lengths(int fs, int length, int fade)
    {
        Assert.Equal(length, ClickSynth.Length(fs));
        Assert.Equal(fade, ClickSynth.FadeLength(fs));
        Assert.Equal(length, ClickSynth.Create(fs, false).Length);
    }

    [Theory]
    [InlineData(false, 1000.0)]
    [InlineData(true, 1500.0)]
    public void Click_waveform_matches_specification(bool accent, double frequency)
    {
        const int fs = 48000;
        var click = ClickSynth.Create(fs, accent);
        var amp = Math.Pow(10, -6 / 20.0);
        for (var n = 0; n < click.Length; n++)
        {
            var g = n < 96 ? 0.5 * (1 - Math.Cos(Math.PI * n / 96))
                : n >= 960 - 96 ? 0.5 * (1 - Math.Cos(Math.PI * (959 - n) / 96))
                : 1.0;
            var expected = amp * g * Math.Sin(2 * Math.PI * frequency * n / fs);
            Assert.Equal(expected, click[n], 6);
        }
        Assert.Equal(0f, click[0]);
        Assert.Equal(0f, click[^1]);
        Assert.InRange(click.Max(Math.Abs), amp - 0.001, amp + 1e-6); // 振幅 −6 dBFS
    }

    [Fact]
    public void Offline_render_one_hour_places_every_click_exactly()
    {
        const int fs = 48000;
        const int block = 4801; // 刻意不整除，讓點擊聲跨區塊
        var renderer = new MetronomeRenderer(fs, new MetronomeSettings(120, 4));
        var normal = ClickSynth.Create(fs, false);
        var accent = ClickSynth.Create(fs, true);
        var buffer = new float[block];
        long total = 3600L * fs;
        long pos = 0;
        long nextBeat = 0;
        var mismatches = 0;
        while (pos < total)
        {
            renderer.Render(buffer, 1);
            // 驗證本區塊：每個取樣都等於「涵蓋它的點擊聲」在該位置的值，否則為 0
            for (var i = 0; i < block; i++)
            {
                var t = pos + i;
                while (ExpectedStart(nextBeat + 1, 120, fs) <= t)
                {
                    nextBeat++;
                }
                var start = ExpectedStart(nextBeat, 120, fs);
                var wave = nextBeat % 4 == 0 ? accent : normal;
                var expected = t - start < wave.Length ? wave[t - start] : 0f;
                if (buffer[i] != expected)
                {
                    mismatches++;
                }
            }
            pos += block;
        }
        Assert.Equal(0, mismatches);
        Assert.Equal(7201, renderer.LastBeat!.Value.BeatIndex + 1);
    }

    [Fact]
    public void Render_duplicates_to_all_channels()
    {
        var r = new MetronomeRenderer(48000, new MetronomeSettings(120, 4));
        var buf = new float[200];
        r.Render(buf, 2);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(buf[2 * i], buf[(2 * i) + 1]);
        }
        Assert.Equal(100, r.Position);
    }

    [Fact]
    public void Renderer_applies_requested_tempo_at_next_bar()
    {
        const int fs = 8000;
        var r = new MetronomeRenderer(fs, new MetronomeSettings(120, 2));
        var buf = new float[fs / 4];
        r.Render(buf, 1);                      // 0–0.25 s，第 0 拍
        r.RequestSettings(new MetronomeSettings(60, 2));
        var starts = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            r.Render(buf, 1);
            if (r.LastBeat is { } b && (starts.Count == 0 || starts[^1] != b.StartSample))
            {
                starts.Add(b.StartSample);
            }
        }
        // 第 0 拍 0 s、第 1 拍 0.5 s（120），第 2 拍（新小節）1.0 s，之後每 1 s（60 BPM）
        Assert.Equal([0L, 4000L, 8000L, 16000L, 24000L, 32000L, 40000L], starts);
    }
}
