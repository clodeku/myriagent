using System.Security.AccessControl;
using System.Security.Principal;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 把資料夾（連同繼承權限的既有子項目）改成目前使用者只能讀取與執行：模擬部署時 runner 帳戶對資料根的權限
/// （db、config 只能讀、其他地方寫不進去；docs/hub-setup.md 第 2 節）。已經開著的控制代碼不受影響，
/// 所以先開好的「服務」連線照樣能寫。擁有者一定能改回 DACL：Dispose 時還原成完全控制，暫存資料夾才刪得掉。
/// </summary>
internal sealed class ReadOnlyAccess : IDisposable
{
    private readonly string dir;
    private readonly SecurityIdentifier user;

    private ReadOnlyAccess(string dir, SecurityIdentifier user)
    {
        this.dir = dir;
        this.user = user;
    }

    public static ReadOnlyAccess Apply(string dir)
    {
        using var me = WindowsIdentity.GetCurrent();
        var user = me.User!;
        Set(dir, user, FileSystemRights.ReadAndExecute);
        return new ReadOnlyAccess(dir, user);
    }

    /// <summary>目前身分能不能在資料夾裡建檔（建了關檔即刪）</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            using (new FileStream(Path.Combine(dir, ".probe-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => Set(dir, user, FileSystemRights.FullControl);

    private static void Set(string dir, SecurityIdentifier user, FileSystemRights rights)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        // SetNamedSecurityInfo：可繼承的項目會套到既有的子資料夾與檔案（db\hub.db 等）
        new DirectoryInfo(dir).SetAccessControl(security);
    }
}
