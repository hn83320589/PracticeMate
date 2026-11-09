using System.Numerics;
using PracticeMate.Core.Music;
using PracticeMate.Core.Pitch;
using PracticeMate.Core.Recording;

namespace PracticeMate.Core.Tuner;

public sealed record TunerSettings
{
    public double A4 { get; init; } = NoteMath.DefaultA4;

    public Transposition Transposition { get; init; } = Transposition.C;

    /// <summary>雜訊門檻：視窗 RMS 低於此值（dBFS）視為無音。</summary>
    public double NoiseGateDbfs { get; init; } = -50.0;

    /// <summary>進入「有音高」所需的 clarity（遲滯上緣）。</summary>
    public double EnterClarity { get; init; } = 0.85;

    /// <summary>維持「有音高」所需的 clarity（遲滯下緣）。</summary>
    public double ExitClarity { get; init; } = 0.75;

    /// <summary>連續幾次無音判定後才顯示「—」，避免偶發的單次失誤讓畫面閃爍。</summary>
    public int ReleaseCount { get; init; } = 3;
}

/// <summary>調音器顯示用讀數。HasPitch 為 false 時畫面顯示「—」。</summary>
public sealed record TunerReading(
    bool HasPitch,
    double FrequencyHz,
    NoteReading Concert,
    NoteReading Written,
    double Clarity,
    double LevelDbfs,
    long EndFrame)
{
    public static TunerReading Empty { get; } = new(false, double.NaN, default, default, 0, double.NegativeInfinity, 0);

    public double Cents => Written.Cents;
}

/// <summary>
/// 即時調音器：每收到 10 ms 新資料（hop = round(fs/100)），對最近一個視窗（約 40 ms，48 kHz → 2048）
/// 做一次音高偵測（每秒 100 次）。輸出經過：
/// <list type="bullet">
/// <item>雜訊門檻與 clarity 遲滯（進入 0.85、維持 0.75），連續 3 次無音才顯示「—」。</item>
/// <item>最近 3 次有效估計（以 MIDI 值）取中位數，去除偶發跳動。</item>
/// </list>
/// 延遲預算：視窗半長 ≈ 21 ms + 區塊 10 ms + 中位數 10 ms，遠低於 100 ms。
/// 讀數以不可變物件發布，UI 執行緒可隨時讀 <see cref="Latest"/>。所有時間以取樣索引表示。
/// </summary>
public sealed class TunerEngine : ICaptureConsumer
{
    private readonly McLeodPitchDetector _detector;
    private readonly float[] _window;
    private readonly int _hop;
    private readonly double[] _recent = new double[3];
    private int _recentCount;
    private int _filled;
    private int _sinceDetect;
    private bool _voiced;
    private int _unvoicedRun;
    private TunerSettings _settings;
    private TunerReading _latest = TunerReading.Empty;
    private long _readingCount;

    public TunerEngine(int sampleRate, TunerSettings? settings = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _settings = Validate(settings ?? new TunerSettings());
        SampleRate = sampleRate;
        WindowSize = WindowSizeFor(sampleRate);
        _window = new float[WindowSize];
        _hop = Math.Max(1, (int)Math.Round(sampleRate / 100.0));
        _detector = new McLeodPitchDetector(sampleRate, WindowSize, new PitchDetectorOptions
        {
            MinFrequency = 50,
            MaxFrequency = 1800,
            // 每 10 ms 都要算一次：減少精修運算以確保單區塊 ≤ 1 ms（錄音分析維持預設 6 / 3）
            RefinementHarmonics = 4,
            RefinementIterations = 2,
        });
    }

    public int SampleRate { get; }

    public int WindowSize { get; }

    public int HopSize => _hop;

    public TunerReading Latest => Volatile.Read(ref _latest);

    /// <summary>已產生的讀數數量（用於量測更新率）。</summary>
    public long ReadingCount => Interlocked.Read(ref _readingCount);

    public TunerSettings Settings => Volatile.Read(ref _settings);

    /// <summary>每次產生新讀數時觸發（在消費者執行緒上）。</summary>
    public event EventHandler<TunerReading>? ReadingProduced;

