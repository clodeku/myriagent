using System.Text.RegularExpressions;

namespace Myriagent.Remote;

/// <summary>高風險的遠端執行指令：<paramref name="Category"/> 是給人看的種類，<paramref name="Match"/> 是指令裡符合的那一段（截短）</summary>
internal sealed record RemoteExecRisk(string Category, string Match);

/// <summary>
/// 遠端執行指令的高風險判斷（2026-10-25 擁有者要求「高風險例如 UAC 的操作都要到該電腦同意」；docs/sync-protocol.md 14.11）：
/// 符合的指令先在被操作的電腦跳出確認，那台的人按同意才執行（<see cref="RemoteExecRules.ConfirmTimeout"/> 內沒有回應當成拒絕）。
/// <para>
/// 這是給代理誤操作的安全網、不是安全邊界：腳本檔、別名、組字串都繞得過。真正的邊界是指令一律以一般權限執行（app 以系統管理員執行時整個不收），
/// 需要系統管理員的操作由 Windows 的 UAC 在那台電腦的桌面上同意。所以寧可多問：關機與結束系統程序、格式化與磁碟分割、開機與復原設定、
/// 要求或繞過系統管理員權限、系統範圍的登錄檔、刪除系統資料夾／使用者資料夾的根／整顆磁碟、帳戶與檔案權限、防毒防火牆與安全設定、
/// 服務與驅動程式、移除已安裝的程式、看不到內容的指令（編碼過的、下載後直接執行的）。
/// </para>
/// 比對前轉小寫、拿掉 cmd 的 <c>^</c> 與 PowerShell 的反引號（跳脫字元），再另外以拿掉引號的版本比一次（<c>sh"u"tdown</c>）。純函式
/// </summary>
internal static class RemoteExecRiskRules
{
    public const string Power = "關機、重新開機、睡眠或結束系統程序";

    public const string Disk = "格式化或變更磁碟分割";

    public const string Boot = "變更開機、復原或備份設定";

    public const string Elevation = "要求或繞過系統管理員權限";

    public const string Registry = "變更系統範圍的登錄檔";

    public const string Delete = "刪除系統資料夾、使用者資料夾或整顆磁碟";

    public const string Accounts = "變更使用者帳戶或檔案權限";

    public const string Security = "變更防毒、防火牆或安全設定";

    public const string Services = "變更服務或驅動程式";

    public const string Uninstall = "移除已安裝的程式";

    public const string Hidden = "執行看不到內容的指令（編碼過或下載後直接執行）";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>單字邊界：前後不是英數字、底線或連字號（PowerShell 的動詞-名詞、參數名）</summary>
    private const string B = @"(?<![\w-])";

    private const string E = @"(?![\w-])";

    /// <summary>一段路徑的結尾：字串尾、空白、引號、分隔符號</summary>
    private const string End = @"(?=$|[\s'"";|&),])";

