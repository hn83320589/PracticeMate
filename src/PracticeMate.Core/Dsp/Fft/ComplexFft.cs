using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;

namespace PracticeMate.Core.Dsp.Fft;

/// <summary>
/// 任意長度的複數 FFT。2 的冪次使用迭代式基數 2；其他長度使用 Bluestein（chirp-z）
/// 演算法，以長度 ≥ 2N−1 的 2 的冪次 FFT 計算卷積。
/// 計畫（plan）建立後不可變，可在多執行緒共用；暫存區每次呼叫向 ArrayPool 租用。
/// 正轉換：X[k] = Σ x[n]·e^(−2πi·kn/N)；反轉換含 1/N 縮放。
/// </summary>
public abstract class ComplexFft
{
    private static readonly ConcurrentDictionary<int, ComplexFft> Cache = new();

    protected ComplexFft(int length) => Length = length;

    public int Length { get; }

    public static ComplexFft Get(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return Cache.GetOrAdd(length, static n => IsPowerOfTwo(n) ? new Radix2Fft(n) : new BluesteinFft(n));
    }

    public void Forward(Span<Complex> data)
    {
        CheckLength(data.Length);
        Transform(data, inverse: false);
    }

    public void Inverse(Span<Complex> data)
    {
        CheckLength(data.Length);
        Transform(data, inverse: true);
        var scale = 1.0 / Length;
        for (var i = 0; i < data.Length; i++)
        {
            data[i] *= scale;
        }
    }

    /// <summary>未縮放的轉換（反向時使用共軛旋轉因子）。</summary>
    internal abstract void Transform(Span<Complex> data, bool inverse);

    internal static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    private void CheckLength(int actual)
    {
        if (actual != Length)
        {
            throw new ArgumentException($"FFT 緩衝區長度 {actual} 與計畫長度 {Length} 不符。");
        }
    }

    /// <summary>e^(−2πi·k/n)，角度先降到 [0, n) 再以 SinCos 直接計算（不遞推，避免累積誤差）。</summary>
    internal static Complex Twiddle(long k, long n)
    {
        var r = k % n;
        if (r < 0)
        {
            r += n;
        }
        var (sin, cos) = Math.SinCos(-2.0 * Math.PI * r / n);
        return new Complex(cos, sin);
    }

    private sealed class Radix2Fft : ComplexFft
    {
        private readonly int[] _bitReverse;
        private readonly Complex[] _twiddles; // e^(−2πi·k/N), k < N/2

        public Radix2Fft(int n) : base(n)
        {
            var bits = BitOperations.Log2((uint)n);
            _bitReverse = new int[n];
            for (var i = 0; i < n; i++)
            {
                var r = 0;
                for (var b = 0; b < bits; b++)
                {
                    r |= ((i >> b) & 1) << (bits - 1 - b);
                }
                _bitReverse[i] = r;
            }
            _twiddles = new Complex[Math.Max(1, n / 2)];
            for (var k = 0; k < n / 2; k++)
            {
                _twiddles[k] = Twiddle(k, n);
            }
        }

        internal override void Transform(Span<Complex> data, bool inverse)
        {
            var n = Length;
            for (var i = 0; i < n; i++)
            {
                var j = _bitReverse[i];
                if (j > i)
                {
                    (data[i], data[j]) = (data[j], data[i]);
                }
            }

            for (var size = 2; size <= n; size <<= 1)
            {
                var half = size >> 1;
                var step = n / size;
                for (var start = 0; start < n; start += size)
                {
                    for (var j = 0; j < half; j++)
                    {
                        var w = _twiddles[j * step];
                        if (inverse)
                        {
                            w = Complex.Conjugate(w);
                        }
                        var a = data[start + j];
                        var b = data[start + j + half] * w;
                        data[start + j] = a + b;
                        data[start + j + half] = a - b;
                    }
                }
            }
        }
    }

    private sealed class BluesteinFft : ComplexFft
    {
        private readonly int _m;
        private readonly ComplexFft _inner;
        private readonly Complex[] _chirp;          // c[n] = e^(−iπ·n²/N)
        private readonly Complex[] _kernelSpectrum;  // FFT(b)，b[n] = conj(c[n])，循環對稱

        public BluesteinFft(int n) : base(n)
        {
            _m = (int)BitOperations.RoundUpToPowerOf2((uint)((2 * n) - 1));
            _inner = Get(_m);
            _chirp = new Complex[n];
            for (var i = 0; i < n; i++)
            {
                // e^(−iπ·i²/N) = e^(−2πi·i²/(2N))，以 i² mod 2N 降冪
                _chirp[i] = Twiddle((long)i * i % (2L * n), 2L * n);
            }
            _kernelSpectrum = new Complex[_m];
            _kernelSpectrum[0] = Complex.Conjugate(_chirp[0]);
            for (var i = 1; i < n; i++)
            {
                var c = Complex.Conjugate(_chirp[i]);
                _kernelSpectrum[i] = c;
                _kernelSpectrum[_m - i] = c;
            }
            _inner.Transform(_kernelSpectrum, inverse: false);
        }

        internal override void Transform(Span<Complex> data, bool inverse)
        {
            var n = Length;
            var rented = ArrayPool<Complex>.Shared.Rent(_m);
            try
            {
                var work = rented.AsSpan(0, _m);
                // 反轉換：conj(FFT(conj(x)))
                for (var i = 0; i < n; i++)
                {
                    var x = inverse ? Complex.Conjugate(data[i]) : data[i];
                    work[i] = x * _chirp[i];
                }
                work[n..].Clear();

                _inner.Transform(work, inverse: false);
                for (var i = 0; i < _m; i++)
                {
                    work[i] *= _kernelSpectrum[i];
                }
                _inner.Transform(work, inverse: true);

                var scale = 1.0 / _m;
                for (var k = 0; k < n; k++)
                {
                    var y = work[k] * scale * _chirp[k];
                    data[k] = inverse ? Complex.Conjugate(y) : y;
                }
            }
            finally
            {
                ArrayPool<Complex>.Shared.Return(rented);
            }
        }
    }
}
