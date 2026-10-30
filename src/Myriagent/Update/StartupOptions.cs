using System.Globalization;
using System.Security.Cryptography;
using Myriagent.UI;

namespace Myriagent.Update;

/// <summary>
/// 命令列：--data &lt;資料夾&gt;；換版幫手 --apply-update --to &lt;release&gt; --wait-pid &lt;pid&gt; --token &lt;token&gt; [--no-relaunch]；
/// 換版後 --after-update &lt;token&gt;、退回後 --after-update-failed &lt;token&gt;、較舊的實例交棒後 --handed-over &lt;token&gt;；--uninstall；--install-here（端到端測試：不問就安裝）；
/// 登入 Windows 時由 Run 機碼啟動 --autostart（AutoStartRules）
/// </summary>
internal sealed record StartupOptions
{
    public string? DataRoot { get; init; }

    public bool ApplyUpdate { get; init; }

    public string? To { get; init; }

    public int? WaitPid { get; init; }

    public string? Token { get; init; }

    public bool NoRelaunch { get; init; }

    public string? AfterUpdateToken { get; init; }

    public string? AfterUpdateFailedToken { get; init; }

    /// <summary>
    /// --handed-over &lt;token&gt;：較舊的實例排空後啟動這一版（新版自動接手舊版，InstanceHandoverRules）。
    /// 接續同 --after-update（resume.json 的 token 相符才接續），但沒有換版幫手在等健康訊號
    /// </summary>
    public string? HandedOverToken { get; init; }

    public bool Uninstall { get; init; }

    public bool InstallHere { get; init; }

    /// <summary>--autostart：登入 Windows 時由 Run 機碼啟動（設定頁「開機時自動啟動」）；主視窗要不要藏著見 AutoStartRules.StartHidden</summary>
    public bool AutoStart { get; init; }

    /// <summary>--apply-update 的參數不完整或格式不對（幫手不做任何事、以結束碼 2 結束）</summary>
    public string? Error { get; init; }

    public static bool IsValidToken(string? token)
        => token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == name)
                    return args[i + 1];
            }
            return null;
        }

        bool Flag(string name) => args.Contains(name);

        var options = new StartupOptions
        {
            DataRoot = Value("--data"),
            ApplyUpdate = Flag("--apply-update"),
            NoRelaunch = Flag("--no-relaunch"),
            Uninstall = Flag("--uninstall"),
            InstallHere = Flag("--install-here"),
            AutoStart = Flag(AutoStartRules.Argument),
            AfterUpdateToken = Value("--after-update") is { } after && IsValidToken(after) ? after : null,
            AfterUpdateFailedToken = Value("--after-update-failed") is { } failed && IsValidToken(failed) ? failed : null,
            HandedOverToken = Value(InstanceHandoverRules.HandedOverArgument) is { } handed && IsValidToken(handed) ? handed : null,
        };
        if (!options.ApplyUpdate)
            return options;

        var to = Value("--to");
        var token = Value("--token");
        int? pid = int.TryParse(Value("--wait-pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : null;
        string? error = null;
        if (!ReleaseIdentity.TryParseRelease(to, out _))
            error = "--to 不是合法的 release";
        else if (pid == null)
            error = "--wait-pid 不合法";
        else if (!IsValidToken(token))
            error = "--token 不合法";
        return options with { To = to, WaitPid = pid, Token = token, Error = error };
    }
}