    private static readonly (string Category, Regex Pattern)[] Single =
    [
        (Power, R($@"{B}(shutdown|psshutdown|tsshutdn|logoff)(\.exe)?{E}")),
        (Power, R($@"{B}(restart|stop)-computer{E}")),
        (Power, R(@"setsuspendstate|exitwindowsex|initiatesystemshutdown")),
        (Power, R($@"{B}(stop-process|spps|kill|taskkill(\.exe)?)\b[^\n;|&]*{B}(csrss|wininit|winlogon|lsass|smss|services|svchost|dwm)(\.exe)?{E}")),

        (Disk, R($@"{B}format(\.com)?\s+['""]?[a-z]:")),
        (Disk, R($@"{B}(diskpart|mkfs[\w.]*)(\.exe)?{E}")),
        (Disk, R($@"{B}(format-volume|clear-disk|initialize-disk|set-disk|remove-partition|new-partition|resize-partition|set-partition){E}")),
        (Disk, R($@"{B}cipher(\.exe)?\b[^\n;|&]*\s/w")),
        (Disk, R($@"{B}chkdsk(\.exe)?\b[^\n;|&]*\s/[frx]{E}")),
        (Disk, R($@"{B}convert(\.exe)?\s+[a-z]:\s*/fs")),
        (Disk, R($@"{B}dd\s+[^\n;|&]*\bof=")),

        (Boot, R($@"{B}(bcdedit|bcdboot|bootrec|bootsect|reagentc)(\.exe)?{E}")),
        (Boot, R($@"{B}(vssadmin|wbadmin)(\.exe)?\b[^\n;|&]*\b(delete|resize)\b")),
        (Boot, R($@"{B}wmic(\.exe)?\b[^\n;|&]*shadowcopy[^\n;|&]*\bdelete\b")),
        (Boot, R($@"{B}(disable-computerrestore|enable-computerrestore|restore-computer){E}")),

        (Elevation, R($@"{B}(runas|sudo|gsudo|psexec|psexec64|paexec)(\.exe)?{E}")),
        (Elevation, R(@"-verb\s*:?\s*['""]?runas")),
        (Elevation, R($@"{B}nircmd\w*(\.exe)?\s+elevate")),
        (Elevation, R($@"{B}schtasks(\.exe)?\b[^\n;|&]*/(rl\s+highest|ru\s+['""]?(system|nt authority))")),
        (Elevation, R(@"-runlevel\s+['""]?highest")),
        // 不跳 UAC 就提權的常見手法：自動提權的系統程式＋目前使用者登錄檔裡的開啟指令
        (Elevation, R($@"{B}(fodhelper|computerdefaults|sdclt|eventvwr|cmstp|wsreset|slui)(\.exe)?{E}")),
        (Elevation, R(@"(hkcu|hkey_current_user)[:\\/][^\n;|&]*\\(ms-settings|mscfile|exefile|folder|launcher\.systemsettings)\\shell")),

        (Registry, R($@"{B}reg(\.exe)?\s+(import|restore|load|unload){E}")),
        (Registry, R($@"{B}reg(\.exe)?\s+(add|delete|copy)\s+['""]?(hklm|hkey_local_machine|hkcr|hkey_classes_root|hku|hkey_users)\b")),
        (Registry, R($@"{B}(regedit|regini)(\.exe)?{E}[^\n;|&]*\s/s{E}|{B}regini(\.exe)?{E}")),

        (Accounts, R($@"{B}net1?(\.exe)?\s+(user|localgroup|accounts)\s+\S")),
        (Accounts, R($@"{B}((new|remove|set|enable|disable|rename)-localuser|(add|remove)-localgroupmember|(new|remove|rename|set)-localgroup){E}")),
        (Accounts, R($@"{B}(takeown|cacls)(\.exe)?{E}")),
        (Accounts, R($@"{B}icacls(\.exe)?\b[^\n;|&]*\s/(grant|deny|remove|setowner|reset|inheritance|restore|setintegritylevel){E}")),
        (Accounts, R($@"{B}set-acl{E}")),

        (Security, R($@"{B}(set|add|remove)-mppreference{E}")),
        (Security, R($@"{B}netsh(\.exe)?\b[^\n;|&]*\b(advfirewall|firewall)\b[^\n;|&]*\b(set|add|delete|reset|import)\b")),
        (Security, R($@"{B}(set|new|remove|disable|enable)-netfirewall(profile|rule){E}")),
        (Security, R($@"{B}(manage-bde|secedit)(\.exe)?{E}")),
        (Security, R($@"{B}(enable|disable|suspend|resume)-bitlocker{E}")),
        (Security, R($@"{B}auditpol(\.exe)?\b[^\n;|&]*/(set|clear|remove){E}")),
        (Security, R($@"{B}wevtutil(\.exe)?\s+(cl|clear-log|sl|set-log){E}")),
        (Security, R($@"{B}(clear-eventlog|set-processmitigation){E}")),

        (Services, R($@"{B}sc(\.exe)?\s+(create|delete|config|failure|sdset){E}")),
        (Services, R($@"{B}(new|remove|set)-service{E}")),
        (Services, R($@"{B}pnputil(\.exe)?\b[^\n;|&]*/(delete-driver|add-driver|remove-device|disable-device){E}")),
        (Services, R($@"{B}dism(\.exe)?\b[^\n;|&]*/(disable-feature|remove-package|remove-capability|remove-driver|set-edition){E}")),
        (Services, R($@"{B}(disable-windowsoptionalfeature|uninstall-windowsfeature|remove-windowscapability){E}")),

        (Uninstall, R($@"{B}winget(\.exe)?\s+(uninstall|remove|rm){E}")),
        (Uninstall, R($@"{B}msiexec(\.exe)?\b[^\n;|&]*\s[/-]x")),
        (Uninstall, R($@"{B}(uninstall-package|remove-appxpackage|remove-appxprovisionedpackage|uninstall-module){E}")),
        (Uninstall, R($@"{B}(choco|scoop)(\.exe)?\s+uninstall{E}")),

        // 另開 PowerShell 的 -EncodedCommand（參數名可以只打前面幾個字）
        (Hidden, R($@"{B}(powershell|pwsh)(\.exe)?\b[^\n;|&]*\s-e(c|n(c(o(d(e(d(c(o(m(m(a(n(d)?)?)?)?)?)?)?)?)?)?)?)?)?\s+['""]?[a-z0-9+/=]{{16,}}")),
        (Hidden, R($@"\|\s*(iex|invoke-expression|sh|bash|zsh|powershell|pwsh)(\.exe)?{E}")),
    ];

