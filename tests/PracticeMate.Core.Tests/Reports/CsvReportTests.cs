using System.Globalization;
using System.Text;
using PracticeMate.Core.Analysis.Beats;
using PracticeMate.Core.Analysis.Notes;
using PracticeMate.Core.Reports;

namespace PracticeMate.Core.Tests.Reports;

public class CsvReportTests
{
    private static string Text(byte[] bytes)
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        return Encoding.UTF8.GetString(bytes[3..]);
    }

    [Fact]
    public void Notes_csv_has_bom_header_and_invariant_numbers()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // 小數點為逗號的地區設定
        try
        {
            var csv = Text(CsvReport.Notes([new NoteEvent(24000, 48000, 69, -12.34)], 48000));
            Assert.Equal("序號,開始(秒),結束(秒),音名,MIDI,音準偏差(音分)\r\n1,0.500,1.000,A4,69,-12.3\r\n", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void Fields_are_escaped(string input, string expected)
    {
        Assert.Equal(expected, CsvReport.Escape(input));
    }

    [Fact]
    public void Undefined_values_are_dashes()
    {
        Assert.Equal("—", CsvReport.Number(double.NegativeInfinity, "F1"));
        Assert.Equal("—", CsvReport.Number(double.NaN, "F1"));
    }

    [Fact]
    public void Beats_csv_lists_beats_off_beats_and_summary()
    {
        var report = BeatAligner.Align([480, 24000 - 960, 12000], 48000, 120, 0);
        var lines = Text(CsvReport.Beats(report, 48000)).Split("\r\n");
        Assert.Equal("拍序,拍點(秒),起音(秒),偏差(毫秒),類別", lines[0]);
        Assert.Equal("1,0.000,0.010,10.0,拖拍", lines[1]);
        Assert.Equal("2,0.500,0.480,-20.0,搶拍", lines[2]);
        Assert.Equal(",,0.250,,拍點外", lines[3]);
        Assert.Contains("趨勢,資料不足", lines);
    }
}
