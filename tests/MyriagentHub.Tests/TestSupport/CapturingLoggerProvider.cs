using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 收集整個測試主機的記錄（格式化後的訊息、例外的完整文字與結構化的參數值）：「記錄裡沒有權杖」這類斷言用（WP4.2 計畫 F51）。
/// 以 <c>services: s =&gt; s.AddSingleton&lt;ILoggerProvider&gt;(provider)</c> 掛進 <see cref="HubTestHost"/>（LoggerFactory 取所有登記的 provider）
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> entries = new();

    public IReadOnlyList<CapturedLog> Entries => entries.ToList();

    /// <summary>所有記錄合成一段文字（訊息、參數值、例外），找子字串用</summary>
    public string AllText() => string.Join("\n", entries.Select(e => e.Text));

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var parts = new List<string> { category, formatter(state, exception) };
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                parts.AddRange(values.Select(kv => kv.Key + "=" + kv.Value));
            if (exception is not null)
                parts.Add(exception.ToString());
            entries.Enqueue(new CapturedLog(logLevel, category, string.Join(" | ", parts)));
        }
    }
}

internal sealed record CapturedLog(LogLevel Level, string Category, string Text);
