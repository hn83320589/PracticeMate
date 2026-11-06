using PracticeMate.Core.Audio;
using PracticeMate.Core.IO.Wav;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.IO;

public sealed class StreamingWavWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-tests-" + Guid.NewGuid().ToString("N"));

    public StreamingWavWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Property_write_then_read_round_trips()
    {
        Prop.ForAll(baseSeed: 3001, iterations: 20, rng =>
        {
            var channels = rng.Next(1, 3);
            var rate = new[] { 8000, 44100, 48000, 96000, 192000 }[rng.Next(5)];
            var frames = rng.Next(0, 5000);
            var data = Enumerable.Range(0, frames * channels).Select(_ => (float)((rng.NextDouble() * 2) - 1)).ToArray();

            var ms = new MemoryStream();
            using (var w = new StreamingWavWriter(ms, rate, channels, headerUpdateIntervalFrames: rate, leaveOpen: true))
            {
                var pos = 0;
                while (pos < frames)
                {
                    var n = Math.Min(rng.Next(1, 700), frames - pos);
                    w.Write(data.AsSpan(pos * channels, n * channels));
                    pos += n;
                }
            }
            ms.Position = 0;
            var r = WavReader.Read(ms);
            Assert.Empty(r.Warnings);
            Assert.Equal(rate, r.Audio.SampleRate);
            Assert.Equal(frames, r.Audio.FrameCount);
            for (var i = 0; i < data.Length; i++)
            {
                Assert.Equal(data[i], r.Audio.Channel(i % channels)[i / channels]);
            }
        });
    }

    [Fact]
    public void Writes_ieee_float_format()
    {
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 48000, 2, 48000, leaveOpen: true))
        {
            w.Write([0.5f, -0.5f]);
        }
        ms.Position = 0;
        var r = WavReader.Read(ms);
        Assert.Equal(WavSampleEncoding.Float32, r.Format.Encoding);
        Assert.Equal(2, r.Format.Channels);
    }

    [Fact]
    public void Header_is_updated_every_interval_so_crash_loses_at_most_last_interval()
    {
        const int rate = 8000;
        var path = Path.Combine(_dir, "crash.wav");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        var writer = new StreamingWavWriter(stream, rate, 1, headerUpdateIntervalFrames: rate);
        var block = new float[rate / 10];
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = i / (float)block.Length;
        }
        for (var i = 0; i < 25; i++) // 2.5 秒，最後一次更新在 2.0 秒
        {
            writer.Write(block);
        }

        // 不呼叫 Dispose/Complete：模擬當機，直接讀取磁碟上的檔案
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var r = WavReader.Read(reader);
            Assert.InRange(r.Audio.FrameCount, 2 * rate, 25 * block.Length);
            Assert.Equal(block[7], r.Audio.Channel(0)[7]);
        }
        Assert.Equal(2 * rate, writer.FramesCommitted);
        Assert.Equal(25 * block.Length, writer.FramesWritten);
        stream.Dispose();
    }

    [Fact]
    public void Header_written_at_start_has_zero_length_and_is_recoverable()
    {
        var path = Path.Combine(_dir, "early.wav");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        var writer = new StreamingWavWriter(stream, 48000, 1, 48000);
        writer.Write(new float[100]);
        stream.Flush();
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var r = WavReader.Read(reader);
            Assert.Equal(100, r.Audio.FrameCount);
            Assert.True(r.HeaderRecovered);
        }
        stream.Dispose();
    }

    [Fact]
    public void Write_rejects_partial_frames()
    {
        using var w = new StreamingWavWriter(new MemoryStream(), 48000, 2, 48000);
        Assert.Throws<ArgumentException>(() => w.Write([1f, 2f, 3f]));
    }

    [Fact]
    public void Write_after_complete_throws()
    {
        var w = new StreamingWavWriter(new MemoryStream(), 48000, 1, 48000);
        w.Complete();
        Assert.Throws<InvalidOperationException>(() => w.Write([1f]));
        w.Dispose();
    }
}
