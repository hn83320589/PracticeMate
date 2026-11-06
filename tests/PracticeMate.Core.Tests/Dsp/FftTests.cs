using System.Numerics;
using PracticeMate.Core.Dsp.Fft;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Dsp;

public class FftTests
{
    private static Complex[] RandomComplex(Random rng, int n) =>
        Enumerable.Range(0, n).Select(_ => new Complex((rng.NextDouble() * 2) - 1, (rng.NextDouble() * 2) - 1)).ToArray();

    private static double Tolerance(int n) => 1e-9 * Math.Max(1.0, Math.Log2(n));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(17)]
    [InlineData(97)]
    [InlineData(1024)]
    [InlineData(1000)]
    [InlineData(4096)]
    [InlineData(8191)]  // 質數
    [InlineData(9973)]  // 質數
    [InlineData(10000)]
    public void Forward_matches_direct_dft(int n)
    {
        var x = RandomComplex(new Random(n), n);
        var expected = ReferenceDft.Forward(x);
        var actual = (Complex[])x.Clone();
        ComplexFft.Get(n).Forward(actual);
        Assert.True(ReferenceDft.RelativeError(actual, expected) <= Tolerance(n),
            $"N={n} 相對誤差 {ReferenceDft.RelativeError(actual, expected):E3}");
    }

    [Theory]
    [InlineData(6)]
    [InlineData(64)]
    [InlineData(101)]
    public void Inverse_matches_direct_inverse_dft(int n)
    {
        var x = RandomComplex(new Random(n + 1), n);
        var expected = ReferenceDft.Inverse(x);
        var actual = (Complex[])x.Clone();
        ComplexFft.Get(n).Inverse(actual);
        Assert.True(ReferenceDft.RelativeError(actual, expected) <= Tolerance(n));
    }

    [Fact]
    public void Property_forward_matches_dft_for_random_lengths()
    {
        Prop.ForAll(baseSeed: 1001, iterations: 40, rng =>
        {
            // 對數均勻分布於 1..10000，兼顧小長度與大長度
            var n = (int)Math.Round(Math.Exp(rng.NextDouble() * Math.Log(10000)));
            n = Math.Clamp(n, 1, 10000);
            var x = RandomComplex(rng, n);
            var expected = ReferenceDft.Forward(x);
            var actual = (Complex[])x.Clone();
            ComplexFft.Get(n).Forward(actual);
            var err = ReferenceDft.RelativeError(actual, expected);
            Assert.True(err <= Tolerance(n), $"N={n} 相對誤差 {err:E3}");
        });
    }

    [Fact]
    public void Property_round_trip_error_is_below_1e_minus_12()
    {
        Prop.ForAll(baseSeed: 1002, iterations: 60, rng =>
        {
            var n = rng.Next(1, 10001);
            var x = RandomComplex(rng, n);
            var y = (Complex[])x.Clone();
            var fft = ComplexFft.Get(n);
            fft.Forward(y);
            fft.Inverse(y);
            var maxErr = x.Zip(y, (a, b) => (a - b).Magnitude).Max();
            Assert.True(maxErr <= 1e-12, $"N={n} 往返最大誤差 {maxErr:E3}");
        });
    }

    [Fact]
    public void Property_real_fft_matches_complex_fft()
    {
        Prop.ForAll(baseSeed: 1003, iterations: 60, rng =>
        {
            var n = rng.Next(1, 5001);
            var x = Enumerable.Range(0, n).Select(_ => (rng.NextDouble() * 2) - 1).ToArray();
            var full = x.Select(v => new Complex(v, 0)).ToArray();
            ComplexFft.Get(n).Forward(full);

            var half = new Complex[(n / 2) + 1];
            RealFft.Get(n).Forward(x, half);

            for (var k = 0; k < half.Length; k++)
            {
                var d = (half[k] - full[k]).Magnitude;
                Assert.True(d <= 1e-9 * Math.Sqrt(n), $"N={n} bin {k} 差 {d:E3}");
            }
        });
    }

    [Fact]
    public void Property_real_inverse_round_trips()
    {
        Prop.ForAll(baseSeed: 1004, iterations: 60, rng =>
        {
            var n = rng.Next(1, 5001);
            var x = Enumerable.Range(0, n).Select(_ => (rng.NextDouble() * 2) - 1).ToArray();
            var spec = new Complex[(n / 2) + 1];
            var fft = RealFft.Get(n);
            fft.Forward(x, spec);
            var back = new double[n];
            fft.Inverse(spec, back);
            var maxErr = x.Zip(back, (a, b) => Math.Abs(a - b)).Max();
            Assert.True(maxErr <= 1e-12, $"N={n} 實數往返誤差 {maxErr:E3}");
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Invalid_length_throws(int n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComplexFft.Get(n));
        Assert.Throws<ArgumentOutOfRangeException>(() => RealFft.Get(n));
    }

    [Fact]
    public void Wrong_buffer_length_throws()
    {
        var fft = ComplexFft.Get(8);
        Assert.Throws<ArgumentException>(() => fft.Forward(new Complex[7]));
        Assert.Throws<ArgumentException>(() => RealFft.Get(8).Forward(new double[8], new Complex[4]));
    }

    [Fact]
    public void Plans_are_cached_per_length()
    {
        Assert.Same(ComplexFft.Get(256), ComplexFft.Get(256));
        Assert.Same(RealFft.Get(300), RealFft.Get(300));
    }

    [Fact]
    public void Plans_are_thread_safe()
    {
        var fft = ComplexFft.Get(1000); // Bluestein：需要暫存區
        var x = RandomComplex(new Random(5), 1000);
        var expected = (Complex[])x.Clone();
        fft.Forward(expected);
        Parallel.For(0, 64, _ =>
        {
            var y = (Complex[])x.Clone();
            fft.Forward(y);
            Assert.True(ReferenceDft.RelativeError(y, expected) == 0);
        });
    }
}
