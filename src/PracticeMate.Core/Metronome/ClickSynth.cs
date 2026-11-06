namespace PracticeMate.Core.Metronome;

/// <summary>
/// 節拍器點擊聲：一般拍 1000 Hz、重音拍 1500 Hz 正弦，長度 20 ms，
/// 頭尾各 2 ms 升餘弦淡入淡出，振幅 −6 dBFS。
/// 長度 L = round(0.020·fs)、淡入淡出 F = round(0.002·fs)：
/// 淡入 g[n] = ½(1 − cos(π·n/F))，n ∈ [0, F)；淡出 g[n] = ½(1 − cos(π·(L−1−n)/F))，n ∈ [L−F, L)。
/// </summary>
public static class ClickSynth
{
    public const double NormalFrequency = 1000.0;
    public const double AccentFrequency = 1500.0;
    public const double DurationSeconds = 0.020;
    public const double FadeSeconds = 0.002;
    public const double LevelDbfs = -6.0;

    public static double Amplitude => Math.Pow(10, LevelDbfs / 20.0);

    public static int Length(int sampleRate) => (int)Math.Round(DurationSeconds * sampleRate, MidpointRounding.AwayFromZero);

    public static int FadeLength(int sampleRate) => (int)Math.Round(FadeSeconds * sampleRate, MidpointRounding.AwayFromZero);

    public static float[] Create(int sampleRate, bool accent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var length = Length(sampleRate);
        var fade = FadeLength(sampleRate);
        var f = accent ? AccentFrequency : NormalFrequency;
        var amp = Amplitude;
        var x = new float[length];
        for (var n = 0; n < length; n++)
        {
            var g = 1.0;
            if (n < fade)
            {
                g = 0.5 * (1 - Math.Cos(Math.PI * n / fade));
            }
            else if (n >= length - fade)
            {
                g = 0.5 * (1 - Math.Cos(Math.PI * (length - 1 - n) / fade));
            }
            x[n] = (float)(amp * g * Math.Sin(2 * Math.PI * f * n / sampleRate));
        }
        return x;
    }
}
