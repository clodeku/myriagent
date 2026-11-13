using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Update;

namespace MyriagentDesk;

/// <summary>服務自己更新時不收的原因（<see cref="Code"/> 是回給用戶端的錯誤碼）</summary>
internal sealed class SelfUpdateException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>服務自己更新（真的寫安裝資料夾、開換版的程式；測試以暫存資料夾與假的啟動換掉）</summary>
internal interface ISelfUpdater
{
    /// <summary>開始收一份更新，回這一次的 id（之前沒送完的作廢）</summary>
    string Begin(IReadOnlyDictionary<string, int> sizes);

    /// <summary>收一段，回這個檔到目前收了幾個位元組</summary>
    int Put(string id, string name, int offset, byte[] data);

    /// <summary>都收完了：驗、放進 incoming、啟動換版的程式。回要換成的版本</summary>
    ReleaseIdentity Apply(string id);
}

/// <summary>
/// 服務自己更新、不經 UAC（擁有者 2026-11-13 決定）。新版的服務檔由 app 經管道分段送進來——**服務不去讀使用者寫得到的資料夾**
/// （系統權限的程序照別人給的路徑讀檔，會被連結帶去讀不該讀的地方、連到別人的主機）。收完在記憶體裡驗
/// （<see cref="DeskServiceRules.CheckSelfUpdate"/>：內建金鑰的簽章、每個檔的雜湊、版本只能往新的），通過才寫進安裝資料夾的
/// <c>incoming</c>（只有系統管理員寫得了），再以那一份的執行檔執行 <c>self-update</c>：它停下服務、把自己那一份裝進去、重新啟動
/// （<see cref="DeskServiceInstaller"/>，啟動不了就退回原本那一份）。信任的伺服器（<c>trust.json</c>）不動：
/// 那是同意 UAC 的人定下的，不讓連得上管道的程式改。
/// 同一個使用者的任何程式都能要求更新——能做到的只有「把服務換成簽章有效、比較新的那一版」
/// </summary>
/// <param name="installRoot">安裝資料夾（<c>%ProgramFiles%\Myriagent\DeskService</c>）</param>
/// <param name="trustedKeys">內建的發佈金鑰（空的＝不自動更新）</param>
/// <param name="running">執行中的服務的版本</param>
/// <param name="inUse">遠端桌面正在用幫手（服務一停幫手就跟著結束）：這時不更新</param>
/// <param name="launch">啟動換版的程式（執行檔、參數）；回那個程序，啟動不了回 null</param>
internal sealed class SelfUpdater(string installRoot, string serviceName, IReadOnlyList<ReleaseKey> trustedKeys, ReleaseIdentity running,
    Func<bool> inUse, Func<string, string, Process?> launch) : ISelfUpdater
{
    private readonly object gate = new();
    private string? id;
    private Dictionary<string, (byte[] Data, int Received)>? upload;
    private bool applying;

    /// <summary>這個組建能不能自己更新（有內建的發佈金鑰）</summary>
    public bool Available => trustedKeys.Count > 0;

    public string Begin(IReadOnlyDictionary<string, int> sizes)
    {
        lock (gate)
        {
            if (applying)
                throw new SelfUpdateException(DeskServiceRules.CodeBusy, "正在換版");
            upload = sizes.ToDictionary(pair => pair.Key, pair => (new byte[pair.Value], 0), StringComparer.Ordinal);
            id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            return id;
        }
    }

    public int Put(string id, string name, int offset, byte[] data)
    {
        lock (gate)
        {
            if (upload is null || id != this.id || applying)
                throw new SelfUpdateException(DeskServiceRules.CodeDenied, "不是目前這一次的更新");
            if (!upload.TryGetValue(name, out var part) || offset != part.Received || data.Length > part.Data.Length - part.Received)
                throw new SelfUpdateException(DeskServiceRules.CodeInvalid, $"{name} 的位移或大小不對");
            data.CopyTo(part.Data, part.Received);
            upload[name] = (part.Data, part.Received + data.Length);
            return part.Received + data.Length;
        }
    }

    public ReleaseIdentity Apply(string id)
    {
        Dictionary<string, byte[]> parts;
        lock (gate)
        {
            if (upload is null || id != this.id)
                throw new SelfUpdateException(DeskServiceRules.CodeDenied, "不是目前這一次的更新");
            if (applying)
                throw new SelfUpdateException(DeskServiceRules.CodeBusy, "正在換版");
            if (upload.Any(pair => pair.Value.Received != pair.Value.Data.Length))
                throw new SelfUpdateException(DeskServiceRules.CodeInvalid, "還沒送完");
            parts = upload.ToDictionary(pair => pair.Key, pair => pair.Value.Data, StringComparer.Ordinal);
            // 這一份用掉了：不管過不過都要重新送
            upload = null;
            this.id = null;
            applying = true;
        }

        try
        {
            if (DeskServiceRules.CheckSelfUpdate(parts, trustedKeys, running, ReadInstalled(), out var release, out var signedBy) is { } reason)
            {
                ServiceLog.Warn("不收的更新：" + reason);
                throw new SelfUpdateException(DeskServiceRules.CodeDenied, reason);
            }

            if (inUse())
                throw new SelfUpdateException(DeskServiceRules.CodeBusy, "遠端桌面正在使用鎖定畫面支援，稍後再更新");

            var incoming = Path.Combine(installRoot, DeskServiceRules.IncomingFolder);
            Stage(incoming, parts);
            var arguments = "self-update" + (serviceName == DeskServiceRules.ServiceName ? string.Empty : " --name " + serviceName);
            var process = launch(Path.Combine(incoming, DeskServiceRules.ExeName), arguments)
                ?? throw new SelfUpdateException(DeskServiceRules.CodeFailed, "換版的程式啟動不了");
            ServiceLog.Info($"自己更新：{running.Release} → {release!.Release}（簽章 {signedBy}），換版的程式已啟動（程序 {process.Id}）");
            Watch(process);
            return release;
        }
        catch (Exception ex)
        {
            lock (gate)
                applying = false;
            if (ex is SelfUpdateException)
                throw;
            if (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ServiceLog.Error("自己更新失敗", ex);
                throw new SelfUpdateException(DeskServiceRules.CodeFailed, ex.Message);
            }

            throw;
        }
    }

    /// <summary>把驗過的位元組寫進 incoming（先清掉上一次的），寫完讀回來再比一次</summary>
    private static void Stage(string incoming, IReadOnlyDictionary<string, byte[]> parts)
    {
        if (Directory.Exists(incoming))
            Directory.Delete(incoming, recursive: true);
        Directory.CreateDirectory(incoming);
        foreach (var (name, bytes) in parts)
        {
            var path = Path.Combine(incoming, name);
            File.WriteAllBytes(path, bytes);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(bytes)))
                throw new IOException($"{name} 寫進去之後內容不對");
        }
    }

    /// <summary>
    /// 換版的程式順利的話會把這個服務停掉（這個程序跟著結束）；它先結束了而服務還在，就是沒換成（記下結束碼、之後可以再試）
    /// </summary>
    private void Watch(Process process)
    {
        var thread = new Thread(() =>
        {
            try
            {
                process.WaitForExit();
                ServiceLog.Warn($"換版的程式結束了、服務還在（沒有換成）：結束碼 {process.ExitCode}（{DeskServiceRules.Describe(DeskServiceRules.ExitOf(process.ExitCode)) ?? "成功"}）");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ServiceLog.Warn("等不到換版的程式：" + ex.Message);
            }
            finally
            {
                process.Dispose();
                lock (gate)
                    applying = false;
            }
        }) { IsBackground = true, Name = "desk-self-update" };
        thread.Start();
    }

    private DeskServiceInstall? ReadInstalled()
    {
        try
        {
            var path = Path.Combine(installRoot, DeskServiceRules.InstallFile);
            return File.Exists(path) && new FileInfo(path).Length <= 64 * 1024 ? DeskServiceRules.ParseInstall(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>真的啟動換版的程式：服務的子程序（系統權限、工作階段 0、沒有視窗）；服務停了它照樣跑完</summary>
    public static Process? Launch(string exe, string arguments)
        => Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });

    /// <summary>清掉上一次換版留下的 incoming（換版的程式自己就在裡面執行，它刪不掉自己）。新的服務啟動後過一下子呼叫</summary>
    public static void CleanIncoming(string installRoot)
    {
        try
        {
            var incoming = Path.Combine(installRoot, DeskServiceRules.IncomingFolder);
            if (Directory.Exists(incoming))
                Directory.Delete(incoming, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Info("上一次換版留下的檔還沒清掉（下次再清）：" + ex.Message);
        }
    }
}
