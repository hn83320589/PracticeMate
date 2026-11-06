using System.Buffers;
using System.Buffers.Binary;

namespace PracticeMate.Core.IO.Wav;

/// <summary>
/// 錄音用的 32 位元浮點 WAV 串流寫入器。
/// <list type="bullet">
/// <item>建立時立即寫入檔頭，資料長度先填 0（當機時讀取器會以實際檔案長度回復）。</item>
/// <item>每累積 <c>headerUpdateIntervalFrames</c> 框，就回寫 RIFF/data/fact 長度並 Flush 到磁碟；
/// 當機時遺失的資料不超過最後一次更新之後的部分。</item>
/// <item>以取樣數而非時鐘決定更新時機，行為可確定、可測試。</item>
/// </list>
/// 非執行緒安全：只能由單一消費者執行緒呼叫。
/// </summary>
public sealed class StreamingWavWriter : IDisposable
{
    private const int HeaderSize = 58; // RIFF(12) + fmt(8+18) + fact(8+4) + data(8)
    private const int RiffSizeOffset = 4;
    private const int FactSampleCountOffset = 46;
    private const int DataSizeOffset = 54;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _channels;
    private readonly long _headerUpdateIntervalFrames;
    private long _framesSinceUpdate;
    private bool _completed;
    private bool _disposed;

    public StreamingWavWriter(Stream stream, int sampleRate, int channels, int headerUpdateIntervalFrames, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headerUpdateIntervalFrames);
        if (!stream.CanSeek || !stream.CanWrite)
        {
            throw new ArgumentException("錄音檔串流必須可寫入且可搜尋。", nameof(stream));
        }
        _stream = stream;
        _leaveOpen = leaveOpen;
        _channels = channels;
        _headerUpdateIntervalFrames = headerUpdateIntervalFrames;
        SampleRate = sampleRate;
        WriteInitialHeader(sampleRate, channels);
    }

    public int SampleRate { get; }

    /// <summary>已寫入串流的框數。</summary>
    public long FramesWritten { get; private set; }

    /// <summary>已記錄在檔頭（當機也保證可讀）的框數。</summary>
    public long FramesCommitted { get; private set; }

    /// <summary>RIFF 32 位元長度限制下最多可寫入的框數。</summary>
    public long MaxFrames => (uint.MaxValue - HeaderSize) / (4L * _channels);

    public void Write(ReadOnlySpan<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("錄音檔已結束，不能再寫入。");
        }
        if (interleaved.Length % _channels != 0)
        {
            throw new ArgumentException($"取樣數 {interleaved.Length} 不是聲道數 {_channels} 的倍數。", nameof(interleaved));
        }
        var frames = interleaved.Length / _channels;
        if (FramesWritten + frames > MaxFrames)
        {
            throw new InvalidOperationException("錄音檔已達 WAV 格式 4 GB 上限。");
        }

        var bytes = ArrayPool<byte>.Shared.Rent(interleaved.Length * 4);
        try
        {
            for (var i = 0; i < interleaved.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), interleaved[i]);
            }
            _stream.Write(bytes, 0, interleaved.Length * 4);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }

        FramesWritten += frames;
        _framesSinceUpdate += frames;
        if (_framesSinceUpdate >= _headerUpdateIntervalFrames)
        {
            UpdateHeader();
        }
    }

    /// <summary>正常結束：寫入最終長度並 Flush。可重複呼叫。</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }
        UpdateHeader();
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            Complete();
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private void UpdateHeader()
    {
        var dataBytes = (uint)(FramesWritten * 4 * _channels);
        var end = _stream.Position;
        Span<byte> buf = stackalloc byte[4];

        _stream.Position = RiffSizeOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)(HeaderSize - 8) + dataBytes);
        _stream.Write(buf);

        _stream.Position = FactSampleCountOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)FramesWritten);
        _stream.Write(buf);

        _stream.Position = DataSizeOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(buf, dataBytes);
        _stream.Write(buf);

        _stream.Position = end;
        if (_stream is FileStream fs)
        {
            fs.Flush(flushToDisk: true);
        }
        else
        {
            _stream.Flush();
        }
        FramesCommitted = FramesWritten;
        _framesSinceUpdate = 0;
    }

    private void WriteInitialHeader(int sampleRate, int channels)
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], HeaderSize - 8);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 18);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 3);                   // IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * channels * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 0);                   // cbSize
        "fact"u8.CopyTo(h[38..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(h[46..], 0);
        "data"u8.CopyTo(h[50..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[54..], 0);
        _stream.Position = 0;
        _stream.Write(h);
        _stream.Flush();
    }
}
