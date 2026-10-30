using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyriagentHub.Hosting;

/// <summary>
/// diag\myriagenthub-YYYYMMDD.log（9.10）：JSON 行、每日一檔、單檔上限 50 MB、保留 14 天。
/// 背景執行緒寫檔，佇列滿了就丟（記錄絕不拖慢請求）；寫檔失敗不往外丟。
/// 記錄內容絕不含權杖、cookie、密碼、訊息原文、驗證碼（各處的訊息範本只放 id 與狀態）。
/// </summary>
internal sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly BlockingCollection<string> queue = new(boundedCapacity: 10_000);
    private readonly string directory;
    private readonly long maxBytes;
    private readonly int retainDays;
    private readonly TimeProvider time;
    private readonly Thread writer;
    private FileStream? stream;
    private string? currentDay;
    private bool capped;
    private long dropped;

    public JsonFileLoggerProvider(string directory, FileLogSettings settings, TimeProvider time)
    {
        this.directory = directory;
        maxBytes = Math.Max(1, settings.MaxFileMb) * 1024L * 1024;
        retainDays = Math.Max(1, settings.RetainDays);
        this.time = time;
        writer = new Thread(WriteLoop) { IsBackground = true, Name = "MyriagentHub 記錄檔" };
        writer.Start();
    }

    public long Dropped => Interlocked.Read(ref dropped);

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(this, categoryName);

    public void Dispose()
    {
        queue.CompleteAdding();
        writer.Join(TimeSpan.FromSeconds(3));
        stream?.Dispose();
        queue.Dispose();
    }

    internal void Enqueue(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        if (queue.IsAddingCompleted)
            return;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("l", level.ToString());
            json.WriteString("c", category);
            if (eventId.Id != 0)
                json.WriteNumber("e", eventId.Id);
            json.WriteString("m", message);
            if (exception is not null)
                json.WriteString("x", exception.ToString());
            json.WriteEndObject();
        }
        try
        {
            if (!queue.TryAdd(Encoding.UTF8.GetString(buffer.ToArray())))
                Interlocked.Increment(ref dropped);
        }
        catch (InvalidOperationException)
        {
            // 已停止接受新記錄（服務正在結束）
        }
    }

    private void WriteLoop()
    {
        foreach (var line in queue.GetConsumingEnumerable())
        {
            try
            {
                var day = time.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (day != currentDay)
                    OpenDay(day);
                if (stream is null || capped)
                    continue;
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                if (stream.Length + bytes.Length > maxBytes)
                {
                    var marker = Encoding.UTF8.GetBytes("{\"l\":\"Warning\",\"m\":\"記錄檔已達單檔上限，今天之後的記錄不再寫入\"}\n");
                    stream.Write(marker);
                    stream.Flush();
                    capped = true;
                    continue;
                }
                stream.Write(bytes);
                if (queue.Count == 0)
                    stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 磁碟滿或權限問題：丟掉這一行，不影響服務
            }
        }
    }

    private void OpenDay(string day)
    {
        stream?.Dispose();
        stream = null;
        capped = false;
        currentDay = day;
        Directory.CreateDirectory(directory);
        stream = new FileStream(Path.Combine(directory, $"myriagenthub-{day}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        capped = stream.Length >= maxBytes;
        DeleteOldFiles();
    }

    private void DeleteOldFiles()
    {
        var cutoff = time.GetLocalNow().Date.AddDays(-retainDays);
        foreach (var file in Directory.GetFiles(directory, "myriagenthub-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParseExact(name["myriagenthub-".Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 刪不掉的舊記錄留到下次
                }
            }
        }
    }
}

internal sealed class JsonFileLogger(JsonFileLoggerProvider provider, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        provider.Enqueue(logLevel, category, eventId, formatter(state, exception), exception);
    }
}
