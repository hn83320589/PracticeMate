namespace PracticeMate.Testing.Reference;

/// <summary>
/// 響度參考實作（與正式程式碼獨立）：以逐取樣差分方程直接做 K 加權，
/// 每個 400 ms 閘控區塊直接加總平方（不使用子區塊累加或任何快取），再依 BS.1770-4 閘控。
/// 48 kHz 使用標準表列係數；其他取樣率依同一組類比參數，以獨立撰寫的雙線性轉換計算。
/// </summary>
public static class ReferenceLoudness
{
    /// <summary>直接 Form I：y[n] = b0x[n] + b1x[n−1] + b2x[n−2] − a1y[n−1] − a2y[n−2]。</summary>
    public static double[] KWeight(IReadOnlyList<double> x, int fs)
    {
        ArgumentNullException.ThrowIfNull(x);
        var (s, h) = Coefficients(fs);
        return Biquad(Biquad(x, s), h);
    }

    public static double Integrated(IReadOnlyList<IReadOnlyList<double>> channels, int fs)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var weighted = channels.Select(c => KWeight(c, fs)).ToArray();
        var n = weighted[0].Length;
        var blockLen = (int)Math.Round(0.4 * fs);
        var step = (int)Math.Round(0.1 * fs);
        var z = new List<double>();
        for (var start = 0; start + blockLen <= n; start += step)
        {
            double sum = 0;
            foreach (var ch in weighted)
            {
                double e = 0;
                for (var i = start; i < start + blockLen; i++)
                {
                    e += ch[i] * ch[i];
                }
                sum += e / blockLen;
            }
            z.Add(sum);
        }
        static double L(double v) => -0.691 + (10 * Math.Log10(v));
        var abs = z.Where(v => v > 0 && L(v) > -70).ToList();
        if (abs.Count == 0)
        {
            return double.NegativeInfinity;
        }
        var rel = L(abs.Average()) - 10;
        var gated = abs.Where(v => L(v) > rel).ToList();
        return gated.Count == 0 ? double.NegativeInfinity : L(gated.Average());
    }

    private static double[] Biquad(IReadOnlyList<double> x, double[] c)
    {
        var y = new double[x.Count];
        for (var n = 0; n < x.Count; n++)
        {
            var x1 = n >= 1 ? x[n - 1] : 0;
            var x2 = n >= 2 ? x[n - 2] : 0;
            var y1 = n >= 1 ? y[n - 1] : 0;
            var y2 = n >= 2 ? y[n - 2] : 0;
            y[n] = (c[0] * x[n]) + (c[1] * x1) + (c[2] * x2) - (c[3] * y1) - (c[4] * y2);
        }
        return y;
    }

    /// <summary>回傳 [b0, b1, b2, a1, a2]（高架、高通）。</summary>
    private static (double[] Shelf, double[] HighPass) Coefficients(int fs)
    {
        if (fs == 48000)
        {
            return ([1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585],
                    [1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621]);
        }
        // 高架
        var f0 = 1681.974450955533;
        var g = 3.999843853973347;
        var q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / fs);
        var vh = Math.Pow(10.0, g / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1.0 + (k / q) + (k * k);
        double[] shelf =
        [
            (vh + (vb * k / q) + (k * k)) / a0,
            2.0 * ((k * k) - vh) / a0,
            (vh - (vb * k / q) + (k * k)) / a0,
            2.0 * ((k * k) - 1.0) / a0,
            (1.0 - (k / q) + (k * k)) / a0,
        ];
        // 高通
        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / fs);
        a0 = 1.0 + (k / q) + (k * k);
        double[] hp = [1.0, -2.0, 1.0, 2.0 * ((k * k) - 1.0) / a0, (1.0 - (k / q) + (k * k)) / a0];
        return (shelf, hp);
    }
}
