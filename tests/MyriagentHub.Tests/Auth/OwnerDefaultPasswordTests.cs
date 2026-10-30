using System.Net;
using MyriagentHub.Auth;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Auth;

/// <summary>
/// 擁有者是否仍是預設密碼（WP4.5 計畫 D25；遠端指令信封的 <c>auth.defaultPassword</c> 與 <c>control</c> 用）：首次啟動是、改密碼與重設擁有者時更新、
/// 值變了才觸發事件、重新啟動照資料庫的雜湊判斷
/// </summary>
public sealed class OwnerDefaultPasswordTests
{
    private const string NewPassword = "new-password-123";

    private const string OtherPassword = "other-password-456";

    [Fact]
    public async Task 首次啟動是預設密碼()
    {
        await using var host = await HubTestHost.StartAsync();
        host.Services.GetRequiredService<OwnerAccountService>().IsDefaultPassword.Should().BeTrue();
    }

    [Fact]
    public async Task 改密碼後不是預設_事件剛好觸發一次()
    {
        await using var host = await HubTestHost.StartAsync();
        var owner = host.Services.GetRequiredService<OwnerAccountService>();
        var events = 0;
        owner.PasswordStateChanged += () => Interlocked.Increment(ref events);
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);

        await ChangeAsync(client, "wrong-current", NewPassword, HttpStatusCode.Forbidden);
        await ChangeAsync(client, OwnerAccount.DefaultPassword, "short", HttpStatusCode.BadRequest);
        owner.IsDefaultPassword.Should().BeTrue("沒改成功的不算");
        events.Should().Be(0);

        await ChangeAsync(client, OwnerAccount.DefaultPassword, NewPassword, HttpStatusCode.OK);
        owner.IsDefaultPassword.Should().BeFalse();
        events.Should().Be(1);

        await ChangeAsync(client, NewPassword, OtherPassword, HttpStatusCode.OK);
        owner.IsDefaultPassword.Should().BeFalse();
        events.Should().Be(1, "換成另一個不是預設的密碼：值沒變，不觸發");
    }

    [Fact]
    public async Task 改成和預設一樣的密碼_又是預設()
    {
        await using var host = await HubTestHost.StartAsync();
        var owner = host.Services.GetRequiredService<OwnerAccountService>();
        var events = 0;
        owner.PasswordStateChanged += () => Interlocked.Increment(ref events);
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);

        await ChangeAsync(client, OwnerAccount.DefaultPassword, NewPassword, HttpStatusCode.OK);
        owner.IsDefaultPassword.Should().BeFalse();
        await ChangeAsync(client, NewPassword, OwnerAccount.DefaultPassword, HttpStatusCode.OK);
        owner.IsDefaultPassword.Should().BeTrue("改回預設是允許的（只警告），旗標跟著改回");
        events.Should().Be(2);
    }

    [Fact]
    public async Task 重設擁有者後又是預設()
    {
        await using var host = await HubTestHost.StartAsync();
        var owner = host.Services.GetRequiredService<OwnerAccountService>();
        var events = 0;
        owner.PasswordStateChanged += () => throw new InvalidOperationException("處理者的錯誤");
        owner.PasswordStateChanged += () => Interlocked.Increment(ref events);
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        await ChangeAsync(client, OwnerAccount.DefaultPassword, NewPassword, HttpStatusCode.OK);
        owner.IsDefaultPassword.Should().BeFalse("前一個處理者丟例外也不影響改密碼的請求");
        events.Should().Be(1, "前一個處理者丟例外，後面的照樣收到");

        var reset = await owner.ResetAsync();
        reset.RevokedSessions.Should().Be(1);
        owner.IsDefaultPassword.Should().BeTrue();
        events.Should().Be(2);

        using var again = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(again);
    }

    [Fact]
    public async Task 以同一個資料夾重新啟動_照資料庫判斷()
    {
        var dir = TempDir.Create();
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                using var client = host.CreateClient();
                await OwnerFlows.LoginOwnerAsync(client);
                await ChangeAsync(client, OwnerAccount.DefaultPassword, NewPassword, HttpStatusCode.OK);
            }
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                var owner = host.Services.GetRequiredService<OwnerAccountService>();
                owner.IsDefaultPassword.Should().BeFalse("啟動時以資料庫的雜湊比對");
                using var client = host.CreateClient();
                await OwnerFlows.LoginOwnerAsync(client, NewPassword);
                await ChangeAsync(client, NewPassword, OwnerAccount.DefaultPassword, HttpStatusCode.OK);
            }
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
                host.Services.GetRequiredService<OwnerAccountService>().IsDefaultPassword.Should().BeTrue("改回預設之後重新啟動（正向對照）");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    private static async Task ChangeAsync(HubClient client, string current, string next, HttpStatusCode expected)
    {
        using var response = await OwnerFlows.ChangePasswordAsync(client, current, next);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
    }
}
