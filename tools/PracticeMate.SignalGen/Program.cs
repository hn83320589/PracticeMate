using System.Globalization;
using System.Text;
using PracticeMate.Testing.Reference;

namespace PracticeMate.SignalGen;

/// <summary>
/// 測試訊號產生器（命令列）。使用測試專案中與正式程式碼獨立的 SignalGenerator / PerformanceSynth /
/// ReferenceLoudness，產生範例錄音（16 位元 PCM WAV）與「預期結果」說明檔。
/// 用法：PracticeMate.SignalGen [輸出資料夾]（預設 ./samples）
/// </summary>
internal static class Program
{
    private const int Fs = 48000;

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "samples");
        Directory.CreateDirectory(outDir);

        DetunedScale(outDir);
        RushingAndDragging(outDir);
        Accelerando(outDir);
        TwoLoudnessLevels(outDir);

        Console.WriteLine($"已產生範例錄音於 {outDir}");
        return 0;
    }

    private static void DetunedScale(string dir)
    {
        int[] scale = [60, 62, 64, 65, 67, 69, 71, 72];
        double[] cents = [-30, 25, -12, 30, 5, -25, 18, -8];
        var notes = scale.Select((m, i) => new SynthNote(m, 0.5 + (i * 0.6), 0.6, cents[i])).ToArray();
        var r = PerformanceSynth.Render(notes, new PerformanceOptions
        {
            Articulation = Articulation.WindLegato, VibratoDepthCents = 5, VibratoRateHz = 5.5, SnrDb = 40, Seed = 1,
        });
        Write(dir, "01-走音音階.wav", r.Signal);

        var md = new StringBuilder();
        md.AppendLine("# 01-走音音階.wav 預期結果").AppendLine();
        md.AppendLine("C 大調音階（C4–C5），管樂連音（30 ms 起音、音與音之間 40 ms 滑音）、±5 音分 5.5 Hz 顫音、SNR 40 dB，A4 = 440 Hz。").AppendLine();
        md.AppendLine("| # | 起始(秒) | 音名 | 刻意偏差(音分) |").AppendLine("|---|---|---|---|");
        for (var i = 0; i < notes.Length; i++)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {i + 1} | {notes[i].OnsetSeconds:F2} | {Name(scale[i])} | {cents[i]:+0;-0;0} |");
        }
        md.AppendLine().AppendLine("音準報告應列出 8 個音，每個音的偏差與上表相差 ≤ 2 音分（顫音使中位數略偏，見 README 已知限制）。");
        File.WriteAllText(Path.Combine(dir, "01-走音音階-預期結果.md"), md.ToString(), Encoding.UTF8);
    }

    private static void RushingAndDragging(string dir)
    {
        double[] deviations = [0, -30, 20, 0, 45, -15, -40, 10, 25, -20, 0, 35, -50, 15, -10, 30];
        var notes = PerformanceSynth.RhythmExercise(100, 1.0, deviations, midi: 67, noteSeconds: 0.25);
        var r = PerformanceSynth.Render(notes, new PerformanceOptions { Articulation = Articulation.Wind, SnrDb = 40, Seed = 2 });
        Write(dir, "02-搶拍與拖拍.wav", r.Signal);

        var md = new StringBuilder();
        md.AppendLine("# 02-搶拍與拖拍.wav 預期結果").AppendLine();
        md.AppendLine("節奏練習：G4、管樂音色（30 ms 起音），**速度 100 BPM、第一拍 1.000 秒**（分析頁輸入這兩個值）。").AppendLine();
        md.AppendLine("| 拍 | 刻意偏差(毫秒，＋拖拍／−搶拍) |").AppendLine("|---|---|");
        for (var i = 0; i < deviations.Length; i++)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {i + 1} | {deviations[i]:+0;-0;0} |");
        }
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"平均 {deviations.Average():+0.0;-0.0} ms、樣本標準差 {StdDev(deviations):F1} ms；每拍報告值與上表相差 ≤ 5 ms；趨勢：速度穩定。");
        File.WriteAllText(Path.Combine(dir, "02-搶拍與拖拍-預期結果.md"), md.ToString(), Encoding.UTF8);
    }

    private static void Accelerando(string dir)
    {
        var notes = PerformanceSynth.RhythmExercise(120, 0.5, new double[20], midi: 70, noteSeconds: 0.2, tempoRampPercent: 2);
        var r = PerformanceSynth.Render(notes, new PerformanceOptions { Articulation = Articulation.Wind, SnrDb = 40, Seed = 3 });
        Write(dir, "03-漸快.wav", r.Signal);
        var md = new StringBuilder();
        md.AppendLine("# 03-漸快.wav 預期結果").AppendLine();
        md.AppendLine("20 個音，速度從 120 BPM 線性漸快到 122.4 BPM（+2%）。**以 120 BPM、第一拍 0.500 秒對照**。").AppendLine();
        md.AppendLine("每拍越來越早到（偏差逐漸變負，最後約 −90 ms，仍在 1/4 拍以內）；節拍報告趨勢應判定為「漸快」。");
        File.WriteAllText(Path.Combine(dir, "03-漸快-預期結果.md"), md.ToString(), Encoding.UTF8);
    }

    private static void TwoLoudnessLevels(string dir)
    {
        var md = new StringBuilder();
        md.AppendLine("# 04/05 響度不同的兩段錄音 預期結果").AppendLine();
        md.AppendLine("同一段樂句以不同音量輸出。預期整合響度由測試專案的參考實作（逐取樣 K 加權＋直接閘控）計算：").AppendLine();
        md.AppendLine("| 檔案 | 整合響度(LUFS) | 取樣峰值(dBFS) |").AppendLine("|---|---|---|");
        foreach (var (file, peak) in new[] { ("04-響度偏小.wav", 0.05), ("05-響度偏大.wav", 0.7) })
        {
            var notes = Enumerable.Range(0, 12).Select(i => new SynthNote(62 + (i % 6), 0.3 + (i * 0.8), 0.7)).ToArray();
            var r = PerformanceSynth.Render(notes, new PerformanceOptions { Articulation = Articulation.Wind, Amplitude = peak, SnrDb = 50, Seed = 4 });
            var quantized = Write(dir, file, r.Signal);
            var lufs = ReferenceLoudness.Integrated([quantized], Fs);
            md.AppendLine(CultureInfo.InvariantCulture, $"| {file} | {lufs:F1} | {20 * Math.Log10(quantized.Max(Math.Abs)):F1} |");
        }
        md.AppendLine().AppendLine("以預設目標 −16 LUFS、真峰值上限 −1 dBTP 正規化後，兩個輸出檔的整合響度都應為 −16.0 ± 0.1 LUFS（若峰值受限，結果會說明改以峰值為準）。");
        File.WriteAllText(Path.Combine(dir, "04-05-響度-預期結果.md"), md.ToString(), Encoding.UTF8);
    }

    /// <summary>寫成 16 位元 PCM；回傳量化後的取樣（預期結果以實際寫入的資料計算）。</summary>
    private static double[] Write(string dir, string name, double[] x)
    {
        var ints = x.Select(v => (long)Math.Clamp(Math.Round(v * 32768), short.MinValue, short.MaxValue)).ToArray();
        File.WriteAllBytes(Path.Combine(dir, name), RefWavBuilder.Pcm(Fs, 1, 16).WithIntSamples(ints).Build());
        Console.WriteLine($"  {name}（{x.Length / (double)Fs:F1} 秒）");
        return ints.Select(v => v / 32768.0).ToArray();
    }

    private static string Name(int midi) =>
        new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[midi % 12] + ((midi / 12) - 1).ToString(CultureInfo.InvariantCulture);

    private static double StdDev(double[] v)
    {
        var mean = v.Average();
        return Math.Sqrt(v.Sum(d => (d - mean) * (d - mean)) / (v.Length - 1));
    }
}
