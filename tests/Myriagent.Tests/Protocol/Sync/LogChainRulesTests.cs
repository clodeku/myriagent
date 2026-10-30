using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 日誌的鏈雜湊（設計 4.12；第 5 階段計畫 5.6、WP5.1）：H_0＝SHA256("myriagent-log/1|"＋logId＋"|"＋gen)、H_n＝SHA256(H_{n-1}‖SHA256(chunk_n))。
/// 固定向量由另一個實作（Python hashlib，見測試裡的值）算出；同一段位元組切法不同結果就不同（伺服器依 log_chunks 的邊界重算）；
/// 串流版與一次版相同；線上的 base64 只收唯一的寫法
/// </summary>
public sealed class LogChainRulesTests
{
    private const string LogId = "cl:ed703f02-0092-409f-907d-3e6ab753ef6c";

    private static readonly byte[] Chunk1 = Encoding.UTF8.GetBytes("{\"type\":\"user\"}\n");

    private static readonly byte[] Chunk2 = Encoding.UTF8.GetBytes("{\"type\":\"assistant\",\"text\":\"你好\"}\n");

    [Fact]
    public void 固定向量_H0與兩段之後()
    {
        var h0 = LogChainRules.Initial(LogId, 1);
        LogChainRules.ToWire(h0).Should().Be("5Tc/1e9HJ40/2iDaknHEoiDDwq8xfXfwmihvdqADbzo=");
        var h1 = LogChainRules.Next(h0, Chunk1);
        LogChainRules.ToWire(h1).Should().Be("RKcavapH5FTg/XNCdGt+JpGnushcoKRm+MCsZwziXBs=");
        var h2 = LogChainRules.Next(h1, Chunk2);
        LogChainRules.ToWire(h2).Should().Be("+PYtc0tcqXw/R1kSKGnC1YqtgyiSWTcxV3b/5K9R3As=");

        LogChainRules.ToWire(LogChainRules.Initial(LogId, 2)).Should().Be("+776GppK0OYaLH8LFfRpeZC3/ceqg7wh7wBe7fJDqFk=", "每個世代各自從頭算");
        LogChainRules.Over(LogId, 1, [Chunk1, Chunk2]).Should().Equal(h2);
    }

    [Fact]
    public void 同一段位元組切法不同_鏈雜湊不同_所以要照區段邊界重算()
    {
        var split = LogChainRules.Over(LogId, 1, [Chunk1, Chunk2]);
        var whole = LogChainRules.Over(LogId, 1, [Chunk1.Concat(Chunk2).ToArray()]);

        LogChainRules.ToWire(whole).Should().Be("ogGQMvtDb8fKLwVFhKo183LYdwVxleRsRUM95eex41Q=");
        whole.Should().NotEqual(split);
        LogChainRules.Over(LogId, 1, [Chunk2, Chunk1]).Should().NotEqual(split, "順序也算");
        LogChainRules.Over("cl:other", 1, [Chunk1, Chunk2]).Should().NotEqual(split, "logId 也在 H_0 裡");
    }

    [Fact]
    public void 串流版與一次版相同_分很多次餵也一樣()
    {
        var h0 = LogChainRules.Initial(LogId, 1);
        var big = new byte[3 * 1024 * 1024 + 17];
        new Random(20261023).NextBytes(big);

        using var hasher = new LogChainRules.Hasher(h0);
        for (var offset = 0; offset < big.Length; offset += 65_537)
            hasher.Append(big.AsSpan(offset, Math.Min(65_537, big.Length - offset)));
        hasher.Length.Should().Be(big.Length);

        hasher.Finish().Should().Equal(LogChainRules.Next(h0, big));
        LogChainRules.Combine(h0, SHA256.HashData(big)).Should().Equal(LogChainRules.Next(h0, big));
        FluentActions.Invoking(() => hasher.Append(Chunk1)).Should().Throw<ObjectDisposedException>("Finish 之後不能再餵");
    }

    [Fact]
    public void 空的一段也是一段()
    {
        var h0 = LogChainRules.Initial(LogId, 1);
        LogChainRules.Next(h0, []).Should().NotEqual(h0);
        using var hasher = new LogChainRules.Hasher(h0);
        hasher.Finish().Should().Equal(LogChainRules.Next(h0, []));
    }

    [Fact]
    public void 線上的base64_只收44字的唯一寫法()
    {
        var chain = LogChainRules.Initial(LogId, 1);
        var wire = LogChainRules.ToWire(chain);

        LogChainRules.TryFromWire(wire, out var back).Should().BeTrue();
        back.Should().Equal(chain);
        LogChainRules.TryFromWire(null, out _).Should().BeFalse();
        LogChainRules.TryFromWire(string.Empty, out _).Should().BeFalse();
        LogChainRules.TryFromWire(wire.TrimEnd('='), out _).Should().BeFalse("沒有補齊的寫法不收");
        LogChainRules.TryFromWire(wire.Replace('+', '-').Replace('/', '_'), out _).Should().BeFalse("base64url 不收（只收標準 base64）");
        LogChainRules.TryFromWire(" " + wire[1..], out _).Should().BeFalse("空白不收");
        Convert.ToBase64String(new byte[33]).Should().HaveLength(44);
        LogChainRules.TryFromWire(Convert.ToBase64String(new byte[33]), out _).Should().BeFalse("44 字但不是 32 位元組");
        // 最後一個字的低位元不是 0 的寫法解得出同樣的位元組，但不是唯一的寫法
        var last = wire[^2];
        var alternate = wire[..^2] + (char)(last + 1) + "=";
        LogChainRules.TryFromWire(alternate, out _).Should().BeFalse();
    }

    [Fact]
    public void 參數不合格丟例外()
    {
        FluentActions.Invoking(() => LogChainRules.Initial(LogId, 0)).Should().Throw<ArgumentOutOfRangeException>("世代從 1 開始");
        FluentActions.Invoking(() => LogChainRules.Next(new byte[31], Chunk1)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LogChainRules.ToWire(new byte[33])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new LogChainRules.Hasher(new byte[16])).Should().Throw<ArgumentException>();
    }
}
