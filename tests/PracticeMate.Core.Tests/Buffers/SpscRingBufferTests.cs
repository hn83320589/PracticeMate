using PracticeMate.Core.Buffers;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Core.Tests.Buffers;

/// <summary>驗收 #5。</summary>
public class SpscRingBufferTests
{
    [Fact]
    public void Capacity_rounds_up_to_power_of_two()
    {
        Assert.Equal(1024, new SpscRingBuffer(1000).Capacity);
        Assert.Equal(131072, new SpscRingBuffer(2 * 48000).Capacity);
    }

    [Fact]
    public void Wraps_around_preserving_order()
    {
        var rb = new SpscRingBuffer(8);
        var dst = new float[8];
        for (var round = 0; round < 10; round++)
        {
            Assert.True(rb.TryWrite([round, round + 0.5f, round + 0.25f]));
            Assert.Equal(3, rb.Read(dst));
            Assert.Equal([round, round + 0.5f, round + 0.25f], dst[..3]);
        }
    }

    [Fact]
    public void Overflow_drops_whole_block_and_keeps_unread_data()
    {
        var rb = new SpscRingBuffer(8);
        Assert.True(rb.TryWrite([1, 2, 3, 4, 5, 6]));
        Assert.False(rb.TryWrite([7, 8, 9]));      // 只剩 2 個空間：整塊丟棄
        Assert.True(rb.TryWrite([10, 11]));         // 剛好放得下
        Assert.False(rb.TryWrite([12]));
        Assert.Equal(2, rb.OverflowCount);
        Assert.Equal(4, rb.DroppedSamples);

        var dst = new float[16];
        var n = rb.Read(dst);
        Assert.Equal([1, 2, 3, 4, 5, 6, 10, 11], dst[..n]);
    }

    [Fact]
    public void Block_larger_than_capacity_is_rejected()
    {
        var rb = new SpscRingBuffer(4);
        Assert.False(rb.TryWrite(new float[5]));
        Assert.Equal(1, rb.OverflowCount);
        Assert.Equal(0, rb.Count);
    }

    [Fact]
    public void Read_from_empty_returns_zero()
    {
        Assert.Equal(0, new SpscRingBuffer(4).Read(new float[4]));
    }

    [Fact]
    public void Partial_read_leaves_rest()
    {
        var rb = new SpscRingBuffer(8);
        rb.TryWrite([1, 2, 3, 4]);
        var dst = new float[3];
        Assert.Equal(3, rb.Read(dst));
        Assert.Equal(1, rb.Count);
        Assert.Equal(7, rb.FreeSpace);
    }

    /// <summary>不同執行緒、隨機區塊大小與速度；生產者等待空間 → 資料不遺失、不重複、順序正確。</summary>
    [Fact]
    public void Property_concurrent_transfer_is_lossless_and_ordered()
    {
        Prop.ForAll(baseSeed: 4001, iterations: 12, rng =>
        {
            var capacity = 1 << rng.Next(6, 12);
            var total = 200_000;
            var producerSeed = rng.Next();
            var consumerSeed = rng.Next();
            var rb = new SpscRingBuffer(capacity);
            var producerNext = 0;

            var producer = Task.Run(() =>
            {
                var r = new Random(producerSeed);
                var next = 0;
                var block = new float[capacity];
                while (next < total)
                {
                    var n = Math.Min(r.Next(1, capacity / 2), total - next);
                    for (var i = 0; i < n; i++)
                    {
                        block[i] = next + i;
                    }
                    var spinner = new SpinWait();
                    while (rb.FreeSpace < n)
                    {
                        spinner.SpinOnce(sleep1Threshold: -1); // 避免 Sleep(1)：Windows 計時器解析度約 15 ms
                    }
                    Assert.True(rb.TryWrite(block.AsSpan(0, n)));
                    next += n;
                    Volatile.Write(ref producerNext, next);
                    if (r.Next(8) == 0)
                    {
                        Thread.SpinWait(r.Next(1, 2000));
                    }
                }
            });

            var received = 0;
            var c = new Random(consumerSeed);
            var dst = new float[capacity];
            var start = TimeProvider.System.GetTimestamp();
            while (received < total)
            {
                if (producer.IsFaulted)
                {
                    producer.GetAwaiter().GetResult();
                }
                Assert.True(TimeProvider.System.GetElapsedTime(start) < TimeSpan.FromSeconds(30), $"逾時：已收到 {received}/{total}，生產者送出 {Volatile.Read(ref producerNext)}");
                var n = rb.Read(dst.AsSpan(0, c.Next(1, capacity)));
                for (var i = 0; i < n; i++)
                {
                    Assert.Equal(received + i, dst[i]);
                }
                received += n;
                if (c.Next(8) == 0)
                {
                    Thread.SpinWait(c.Next(1, 2000));
                }
            }
            producer.GetAwaiter().GetResult();
            Assert.Equal(0, rb.OverflowCount);
        });
    }

    /// <summary>生產者從不等待；被接受的區塊必須完整、依序到達，被拒絕的區塊數與取樣數必須正確計數。</summary>
    [Fact]
    public void Property_concurrent_overflow_is_counted_exactly_and_never_overwrites()
    {
        Prop.ForAll(baseSeed: 4002, iterations: 12, rng =>
        {
            var capacity = 1 << rng.Next(5, 10);
            const int blocks = 20_000;
            var producerSeed = rng.Next();
            var consumerSeed = rng.Next();
            var rb = new SpscRingBuffer(capacity);
            var accepted = new List<float>();
            long rejectedBlocks = 0, rejectedSamples = 0;
            var done = false;

            var producer = Task.Run(() =>
            {
                var r = new Random(producerSeed);
                var value = 0f;
                var block = new float[capacity];
                for (var b = 0; b < blocks; b++)
                {
                    var n = r.Next(1, capacity / 2);
                    for (var i = 0; i < n; i++)
                    {
                        block[i] = value++;
                    }
                    if (rb.TryWrite(block.AsSpan(0, n)))
                    {
                        accepted.AddRange(block.AsSpan(0, n).ToArray());
                    }
                    else
                    {
                        rejectedBlocks++;
                        rejectedSamples += n;
                    }
                    if (r.Next(4) == 0)
                    {
                        Thread.SpinWait(r.Next(1, 500));
                    }
                }
                Volatile.Write(ref done, true);
            });

            var received = new List<float>();
            var c = new Random(consumerSeed);
            var dst = new float[capacity];
            while (!Volatile.Read(ref done) || rb.Count > 0)
            {
                var n = rb.Read(dst.AsSpan(0, c.Next(1, capacity)));
                received.AddRange(dst.AsSpan(0, n).ToArray());
                Thread.SpinWait(c.Next(1, 3000)); // 慢消費者，製造溢位
            }
            producer.GetAwaiter().GetResult();
            received.AddRange(Drain(rb));

            Assert.Equal(accepted, received);
            Assert.Equal(rejectedBlocks, rb.OverflowCount);
            Assert.Equal(rejectedSamples, rb.DroppedSamples);
        });
    }

    private static float[] Drain(SpscRingBuffer rb)
    {
        var dst = new float[rb.Capacity];
        var n = rb.Read(dst);
        return dst[..n];
    }
}
