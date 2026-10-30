using MyriagentHub.Hosting;

namespace MyriagentHub.Trust;

/// <summary>信任（WP3.2）：伺服器身分金鑰、信任資料（裝置名冊）的發布</summary>
internal static class TrustRegistration
{
    public static IServiceCollection AddHubTrust(this IServiceCollection services)
    {
        services.AddSingleton<HubIdentity>();
        services.AddSingleton<TrustService>();
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.Trust, "伺服器身分", async ct =>
        {
            await sp.GetRequiredService<HubIdentity>().InitializeAsync(ct);
            await sp.GetRequiredService<TrustService>().PublishAsync(ct);
        }));
        return services;
    }
}
