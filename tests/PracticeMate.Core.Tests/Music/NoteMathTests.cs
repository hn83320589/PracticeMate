using PracticeMate.Core.Music;

namespace PracticeMate.Core.Tests.Music;

/// <summary>驗收 #7。</summary>
public class NoteMathTests
{
    [Theory]
    [InlineData(60, "C4")]
    [InlineData(69, "A4")]
    [InlineData(59, "B3")]
    [InlineData(58, "A#3")]
    [InlineData(33, "A1")]
    [InlineData(93, "A6")]
    [InlineData(61, "C#4")]
    [InlineData(0, "C-1")]
    public void Names_use_sharps_and_c4_is_midi_60(int midi, string name)
    {
        Assert.Equal(name, NoteMath.NameOf(midi));
    }

    [Theory]
    [InlineData(440.0)]
    [InlineData(442.0)]
    public void A4_reference_is_zero_cents(double a4)
    {
        var r = NoteMath.FromFrequency(a4, a4);
        Assert.Equal(69, r.NearestMidi);
        Assert.Equal(0, r.Cents, 9);
    }

    [Theory]
    [InlineData(440.0)]
    [InlineData(442.0)]
    public void Exactly_plus_50_cents_takes_higher_note(double a4)
    {
        var f = a4 * Math.Pow(2, 0.5 / 12); // A4 + 50 音分
        var r = NoteMath.FromFrequency(f, a4);
        Assert.Equal(70, r.NearestMidi);     // A#4
        Assert.Equal(-50, r.Cents, 9);
    }

    [Theory]
    [InlineData(440.0)]
    [InlineData(442.0)]
    public void Exactly_minus_50_cents_takes_higher_note(double a4)
    {
        var f = a4 * Math.Pow(2, -0.5 / 12); // A4 − 50 音分 = G#4 + 50 音分
        var r = NoteMath.FromFrequency(f, a4);
        Assert.Equal(69, r.NearestMidi);
        Assert.Equal(-50, r.Cents, 9);
    }

    [Theory]
    [InlineData(440.0, 49.99, 69, 49.99)]
    [InlineData(440.0, 50.01, 70, -49.99)]
    [InlineData(442.0, -49.99, 69, -49.99)]
    [InlineData(442.0, -50.01, 68, 49.99)]
    public void Near_boundary_values(double a4, double centsFromA4, int expectedMidi, double expectedCents)
    {
        var f = a4 * Math.Pow(2, centsFromA4 / 1200);
        var r = NoteMath.FromFrequency(f, a4);
        Assert.Equal(expectedMidi, r.NearestMidi);
        Assert.Equal(expectedCents, r.Cents, 6);
    }

    [Fact]
    public void A440_measured_against_A442_is_about_minus_7_9_cents()
    {
        var r = NoteMath.FromFrequency(440, 442);
        Assert.Equal(69, r.NearestMidi);
        // m 刻意四捨五入到 1e-9 半音（= 1e-7 音分）以穩定邊界判定
        Assert.Equal(1200 * Math.Log2(440.0 / 442.0), r.Cents, 6);
    }

    [Fact]
    public void Bflat_instrument_written_c4_sounds_bflat3()
    {
        // 實際 B♭3（MIDI 58，升記號寫作 A#3）→ 記譜 C4
        var concert = NoteMath.FromFrequency(NoteMath.MidiToFrequency(58));
        Assert.Equal("A#3", concert.Name);
        var written = NoteMath.ToWritten(concert, Transposition.BFlat);
        Assert.Equal("C4", written.Name);
        Assert.Equal(concert.Cents, written.Cents);
    }

    [Theory]
    [InlineData(Transposition.C, 0)]
    [InlineData(Transposition.BFlat, 2)]
    [InlineData(Transposition.EFlat, 9)]
    [InlineData(Transposition.F, 7)]
    public void Transposition_semitones(Transposition t, int semis)
    {
        Assert.Equal(semis, NoteMath.TranspositionSemitones(t));
        Assert.Equal(60 + semis, NoteMath.ToWritten(NoteMath.FromMidi(60), t).NearestMidi);
    }

    [Fact]
    public void Eflat_alto_sax_concert_eflat4_is_written_c5()
    {
        var w = NoteMath.ToWritten(NoteMath.FromMidi(63), Transposition.EFlat);
        Assert.Equal("C5", w.Name);
    }

    [Fact]
    public void Octave_boundary_b3_to_c4()
    {
        Assert.Equal(3, NoteMath.OctaveOf(59));
        Assert.Equal(4, NoteMath.OctaveOf(60));
        Assert.Equal(-1, NoteMath.OctaveOf(11));
    }

    [Theory]
    [InlineData(414.9)]
    [InlineData(466.1)]
    public void A4_outside_415_466_is_rejected(double a4)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NoteMath.ValidateA4(a4));
        Assert.Contains("415", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Midi_frequency_round_trip()
    {
        for (var m = 33; m <= 93; m++)
        {
            Assert.Equal(m, NoteMath.FrequencyToMidi(NoteMath.MidiToFrequency(m, 415), 415), 9);
        }
    }

    [Fact]
    public void Non_positive_frequency_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteMath.FromFrequency(0));
    }
}
