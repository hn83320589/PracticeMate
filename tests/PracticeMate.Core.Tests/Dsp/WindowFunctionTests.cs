using PracticeMate.Core.Dsp.Windows;

namespace PracticeMate.Core.Tests.Dsp;

public class WindowFunctionTests
{
    [Fact]
    public void Symmetric_hann_has_zero_ends_and_unit_center()
    {
        var w = WindowFunction.Create(WindowType.Hann, 9, periodic: false);
        Assert.Equal(0, w[0], 15);
        Assert.Equal(0, w[8], 15);
        Assert.Equal(1, w[4], 15);
    }

    [Fact]
    public void Periodic_hann_sums_to_half_length()
    {
        var w = WindowFunction.Create(WindowType.Hann, 1024);
        Assert.Equal(512, w.Sum(), 9);
        Assert.Equal(0, w[0], 15);
        Assert.Equal(1, w[512], 15);
    }

    [Fact]
    public void Hamming_ends_are_0_08()
    {
        var w = WindowFunction.Create(WindowType.Hamming, 11, periodic: false);
        Assert.Equal(0.08, w[0], 12);
        Assert.Equal(0.08, w[10], 12);
        Assert.Equal(1.0, w[5], 12);
    }

    [Fact]
    public void BlackmanHarris_ends_and_center()
    {
        var w = WindowFunction.Create(WindowType.BlackmanHarris, 101, periodic: false);
        Assert.Equal(0.35875 - 0.48829 + 0.14128 - 0.01168, w[0], 12);
        Assert.Equal(1.0, w[50], 12);
    }

    [Theory]
    [InlineData(WindowType.Hann)]
    [InlineData(WindowType.Hamming)]
    [InlineData(WindowType.BlackmanHarris)]
    public void Symmetric_windows_are_symmetric(WindowType type)
    {
        var w = WindowFunction.Create(type, 64, periodic: false);
        for (var i = 0; i < 32; i++)
        {
            Assert.Equal(w[i], w[63 - i], 14);
        }
    }

    [Fact]
    public void Length_one_window_is_one()
    {
        Assert.Equal([1.0], WindowFunction.Create(WindowType.BlackmanHarris, 1));
    }

    [Fact]
    public void BesselI0_matches_known_values()
    {
        Assert.Equal(1.0, WindowFunction.BesselI0(0), 15);
        Assert.Equal(1.2660658777520082, WindowFunction.BesselI0(1), 13);
        Assert.Equal(2815.716628466254, WindowFunction.BesselI0(10), 8);
    }
}