    /// <summary>直接執行字串的指令（iex）＋解碼或下載：看不到真正執行的內容</summary>
    private static readonly Regex InvokeExpression = R($@"{B}(iex|invoke-expression){E}|\[scriptblock\]::create");

    private static readonly Regex Fetched = R($@"frombase64string|downloadstring|downloaddata|downloadfile|net\.webclient|{B}(invoke-webrequest|iwr|invoke-restmethod|irm|curl|wget|start-bitstransfer){E}");

    /// <summary>刪除（含清空、鏡像覆寫）的指令</summary>
    private static readonly Regex DeleteVerb = R(
        $@"{B}(remove-item|ri|rm|del|erase|rd|rmdir|rimraf|rmtree|shred|clear-content|clear-recyclebin|format-volume)(\.exe)?{E}" +
        @"|\[(system\.)?io\.(file|directory)\]::delete|\.delete\(|" +
        $@"{B}robocopy(\.exe)?\b[^\n;|&]*\s/(mir|purge){E}");

    /// <summary>刪除時要確認的位置：系統資料夾（含底下）、使用者資料夾與整顆磁碟的根</summary>
    private static readonly Regex ProtectedPath = R(
        // 整顆磁碟的根：C:、C:\、C:\*（前面不是英數字，排除 http: 這類）
        $@"(?<![\w\\/.:])[a-z]:[\\/]?\*?{End}" +
        // 系統資料夾與底下
        $@"|(?<!\w)[a-z]:[\\/]+(windows|program files( \(x86\))?|programdata|system volume information|\$recycle\.bin|recovery|boot|efi)(?=$|[\\/\s'"";|&),*])" +
        // 使用者資料夾的根（C:\Users、C:\Users\*）
        $@"|(?<!\w)[a-z]:[\\/]+users[\\/]*\*?{End}" +
        // 系統資料夾的環境變數（含底下）
        @"|(\$env:|%)(windir|systemroot|programfiles|programfiles\(x86\)|programw6432|programdata|commonprogramfiles)\b" +
        // 磁碟與家目錄的根
        $@"|(\$env:(systemdrive|userprofile|homedrive)|%(systemdrive|userprofile|homedrive)%|\$home|(?<![\w~])~)[\\/]*\*?{End}" +
        // Git Bash 的根與磁碟（/、/*、/c/、/c/windows）
        $@"|(?<![\w.~$/-])/\*?{End}|(?<!\w)/[a-z]/\*?{End}|(?<!\w)/[a-z]/(windows|program files|programdata|users/?{End})");

    /// <summary>高風險時回原因；不是高風險回 null。<paramref name="shell"/> 目前不影響判斷（各殼層的寫法一起比）</summary>
    public static RemoteExecRisk? Classify(string? shell, string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var plain = command.ToLowerInvariant().Replace("^", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        var unquoted = plain.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);
        return Check(plain) ?? (unquoted == plain ? null : Check(unquoted));
    }

    private static RemoteExecRisk? Check(string text)
    {
        foreach (var (category, pattern) in Single)
        {
            var match = pattern.Match(text);
            if (match.Success)
                return new RemoteExecRisk(category, Clip(match.Value));
        }

        var invoke = InvokeExpression.Match(text);
        if (invoke.Success && Fetched.IsMatch(text))
            return new RemoteExecRisk(Hidden, Clip(invoke.Value));

        var delete = DeleteVerb.Match(text);
        if (delete.Success && ProtectedPath.Match(text) is { Success: true } path)
            return new RemoteExecRisk(Delete, Clip(delete.Value.Trim() + " … " + path.Value.Trim()));
        return null;
    }

    private static Regex R(string pattern) => new(pattern, Options, TimeSpan.FromSeconds(1));

    private static string Clip(string value)
    {
        var text = value.Trim();
        return text.Length <= 80 ? text : text[..80] + "…";
    }
}
