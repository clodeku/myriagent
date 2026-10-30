using System.Runtime.CompilerServices;
using Myriagent.Core;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 測試程序的共同環境：載入測試組件時就把 app 資料夾（MYRIAGENT_AGENT_DATA）指到暫存資料夾，
/// 避免任何測試（例如 Log、AppSettings 存檔）寫進使用者真正的 %LOCALAPPDATA%\Myriagent。
/// 也清掉繼承來的對話標記（BackgroundProgramRules.MarkerVariable）：在 app 的對話裡由代理跑測試時，測試開的輔助程式（ping 之類）
/// 會繼承那個對話的標記，被 app 當成「代理另開的程式」，結束時在那個對話留一則提示（2026-10-24）。要標記的測試替子程序另外設
/// </summary>
internal static class TestEnvironment
{
    public static string DataRoot { get; private set; } = string.Empty;

#pragma warning disable CA2255 // 測試組件需要在任何測試前設定環境
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "Myriagent.Tests", $"data-{Environment.ProcessId}");
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable("MYRIAGENT_AGENT_DATA", DataRoot);
        Environment.SetEnvironmentVariable(global::Myriagent.Background.BackgroundProgramRules.MarkerVariable, null);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TempDirectory.DeleteWithRetry(DataRoot);
    }
}

/// <summary>拋棄式暫存資料夾（%TEMP%\Myriagent.Tests\{分類}\{亂數}），Dispose 時刪除（檔案被占用時重試幾次，刪不掉就留著）</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string category)
    {
        Root = Path.Combine(Path.GetTempPath(), "Myriagent.Tests", category, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>建立檔案（含上層資料夾），回傳完整路徑</summary>
    public string WriteFile(string relativePath, string content = "x")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => DeleteWithRetry(Root);

    /// <summary>刪除資料夾；被占用時（剛結束的 CLI 程序還沒放開工作資料夾）每 200 毫秒重試，最多約 3 秒</summary>
    internal static void DeleteWithRetry(string path)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>
/// 改寫全域的 AppPaths（帳戶、設定、用量歷史都寫在這裡）的測試共用這個集合，循序執行，避免互相覆蓋根目錄。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppPathsCollection
{
    public const string Name = "全域 AppPaths（循序執行）";
}

/// <summary>把 AppPaths 暫時指到指定資料夾，Dispose 時還原（還原後回到 MYRIAGENT_AGENT_DATA 的測試暫存資料夾）</summary>
internal sealed class AppPathsScope : IDisposable
{
    public AppPathsScope(string root) => AppPaths.Override(root);

    public void Dispose() => AppPaths.Override(null);
}