    /// <summary>約 40 ms 進位到 2 的冪次（48 kHz → 2048）。</summary>
    public static int WindowSizeFor(int sampleRate) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Ceiling(0.04 * sampleRate));

    /// <summary>變更 A4 或移調（任意執行緒）；下一筆讀數生效。</summary>
    public void UpdateSettings(TunerSettings settings) => Volatile.Write(ref _settings, Validate(settings));

    public void Consume(ReadOnlySpan<float> interleaved, int channels, long firstFrame)
    {
        var frames = interleaved.Length / channels;
        var f = 0;
        while (f < frames)
        {
            // 一次最多推進到下一個 hop 邊界
            var n = Math.Min(frames - f, _hop - _sinceDetect);
            Shift(n);
            for (var i = 0; i < n; i++)
            {
                var baseIndex = (f + i) * channels;
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    sum += interleaved[baseIndex + c];
                }
                _window[WindowSize - n + i] = sum / channels;
            }
            f += n;
            _filled = Math.Min(WindowSize, _filled + n);
            _sinceDetect += n;
            if (_sinceDetect >= _hop)
            {
                _sinceDetect = 0;
                if (_filled >= WindowSize)
                {
                    Produce(firstFrame + f);
                }
            }
        }
    }

    private void Shift(int n)
    {
        if (n > 0)
        {
            Array.Copy(_window, n, _window, 0, WindowSize - n);
        }
    }

    private void Produce(long endFrame)
    {
        var settings = Settings;
        var e = _detector.Detect(_window);
        var clarityNeeded = _voiced ? settings.ExitClarity : settings.EnterClarity;
        var frameVoiced = e.HasPitch && e.RmsDbfs >= settings.NoiseGateDbfs && e.Clarity >= clarityNeeded;

        TunerReading reading;
        if (frameVoiced)
        {
            _voiced = true;
            _unvoicedRun = 0;
            PushRecent(NoteMath.FrequencyToMidi(e.FrequencyHz, settings.A4));
            reading = MakeReading(MedianRecent(), e.Clarity, e.RmsDbfs, endFrame, settings);
        }
        else
        {
            _unvoicedRun++;
            var previous = Latest;
            if (_voiced && _unvoicedRun < settings.ReleaseCount && previous.HasPitch)
            {
                // 短暫失誤：維持上一個音高
                reading = previous with { LevelDbfs = e.RmsDbfs, EndFrame = endFrame };
            }
            else
            {
                _voiced = false;
                _recentCount = 0;
                reading = TunerReading.Empty with { LevelDbfs = e.RmsDbfs, EndFrame = endFrame };
            }
        }

        Volatile.Write(ref _latest, reading);
        Interlocked.Increment(ref _readingCount);
        ReadingProduced?.Invoke(this, reading);
    }

    private static TunerReading MakeReading(double midi, double clarity, double level, long endFrame, TunerSettings s)
    {
        var concert = NoteMath.FromMidi(midi);
        return new TunerReading(true, NoteMath.MidiToFrequency(midi, s.A4), concert,
            NoteMath.ToWritten(concert, s.Transposition), clarity, level, endFrame);
    }

    private void PushRecent(double midi)
    {
        // 音高大跳（> 1 半音）時清空歷史，讓新音立即反映，不被舊值的中位數拖住
        if (_recentCount > 0 && Math.Abs(midi - _recent[(_recentCount - 1) % 3]) > 1.0)
        {
            _recentCount = 0;
        }
        _recent[_recentCount % 3] = midi;
        _recentCount++;
    }

    private double MedianRecent()
    {
        var n = Math.Min(_recentCount, 3);
        if (n < 3)
        {
            return _recent[(_recentCount - 1) % 3];
        }
        double a = _recent[0], b = _recent[1], c = _recent[2];
        return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }

    private static TunerSettings Validate(TunerSettings s)
    {
        NoteMath.ValidateA4(s.A4);
        if (s.ExitClarity > s.EnterClarity)
        {
            throw new ArgumentException("維持門檻不可高於進入門檻。", nameof(s));
        }
        return s;
    }
}
