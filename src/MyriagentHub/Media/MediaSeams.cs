using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Media;

/// <summary>WP4.5 把裝置的 CommandOutput 交給瀏覽器之前呼叫（WP4.7 計畫 D37）：回 null 就丟掉</summary>
internal interface ICommandOutputRewriter
{
    Task<JObject?> RewriteAsync(string deviceId, string commandName, string? conversationId, JObject message, CancellationToken ct);
}

/// <summary>WP4.5 簽 addUploaded、conversationCreate 的 uploads 之前呼叫（WP4.7 計畫 D21）：false 就以 invalid 拒絕</summary>
internal interface IUploadRegistry
{
    bool TryVerify(IReadOnlyList<UploadClaim> uploads);
}

internal sealed record UploadClaim(string UploadId, long Size, string Sha256);

/// <summary>WP4.7 沒合併時的預設：files、fileRef 丟掉（裡面沒有可用的網址），其他原樣</summary>
internal sealed class DefaultCommandOutputRewriter : ICommandOutputRewriter
{
    public Task<JObject?> RewriteAsync(string deviceId, string commandName, string? conversationId, JObject message, CancellationToken ct)
        => Task.FromResult(message.Value<string>("type") is "files" or "fileRef" ? null : message);
}

/// <summary>WP4.7 沒合併時的預設：沒有上傳端點，一律不通過</summary>
internal sealed class NoUploadRegistry : IUploadRegistry
{
    public bool TryVerify(IReadOnlyList<UploadClaim> uploads) => false;
}

internal static class MediaSeamRegistration
{
    /// <summary>兩包都可以呼叫（TryAdd）；WP4.7 的 AddHubMedia 之後以 AddSingleton 換成實作</summary>
    public static IServiceCollection AddMediaSeams(this IServiceCollection services)
    {
        services.TryAddSingleton<ICommandOutputRewriter, DefaultCommandOutputRewriter>();
        services.TryAddSingleton<IUploadRegistry, NoUploadRegistry>();
        return services;
    }
}
