using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Update;
using Microsoft.Win32.SafeHandles;

namespace MyriagentDesk;

/// <summary>安裝要用的東西（真的由 <see cref="Program"/> 給；測試給暫存資料夾與假的服務控制）</summary>
/// <param name="SourceDirectory">服務檔在哪裡（安裝器自己所在的資料夾＝app 的資料夾，使用者寫得到）</param>
/// <param name="ProgramFiles">裝到哪個 Program Files（只有系統管理員能寫）</param>
/// <param name="Identity">這一份服務檔是哪一版（安裝器和服務是同一個 exe）</param>
/// <param name="TrustedKeys">信任的發佈簽章金鑰（空的＝這個組建沒有內建金鑰，不驗）</param>
/// <param name="Trust">信任的伺服器（安全桌面的輸入要它；這台還沒配對是 null：裝了只能看）</param>
internal sealed record DeskInstallOptions(string ServiceName, string SourceDirectory, string ProgramFiles, ReleaseIdentity Identity,
    IReadOnlyList<ReleaseKey> TrustedKeys, DateTimeOffset Now, DeskServiceTrust? Trust = null);

/// <summary>
/// 安裝、更新、移除服務（<c>MyriagentDesk.exe install／uninstall</c>，以系統管理員執行）。
/// 來源是 app 的資料夾（使用者寫得到），所以：每個檔以「不跟著連結走」的方式開、不是一般的檔就不裝；整個讀進記憶體後才算雜湊、
/// 寫出去的就是算過雜湊的那幾個位元組；資料夾裡有發佈清單（受管理的安裝）時，清單的簽章與每個服務檔的雜湊都要對得上才裝。
/// 這擋的是「以系統管理員的身分把不該複製的檔複製到大家讀得到的地方」與損毀的檔——擋不了「app 的資料夾整個被換掉」
/// （那時連安裝器自己都是別人的，同意 UAC 的那一下才是邊界）
/// </summary>
internal sealed class DeskServiceInstaller(IServiceControl control)
{
    public const string ManifestFile = "release.manifest.json";

