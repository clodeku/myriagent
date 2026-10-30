using System.Diagnostics;
using System.Globalization;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 量測的樣本（毫秒）：可以多執行緒加入，結束時取百分位數（最近排名法：第 ⌈p·n⌉ 小的值）。WP4.2 計畫步 14 的延遲與寫入量測用
/// </summary>
internal sealed class LatencySamples
{
    private readonly object gate = new();
    private readonly List<double> values = [];

    public int Count
    {
        get
        {
            lock (gate)
                return values.Count;
        }
    }

    public void Add(double milliseconds)
    {
        lock (gate)
            values.Add(milliseconds);
    }

    /// <summary>從 <paramref name="startTimestamp"/>（<see cref="Stopwatch.GetTimestamp"/>）到現在</summary>
    public double AddSince(long startTimestamp)
    {
        var ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        Add(ms);
        return ms;
    }

    public double Percentile(double p)
    {
        lock (gate)
        {
            if (values.Count == 0)
                return double.NaN;
            var sorted = values.Order().ToList();
            var rank = (int)Math.Ceiling(p * sorted.Count);
            return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
        }
    }

    public double Max
    {
        get
        {
            lock (gate)
                return values.Count == 0 ? double.NaN : values.Max();
        }
    }

    /// <summary>給一行 JSON 用的摘要：筆數、p50、p95、p99、最大（毫秒，小數一位）</summary>
    public Dictionary<string, object> Summary() => new()
    {
        ["n"] = Count,
        ["p50"] = Round(Percentile(0.50)),
        ["p95"] = Round(Percentile(0.95)),
        ["p99"] = Round(Percentile(0.99)),
        ["max"] = Round(Max),
    };

    private static double Round(double value) => double.IsNaN(value) ? -1 : Math.Round(value, 1);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"n={Count} p50={Percentile(0.5):0.0} p95={Percentile(0.95):0.0} p99={Percentile(0.99):0.0} max={Max:0.0} ms");
}
