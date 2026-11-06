using System.Globalization;
using System.Text;
using PracticeMate.Core.Analysis.Beats;
using PracticeMate.Core.Analysis.Loudness;
using PracticeMate.Core.Analysis.Notes;

namespace PracticeMate.Core.Reports;

/// <summary>
/// 報告匯出為 CSV：UTF-8 with BOM（Excel 可正確顯示中文）、CRLF 換行、InvariantCulture 數字、
/// RFC 4180 跳脫（含逗號、引號或換行的欄位以雙引號包住，內部引號重複）。
/// 時間只在此處由取樣索引換算成秒。
/// </summary>
public static class CsvReport
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly string[] NoteHeader = ["序號", "開始(秒)", "結束(秒)", "音名", "MIDI", "音準偏差(音分)"];
    private static readonly string[] BeatHeader = ["拍序", "拍點(秒)", "起音(秒)", "偏差(毫秒)", "類別"];
    private static readonly string[] LoudnessSeriesHeader = ["時間(秒)", "瞬時(LUFS)", "短期(LUFS)"];

    public static byte[] Notes(IReadOnlyList<NoteEvent> notes, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var rows = new List<string[]> { NoteHeader };
        rows.AddRange(notes.Select((n, i) => new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture),
            Seconds(n.StartSample, sampleRate),
            Seconds(n.EndSample, sampleRate),
            n.Name,
            n.Midi.ToString(CultureInfo.InvariantCulture),
            n.Cents.ToString("F1", CultureInfo.InvariantCulture),
        }));
        return Encode(rows);
    }

    public static byte[] Beats(BeatReport report, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = new List<string[]> { BeatHeader };
        rows.AddRange(report.Beats.Select(b => new[]
        {
            (b.BeatIndex + 1).ToString(CultureInfo.InvariantCulture),
            Seconds(b.BeatSample, sampleRate),
            Seconds(b.OnsetSample, sampleRate),
            b.DeviationMs.ToString("F1", CultureInfo.InvariantCulture),
            b.DeviationMs > 0 ? "拖拍" : b.DeviationMs < 0 ? "搶拍" : "準拍",
        }));
        rows.AddRange(report.OffBeatOnsets.Select(o => new[] { string.Empty, string.Empty, Seconds(o, sampleRate), string.Empty, "拍點外" }));
        rows.Add([]);
        rows.Add(["平均(毫秒)", Number(report.MeanMs, "F1")]);
        rows.Add(["標準差(毫秒)", Number(report.StandardDeviationMs, "F1")]);
        rows.Add(["趨勢", report.Trend.Description]);
        rows.Add(["斜率(毫秒/拍)", Number(report.Trend.SlopeMsPerBeat, "F2")]);
        rows.Add(["p 值", Number(report.Trend.PValue, "F4")]);
        return Encode(rows);
    }

    public static byte[] Loudness(LoudnessResult loudness, TruePeakResult truePeak)
    {
        ArgumentNullException.ThrowIfNull(loudness);
        ArgumentNullException.ThrowIfNull(truePeak);
        var rows = new List<string[]>
        {
            new[] { "整合響度(LUFS)", Number(loudness.IntegratedLufs, "F1") },
            new[] { "響度範圍(LU)", Number(loudness.LoudnessRangeLu, "F1") },
            new[] { "最大瞬時(LUFS)", Number(loudness.MaxMomentaryLufs, "F1") },
            new[] { "最大短期(LUFS)", Number(loudness.MaxShortTermLufs, "F1") },
            new[] { "真峰值(dBTP)", Number(truePeak.Dbtp, "F1") },
            Array.Empty<string>(),
            LoudnessSeriesHeader,
        };
        var shortByEnd = loudness.ShortTerm.ToDictionary(p => p.EndSample, p => p.Lufs);
        rows.AddRange(loudness.Momentary.Select(m => new[]
        {
            Seconds(m.EndSample, loudness.SampleRate),
            Number(m.Lufs, "F1"),
            shortByEnd.TryGetValue(m.EndSample, out var s) ? Number(s, "F1") : string.Empty,
        }));
        return Encode(rows);
    }

    /// <summary>無法定義的數值（−∞、NaN）輸出「—」，與畫面一致。</summary>
    public static string Number(double value, string format) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "—" : value.ToString(format, CultureInfo.InvariantCulture);

    public static string Escape(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    }

    private static string Seconds(long sample, int sampleRate) =>
        ((double)sample / sampleRate).ToString("F3", CultureInfo.InvariantCulture);

    private static byte[] Encode(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Escape))).Append("\r\n");
        }
        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(sb.ToString());
        return [.. preamble, .. body];
    }
}
