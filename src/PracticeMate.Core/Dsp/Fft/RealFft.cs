using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;

namespace PracticeMate.Core.Dsp.Fft;

/// <summary>
/// 實數輸入的 FFT，輸出 N/2+1 個非負頻率的 bin。
/// N 為偶數時把實數序列打包成 N/2 點複數序列做一次 FFT 再分離（約省一半運算）；
/// N 為奇數時直接使用複數 FFT。
/// </summary>
public sealed class RealFft
{
    private static readonly ConcurrentDictionary<int, RealFft> Cache = new();

    private readonly ComplexFft _fft;          // 偶數：N/2 點；奇數：N 點
    private readonly Complex[] _postTwiddles;  // e^(−2πi·k/N)，k ≤ N/2
    private readonly bool _packed;

    private RealFft(int length)
    {
        Length = length;
        _packed = length % 2 == 0;
        _fft = ComplexFft.Get(_packed ? length / 2 : length);
        _postTwiddles = new Complex[(length / 2) + 1];
        for (var k = 0; k < _postTwiddles.Length; k++)
        {
            _postTwiddles[k] = ComplexFft.Twiddle(k, length);
        }
    }

    public int Length { get; }

    public int SpectrumLength => (Length / 2) + 1;

    public static RealFft Get(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return Cache.GetOrAdd(length, static n => new RealFft(n));
    }

    public void Forward(ReadOnlySpan<double> input, Span<Complex> spectrum)
    {
        CheckLengths(input.Length, spectrum.Length);
        if (!_packed)
        {
            ForwardUnpacked(input, spectrum);
            return;
        }

        var h = Length / 2;
        var rented = ArrayPool<Complex>.Shared.Rent(h);
        try
        {
            var z = rented.AsSpan(0, h);
            for (var i = 0; i < h; i++)
            {
                z[i] = new Complex(input[2 * i], input[(2 * i) + 1]);
            }
            _fft.Forward(z);

            for (var k = 0; k <= h; k++)
            {
                var zk = z[k % h];
                var zc = Complex.Conjugate(z[(h - k) % h]);
                var even = (zk + zc) * 0.5;
                var odd = (zk - zc) * new Complex(0, -0.5);
                spectrum[k] = even + (_postTwiddles[k] * odd);
            }
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(rented);
        }
    }

    /// <summary>由 N/2+1 個 bin 還原實數序列（含 1/N 縮放）。虛部的不對稱成分會被忽略。</summary>
    public void Inverse(ReadOnlySpan<Complex> spectrum, Span<double> output)
    {
        CheckLengths(output.Length, spectrum.Length);
        if (!_packed)
        {
            InverseUnpacked(spectrum, output);
            return;
        }

        var h = Length / 2;
        var rented = ArrayPool<Complex>.Shared.Rent(h);
        try
        {
            var z = rented.AsSpan(0, h);
            for (var k = 0; k < h; k++)
            {
                var xk = spectrum[k];
                var xc = Complex.Conjugate(spectrum[h - k]);
                var even = (xk + xc) * 0.5;
                var odd = (xk - xc) * 0.5 * Complex.Conjugate(_postTwiddles[k]);
                z[k] = even + (Complex.ImaginaryOne * odd);
            }
            _fft.Inverse(z);
            for (var i = 0; i < h; i++)
            {
                output[2 * i] = z[i].Real;
                output[(2 * i) + 1] = z[i].Imaginary;
            }
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(rented);
        }
    }

    private void ForwardUnpacked(ReadOnlySpan<double> input, Span<Complex> spectrum)
    {
        var rented = ArrayPool<Complex>.Shared.Rent(Length);
        try
        {
            var z = rented.AsSpan(0, Length);
            for (var i = 0; i < Length; i++)
            {
                z[i] = input[i];
            }
            _fft.Forward(z);
            z[..spectrum.Length].CopyTo(spectrum);
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(rented);
        }
    }

    private void InverseUnpacked(ReadOnlySpan<Complex> spectrum, Span<double> output)
    {
        var rented = ArrayPool<Complex>.Shared.Rent(Length);
        try
        {
            var z = rented.AsSpan(0, Length);
            for (var k = 0; k < spectrum.Length; k++)
            {
                z[k] = spectrum[k];
            }
            for (var k = spectrum.Length; k < Length; k++)
            {
                z[k] = Complex.Conjugate(spectrum[Length - k]);
            }
            _fft.Inverse(z);
            for (var i = 0; i < Length; i++)
            {
                output[i] = z[i].Real;
            }
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(rented);
        }
    }

    private void CheckLengths(int timeLength, int spectrumLength)
    {
        if (timeLength != Length || spectrumLength != SpectrumLength)
        {
            throw new ArgumentException(
                $"實數 FFT 長度不符：時域 {timeLength}（應為 {Length}）、頻域 {spectrumLength}（應為 {SpectrumLength}）。");
        }
    }
}