    public const string SignatureFile = "release.manifest.sig";

    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);

    public DeskServiceExit Install(DeskInstallOptions options)
    {
        // 1. 讀服務檔
        var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in DeskServiceRules.PayloadFiles)
        {
            var status = PlainFile.TryRead(Path.Combine(options.SourceDirectory, name), DeskServiceRules.MaxPayloadFileBytes, out var bytes);
            if (status != PlainFileStatus.Ok)
            {
                ServiceLog.Error($"服務檔 {name}：{status}");
                return status == PlainFileStatus.Missing ? DeskServiceExit.PayloadMissing : DeskServiceExit.PayloadInvalid;
            }
            payload[name] = bytes;
            hashes[name] = Sha256(bytes);
        }

        // 2. 有發佈清單就要對得上
        if (CheckSignature(options, hashes, out var signedBy) is { } reason)
        {
            ServiceLog.Error("服務檔沒有通過發佈簽章的檢查：" + reason);
            return DeskServiceExit.SignatureInvalid;
        }

        var payloadHash = DeskServiceRules.PayloadHash(hashes);
        var root = DeskServiceRules.InstallRoot(options.ProgramFiles, options.ServiceName);
        var folderName = DeskServiceRules.PayloadFolder(payloadHash);
        var folder = Path.Combine(root, folderName);
        ServiceLog.Info($"安裝 {options.ServiceName}：{options.Identity.Release}，服務檔 {folderName}，簽章 {signedBy ?? "沒有（開發版）"}");

        // 3. 先停下（重裝同一份時檔案正被服務用著）
        try
        {
            control.Stop(options.ServiceName, ServiceTimeout);
        }
        catch (Exception ex) when (IsServiceError(ex))
        {
            ServiceLog.Error("停不下現有的服務", ex);
            return DeskServiceExit.ServiceFailed;
        }

        // 4. 寫進自己的資料夾，寫完再讀回來比一次
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var (name, bytes) in payload)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllBytes(path, bytes);
                if (Sha256(File.ReadAllBytes(path)) != hashes[name])
                    throw new IOException($"{name} 寫進去之後內容不對");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Error("複製服務檔失敗", ex);
            return DeskServiceExit.CopyFailed;
        }

        // 5. 登記（或把現有的指到新的資料夾）
        var display = options.ServiceName == DeskServiceRules.ServiceName ? DeskServiceRules.DisplayName : $"{DeskServiceRules.DisplayName}（測試 {options.ServiceName}）";
        try
        {
            control.CreateOrUpdate(options.ServiceName, display, DeskServiceRules.Description, DeskServiceRules.BinPath(Path.Combine(folder, DeskServiceRules.ExeName)));
        }
        catch (Exception ex) when (IsServiceError(ex))
        {
            ServiceLog.Error("登記服務失敗", ex);
            return DeskServiceExit.ServiceFailed;
        }

        // 6. 安裝紀錄（啟動之前寫：啟動不了時 app 看得到「裝了但沒在執行」）
        try
        {
            var install = new DeskServiceInstall(options.ServiceName, options.Identity.Version.ToString(), options.Identity.Build, payloadHash, folderName, signedBy,
                options.Now.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(root, DeskServiceRules.InstallFile), DeskServiceRules.SerializeInstall(install), new UTF8Encoding(false));
            // 信任的伺服器：這一次安裝給的為準（同意 UAC 的那一下定下來，之後只有系統管理員改得了）；沒給就拿掉舊的——
            // 不留著上一次配對的伺服器
            var trustPath = Path.Combine(root, DeskServiceRules.TrustFile);
            if (options.Trust is { } anchor)
                File.WriteAllText(trustPath, DeskServiceRules.SerializeTrust(anchor), new UTF8Encoding(false));
            else
                File.Delete(trustPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Error("寫不了安裝紀錄", ex);
            return DeskServiceExit.CopyFailed;
        }

        // 7. 啟動
        try
        {
            control.Start(options.ServiceName, ServiceTimeout);
        }
        catch (Exception ex) when (IsServiceError(ex))
        {
            ServiceLog.Error("服務啟動不了", ex);
            return DeskServiceExit.StartFailed;
        }

        // 8. 新的起來了才清舊的
        RemoveOtherPayloads(root, folderName);
        ServiceLog.Info($"安裝完成：{options.ServiceName}");
        return DeskServiceExit.Ok;
    }

    public DeskServiceExit Uninstall(string serviceName, string programFiles)
    {
        var root = DeskServiceRules.InstallRoot(programFiles, serviceName);
        ServiceLog.Info($"移除 {serviceName}");
        try
        {
            try
            {
                control.Stop(serviceName, ServiceTimeout);
            }
            catch (TimeoutException ex)
            {
                // 停不下來也照樣移除登記（下次開機就不在了）
                ServiceLog.Warn("服務停不下來，照樣移除登記：" + ex.Message);
            }
            control.Delete(serviceName);
        }
        catch (Exception ex) when (IsServiceError(ex))
        {
            ServiceLog.Error("移除服務失敗", ex);
            return DeskServiceExit.ServiceFailed;
        }

        // 記錄也在這個資料夾裡：之後不再寫
        ServiceLog.UseDirectory(null);
        // 服務回報停止之後程序還要一下子才結束、放開檔案：試幾次
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
        return DeskServiceExit.Ok;
    }

    /// <summary>沒有發佈清單（開發版）或這個組建沒有內建金鑰：不驗、<paramref name="signedBy"/> 是 null。有清單就一定要過，回 null 表示通過</summary>
    private static string? CheckSignature(DeskInstallOptions options, IReadOnlyDictionary<string, string> hashes, out string? signedBy)
    {
        signedBy = null;
        if (options.TrustedKeys.Count == 0)
            return null;
        var manifestStatus = PlainFile.TryRead(Path.Combine(options.SourceDirectory, ManifestFile), ReleaseManifestRules.MaxManifestBytes, out var manifestBytes);
        if (manifestStatus == PlainFileStatus.Missing)
            return null;
        if (manifestStatus != PlainFileStatus.Ok)
            return "發佈清單不是一般的檔";
        if (PlainFile.TryRead(Path.Combine(options.SourceDirectory, SignatureFile), ReleaseManifestRules.MaxSignatureBytes, out var signatureBytes) != PlainFileStatus.Ok)
            return "發佈清單沒有簽章檔";
        if (!ReleaseManifestRules.TryParseSignature(Encoding.UTF8.GetString(signatureBytes), out var signature))
            return "簽章檔的格式不對";
        if (ReleaseManifestRules.VerifySignature(manifestBytes, signature, options.TrustedKeys, []) is { } bad)
            return bad;
        if (!ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out var error))
            return error ?? "發佈清單解析不了";
        if (DeskServiceRules.CheckPayload(manifest, hashes) is { } mismatch)
            return mismatch;
        signedBy = signature.Kid;
        return null;
    }

    private static void RemoveOtherPayloads(string root, string keep)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name != keep && DeskServiceRules.IsPayloadFolder(name))
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn("舊的服務檔沒有清乾淨（下次安裝再清）：" + ex.Message);
        }
    }

    private static bool IsServiceError(Exception ex)
        => ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException;

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal enum PlainFileStatus
{
    Ok,
    Missing,

    /// <summary>不是一般的檔（符號連結、接合點、資料夾）、太大、開不了</summary>
    Invalid,
}

/// <summary>
/// 以系統管理員的身分讀使用者寫得到的資料夾裡的檔：開檔時不跟著連結走（<c>FILE_FLAG_OPEN_REPARSE_POINT</c>），
/// 開到的是連結本身就不讀——不然使用者把檔名換成指向別處的連結，系統管理員就替他把讀不到的檔讀出來、複製到大家讀得到的地方
/// </summary>
internal static class PlainFile
{
    public static PlainFileStatus TryRead(string path, long maxBytes, out byte[] bytes)
    {
        bytes = [];
        using var handle = CreateFileW(path, GenericRead, FileShareRead | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagSequentialScan, IntPtr.Zero);
        if (handle.IsInvalid)
            return Marshal.GetLastWin32Error() is ErrorFileNotFound or ErrorPathNotFound ? PlainFileStatus.Missing : PlainFileStatus.Invalid;
        try
        {
            if ((File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return PlainFileStatus.Invalid;
            using var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
            if (stream.Length > maxBytes)
                return PlainFileStatus.Invalid;
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            bytes = buffer;
            return PlainFileStatus.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlainFileStatus.Invalid;
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
