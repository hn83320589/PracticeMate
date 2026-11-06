using System.Numerics;
using System.Runtime.InteropServices;

namespace PracticeMate.Core.Buffers;

/// <summary>
/// 單一生產者、單一消費者的無鎖環狀緩衝區（float）。
/// <list type="bullet">
/// <item>讀寫索引為單調遞增的 64 位元計數器，以 Volatile.Read/Write 發布（acquire/release 語意）。</item>
/// <item>寫入為「整塊或全不」：空間不足時整塊丟棄並計數，絕不覆寫尚未讀取的資料，也不阻塞。</item>
/// <item>兩個索引放在不同 cache line，避免生產者與消費者互相干擾（false sharing）。</item>
/// </list>
/// </summary>
public sealed class SpscRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private Indices _indices;

    /// <param name="minimumCapacity">最少容量（取樣數），會進位到 2 的冪次。</param>
    public SpscRingBuffer(int minimumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCapacity);
        var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minimumCapacity);
        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>目前可讀取的取樣數（近似值，跨執行緒呼叫時僅供參考）。</summary>
    public int Count => (int)(Volatile.Read(ref _indices.Write) - Volatile.Read(ref _indices.Read));

    /// <summary>目前可寫入的取樣數（生產者端呼叫時是保守值）。</summary>
    public int FreeSpace => Capacity - Count;

    /// <summary>因空間不足而被丟棄的區塊數。</summary>
    public long OverflowCount => Volatile.Read(ref _indices.Overflows);

    /// <summary>因空間不足而被丟棄的取樣數。</summary>
    public long DroppedSamples => Volatile.Read(ref _indices.Dropped);

    /// <summary>生產者呼叫。空間不足時整塊丟棄、計數並回傳 false。不阻塞、不配置。</summary>
    public bool TryWrite(ReadOnlySpan<float> block)
    {
        var write = _indices.Write;                       // 只有生產者會寫，直接讀即可
        var read = Volatile.Read(ref _indices.Read);      // acquire：看到消費者釋放的空間
        var free = Capacity - (write - read);
        if (block.Length > free)
        {
            Volatile.Write(ref _indices.Overflows, _indices.Overflows + 1);
            Volatile.Write(ref _indices.Dropped, _indices.Dropped + block.Length);
            return false;
        }

        var start = (int)(write & _mask);
        var first = Math.Min(block.Length, Capacity - start);
        block[..first].CopyTo(_buffer.AsSpan(start));
        block[first..].CopyTo(_buffer.AsSpan(0));
        Volatile.Write(ref _indices.Write, write + block.Length); // release：資料先寫完才發布
        return true;
    }

    /// <summary>消費者呼叫。讀取最多 destination.Length 個取樣，回傳實際讀取數。</summary>
    public int Read(Span<float> destination)
    {
        var read = _indices.Read;
        var write = Volatile.Read(ref _indices.Write);
        var count = (int)Math.Min(destination.Length, write - read);
        if (count == 0)
        {
            return 0;
        }
        var start = (int)(read & _mask);
        var first = Math.Min(count, Capacity - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
        Volatile.Write(ref _indices.Read, read + count);
        return count;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Indices
    {
        [FieldOffset(64)] public long Write;
        [FieldOffset(72)] public long Overflows;
        [FieldOffset(80)] public long Dropped;
        [FieldOffset(192)] public long Read;
    }
}
