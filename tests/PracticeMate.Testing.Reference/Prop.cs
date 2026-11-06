using System.Globalization;

namespace PracticeMate.Testing.Reference;

/// <summary>
/// 極簡的性質測試工具：以固定基礎種子產生每次迭代的種子，失敗時拋出含可重現種子的例外。
/// 設定環境變數 <c>PM_PROP_SEED</c> 可只重跑指定種子。
/// </summary>
public static class Prop
{
    public const string SeedEnvironmentVariable = "PM_PROP_SEED";

    public static void ForAll(int baseSeed, int iterations, Action<Random> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        var overrideSeed = Environment.GetEnvironmentVariable(SeedEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideSeed))
        {
            var seed = int.Parse(overrideSeed, CultureInfo.InvariantCulture);
            Run(seed, -1, body);
            return;
        }

        for (var i = 0; i < iterations; i++)
        {
            Run(SeedFor(baseSeed, i), i, body);
        }
    }

    /// <summary>由基礎種子與迭代序號導出種子（固定、可重現）。</summary>
    public static int SeedFor(int baseSeed, int iteration) => unchecked((baseSeed * 7919) + (iteration * 104729));

    private static void Run(int seed, int iteration, Action<Random> body)
    {
        try
        {
            body(new Random(seed));
        }
        catch (Exception ex)
        {
            throw new PropertyFailedException(seed, iteration, ex);
        }
    }
}

public sealed class PropertyFailedException : Exception
{
    public PropertyFailedException(int seed, int iteration, Exception inner)
        : base(string.Create(CultureInfo.InvariantCulture,
            $"性質測試失敗：seed={seed}（第 {iteration} 次迭代）。重現方式：設定環境變數 {Prop.SeedEnvironmentVariable}={seed}。原因：{inner.Message}"),
            inner)
    {
        Seed = seed;
        Iteration = iteration;
    }

    public PropertyFailedException() { }
    public PropertyFailedException(string message) : base(message) { }
    public PropertyFailedException(string message, Exception inner) : base(message, inner) { }

    public int Seed { get; }
    public int Iteration { get; }
}
