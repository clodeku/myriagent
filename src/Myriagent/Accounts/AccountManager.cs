using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>
/// 多帳戶管理：帳戶清單（accounts.json）、隔離的設定資料夾、登入狀態檢查、登入／登出、用量輪詢與合併、憑證檔監看。
/// 所有狀態只在 UI 執行緒讀寫（背景查詢完成後經 post 回到 UI 執行緒），事件也在 UI 執行緒觸發。
/// 第一次啟動自動加入這台電腦的預設 Claude 與 Codex 帳戶（沿用使用者原本的登入）。
/// </summary>
internal sealed class AccountManager : IDisposable
{
    private sealed record CodexProbe(AccountStatus Status, string? Email, string? Plan, JObject? Limits, string? Error, IReadOnlyList<AgentModelOption>? Models = null);

    private sealed record GrokProbe(AccountStatus Status, string? Email, string? Plan, JObject? Billing, string? Error, IReadOnlyList<AgentModelOption>? Models = null);

    /// <summary>一次登入：取消來源（使用者按取消、帳戶被移除）＋登入程序是否已關閉（移除帳戶要等它關閉才刪資料夾）</summary>
    private sealed class LoginRun
    {
        public LoginRun(CancellationToken lifetime) => Cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        public CancellationTokenSource Cancel { get; }

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 遠端登入（WP4.8 D7）這一次的里程碑：Codex 拿到合格的驗證碼、Claude 的主控台已啟動時設結果，這次登入在里程碑之前結束時設 Failed。
        /// 建立這次登入時帶入（只會送到這一次，另一個本機登入的里程碑不會被誤認）；本機登入是 null
        /// </summary>
        public TaskCompletionSource<RemoteLoginStart>? RemoteStart { get; init; }

        /// <summary>遠端登入時目錄的預期身分（D12、D14；登入完成後比對）；本機登入是 null</summary>
        public RemoteLoginExpectation? Expectation { get; init; }

        /// <summary>Codex：登入完成後在同一條 app-server 連線以 account/read 讀到的信箱（D12；離開連線之後才比對）</summary>
        public string? CodexEmail { get; set; }

        /// <summary>Grok：登入完成後在同一條 ACP 連線以 _x.ai/auth/info 讀到的信箱（同 CodexEmail）</summary>
        public string? GrokEmail { get; set; }

        /// <summary>Antigravity：登入完成後以 agy 的記錄讀到的信箱（設計 2026-10-22-grok-antigravity-sync D1）</summary>
        public string? AntigravityEmail { get; set; }
    }

    /// <summary>移除帳戶時沒刪乾淨的資料夾旁邊留的待刪標記（accounts\{Id}.deleting）：不會被當成帳戶還原，下次啟動再刪</summary>
    private const string PendingDeleteSuffix = ".deleting";

    /// <summary>這個帳戶還不知道模型清單（中樞提供；true 時查狀態順便探出模型與「/」指令）</summary>
    public Func<string, bool>? NeedsCapabilities { get; set; }

    /// <summary>探出帳戶的模型與「/」指令（UI 執行緒）</summary>
    public event Action<string, AccountCapabilities>? CapabilitiesDiscovered;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ClaudeActivePoll = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClaudeIdlePoll = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CodexActivePoll = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CodexIdlePoll = TimeSpan.FromMinutes(15);

    /// <summary>Antigravity：以 agy -p /usage --output-format json 查額度（對話中沒有推送，每輪結束另外重查）</summary>
    private static readonly TimeSpan AntigravityActivePoll = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AntigravityIdlePoll = TimeSpan.FromMinutes(15);

    /// <summary>Grok：額度只有每週（或每月）一個窗，對話中沒有推送，每輪結束另外重查</summary>
    private static readonly TimeSpan GrokActivePoll = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GrokIdlePoll = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    /// <summary>CLI 正在更新（屏障或安裝變動）時暫停的查詢，多久後再試</summary>
    private static readonly TimeSpan CliPausedRetry = TimeSpan.FromSeconds(30);

    private readonly Action<Action> post;
    private readonly CliManager cli;
    private readonly ClaudeUsageClient claudeUsage;
    private readonly UsageHistoryStore history;
    private readonly Func<string, int> runningSessions;
    private readonly List<AgentAccount> accounts = new();
    private readonly Dictionary<string, AccountRuntimeState> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UsageSnapshot> usage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> nextPoll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Task Task, bool IncludeStatus)> refreshTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> soonPoll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> errorCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoginRun> logins = new(StringComparer.Ordinal);

    /// <summary>
    /// 遠端登入進行中、身分還沒比對完的帳戶（WP4.8 D14）：期間的查詢一律延後、進行中的查詢回來也不套用，
    /// 錯的身分（登到別人的帳號）在登出之前不會寫進帳戶、也不會發布。在登入的 try 之內登記、finally 放開
    /// </summary>
    private readonly HashSet<string> identityHold = new(StringComparer.Ordinal);

    /// <summary>有登入視窗開著（任一帳戶；UI 執行緒）：app 更新不在這時候重啟</summary>
    public bool HasActiveLogin => logins.Count > 0;

    /// <summary>這個帳戶的查詢正因遠端登入的身分比對暫停（D14；UI 執行緒）</summary>
    internal bool IsIdentityHeld(string accountId) => identityHold.Contains(accountId);

    /// <summary>
    /// 保守模式的帳戶（設計 6.5：憑證檔不存在、權杖存在 Windows 認證管理員，看不到期限）：不跑任何短命的 claude，
    /// 登入狀態只看 CLI 設定檔（oauthAccount 被清掉＝在外部登出了）
    /// </summary>
    private readonly HashSet<string> conservative = new(StringComparer.Ordinal);

    /// <summary>對話剛跑完一輪、下次輪詢要重新看一次 CLI 設定檔的帳戶（保守模式；只在檢查真的開始時才移除）</summary>
    private readonly HashSet<string> statusAfterTurn = new(StringComparer.Ordinal);

    /// <summary>
    /// 沒做成、要補做的 Claude 登入狀態檢查與它的理由（CLI 更新期間被暫停、憑證或 CLI 設定暫時讀不到）：
    /// 下次輪詢以這個理由補查，檢查真的開始時才移除（暫停期間憑證檔變動、使用者按的更新都不會就此消失）
    /// </summary>
    private readonly Dictionary<string, ClaudeStatusReason> pendingStatus = new(StringComparer.Ordinal);

    /// <summary>Claude 帳戶讓 CLI 換發權杖的狀況（ClaudeTokenRecoveryRules；UI 執行緒）</summary>
    private sealed class TokenRefreshState
    {
        /// <summary>上一次開始換發的時間（兩次之間至少隔 RefreshMinInterval）</summary>
        public DateTimeOffset? LastAttempt { get; set; }

        /// <summary>換發用的 claude -p 還在跑（移除帳戶時要等它結束才刪資料夾）</summary>
        public Task? Running { get; set; }

        /// <summary>只在帳戶資料夾要刪掉時才取消（結束程序）：帳戶的生命週期取消不連動，換發途中被結束，refresh token 可能已經用掉卻沒寫回</summary>
        public CancellationTokenSource? Cancel { get; set; }

        /// <summary>上一次沒成功的原因；成功後清掉</summary>
        public string? LastError { get; set; }
    }

    private readonly Dictionary<string, TokenRefreshState> tokenRefresh = new(StringComparer.Ordinal);

    /// <summary>權杖過期、正借用其他登入查用量的帳戶與那個登入的名稱（記錄用：換了來源才寫記錄）</summary>
    private readonly Dictionary<string, string> borrowedFrom = new(StringComparer.Ordinal);

    /// <summary>每個帳戶的登入與查詢共用的取消來源：移除帳戶時取消，登入主控台、app-server 先結束，才放開資料夾裡的檔案</summary>
    private readonly Dictionary<string, CancellationTokenSource> lifetimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly System.Threading.Timer timer;
    private bool disposed;

    /// <summary>帳戶清單、狀態或用量改變（UI 執行緒）</summary>
    public event Action? Changed;

    /// <summary>登入流程的進度（帳戶、訊息、是否結束、是否成功）</summary>
    public event Action<string, string, bool, bool?>? LoginProgress;

    /// <summary>遠端登入的身分和目錄不同（WP4.8 D15；UI 執行緒）：帳戶、在同一個資料夾的登出是否成功。不帶信箱</summary>
    public event Action<string, bool>? RemoteIdentityMismatch;

    /// <summary>
    /// 存下一份新看到的用量快照（UI 執行緒）：這台查到的（定期查詢、對話中的 rate_limit_event／rateLimits/updated）與同步來、取代目前值的共享用量。
    /// 數字沒變也發（中樞的每週 5% 分段紀錄要每一次的觀察，UsageSegmentRules）；推算的重置（快取）不發
    /// </summary>
    public event Action<string, UsageSnapshot>? UsageObserved;

    public AccountManager(Action<Action> post, CliManager cli, HttpClient http, Func<string, int> runningSessions)
    {
        this.post = post;
        this.cli = cli;
        this.runningSessions = runningSessions;
        claudeUsage = new ClaudeUsageClient(http);
        history = new UsageHistoryStore(AppPaths.UsageDirectory);
        ResolveLauncher = cli.GetLauncher;
        timer = new System.Threading.Timer(_ => post(Tick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>取得 CLI 的啟動方式（沒安裝回 null）；預設由 CliManager 找，測試換成假的 CLI</summary>
    internal Func<AgentProvider, AgentLauncher?> ResolveLauncher { get; set; }

    /// <summary>
    /// 用量查詢的閘門（多電腦同步，設計 6.6）：false 時這次不查官方用量端點（Claude 的 /api/oauth/usage、Codex 的 account/rateLimits/read），
    /// 不標過期、不改用量；定期輪詢與手動更新一樣（都走 RefreshCoreAsync）。null＝一律查（未配對、沒有同步）
    /// </summary>
    public IUsagePollPolicy? UsagePollPolicy { get; set; }

    /// <summary>
    /// 憑證檔最上層的 organizationUuid 能不能當成 orgId（D4 的閘門；預設照 AccountIdentityRules.CredentialsOrgConfirmed，E-13 證明相等才打開）。
    /// 測試接縫
    /// </summary>
    internal bool UseCredentialsOrg { get; set; } = AccountIdentityRules.CredentialsOrgConfirmed;

    /// <summary>CLI 設定檔 oauthAccount.organizationUuid 能不能當成 orgId（D4 的閘門；預設照 AccountIdentityRules.GlobalConfigOrgConfirmed）。測試接縫</summary>
    internal bool UseGlobalConfigOrg { get; set; } = AccountIdentityRules.GlobalConfigOrgConfirmed;

    /// <summary>
    /// Claude 帳戶權杖過期、又借不到同身分的其他登入時，要不要讓 CLI 換發（每次問一次，設定改了立即生效）。
    /// 預設關：app 在 AppController 接上設定頁「閒置的 Claude 帳戶自動換發權杖」（實機探針模式一律關）；測試沒打開就不會對真的帳戶跑 claude -p
    /// </summary>
    public Func<bool> ClaudeTokenRefreshEnabled { get; set; } = () => false;

    /// <summary>
    /// 沒登記成帳戶、也可以借來查用量的 Claude 設定資料夾（ClaudeTokenRecoveryRules；同一個資料夾不會算兩次）。
    /// 預設沒有：app 在 AppController 換成 SystemDefaultClaudeUsageSources（這台電腦的預設設定資料夾），測試不會讀到真的 ~/.claude
    /// </summary>
    internal Func<IEnumerable<AgentAccount>> ExtraClaudeUsageSources { get; set; } = Array.Empty<AgentAccount>;

    /// <summary>
    /// 隔離帳戶共用系統預設帳戶的設定（AgentAccount.ShareCustomizations）。預設沒有（不共用）：app 在 AppController 接上
    /// 以這台系統預設設定資料夾為來源的 SharedCustomizations，測試不會讀寫真的 ~/.claude、~/.codex
    /// </summary>
    internal SharedCustomizations? Shared { get; set; }

    /// <summary>這台電腦的預設 Claude 設定資料夾（繼承的 CLAUDE_CONFIG_DIR 或 ~/.claude；只在它沒登記成帳戶時才借得到）</summary>
    internal static IEnumerable<AgentAccount> SystemDefaultClaudeUsageSources()
        => new[] { CreateSystemDefault(AgentProvider.Claude, AccountRules.SystemDefaultId(AgentProvider.Claude)) };

    /// <summary>測試用：這個帳戶正在讓 CLI 換發權杖</summary>
    internal bool IsRefreshingClaudeToken(string accountId) => tokenRefresh.TryGetValue(accountId, out var refresh) && refresh.Running != null;

    /// <summary>測試用：這個帳戶正在借用的登入名稱（沒有借用為 null）</summary>
    internal string? GetBorrowedUsageSource(string accountId) => borrowedFrom.GetValueOrDefault(accountId);

    /// <summary>測試用：這個帳戶進行中的查詢（ReportQuotaExhausted 觸發的重查等）結束；沒有在查時已完成。查詢的錯誤不往外丟</summary>
    internal Task RefreshSettledForTest(string accountId)
        => refreshTasks.TryGetValue(accountId, out var running) ? WaitQuietlyAsync(running.Task) : Task.CompletedTask;

    /// <summary>Codex 裝置碼登入最長等多久（預設 15 分鐘；實際再和驗證碼的到期取較早者）。測試接縫</summary>
    internal TimeSpan DeviceCodeTimeout { get; set; } = CodexDeviceCodeRules.MaxLifetime;

    /// <summary>登入流程開瀏覽器的出口（Codex 一般登入、Grok 登入；裝置碼登入不開）。測試換成計數器</summary>
    internal Action<Uri> OpenBrowser { get; set; } = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    /// <summary>最近一次 LoginAsync 收到的登入選項（測試觀察用：只記方法與有沒有帶預期信箱，不記信箱字串）；還沒登入過是 null</summary>
    internal (LoginMethod Method, bool HasClaudeEmail)? LastLoginRequest { get; private set; }

    /// <summary>遠端登入等里程碑的期限（WP4.8 D8；預設 25 秒，閘門 30 秒沒有完成信號就是 unknown）。測試接縫</summary>
    internal TimeSpan RemoteStartDeadline { get; set; } = RemoteAccountRules.StartDeadline;

    /// <summary>遠端登出、身分不符的自動登出等 CLI 結束的期限（WP4.8 D13、D16；預設 20 秒）。測試接縫</summary>
    internal TimeSpan RemoteLogoutDeadline { get; set; } = RemoteAccountRules.LogoutDeadline;

    /// <summary>這次執行的帳戶清單是唯讀的（accounts.json 讀不到，先以預設帳戶執行、不存檔）：同步不能把清單裡沒有的帳戶當成已移除</summary>
    public bool IsAccountListReadOnly => readOnlyAccounts;

    /// <summary>這次要不要查官方用量端點（閘門；UI 執行緒）</summary>
    private bool ShouldPollUsage(AgentAccount account) => UsagePollPolicy?.ShouldPoll(account) ?? true;

    public IReadOnlyList<AgentAccount> Accounts => accounts;

    public AgentAccount? Find(string? id) => id == null ? null : accounts.FirstOrDefault(a => a.Id == id);

    public AccountRuntimeState GetState(string id)
    {
        if (!states.TryGetValue(id, out var state))
        {
            state = new AccountRuntimeState();
            states[id] = state;
        }
        return state;
    }

    public UsageSnapshot? GetUsage(string id) => usage.TryGetValue(id, out var snapshot) ? snapshot : null;

    public List<double[]> GetHistory(string accountId, string key) => history.GetView(accountId, key, DateTimeOffset.UtcNow);

    /// <summary>帳戶的登入與查詢共用的取消權杖（移除帳戶時取消）</summary>
    private CancellationToken LifetimeOf(string accountId)
    {
        if (!lifetimes.TryGetValue(accountId, out var lifetime))
        {
            lifetime = new CancellationTokenSource();
            lifetimes[accountId] = lifetime;
        }
        return lifetime.Token;
    }

    // ───────────────────────────── 初始化與存檔 ─────────────────────────────

    /// <summary>啟動時要告訴使用者的事（例如帳戶清單損毀已從資料夾還原）；中樞在介面就緒後顯示</summary>
    public string? StartupWarning { get; private set; }

    public void Initialize()
    {
        var (status, saved, backup) = JsonFile.TryRead<List<AgentAccount>>(AppPaths.AccountsFile);
        switch (status)
        {
            case JsonReadStatus.Ok when saved!.Count > 0:
                accounts.AddRange(saved.OrderBy(a => a.Order));
                break;
            case JsonReadStatus.Unreadable:
            {
                // 讀不到（被占用）：不能用預設清單蓋掉原檔；本次先用預設帳戶執行、不存檔
                var recovered = RecoverIsolatedAccounts();
                accounts.AddRange(CreateSystemDefaults(recovered));
                accounts.AddRange(recovered);
                readOnlyAccounts = true;
                StartupWarning = "帳戶清單暫時讀不到（可能被其他程式占用），這次先以本機預設帳戶與資料夾內找到的帳戶執行，不會改動原清單；請重新開啟 app。";
                break;
            }
            default:
            {
                // 不存在（第一次啟動）或損毀（已備份）：系統預設帳戶＋從資料夾找回 app 建立過的隔離帳戶
                var recovered = RecoverIsolatedAccounts();
                accounts.AddRange(CreateSystemDefaults(recovered));
                accounts.AddRange(recovered);
                if (status == JsonReadStatus.Corrupt)
                    StartupWarning = $"帳戶清單已損毀（原檔備份為 {backup}），已從資料夾還原 {recovered.Count} 個帳戶，名稱與顏色需要重新設定。";
                Save();
                break;
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var account in accounts)
        {
            var last = history.GetLastSnapshot(account.Id);
            if (last != null)
                usage[account.Id] = last with { Windows = UsageRules.ApplyResets(last.Windows, now), Stale = true, Source = "cache" };
            PrepareIsolatedFolder(account);
            Watch(account);
            // 啟動時：Claude 帳戶的身分快取有效（憑證檔沒變）就不跑 claude auth status（6.5）
            _ = RefreshAsync(account.Id, includeStatus: true, skipIfRunning: false, ClaudeStatusReason.Startup);
        }

        _ = Task.Run(RetryPendingDeletes);
        timer.Change(TickInterval, TickInterval);
    }

    /// <summary>測試用：直接放入帳戶與狀態（不存檔、不查狀態、不監看、不登入）</summary>
    internal void Seed(IEnumerable<AgentAccount> seeded, AccountStatus status)
    {
        foreach (var account in seeded)
        {
            accounts.Add(account);
            var state = GetState(account.Id);
            state.Status = status;
            state.StatusText = AccountRules.DescribeStatus(account.Provider, status);
        }
    }

    /// <summary>測試用：直接放入用量快照</summary>
    internal void SeedUsage(string accountId, UsageSnapshot snapshot) => usage[accountId] = snapshot;

    /// <summary>測試用：下次輪詢的時間</summary>
    internal DateTimeOffset? GetNextPoll(string accountId) => nextPoll.TryGetValue(accountId, out var due) ? due : null;

    /// <summary>測試用：排定下次輪詢的時間</summary>
    internal void SeedNextPoll(string accountId, DateTimeOffset due) => nextPoll[accountId] = due;

    /// <summary>測試用：是否正在監看帳戶的憑證檔</summary>
    internal bool IsWatching(string accountId) => watchers.ContainsKey(accountId);

    /// <summary>
    /// 測試用：把帳戶清單標成唯讀（Initialize 讀不到 accounts.json 時的狀態）。測試不走 Initialize 的這條路：
    /// 它會建出指向使用者真正 ~/.claude、~/.codex 的預設帳戶並開始查詢
    /// </summary>
    internal void SeedListReadOnly() => readOnlyAccounts = true;

    private bool readOnlyAccounts;

    /// <summary>
    /// 本機預設帳戶（帳戶清單遺失、損毀或讀不到時重建）：沿用原本的 Id，既有對話、用量歷史與模型設定才接得回來。
    /// 現在建立的一律用固定 Id；舊版以隨機 Id 建立的從用量歷史找回——移除帳戶時用量歷史檔會一起刪掉，
    /// 所以不屬於隔離帳戶、時間窗看得出是這種代理的歷史檔就是它的本機預設帳戶（正好一個才採用，不確定就用固定 Id）。
    /// </summary>
    internal List<AgentAccount> CreateSystemDefaults(IEnumerable<AgentAccount> recovered)
    {
        var known = history.ListAccountIds();
        var excluded = recovered.Select(a => a.Id)
            .Concat(AgentProviders.All.Select(AccountRules.SystemDefaultId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string IdFor(AgentProvider provider)
        {
            var fixedId = AccountRules.SystemDefaultId(provider);
            if (known.Contains(fixedId, StringComparer.OrdinalIgnoreCase))
                return fixedId;
            var legacy = known.Where(id => !excluded.Contains(id) && AccountRules.GuessProviderFromUsageKeys(history.GetKeys(id)) == provider).ToList();
            return legacy.Count == 1 ? legacy[0] : fixedId;
        }

        return AgentProviders.All.Select(provider => CreateSystemDefault(provider, IdFor(provider))).ToList();
    }

    private static AgentAccount CreateSystemDefault(AgentProvider provider, string id)
    {
        var (label, color) = AccountRules.SystemDefaultAppearance(provider);
        return new AgentAccount { Id = id, Provider = provider, Label = label, Color = color };
    }

    /// <summary>
    /// 清單裡沒有這種代理的帳戶時補上它的本機預設帳戶（升級後第一次啟動補上 Antigravity；呼叫端記下「已補過」，使用者之後移除不會再補）。
    /// 回傳 true＝清單裡已經有、或已補上並存檔；false＝帳戶清單唯讀（這次讀不到原檔），沒有存檔，下次啟動要再補
    /// </summary>
    public bool EnsureSystemDefault(AgentProvider provider)
    {
        if (accounts.Any(a => a.Provider == provider))
            return true;
        if (readOnlyAccounts)
            return false;
        var account = AddSystemDefault(provider, null, null);
        _ = RefreshAsync(account.Id, includeStatus: true);
        return true;
    }

    /// <summary>加入本機預設帳戶（Antigravity 只有這一種帳戶）：存檔、開始檢查狀態</summary>
    private AgentAccount AddSystemDefault(AgentProvider provider, string? label, string? color)
    {
        var account = CreateSystemDefault(provider, AccountRules.SystemDefaultId(provider));
        if (!string.IsNullOrWhiteSpace(label))
            account.Label = label.Trim().Length <= 40 ? label.Trim() : label.Trim()[..40];
        if (AccountRules.NormalizeColor(color) is { } c)
            account.Color = c;
        accounts.Add(account);
        Save();
        SetStatus(account, AccountStatus.Checking, null);
        Watch(account);
        RaiseChanged();
        return account;
    }

    /// <summary>
    /// 帳戶清單遺失或損毀時，從 accounts\{id}\claude、accounts\{id}\codex 資料夾找回 app 建立過的隔離帳戶
    /// （登入憑證還在資料夾裡，只是名稱與顏色要重設）；已移除、留有待刪標記的資料夾不算
    /// </summary>
    internal static List<AgentAccount> RecoverIsolatedAccounts()
    {
        var result = new List<AgentAccount>();
        if (!Directory.Exists(AppPaths.AccountsDirectory))
            return result;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.AccountsDirectory))
            {
                var id = Path.GetFileName(dir);
                if (!IsAccountFolderName(id) || File.Exists(dir + PendingDeleteSuffix))
                    continue;
                foreach (var provider in AgentProviders.All.Where(AccountRules.SupportsIsolatedAccounts))
                {
                    var config = Path.Combine(dir, IsolatedFolderName(provider));
                    if (!Directory.Exists(config))
                        continue;
                    result.Add(new AgentAccount
                    {
                        Id = id,
                        Provider = provider,
                        Label = $"{AccountRules.ProviderName(provider)} 帳戶（還原 {id[..6]}）",
                        // 還原的帳戶用第二順位的顏色，和本機預設帳戶區分
                        Color = provider switch
                        {
                            AgentProvider.Claude => "#E5A04B",
                            AgentProvider.Codex => "#4FA3F7",
                            AgentProvider.Grok => "#3FB950",
                            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
                        },
                        ConfigDirectory = config,
                    });
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"從資料夾還原帳戶失敗：{ex.Message}");
        }
        return result;
    }

    /// <summary>隔離帳戶的設定資料夾在 accounts\{id}\ 底下的名稱</summary>
    private static string IsolatedFolderName(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "claude",
        AgentProvider.Codex => "codex",
        AgentProvider.Grok => "grok",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "這種代理沒有隔離帳戶"),
    };

    /// <summary>
    /// 隔離帳戶的資料夾要先放好的設定：Grok 的 config.toml 標記「Claude 設定已匯入過」，grok 才不會讀使用者的 ~/.claude
    /// （新增時、每次啟動時檢查，還原的資料夾也補上）
    /// </summary>
    private static void PrepareIsolatedFolder(AgentAccount account)
    {
        if (account.Provider == AgentProvider.Grok && !account.IsSystemDefault && !string.IsNullOrWhiteSpace(account.ConfigDirectory))
            GrokAcpRules.EnsureIsolatedHome(account.ConfigDirectory);
    }

    /// <summary>app 建立的帳戶資料夾名稱（帳戶 Id：32 碼十六進位）</summary>
    private static bool IsAccountFolderName(string name) => name.Length == 32 && name.All(Uri.IsHexDigit);

    /// <summary>上次移除帳戶時沒刪乾淨的資料夾（留有待刪標記）：再刪一次，刪掉了就清掉標記（啟動時在背景執行）</summary>
    internal static void RetryPendingDeletes()
    {
        try
        {
            if (!Directory.Exists(AppPaths.AccountsDirectory))
                return;
            foreach (var marker in Directory.EnumerateFiles(AppPaths.AccountsDirectory, "*" + PendingDeleteSuffix).ToList())
            {
                var root = marker[..^PendingDeleteSuffix.Length];
                if (!IsAccountFolderName(Path.GetFileName(root)))
                    continue;
                if (TryDeleteDirectory(root))
                    TryDeleteFile(marker);
                else
                    Log.Warn($"已移除帳戶的資料夾仍有檔案使用中，下次啟動再刪：{root}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪除已移除帳戶的資料夾失敗：{ex.Message}");
        }
    }

    private void Save()
    {
        if (readOnlyAccounts)
            return;
        for (int i = 0; i < accounts.Count; i++)
            accounts[i].Order = i;
        try
        {
            JsonFile.Write(AppPaths.AccountsFile, accounts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"帳戶存檔失敗：{ex.Message}");
        }
    }

    private void RaiseChanged()
    {
        if (!disposed)
            Changed?.Invoke();
    }

    // ───────────────────────────── 帳戶增刪改 ─────────────────────────────

    /// <summary>
    /// 新增獨立帳戶（建立隔離的設定資料夾）並開始登入。不支援隔離帳戶的代理（Antigravity）改為加回本機預設帳戶：
    /// 已經有就丟 InvalidOperationException（訊息給使用者看）；加回後檢查狀態，沒登入才開登入視窗。
    /// claim＝「在這台登入」帳戶目錄的邏輯帳戶（WP3.4）：只有 Claude、Codex（其他種類丟 ArgumentException；中樞先以
    /// AccountIdentityRules.CheckCatalogClaim 擋下），名稱與顏色照目錄的規則清理，存下宣告，Claude 登入帶目錄的預期信箱。
    /// startLogin＝false 時只建立、不開始登入（WP4.8 D11：遠端登入接著以 StartRemoteLoginAsync 開始；本機預設帳戶只檢查狀態）
    /// </summary>
    public AgentAccount Add(AgentProvider provider, string? label, string? color, AccountClaim? claim = null, bool startLogin = true)
    {
        if (claim is not null)
        {
            switch (provider)
            {
                case AgentProvider.Claude:
                case AgentProvider.Codex:
                case AgentProvider.Grok:
                    break;
                case AgentProvider.Antigravity:
                    throw new ArgumentException($"{AccountRules.ProviderName(provider)} 一台電腦只有一個登入，不能從目錄在這台登入", nameof(provider));
                default:
                    throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
            }
        }
        else if (!AccountRules.SupportsIsolatedAccounts(provider))
        {
            if (accounts.Any(a => a.Provider == provider))
                throw new InvalidOperationException($"{AccountRules.ProviderName(provider)} CLI 一台電腦只能登入一個帳戶，帳戶清單裡已經有了");
            var added = AddSystemDefault(provider, label, color);
            _ = startLogin ? RefreshThenLoginAsync(added.Id) : RefreshAsync(added.Id, includeStatus: true);
            return added;
        }

        var account = claim is null
            ? new AgentAccount
            {
                Provider = provider,
                Label = string.IsNullOrWhiteSpace(label) ? AccountRules.DefaultLabel(provider, accounts) : label.Trim(),
                Color = AccountRules.NormalizeColor(color) ?? AccountRules.DefaultColor(provider, accounts),
            }
            : new AgentAccount
            {
                Provider = provider,
                Label = AccountRules.CleanCatalogLabel(label) ?? AccountRules.DefaultLabel(provider, accounts),
                Color = AccountRules.NormalizeCatalogColor(color) ?? AccountRules.DefaultColor(provider, accounts),
                LogicalAccountId = claim.LogicalAccountId,
            };
        account.ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, account.Id, IsolatedFolderName(provider));
        Directory.CreateDirectory(account.ConfigDirectory);
        PrepareIsolatedFolder(account);
        accounts.Add(account);
        Save();
        GetState(account.Id).Status = AccountStatus.NotLoggedIn;
        GetState(account.Id).StatusText = AccountRules.DescribeStatus(provider, AccountStatus.NotLoggedIn);
        Watch(account);
        RaiseChanged();
        if (startLogin)
            _ = LoginAsync(account.Id, claim is null ? null : new LoginRequest(ClaudeEmail: claim.ExpectedEmail));
        return account;
    }

    public void Update(string id, string? label, string? color, bool? safeMode, bool? shareCustomizations = null)
    {
        var account = Find(id);
        if (account == null)
            return;
        if (!string.IsNullOrWhiteSpace(label))
            account.Label = label.Trim().Length <= 40 ? label.Trim() : label.Trim()[..40];
        if (AccountRules.NormalizeColor(color) is { } c)
            account.Color = c;
        if (safeMode.HasValue && AccountRules.SupportsSafeMode(account.Provider))
            account.SafeMode = safeMode.Value;
        if (shareCustomizations.HasValue && !account.IsSystemDefault && AccountRules.SupportsSharedCustomizations(account.Provider)
            && account.ShareCustomizations != shareCustomizations.Value)
        {
            account.ShareCustomizations = shareCustomizations.Value;
            // 關掉：拿掉指向來源的連結、移回原件（背景執行緒；打開不用做什麼，下次啟動 CLI 時才同步與連結）
            if (!shareCustomizations.Value && Shared is { } shared)
            {
                var (provider, directory) = (account.Provider, account.ConfigDirectory!);
                _ = Task.Run(() => shared.Unshare(provider, directory));
            }
        }
        Save();
        RaiseChanged();
    }

    public void Reorder(IReadOnlyList<string> ids)
    {
        var ordered = ids.Select(Find).Where(a => a != null).Select(a => a!).ToList();
        ordered.AddRange(accounts.Where(a => !ordered.Contains(a)));
        accounts.Clear();
        accounts.AddRange(ordered);
        Save();
        RaiseChanged();
    }

    /// <summary>
    /// 套用帳戶目錄（另一台電腦改的）的名稱與顏色：名稱以 CleanCatalogLabel 清理（上限 60 字），顏色以 NormalizeCatalogColor 處理（#RGB 展開）；
    /// 清理後不合格的那一項不改。值都相同回 false（不存檔、不通知）；有改變才存檔並通知一次。不經 Update：那是本機輸入（截到 40 字），
    /// 而且同步的掛鉤在處理網頁指令的地方，這裡套用伺服器的值不會回送
    /// </summary>
    public bool ApplyCatalogAppearance(string accountId, string label, string color)
    {
        var account = Find(accountId);
        if (account == null)
            return false;
        var newLabel = AccountRules.CleanCatalogLabel(label) ?? account.Label;
        var newColor = AccountRules.NormalizeCatalogColor(color) ?? account.Color;
        if (newLabel == account.Label && newColor == account.Color)
            return false;
        account.Label = newLabel;
        account.Color = newColor;
        Save();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 套用帳戶目錄的順序：只排列清單裡有、而且存在的帳戶——它們原本占的位置依新的順序重新分配，沒列到的帳戶留在原位
    /// （保持相對位置）。順序沒變回 false（不存檔、不通知）
    /// </summary>
    public bool ApplyCatalogOrder(IReadOnlyList<string> orderedIds)
    {
        var listed = new List<AgentAccount>();
        foreach (var id in orderedIds)
        {
            if (Find(id) is { } account && !listed.Contains(account))
                listed.Add(account);
        }
        var slots = new List<int>();
        for (int i = 0; i < accounts.Count; i++)
        {
            if (listed.Contains(accounts[i]))
                slots.Add(i);
        }
        var result = accounts.ToList();
        for (int i = 0; i < slots.Count; i++)
            result[slots[i]] = listed[i];
        if (result.SequenceEqual(accounts))
            return false;
        accounts.Clear();
        accounts.AddRange(result);
        Save();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 套用共享用量（多電腦同步：別台電腦查到的，來源 shared）：每一筆都記進用量歷史（點的時間用給定的伺服器時間），
    /// ReplaceCurrent 的才換成目前值（追趕積壓期間本機有自己查到的值時只記歷史）。最後通知一次
    /// </summary>
    public void ApplySharedUsage(IReadOnlyList<(string AccountId, UsageSnapshot Snapshot, DateTimeOffset PointTime, bool ReplaceCurrent)> items)
    {
        bool any = false;
        foreach (var (accountId, snapshot, pointTime, replaceCurrent) in items)
        {
            if (Find(accountId) == null)
                continue;
            history.Record(accountId, snapshot, pointTime, updateLast: replaceCurrent);
            if (replaceCurrent)
            {
                usage[accountId] = snapshot;
                UsageObserved?.Invoke(accountId, snapshot);
            }
            any = true;
        }
        if (any)
            RaiseChanged();
    }

    /// <summary>
    /// 多電腦同步要的身分資訊（Claude：orgId、憑證與登入的到期時間；「在這台登入」宣告的邏輯帳戶）：這次執行查到的優先，
    /// 沒有就用身分快取（accounts.json 的 LastIdentity；orgId 要快取的 email 和帳戶現在的 email 相同才用，外部換了帳號不能沿用舊的 org）。
    /// 登入到期不存檔，這次執行還沒讀過憑證檔時是 null。orgId 不明時另帶保守模式的 org 提示（SEC-1，只用來判斷不符）
    /// </summary>
    public AccountSyncIdentity GetSyncIdentity(string accountId)
    {
        var account = Find(accountId);
        if (account == null)
            return new AccountSyncIdentity(null, null, null, null);
        var state = GetState(accountId);
        var cache = account.LastIdentity;
        var orgId = state.OrgId ?? AccountIdentityRules.ResolveClaudeOrgId(null, cache?.OrgId, cache?.Email, account.Email, null, null, false, false);
        return new AccountSyncIdentity(orgId, state.AccessExpiresAt ?? cache?.ExpiresAt, state.LoginExpiresAt, account.LogicalAccountId,
            orgId is null ? state.OrgIdHint : null);
    }

    /// <summary>
    /// 移除帳戶；deleteData＝同時刪除隔離的設定資料夾（登入憑證與 CLI 對話紀錄）。系統預設帳戶的資料夾一律不刪。
    /// 進行中的登入與查詢一併取消（登入主控台、app-server 結束後才放開資料夾裡的檔案，也釋放登入鎖）
    /// </summary>
    public bool Remove(string id, bool deleteData)
    {
        var account = Find(id);
        if (account == null)
            return true;
        // 先記下進行中的登入與查詢（刪資料夾前要等它們的程序結束），清掉所有狀態後才取消，取消觸發的接續看到的就是已移除的帳戶
        var deleteRoot = deleteData ? GetDeletableAccountRoot(account) : null;
        var pending = new List<Task>();
        if (logins.Remove(id, out var login))
            pending.Add(login.Closed.Task);
        if (refreshTasks.Remove(id, out var refresh))
            pending.Add(refresh.Task);
        // 換發權杖的 claude -p：資料夾要刪才結束它（開著資料夾裡的檔案，等它結束才刪）；資料夾留著（系統預設帳戶、只從清單移除）
        // 就讓它換發完（使用者在終端機也用同一個登入），結果不再處理
        if (tokenRefresh.Remove(id, out var tokenRun) && tokenRun.Running != null && deleteRoot != null)
        {
            tokenRun.Cancel?.Cancel();
            pending.Add(tokenRun.Running);
        }
        borrowedFrom.Remove(id);
        lifetimes.Remove(id, out var lifetime);
        accounts.Remove(account);
        states.Remove(id);
        usage.Remove(id);
        nextPoll.Remove(id);
        soonPoll.Remove(id);
        errorCounts.Remove(id);
        conservative.Remove(id);
        statusAfterTurn.Remove(id);
        pendingStatus.Remove(id);
        identityHold.Remove(id);
        if (watchers.Remove(id, out var watcher))
            watcher.Dispose();
        history.Remove(id);
        Save();
        lifetime?.Cancel();

        if (deleteRoot != null)
            _ = DeleteAccountDataAsync(account, deleteRoot, pending);
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 刪除已移除帳戶的資料夾：先在旁邊留下待刪標記（刪到一半失敗、帳戶清單又遺失時不會被當成帳戶還原，下次啟動也會再刪），
    /// 等它的登入與查詢程序結束（app-server 開著資料夾裡的 sqlite 檔），再到背景執行緒刪（被占用要重試，不卡畫面）
    /// </summary>
    private async Task DeleteAccountDataAsync(AgentAccount account, string root, List<Task> pending)
    {
        var marker = root + PendingDeleteSuffix;
        try
        {
            if (Directory.Exists(root))
                File.WriteAllText(marker, account.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"無法建立待刪標記：{ex.Message}");
        }
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            // 逾時或工作本身失敗：照樣試著刪
            Log.Warn($"等待「{account.Label}」的登入或查詢結束失敗：{ex.Message}");
        }
        if (await Task.Run(() => TryDeleteDirectory(root)))
            TryDeleteFile(marker);
        else
            post(() => LoginProgress?.Invoke(account.Id, $"「{account.Label}」的資料夾有檔案正在使用中，沒有完全刪除，下次啟動時會再刪：{root}", true, false));
    }

    /// <summary>
    /// 可以刪的帳戶資料夾：只限 app 建立的 accounts\{帳戶 Id}（上一層必須正好是 accounts 資料夾、名稱必須是帳戶 Id）；
    /// 系統預設帳戶或位置不符（例如手動改過的設定）一律不刪
    /// </summary>
    internal static string? GetDeletableAccountRoot(AgentAccount account)
    {
        if (account.IsSystemDefault)
            return null;
        try
        {
            var config = Path.GetFullPath(account.ConfigDirectory!.TrimEnd('\\', '/'));
            var root = Path.GetDirectoryName(config);
            var accountsDir = Path.GetFullPath(AppPaths.AccountsDirectory).TrimEnd('\\');
            if (root == null || !string.Equals(Path.GetDirectoryName(root), accountsDir, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(root), account.Id, StringComparison.OrdinalIgnoreCase))
                return null;
            return root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 刪除帳戶資料夾：Claude 外掛市集是 git clone，含唯讀的物件檔，一併清掉唯讀屬性再刪（DirectoryRemover，不跟進連結）；
    /// 被占用時（剛結束的 CLI 程序還沒放開檔案）每 500 毫秒重試，最多約 5 秒
    /// </summary>
    private static bool TryDeleteDirectory(string path) => DirectoryRemover.TryDelete(path, attempts: 10, retryDelayMs: 500);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪除 {path} 失敗：{ex.Message}");
        }
    }

    /// <summary>Claude initialize 回報的帳戶資訊（email、方案）</summary>
    public void UpdateAccountInfo(string id, string? email, string? plan)
    {
        var account = Find(id);
        if (account == null)
            return;
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(email) && account.Email != email)
        {
            account.Email = email;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(plan) && string.IsNullOrWhiteSpace(account.Plan))
        {
            account.Plan = AccountRules.DescribeClaudePlan(plan, null) ?? plan;
            changed = true;
        }
        if (changed)
        {
            Save();
            RaiseChanged();
        }
    }

    // ───────────────────────────── 狀態與用量 ─────────────────────────────

    internal void Tick()
    {
        if (disposed)
            return;
        var now = DateTimeOffset.UtcNow;
        bool resetApplied = false;
        foreach (var account in accounts.ToList())
        {
            // 顯示的用量到了重置時間就先推算歸零（不論這次要不要查）：沒被指派查用量、查詢失敗時才不會一直停在重置前的數字
            if (UsageRules.ApplyDueResets(GetUsage(account.Id), now) is { } reset)
            {
                usage[account.Id] = reset;
                resetApplied = true;
            }
            if (nextPoll.TryGetValue(account.Id, out var due) && due > now)
                continue;
            // 未登入、未安裝、上次檢查失敗的帳戶在輪詢到期時一併重查狀態：
            // 在外部登入、安裝 CLI、設定資料夾之後才出現，或上次暫時性逾時／錯誤後都能自動恢復
            bool recheckStatus = AccountRules.ShouldRecheckStatusOnTick(GetState(account.Id).Status);
            // 保守模式的帳戶：對話剛跑完一輪，重新看一次 CLI 設定檔（在外部登出了就改判）；之前沒做成的檢查以原本的理由補查。
            // 兩者都等檢查真的開始才移除：這次被 skipIfRunning 略過（已有查詢進行中）或又被暫停，下次輪詢照樣補
            var reason = statusAfterTurn.Contains(account.Id) ? ClaudeStatusReason.AfterTurn : ClaudeStatusReason.Periodic;
            bool pending = pendingStatus.TryGetValue(account.Id, out var pendingReason);
            if (pending)
                reason = AccountRules.StrongerStatusReason(reason, pendingReason);
            _ = RefreshAsync(account.Id, includeStatus: recheckStatus || pending || reason == ClaudeStatusReason.AfterTurn, skipIfRunning: true, reason);
        }
        if (resetApplied)
            RaiseChanged();
        history.Flush();
    }

    private TimeSpan PollInterval(AgentAccount account)
        => runningSessions(account.Id) > 0 ? ActivePoll(account.Provider) : IdlePoll(account.Provider);

    private static TimeSpan ActivePoll(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ClaudeActivePoll,
        AgentProvider.Codex => CodexActivePoll,
        AgentProvider.Antigravity => AntigravityActivePoll,
        AgentProvider.Grok => GrokActivePoll,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private static TimeSpan IdlePoll(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ClaudeIdlePoll,
        AgentProvider.Codex => CodexIdlePoll,
        AgentProvider.Antigravity => AntigravityIdlePoll,
        AgentProvider.Grok => GrokIdlePoll,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 一輪對話完成：10 秒後查一次用量（讓伺服器端統計跟上）；保守模式的帳戶順便重新看一次 CLI 設定檔（不跑 claude）
    /// </summary>
    public void ReportTurnCompleted(string accountId)
    {
        var account = Find(accountId);
        if (account == null)
            return;
        if (AccountRules.HasUsageQuota(account.Provider))
            PollSoon(accountId, TimeSpan.FromSeconds(10));
        if (conservative.Contains(accountId))
            statusAfterTurn.Add(accountId);
    }

    /// <summary>
    /// 對話回報額度已用完（AgentQuotaExhaustedEvent）：伺服器端額度已經鎖定，立即觸發背景重查讓側欄圓環更新（特別是不推 AgentRateLimitEvent 的 Antigravity／Grok）
    /// </summary>
    public void ReportQuotaExhausted(string accountId)
    {
        var account = Find(accountId);
        if (account == null || !AccountRules.HasUsageQuota(account.Provider))
            return;
        PollSoon(accountId, TimeSpan.Zero);
        _ = RefreshAsync(accountId, includeStatus: false, skipIfRunning: true);
    }

    /// <summary>
    /// 這種 CLI 正在更新（app 的更新屏障進行中，或偵測到安裝正在變動）：不起它的程序（auth status、能力探查、用量查詢與登入用的 app-server）——
    /// 它們會擋住 npm 換檔、也會讓更新判斷失準（設計 7.3.1 第 1 步）。進行中的照常跑完（CliManager 換檔前最多等 30 秒）
    /// </summary>
    private bool CliPaused(AgentProvider provider)
    {
        var state = cli.Get(provider);
        return state.UpdatePhase != CliUpdatePhase.None || state.InstallChanging;
    }

    /// <summary>測試用：保守模式的帳戶</summary>
    internal bool IsConservative(string accountId) => conservative.Contains(accountId);

    /// <summary>測試用：還沒做成、等著補做的登入狀態檢查的理由（沒有為 null）</summary>
    internal ClaudeStatusReason? GetPendingStatusCheck(string accountId) => pendingStatus.TryGetValue(accountId, out var reason) ? reason : null;

    /// <summary>這次的登入狀態檢查沒做成（CLI 正在更新、憑證或 CLI 設定暫時讀不到）：記下理由（合併時取較強的），30 秒後補查</summary>
    private void DeferStatusCheck(string accountId, ClaudeStatusReason reason)
    {
        pendingStatus[accountId] = pendingStatus.TryGetValue(accountId, out var existing) ? AccountRules.StrongerStatusReason(existing, reason) : reason;
        PollSoon(accountId, CliPausedRetry);
    }

    /// <summary>登入狀態檢查真的開始了：之前沒做成的與對話結束後要做的都由這次負責（這次又沒做成會重新記下）</summary>
    private void BeginStatusCheck(string accountId)
    {
        pendingStatus.Remove(accountId);
        statusAfterTurn.Remove(accountId);
    }

    /// <summary>帳戶開始執行對話：下次查詢不晚於「執行中」的間隔（閒置帳戶開始跑長任務時不必等 10 分鐘）</summary>
    public void ReportActivity(string accountId)
    {
        var account = Find(accountId);
        if (account != null)
            PollSoon(accountId, ActivePoll(account.Provider));
    }

    /// <summary>
    /// 進行中的查詢回到 UI 執行緒時，這個帳戶的遠端登入正在比對身分（D14）：不套用這次的結果，<see cref="RemoteAccountRules.IdentityHoldRetry"/> 後再查。
    /// 回 true＝已延後，呼叫端直接返回
    /// </summary>
    private bool DeferIfIdentityHeld(string accountId)
    {
        if (!IsIdentityHeld(accountId))
            return false;
        PollSoon(accountId, RemoteAccountRules.IdentityHoldRetry);
        return true;
    }

    internal void PollSoon(string accountId, TimeSpan delay)
    {
        if (Find(accountId) == null)
            return;
        var due = DateTimeOffset.UtcNow + delay;
        if (!soonPoll.TryGetValue(accountId, out var pending) || pending > due)
            soonPoll[accountId] = due;
        if (!nextPoll.TryGetValue(accountId, out var current) || current > due)
            nextPoll[accountId] = due;
    }

    /// <summary>
    /// 重新檢查（includeStatus＝連登入狀態一起查；Codex 的查詢本來就包含帳戶資訊）。
    /// 同一帳戶已有查詢進行中：skipIfRunning（定期輪詢）就略過；否則要等到一次「這次呼叫之後才開始、也查了需要內容」的查詢結束才返回
    /// （登入完成、使用者按更新要看到最新結果）：等待期間別人已經開始新的一次就等那一次，沒有就自己查。
    /// </summary>
    public Task RefreshAsync(string accountId, bool includeStatus, bool skipIfRunning = false)
        => RefreshAsync(accountId, includeStatus, skipIfRunning, includeStatus ? ClaudeStatusReason.Manual : ClaudeStatusReason.Periodic);

    /// <param name="reason">為什麼要確認登入狀態（Claude：決定能不能用身分快取、要不要跑 auth status）</param>
    internal async Task RefreshAsync(string accountId, bool includeStatus, bool skipIfRunning, ClaudeStatusReason reason)
    {
        var before = refreshTasks.TryGetValue(accountId, out var initial) ? initial.Task : null;
        if (before != null && skipIfRunning)
            return;
        while (refreshTasks.TryGetValue(accountId, out var running) && !running.Task.IsCompleted)
        {
            await WaitQuietlyAsync(running.Task);
            if (running.Task != before && (running.IncludeStatus || !includeStatus))
                return;
        }

        var account = Find(accountId);
        if (account == null || disposed)
            return;
        if (IsIdentityHeld(accountId))
        {
            // 遠端登入的身分還沒比對完（D14）：不起任何程序，稍後再查（登入結束時本來就會重新查詢）
            PollSoon(accountId, RemoteAccountRules.IdentityHoldRetry);
            return;
        }
        var task = RefreshCoreAsync(account, includeStatus, reason);
        refreshTasks[accountId] = (task, includeStatus);
        try
        {
            await task;
        }
        finally
        {
            if (refreshTasks.TryGetValue(accountId, out var current) && current.Task == task)
                refreshTasks.Remove(accountId);
        }
    }

    private static async Task WaitQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Log.Warn($"等待帳戶查詢失敗：{ex.Message}");
        }
    }

    private async Task RefreshCoreAsync(AgentAccount account, bool includeStatus, ClaudeStatusReason reason)
    {
        var accountId = account.Id;
        var started = DateTimeOffset.UtcNow;
        nextPoll[accountId] = started + PollInterval(account);
        // 已經到期的「盡快再查」就是這一次；還沒到期的（或查詢期間才提出的）留著，結束時再決定下次時間
        if (soonPoll.TryGetValue(accountId, out var due) && due <= started)
            soonPoll.Remove(accountId);
        try
        {
            switch (account.Provider)
            {
                case AgentProvider.Claude:
                    await RefreshClaudeAsync(account, includeStatus, reason);
                    break;
                case AgentProvider.Codex:
                    await RefreshCodexAsync(account);
                    break;
                case AgentProvider.Antigravity:
                    await RefreshAntigravityAsync(account);
                    break;
                case AgentProvider.Grok:
                    await RefreshGrokAsync(account);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null);
            }
        }
        catch (Exception ex)
        {
            // 帳戶已移除（查詢被取消）就不再記狀態
            if (!disposed && Find(accountId) != null)
            {
                Log.Warn($"帳戶 {account.Label} 檢查失敗：{ex.Message}");
                SetStatus(account, AccountStatus.Error, $"檢查失敗：{ex.Message}");
            }
        }
        finally
        {
            if (Find(accountId) != null)
            {
                var status = GetState(accountId).Status;
                if (status == AccountStatus.Error)
                    errorCounts[accountId] = errorCounts.GetValueOrDefault(accountId) + 1;
                else
                    errorCounts.Remove(accountId);
                // 檢查失敗時以短間隔退避重試（1 → 2 → 4 分鐘…）；查詢期間有人要求「盡快再查」就保留比較早的那個時間
                var delay = AccountRules.NextPollDelay(status, errorCounts.GetValueOrDefault(accountId), PollInterval(account));
                var normal = DateTimeOffset.UtcNow + delay;
                nextPoll[accountId] = soonPoll.Remove(accountId, out var soon) && soon < normal ? soon : normal;
                // 設定資料夾在啟動時還不存在（之後才登入、才裝 CLI）或監看已失效：補建憑證檔監看
                if (!disposed && !watchers.ContainsKey(accountId))
                    Watch(account);
            }
            RaiseChanged();
        }
    }

    public Task RefreshAllAsync(bool includeStatus)
        => Task.WhenAll(accounts.ToList().Select(a => RefreshAsync(a.Id, includeStatus)));

    private async Task RefreshClaudeAsync(AgentAccount account, bool includeStatus, ClaudeStatusReason reason)
    {
        var lifetime = LifetimeOf(account.Id);
        var credentialFile = AccountRules.GetClaudeCredentialFile(account);
        if (includeStatus || GetState(account.Id).Status is AccountStatus.Checking or AccountStatus.Error)
        {
            if (CliPaused(AgentProvider.Claude))
            {
                // Claude CLI 正在更新：不起 claude 程序，記下這次要查的理由（憑證檔變了、使用者按了更新），更新完以同樣的理由補查（用量查詢是 HTTP，照常）
                DeferStatusCheck(account.Id, reason);
            }
            else if (ResolveLauncher(AgentProvider.Claude) is not { } launcher)
            {
                BeginStatusCheck(account.Id);
                SetStatus(account, AccountStatus.NotInstalled, null);
            }
            else
            {
                await CheckClaudeStatusAsync(account, launcher, reason, lifetime);
                // 身分比對期間（D14）延後的檢查：也不查用量（憑證檔可能已是還沒比對的新登入）
                if (disposed || Find(account.Id) == null || IsIdentityHeld(account.Id))
                    return;
            }
        }

        if (GetState(account.Id).Status is not (AccountStatus.Ready or AccountStatus.ApiKey))
            return;
        // 用量閘門（多電腦同步）：這台沒被指派查這個身分的用量就不查，也不標過期（畫面顯示共享的用量）
        if (!ShouldPollUsage(account))
            return;

        var (outcome, json, error) = await Task.Run(() => claudeUsage.FetchAsync(credentialFile, lifetime));
        if (disposed || Find(account.Id) == null)
            return;
        var now = DateTimeOffset.UtcNow;
        switch (outcome)
        {
            case ClaudeUsageOutcome.Success:
            {
                var (windows, note) = UsageRules.ParseClaudeUsage(json);
                // 回了 200 卻解析不出任何時間窗（端點改版、代理回了空內容）：當成查詢失敗，保留上次的資料與走勢
                if (windows.Count == 0)
                    MarkStale(account.Id, "用量回應的格式無法解析");
                else
                {
                    NoteBorrowedSource(account, null);
                    StoreUsage(account.Id, new UsageSnapshot(now, "api", windows, null, note, null, false));
                }
                break;
            }
            case ClaudeUsageOutcome.TokenExpired:
                await RecoverExpiredClaudeUsageAsync(account, credentialFile, lifetime);
                break;
            case ClaudeUsageOutcome.NoCredentials:
                MarkStale(account.Id, conservative.Contains(account.Id)
                    ? "登入憑證存在 Windows 認證管理員，app 讀不到；用量改由對話回報"
                    : "找不到登入憑證（API Key 或第三方登入沒有訂閱用量）");
                break;
            default:
                MarkStale(account.Id, error);
                break;
        }
    }

    /// <summary>可以借來查用量的登入：其他 Claude 帳戶與沒登記成帳戶的本機預設設定資料夾（UI 執行緒收集；讀檔在背景）</summary>
    private sealed record BorrowCandidate(AgentAccount Account, string Label);

    /// <summary>
    /// 用量查詢回「權杖已過期」（ClaudeTokenRecoveryRules）：先借用同身分（CLI 設定的 email 與 organizationUuid 都相同）、權杖還有效的其他登入，
    /// 唯讀查官方用量端點（不換發任何權杖）；借不到就標成過時，能讓 CLI 換發時在背景替這個帳戶跑一次最小的 claude -p
    /// （CLI 換發後寫回憑證檔：監看與換發結束時都會再查）
    /// </summary>
    private async Task RecoverExpiredClaudeUsageAsync(AgentAccount account, string credentialFile, CancellationToken lifetime)
    {
        var candidates = accounts.Where(a => a.Provider == AgentProvider.Claude && a.Id != account.Id)
            .Select(a => new BorrowCandidate(a, a.Label))
            .Concat(ExtraClaudeUsageSources().Select(a => new BorrowCandidate(a, "本機預設設定資料夾")))
            .ToList();
        var (source, outcome, json, ownCredentials) = await Task.Run(async () =>
        {
            var picked = PickClaudeBorrowSource(account, candidates, DateTimeOffset.UtcNow);
            if (picked == null)
                return ((ClaudeUsageCredentialSource?)null, ClaudeUsageOutcome.TokenExpired, (JObject?)null, ReadCredentialsQuietly(credentialFile));
            var (fetched, body, _) = await claudeUsage.FetchAsync(picked.CredentialFile, lifetime);
            // 自己的憑證在查詢之後才讀：借用的查詢最久要等 20 秒，這段期間 CLI 可能已經換發過（就不必再跑 claude -p）
            return (picked, fetched, body, ReadCredentialsQuietly(credentialFile));
        });
        if (disposed || Find(account.Id) == null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (source != null)
        {
            if (outcome == ClaudeUsageOutcome.Success && UsageRules.ParseClaudeUsage(json) is { Windows.Count: > 0 } parsed)
            {
                NoteBorrowedSource(account, source.Label);
                StoreUsage(account.Id, new UsageSnapshot(now, "api", parsed.Windows, null, parsed.Note, null, false));
                return;
            }
            Log.Warn($"「{account.Label}」借用「{source.Label}」的登入查用量失敗（{outcome}）");
        }
        NoteBorrowedSource(account, null);

        var block = DecideClaudeTokenRefresh(account, ownCredentials, now, out var launcher);
        if (block == ClaudeTokenRefreshBlock.None)
            StartClaudeTokenRefresh(account, launcher!, credentialFile);
        var refresh = tokenRefresh.GetValueOrDefault(account.Id);
        MarkStale(account.Id, ClaudeTokenRecoveryRules.DescribeExpired(block, refresh?.LastError,
            refresh?.LastAttempt + ClaudeTokenRecoveryRules.RefreshMinInterval, now));
    }

    /// <summary>
    /// 從其他登入裡挑一個可以借來查用量的（背景執行緒：讀 CLI 設定與憑證檔的中繼欄位）。這個帳戶自己的 CLI 設定讀不到 email 或 org 就不借；
    /// 同一個設定資料夾（含這個帳戶自己的）只看一次
    /// </summary>
    private static ClaudeUsageCredentialSource? PickClaudeBorrowSource(AgentAccount account, IReadOnlyList<BorrowCandidate> candidates, DateTimeOffset now)
    {
        var own = ReadClaudeGlobalConfig(account);
        if (!own.Readable || own.OAuthEmail == null || own.OAuthOrgId == null)
            return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (NormalizeConfigDirectory(account) is { } ownDirectory)
            seen.Add(ownDirectory);
        var sources = new List<ClaudeUsageCredentialSource>();
        foreach (var candidate in candidates)
        {
            if (NormalizeConfigDirectory(candidate.Account) is not { } directory || !seen.Add(directory))
                continue;
            var config = ReadClaudeGlobalConfig(candidate.Account);
            if (!config.Readable || !config.HasOAuthAccount)
                continue;
            var file = AccountRules.GetClaudeCredentialFile(candidate.Account);
            var token = ClaudeUsageClient.ReadToken(ReadCredentialsQuietly(file));
            sources.Add(new ClaudeUsageCredentialSource(file, candidate.Label, config.OAuthEmail, config.OAuthOrgId, token?.ExpiresAt, token != null));
        }
        return ClaudeTokenRecoveryRules.PickBorrowSource(own.OAuthEmail, own.OAuthOrgId, sources, now);
    }

    /// <summary>帳戶的設定資料夾（完整路徑、去掉結尾的分隔字元，比對是不是同一個資料夾用）；路徑不合法回 null</summary>
    private static string? NormalizeConfigDirectory(AgentAccount account)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(AccountRules.ResolveConfigDirectory(account)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>讀 Claude 憑證檔（不存在、讀不到都回 null；ReadWrite｜Delete 共用模式，不擋 CLI 寫入）</summary>
    private static string? ReadCredentialsQuietly(string file)
    {
        try
        {
            return ClaudeUsageClient.ReadCredentials(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>記下帳戶正借用哪個登入查用量（label＝null：不再借用）；換了來源才寫記錄</summary>
    private void NoteBorrowedSource(AgentAccount account, string? label)
    {
        var previous = borrowedFrom.GetValueOrDefault(account.Id);
        if (previous == label)
            return;
        if (label == null)
        {
            borrowedFrom.Remove(account.Id);
            Log.Info($"「{account.Label}」不再借用「{previous}」的登入查用量");
        }
        else
        {
            borrowedFrom[account.Id] = label;
            Log.Info($"「{account.Label}」的登入權杖已過期：改用同身分的「{label}」的登入查用量（唯讀，不換發任何權杖）");
        }
    }

    /// <summary>現在能不能讓 CLI 換發這個帳戶的權杖（ClaudeTokenRecoveryRules.CanRefreshWithCli；UI 執行緒）</summary>
    private ClaudeTokenRefreshBlock DecideClaudeTokenRefresh(AgentAccount account, string? credentialsJson, DateTimeOffset now, out AgentLauncher? launcher)
    {
        bool enabled = ClaudeTokenRefreshEnabled();
        launcher = enabled ? ResolveLauncher(AgentProvider.Claude) : null;
        var refresh = tokenRefresh.GetValueOrDefault(account.Id);
        return ClaudeTokenRecoveryRules.CanRefreshWithCli(enabled, launcher != null, GetState(account.Id).Status, conservative.Contains(account.Id),
            logins.ContainsKey(account.Id), CliPaused(AgentProvider.Claude), runningSessions(account.Id), refresh?.Running != null,
            AccountRules.InspectClaudeToken(credentialsJson, now), ClaudeTokenRecoveryRules.HasRefreshToken(credentialsJson), refresh?.LastAttempt, now);
    }

    /// <summary>
    /// 在背景替權杖過期的帳戶開一個不送訊息的 Claude 程序、以 get_usage 請 CLI 代查用量（ClaudeControlClient、ClaudeTokenRecoveryRules.BuildUsageRequest）：
    /// CLI 查之前自己換發過期的權杖、寫回憑證檔，不呼叫模型（不用額度、不開始新的 5 小時時間窗）；查到的用量直接存下。
    /// 不加入 app 的工作物件：app 結束或重啟時不會在換發途中把它結束（換發送出後被結束，refresh token 可能就被用掉）；
    /// 只有逾時（RefreshTimeout）與帳戶資料夾要刪掉時才結束它。多個 CLI 程序同時換發由 CLI 自己協調（鎖定設定資料夾、拿到鎖後重讀憑證檔）
    /// </summary>
    private void StartClaudeTokenRefresh(AgentAccount account, AgentLauncher launcher, string credentialFile)
    {
        var accountId = account.Id;
        if (!tokenRefresh.TryGetValue(accountId, out var refresh))
            tokenRefresh[accountId] = refresh = new TokenRefreshState();
        var previousAttempt = refresh.LastAttempt;
        refresh.LastAttempt = DateTimeOffset.UtcNow;
        var cancel = new CancellationTokenSource();
        refresh.Cancel = cancel;
        Log.Info($"「{account.Label}」的登入權杖已過期、沒有同身分的其他登入可以借用：讓 Claude CLI 代查用量並換發權杖（get_usage，不送訊息）");
        refresh.Running = Task.Run(async () =>
        {
            // 一定要回報（例外也一樣），不然這個帳戶會一直停在「正在換發」
            var run = new CliRunResult(-1, string.Empty, "換發程序沒有執行", false);
            var after = ClaudeTokenState.Unreadable;
            JObject? usage = null;
            try
            {
                // 起 claude -p 之前最後確認一次 CLI 沒有開始更新（CliManager 自己加鎖，背景執行緒可以讀；這個程序不在 app 的工作物件裡，更新屏障等不到它）
                if (CliPaused(AgentProvider.Claude))
                {
                    post(() => OnClaudeTokenRefreshSkipped(accountId, refresh, previousAttempt));
                    return;
                }
                (usage, run) = await QueryUsageWithCliAsync(account, launcher, cancel.Token);
                after = await ReadTokenStateAfterRefreshAsync(credentialFile);
            }
            catch (OperationCanceledException)
            {
                // 帳戶資料夾要刪（CliRunner 已結束程序）：帳戶已移除，回報也不會處理
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"「{account.Label}」讓 Claude CLI 換發權杖時發生錯誤：{ex.Message}");
                run = new CliRunResult(-1, string.Empty, ex.Message, false);
            }
            finally
            {
                cancel.Dispose();
            }
            post(() => OnClaudeTokenRefreshDone(accountId, refresh, after, run, usage));
        });
    }

    /// <summary>
    /// 開一個不送訊息的 Claude 程序問 get_usage（背景執行緒）：不加入 app 的工作物件（換發途中不能被砍）。回傳用量端點形狀的 rate_limits
    /// （沒有就是 null）與給 EvaluateRefresh 的結果（成功＝結束碼 0；逾時、失敗帶原因）
    /// </summary>
    private static async Task<(JObject? Usage, CliRunResult Run)> QueryUsageWithCliAsync(AgentAccount account, AgentLauncher launcher, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ClaudeTokenRecoveryRules.RefreshTimeout);
        try
        {
            await using var client = await ClaudeControlClient.StartAsync(launcher, env => AccountRules.ApplyCommandEnvironment(account, env), killWithApp: false, timeout.Token);
            var response = await client.RequestAsync(ClaudeTokenRecoveryRules.BuildUsageRequest(), ClaudeTokenRecoveryRules.RefreshTimeout, timeout.Token);
            if (response == null)
                return (null, new CliRunResult(-1, string.Empty, client.StderrTail() is { Length: > 0 } tail ? tail : "CLI 沒有回應", true));
            if (!response.Success)
                return (null, new CliRunResult(1, string.Empty, response.Error ?? "CLI 查不到用量", false));
            return (ClaudeTokenRecoveryRules.ParseCliUsage(response.Response), new CliRunResult(0, string.Empty, string.Empty, false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, new CliRunResult(-1, string.Empty, "執行逾時", true));
        }
        catch (InvalidOperationException ex)
        {
            return (null, new CliRunResult(-1, string.Empty, ex.Message, false));
        }
    }

    /// <summary>換發正要開始時 CLI 開始更新了（UI 執行緒）：這次不算一次嘗試，CLI 更新完（30 秒後）再查</summary>
    private void OnClaudeTokenRefreshSkipped(string accountId, TokenRefreshState refresh, DateTimeOffset? previousAttempt)
    {
        if (!tokenRefresh.TryGetValue(accountId, out var current) || current != refresh)
            return;
        refresh.Running = null;
        refresh.Cancel = null;
        refresh.LastAttempt = previousAttempt;
        if (Find(accountId) != null && !disposed)
        {
            MarkStale(accountId, ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.CliPaused, null, null, DateTimeOffset.UtcNow));
            PollSoon(accountId, CliPausedRetry);
        }
    }

    /// <summary>換發結束後的權杖狀況：CLI 剛寫完檔時可能還讀不到或讀不懂，最多再讀兩次</summary>
    private static async Task<ClaudeTokenState> ReadTokenStateAfterRefreshAsync(string credentialFile)
    {
        var state = ClaudeTokenState.Unreadable;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500);
            var text = ReadCredentialsQuietly(credentialFile);
            state = text == null && File.Exists(credentialFile) ? ClaudeTokenState.Unreadable : AccountRules.InspectClaudeToken(text, DateTimeOffset.UtcNow);
            if (state != ClaudeTokenState.Unreadable)
                break;
        }
        return state;
    }

    /// <summary>換發結束（UI 執行緒）：記下結果，重新確認登入狀態並查用量（監看也會觸發；換發沒成功時也要把說明換掉）</summary>
    private void OnClaudeTokenRefreshDone(string accountId, TokenRefreshState refresh, ClaudeTokenState after, CliRunResult run, JObject? usage = null)
    {
        // 帳戶已移除（或換成新的狀態物件）：不再處理
        if (!tokenRefresh.TryGetValue(accountId, out var current) || current != refresh)
            return;
        refresh.Running = null;
        refresh.Cancel = null;
        var account = Find(accountId);
        if (account == null || disposed)
            return;
        // CLI 代查到的用量先存下（權杖沒寫回憑證檔時——例如存在 Windows 認證管理員——至少用量是新的）
        if (usage != null && UsageRules.ParseClaudeUsage(usage) is { Windows.Count: > 0 } parsed)
            StoreUsage(accountId, new UsageSnapshot(DateTimeOffset.UtcNow, "api", parsed.Windows, null, parsed.Note, null, false));
        var (result, detail) = ClaudeTokenRecoveryRules.EvaluateRefresh(after, run);
        refresh.LastError = result == ClaudeTokenRefreshResult.Refreshed ? null : detail;
        switch (result)
        {
            case ClaudeTokenRefreshResult.Refreshed:
                Log.Info($"「{account.Label}」的權杖已由 Claude CLI 換發");
                break;
            case ClaudeTokenRefreshResult.LoginLost:
                Log.Warn($"「{account.Label}」換發權杖被拒，CLI 清空了登入：要重新登入");
                break;
            case ClaudeTokenRefreshResult.Failed:
                Log.Warn($"「{account.Label}」讓 Claude CLI 換發權杖沒有成功：{detail}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result), result, null);
        }
        _ = RefreshAsync(accountId, includeStatus: true, skipIfRunning: false, ClaudeStatusReason.CredentialsChanged);
    }

    /// <summary>
    /// 設定頁打開「閒置的 Claude 帳戶自動換發權杖」：資料過時的 Claude 帳戶下次輪詢（20 秒內）就再查一次（權杖過期的會開始換發）
    /// </summary>
    public void PollStaleClaudeSoon()
    {
        foreach (var account in accounts.Where(a => a.Provider == AgentProvider.Claude && GetUsage(a.Id) is { Stale: true }))
            PollSoon(account.Id, TimeSpan.Zero);
    }

    /// <summary>
    /// 確認 Claude 的登入狀態（設計 6.5，AccountRules.DecideClaudeStatusCheck）：啟動與定期重查時身分快取有效＝直接判定已登入；
    /// 權杖快到期、沒有 expiresAt＝不跑 CLI、等權杖更新；權杖是空字串＝登入已失效；憑證檔不在而權杖在 Windows 認證管理員＝保守模式（不跑 CLI）；
    /// 憑證檔或 CLI 設定讀不到、讀不懂＝不跑 CLI、稍後再查；其他才跑 claude auth status（帶 DISABLE_AUTOUPDATER=1），成功後記下身分快取
    /// </summary>
    private async Task CheckClaudeStatusAsync(AgentAccount account, AgentLauncher launcher, ClaudeStatusReason reason, CancellationToken lifetime)
    {
        BeginStatusCheck(account.Id);
        var state = GetState(account.Id);
        var credentialFile = AccountRules.GetClaudeCredentialFile(account);
        var (credentials, globalConfig) = await Task.Run(() =>
        {
            var info = ReadCredentialInfo(credentialFile);
            // 憑證檔不在：看 CLI 設定，權杖可能改存在 Windows 認證管理員（只在這時才讀：設定檔可能很大）
            return (info, info.Exists ? null : ReadClaudeGlobalConfig(account));
        });
        if (disposed || Find(account.Id) == null)
            return;
        if (DeferIfIdentityHeld(account.Id))
            return;
        // 多電腦同步要的憑證與登入到期時間：只讀 expiresAt、refreshTokenExpiresAt 這兩個中繼欄位（不讀權杖；已經讀進記憶體，不多讀檔）
        state.AccessExpiresAt = AccountRules.ReadClaudeExpiresAt(credentials.Json);
        state.LoginExpiresAt = AccountIdentityRules.ReadClaudeLoginExpiresAt(credentials.Json);
        var detailedPlan = AccountRules.ReadClaudePlanFromCredentials(credentials.Json);
        var cache = account.LastIdentity;
        var credentialOrgId = AccountIdentityRules.ReadClaudeCredentialOrgId(credentials.Json);
        // orgId 的來源（AccountIdentityRules.ResolveClaudeOrgId）：在 ApplyIdentity 之後呼叫，帳戶的 email 就是這次判定的 email；
        // 憑證檔與 CLI 設定的 organizationUuid 在 E-13 確認之前不用（D4）。CLI 設定只在憑證檔不存在時才讀
        string? ResolveOrgId(string? fromAuthStatus) => AccountIdentityRules.ResolveClaudeOrgId(fromAuthStatus, cache?.OrgId, cache?.Email, account.Email,
            credentialOrgId, globalConfig?.OAuthOrgId, UseCredentialsOrg, UseGlobalConfigOrg);
        var decision = AccountRules.DecideClaudeStatusCheck(credentials, cache, reason, DateTimeOffset.UtcNow, globalConfig);
        if (decision != ClaudeStatusDecision.Conservative)
            conservative.Remove(account.Id);
        switch (decision)
        {
            case ClaudeStatusDecision.UseCache:
                ApplyIdentity(account, cache!.Email, detailedPlan ?? cache.Plan);
                state.OrgId = ResolveOrgId(null);
                state.OrgIdHint = null;
                SetStatus(account, cache.Status, null);
                return;
            case ClaudeStatusDecision.TokenPending:
                // 權杖已過期或快到期：auth status 會開始換發權杖卻不等結果就結束（可能把 refresh token 用掉、之後被登出），先不查；
                // CLI 在對話中自己換發、寫回憑證檔後，監看會再查一次。已知是 API 計費的登入就維持原狀
                ApplyIdentity(account, null, detailedPlan);
                state.OrgId = ResolveOrgId(null);
                state.OrgIdHint = null;
                if (state.Status != AccountStatus.ApiKey)
                    SetStatus(account, AccountStatus.Ready, "已登入（權杖待更新）");
                return;
            case ClaudeStatusDecision.LoginExpired:
                // CLI 換發被拒後把權杖清成空字串：不必跑 CLI 就知道登入已失效
                SetLastIdentity(account, null);
                state.OrgIdHint = null;
                SetStatus(account, AccountStatus.NotLoggedIn, "登入已失效，請重新登入");
                return;
            case ClaudeStatusDecision.Conservative:
            {
                // 權杖在 Windows 認證管理員（看不到期限）：任何短命的 claude 都可能把 refresh token 用掉，一律不跑。
                // 沿用上次確認的身分；沒有（升級後第一次、還原的帳戶）就以 CLI 設定的 oauthAccount 判定已登入。之後在外部登出
                // （CLI 清掉 oauthAccount）會在對話結束後、手動更新、下次啟動時看出來
                if (conservative.Add(account.Id))
                    Log.Warn($"「{account.Label}」的登入憑證存在 Windows 認證管理員（看不到權杖期限）：不跑 claude auth status 與能力探查，登入狀態改看 CLI 設定檔");
                var kept = cache?.Status ?? AccountStatus.Ready;
                ApplyIdentity(account, globalConfig?.OAuthEmail ?? cache?.Email, cache?.Plan);
                state.OrgId = ResolveOrgId(null);
                // org 一直不明時記下 CLI 設定的 organizationUuid（未確認，D4）：只拿來判斷「在這台登入」的帳戶和目錄鍵的 org 不符（SEC-1）
                state.OrgIdHint = AccountIdentityRules.ClaudeOrgIdHint(state.OrgId, globalConfig?.OAuthOrgId, globalConfig?.OAuthEmail, account.Email);
                // API 計費的登入照常顯示計費說明
                SetStatus(account, kept, kept == AccountStatus.ApiKey ? null : CredentialManagerStatusText);
                return;
            }
            case ClaudeStatusDecision.CredentialsUnreadable:
                // 憑證檔正被寫入（CLI 換發權杖）或內容壞了：不能判斷權杖就不跑 CLI；記成檢查失敗（保留用量、不擋送出），稍後以同樣的理由再查
                SetStatus(account, AccountStatus.Error, "檢查失敗：暫時讀不到登入憑證，稍後再查");
                DeferStatusCheck(account.Id, reason);
                return;
            case ClaudeStatusDecision.ConfigUnreadable:
                // 憑證檔不在、CLI 設定又暫時讀不到（CLI 正在寫入）：不知道權杖是不是在認證管理員，不跑 CLI；曾經登入就先沿用，稍後再查
                if (cache is { Status: AccountStatus.Ready or AccountStatus.ApiKey })
                {
                    ApplyIdentity(account, cache.Email, cache.Plan);
                    SetStatus(account, cache.Status, cache.Status == AccountStatus.ApiKey ? null : "已登入（暫時讀不到 CLI 設定，稍後再確認）");
                }
                else
                {
                    SetStatus(account, AccountStatus.Error, "檢查失敗：暫時讀不到 Claude Code 的設定檔，稍後再查");
                }
                DeferStatusCheck(account.Id, reason);
                return;
        }

        var result = await Task.Run(() => CliRunner.RunAsync(launcher, new[] { "auth", "status", "--json" },
            env => AccountRules.ApplyCommandEnvironment(account, env), TimeSpan.FromSeconds(30), lifetime));
        if (disposed || Find(account.Id) == null)
            return;
        if (DeferIfIdentityHeld(account.Id))
            return;
        var output = result.Stdout + "\n" + result.Stderr;
        var (status, email, plan) = AccountRules.EvaluateClaudeAuthStatus(output);
        ApplyIdentity(account, email, detailedPlan ?? plan);
        // 讀不到結果（逾時、啟動失敗）記成檢查失敗：保留用量、不擋送出，下次輪詢再查；讀到了才更新 orgId（多電腦同步的身分鍵）。
        // 未登入就沒有 orgId（不沿用快取與檔案）
        if (status != AccountStatus.Error)
        {
            state.OrgId = status is AccountStatus.Ready or AccountStatus.ApiKey ? ResolveOrgId(AccountRules.ReadClaudeOrgId(output)) : null;
            state.OrgIdHint = null;
        }
        SetStatus(account, status, status == AccountStatus.Error
            ? $"檢查失敗：{AccountRules.DescribeClaudeAuthStatusFailure(result.Stderr, result.TimedOut)}"
            : null);
        if (status != AccountStatus.Error)
            SetLastIdentity(account, AccountRules.BuildIdentityCache(status, account.Email, account.Plan, credentials, DateTime.Now, state.OrgId));
        // auth status 最多跑 30 秒：這段期間 Claude CLI 可能開始更新，再確認一次才起能力探查（暫停時保留 NeedsCapabilities，下次確認登入狀態再探）
        if (status == AccountStatus.Ready && !CliPaused(AgentProvider.Claude) && NeedsCapabilities?.Invoke(account.Id) == true)
            _ = DiscoverClaudeCapabilitiesAsync(account, launcher);
    }

    /// <summary>Windows 認證管理員的登入（保守模式）的狀態文字</summary>
    internal const string CredentialManagerStatusText = "已登入（憑證在 Windows 認證管理員）";

    /// <summary>
    /// 讀 Claude CLI 的全域設定檔（AccountRules.GetClaudeGlobalConfigPaths；以 ReadWrite｜Delete 共用模式開檔，不擋 CLI 寫入或改名覆寫）。
    /// 不存在回 Missing；存在卻讀不到或讀不懂回 Unreadable
    /// </summary>
    internal static ClaudeGlobalConfigInfo ReadClaudeGlobalConfig(AgentAccount account)
    {
        var (legacy, config) = AccountRules.GetClaudeGlobalConfigPaths(account);
        try
        {
            var path = File.Exists(legacy) ? legacy : config;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return AccountRules.ParseClaudeGlobalConfig(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ClaudeGlobalConfigInfo.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ClaudeGlobalConfigInfo.Unreadable;
        }
    }

    /// <summary>記下（或清掉）帳戶的身分快取；和原本一樣就不存檔</summary>
    private void SetLastIdentity(AgentAccount account, AccountIdentityCache? cache)
    {
        var current = account.LastIdentity;
        if (current == null && cache == null)
            return;
        if (current != null && cache != null && current.Status == cache.Status && current.Email == cache.Email && current.Plan == cache.Plan
            && current.CredentialSize == cache.CredentialSize && current.CredentialWriteTicks == cache.CredentialWriteTicks && current.ExpiresAt == cache.ExpiresAt
            && current.OrgId == cache.OrgId)
            return;
        account.LastIdentity = cache;
        Save();
    }

    /// <summary>
    /// 讀 Claude 憑證檔的大小、最後寫入時間與內容（以 ReadWrite｜Delete 共用模式開檔：不擋 CLI 換發權杖時寫入或改名覆寫）。
    /// 不存在回 Missing；存在但讀不到（被占用）回 Json＝null（不能判斷權杖，不跑 CLI）
    /// </summary>
    internal static ClaudeCredentialInfo ReadCredentialInfo(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            var written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            using var reader = new StreamReader(stream);
            return new ClaudeCredentialInfo(true, size, written, reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ClaudeCredentialInfo.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClaudeCredentialInfo(true, 0, default, null);
        }
    }

    private async Task DiscoverClaudeCapabilitiesAsync(AgentAccount account, AgentLauncher launcher)
    {
        try
        {
            // 起 claude -p 之前最後確認一次 CLI 沒有開始更新（CliManager 自己加鎖，背景執行緒可以讀）
            var capabilities = await Task.Run(() => CapabilityProbe.ProbeClaudeAsync(launcher, account, CancellationToken.None, () => !CliPaused(AgentProvider.Claude)));
            if (capabilities != null && !disposed && Find(account.Id) != null)
                CapabilitiesDiscovered?.Invoke(account.Id, capabilities);
        }
        catch (Exception ex)
        {
            Log.Warn($"探查 {account.Label} 的模型清單失敗：{ex.Message}");
        }
    }

    private async Task RefreshCodexAsync(AgentAccount account)
    {
        if (CliPaused(AgentProvider.Codex))
        {
            // Codex CLI 正在更新（npm 屏障、安裝變動）：不起 app-server（會擋住換檔、讓更新判斷失準），更新完再查
            PollSoon(account.Id, CliPausedRetry);
            return;
        }
        var launcher = ResolveLauncher(AgentProvider.Codex);
        if (launcher == null)
        {
            SetStatus(account, AccountStatus.NotInstalled, null);
            return;
        }

        // 要送哪些請求在 UI 執行緒先決定（用量閘門讀同步的快照）；account/read 一律送：登入狀態與 email 靠它
        var probePlan = AccountRules.CodexProbePlan(ShouldPollUsage(account), NeedsCapabilities?.Invoke(account.Id) == true);
        var lifetime = LifetimeOf(account.Id);
        var probe = await Task.Run(async () =>
        {
            // 請求可以被取消（移除帳戶時讓 app-server 盡快關閉、放開資料夾裡的檔案）
            await using var client = await CodexAppServerClient.StartAsync(launcher, env => AccountRules.ApplyEnvironment(account, env), CancellationToken.None);
            var (accountResult, accountError) = await client.RequestAsync("account/read", new JObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(20), lifetime);
            if (accountError != null)
                return new CodexProbe(AccountStatus.Error, null, null, null, accountError);
            var (status, email, plan) = AccountRules.EvaluateCodexAccount(accountResult);
            if (status != AccountStatus.Ready)
                return new CodexProbe(status, email, plan, null, null);
            JObject? limits = null;
            string? limitsError = null;
            if (probePlan.RateLimits)
                (limits, limitsError) = await client.RequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(30), lifetime);
            var models = probePlan.Models ? await CapabilityProbe.ProbeCodexModelsAsync(client) : null;
            return new CodexProbe(status, email, plan, limits, limitsError, models);
        });
        if (disposed || Find(account.Id) == null)
            return;
        // 查詢開始之後才開始的遠端登入（D14）：這次讀到的身分與用量都不套用，稍後再查
        if (DeferIfIdentityHeld(account.Id))
            return;

        ApplyIdentity(account, probe.Email, probe.Plan);
        SetStatus(account, probe.Status, probe.Status == AccountStatus.Error ? $"檢查失敗：{probe.Error}" : null);
        if (probe.Models != null)
            CapabilitiesDiscovered?.Invoke(account.Id, new AccountCapabilities(probe.Models, Array.Empty<AgentSlashCommand>()));
        if (probe.Limits != null)
        {
            ApplyCodexLimits(account.Id, probe.Limits);
        }
        else if (probe.Status == AccountStatus.Ready && probePlan.RateLimits)
        {
            // 閘門擋下、沒查用量時不標過期（畫面顯示共享的用量）
            MarkStale(account.Id, probe.Error);
        }
    }

    /// <summary>Codex account/rateLimits/read 的結果存成這個帳戶的用量（UI 執行緒）</summary>
    private void ApplyCodexLimits(string accountId, JObject limits)
    {
        var (windows, credits) = UsageRules.ParseCodexRateLimits(limits);
        // 看不懂的回應（沒有任何額度桶）當成查詢失敗，保留上次的資料與走勢；桶裡沒有時間窗則是合法的（沒有用量上限的方案）
        if (windows.Count == 0 && !UsageRules.HasCodexRateLimits(limits))
        {
            MarkStale(accountId, "用量回應的格式無法解析");
            return;
        }
        var (resetCredits, resetExpires) = UsageRules.ParseCodexResetCredits(limits);
        StoreUsage(accountId, new UsageSnapshot(DateTimeOffset.UtcNow, "api", windows, credits, UsageRules.DescribeCodexBlock(limits), null, false,
            resetCredits, resetExpires));
    }

    // ───────────────────────────── Codex 免費重置 ─────────────────────────────

    /// <summary>正在使用免費重置的帳戶（同一個帳戶同時只送一次；帳戶卡的「使用重置」顯示進行中）</summary>
    private readonly HashSet<string> resetCreditBusy = new(StringComparer.Ordinal);

    /// <summary>這個帳戶正在使用免費重置（AccountView 的 resetCreditBusy）</summary>
    public bool IsResetCreditBusy(string accountId) => resetCreditBusy.Contains(accountId);

    /// <summary>
    /// 使用一次 Codex 的免費重置（<c>account/rateLimitResetCredit/consume</c>，會真的用掉一次；網頁先確認過）。UI 執行緒呼叫，回給使用者看的結果。
    /// 檢查不過（<see cref="CodexResetCreditRules.Block"/>）什麼都不做；送了之後不論結果，以同一個 app-server 重讀用量存起來
    /// （不經用量閘門：剛重置的額度與剩下的次數要馬上看得到）
    /// </summary>
    public async Task<CodexResetCreditResult> UseCodexResetCreditAsync(string accountId)
    {
        var account = Find(accountId);
        var launcher = account?.Provider == AgentProvider.Codex ? ResolveLauncher(AgentProvider.Codex) : null;
        var block = CodexResetCreditRules.Block(account?.Provider, account is null ? AccountStatus.Checking : GetState(accountId).Status,
            GetUsage(accountId)?.ResetCredits, resetCreditBusy.Contains(accountId), CliPaused(AgentProvider.Codex), launcher != null);
        if (block != null || account is null || launcher is null)
            return new CodexResetCreditResult(false, block ?? "找不到這個帳戶", "info");

        resetCreditBusy.Add(accountId);
        RaiseChanged();
        var lifetime = LifetimeOf(accountId);
        try
        {
            var (result, limits) = await Task.Run(async () =>
            {
                CodexResetCreditResult outcome;
                JObject? read = null;
                try
                {
                    await using var client = await CodexAppServerClient.StartAsync(launcher, env => AccountRules.ApplyEnvironment(account, env), lifetime);
                    var (consumed, error) = await client.RequestAsync(CodexResetCreditRules.ConsumeMethod, CodexResetCreditRules.BuildParams(Guid.NewGuid()),
                        CodexResetCreditRules.ConsumeTimeout, lifetime);
                    outcome = CodexResetCreditRules.Describe(consumed, error);
                    (read, _) = await client.RequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(30), lifetime);
                }
                catch (OperationCanceledException)
                {
                    outcome = new CodexResetCreditResult(false, "帳戶已移除或 app 正在結束，沒有完成", "info");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    outcome = CodexResetCreditRules.Describe(null, ex.Message);
                }
                return (outcome, read);
            });
            if (!disposed && Find(accountId) != null && limits != null)
                ApplyCodexLimits(accountId, limits);
            Log.Info($"Codex 免費重置（{accountId}）：{result.Text}");
            return result;
        }
        finally
        {
            resetCreditBusy.Remove(accountId);
            if (!disposed)
                RaiseChanged();
        }
    }

    /// <summary>
    /// Antigravity：依 <see cref="AccountRules.AntigravityProbePlan"/> 執行。
    /// 還沒有模型清單、或這次不查額度（多電腦同步由別台負責）時跑 agy models；
    /// 用量閘門放行時跑 agy -p /usage --output-format json（模型清單已知時只跑這一次，同時確認登入狀態、從記錄讀信箱並取額度）
    /// </summary>
    private async Task RefreshAntigravityAsync(AgentAccount account)
    {
        if (CliPaused(AgentProvider.Antigravity))
        {
            PollSoon(account.Id, CliPausedRetry);
            return;
        }
        var launcher = ResolveLauncher(AgentProvider.Antigravity);
        if (launcher == null)
        {
            SetStatus(account, AccountStatus.NotInstalled, null);
            return;
        }

        var plan = AccountRules.AntigravityProbePlan(ShouldPollUsage(account), NeedsCapabilities?.Invoke(account.Id) == true);
        var commandTimeout = AntigravityCommandTimeout;
        var agyConfigDir = ResolveAntigravityConfigDirectory?.Invoke(account) ?? AccountRules.ResolveConfigDirectory(account);
        var lifetime = LifetimeOf(account.Id);
        var probe = await Task.Run(async () =>
        {
            AntigravityStreamRules.ModelsEvaluation? models = null;
            IReadOnlyList<AgentSlashCommand>? commands = null;
            string? email = null;
            if (plan.Models)
            {
                var (run, modelsEmail) = await RunAntigravityWithLogAsync(launcher, account, "models",
                    AntigravityStreamRules.ModelsArgumentsWithLog, commandTimeout, allowRetry: true, lifetime);
                models = AntigravityStreamRules.EvaluateModelsCommand(run.ExitCode, run.Stdout, run.Stderr, run.TimedOut);
                email = modelsEmail;
                if (models.Status != AccountStatus.Ready)
                    return (Models: models, Commands: commands, Usage: (AntigravityStreamRules.UsageEvaluation?)null, ParsedUsage: ((IReadOnlyList<UsageWindow> Windows, string? Note)?)null, Credits: (string?)null, Email: email);

                commands = AntigravityStreamRules.DiscoverLocalCommands(agyConfigDir);
            }

            AntigravityStreamRules.UsageEvaluation? usageEval = null;
            (IReadOnlyList<UsageWindow> Windows, string? Note)? parsedUsage = null;
            string? credits = null;
            if (plan.Usage)
            {
                if (email == null)
                {
                    var (run, usageEmail) = await RunAntigravityWithLogAsync(launcher, account, "usage",
                        AntigravityStreamRules.UsageArgumentsWithLog, commandTimeout, allowRetry: true, lifetime);
                    usageEval = AntigravityStreamRules.EvaluateUsageCommand(run.ExitCode, run.Stdout, run.Stderr, run.TimedOut);
                    email = usageEmail;
                }
                else
                {
                    var run = await RunAntigravityCommandAsync(launcher, account, "usage",
                        AntigravityStreamRules.UsageArguments, commandTimeout, lifetime);
                    usageEval = AntigravityStreamRules.EvaluateUsageCommand(run.ExitCode, run.Stdout, run.Stderr, run.TimedOut);
                }

                if (usageEval.UsageData != null)
                {
                    parsedUsage = UsageRules.ParseAntigravityUsage(usageEval.UsageData);
                    // 只有當有額度窗耗盡鎖定時，才補查一次 AI 點數餘額（避免每次背景輪詢多起一個 language server 程序）
                    if (parsedUsage is { } pu && pu.Windows.Any(w => w.Locked))
                    {
                        var creditsRun = await CliRunner.RunAsync(launcher, AntigravityStreamRules.CreditsArguments,
                            env => AccountRules.ApplyCommandEnvironment(account, env), commandTimeout, lifetime, EnsureAntigravityEmptyWorkspace());
                        credits = AntigravityStreamRules.ParseCreditsBalance(creditsRun.ExitCode, creditsRun.Stdout);
                    }
                }
            }
            return (Models: models, Commands: commands, Usage: usageEval, ParsedUsage: parsedUsage, Credits: credits, Email: email);
        });
        if (disposed || Find(account.Id) == null)
            return;
        if (DeferIfIdentityHeld(account.Id))
            return;

        var status = probe.Models?.Status ?? probe.Usage!.Status;
        var statusError = probe.Models?.Status == AccountStatus.Error ? probe.Models.Error
            : probe.Models == null && probe.Usage?.Status == AccountStatus.Error ? probe.Usage.Error
            : null;
        if (probe.Models?.Status == AccountStatus.Ready && probe.Usage?.Status == AccountStatus.NotLoggedIn)
            status = AccountStatus.NotLoggedIn;

        if (status == AccountStatus.Error)
            Log.Warn($"帳戶 {account.Label} 檢查失敗：{statusError}");
        else if (probe.Usage?.Status == AccountStatus.Error)
            Log.Warn($"帳戶 {account.Label} 查詢用量失敗：{probe.Usage.Error}");

        ApplyIdentity(account, probe.Email, null);
        SetStatus(account, status, status == AccountStatus.Error ? $"檢查失敗：{statusError}" : null);
        if (status != AccountStatus.Ready)
        {
            if (status == AccountStatus.Error && plan.Usage)
                MarkStale(account.Id, statusError);
            return;
        }
        if (probe.Models != null && (probe.Models.Models.Count > 0 || probe.Commands is { Count: > 0 }))
            CapabilitiesDiscovered?.Invoke(account.Id, new AccountCapabilities(probe.Models.Models, probe.Commands ?? AntigravityStreamRules.BuiltinWorkflowCommands));
        if (!plan.Usage || probe.Usage == null)
            return;
        if (probe.ParsedUsage is { } parsed)
            StoreUsage(account.Id, new UsageSnapshot(DateTimeOffset.UtcNow, "api", parsed.Windows, probe.Credits, parsed.Note, null, false));
        else
            MarkStale(account.Id, probe.Usage.Error ?? "用量回應的格式無法解析");
    }

    /// <summary>這一次的 agy 記錄寫到暫存檔，取登入的信箱（D1：同步以 antigravity:{信箱} 認身分；不讀認證管理員、不碰權杖），讀完就刪；逾時可重試一次</summary>
    private async Task<(CliRunResult Run, string? Email)> RunAntigravityWithLogAsync(
        AgentLauncher launcher,
        AgentAccount account,
        string prefix,
        Func<string, IReadOnlyList<string>> buildArgs,
        TimeSpan timeout,
        bool allowRetry,
        CancellationToken ct)
    {
        var workDir = EnsureAntigravityEmptyWorkspace();
        for (int attempt = 0; ; attempt++)
        {
            var logFile = Path.Combine(AppPaths.CacheDirectory, "agy-log", $"{prefix}-{Guid.NewGuid():N}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            var run = await CliRunner.RunAsync(launcher, buildArgs(logFile),
                env => AccountRules.ApplyCommandEnvironment(account, env), timeout, ct, workDir);
            var email = ReadSignedInEmail(logFile);
            if (!run.TimedOut || !allowRetry || attempt > 0 || ct.IsCancellationRequested || CliPaused(AgentProvider.Antigravity))
                return (run, email);
            Log.Warn($"Antigravity 查詢（{prefix}）逾時（{timeout.TotalSeconds:0.#} 秒），重試一次");
        }
    }

    /// <summary>不帶記錄檔的短命 agy 查詢（信箱已知時的額度查詢）：跑在空的快取資料夾，逾時重試一次</summary>
    private async Task<CliRunResult> RunAntigravityCommandAsync(
        AgentLauncher launcher,
        AgentAccount account,
        string prefix,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var workDir = EnsureAntigravityEmptyWorkspace();
        for (int attempt = 0; ; attempt++)
        {
            var run = await CliRunner.RunAsync(launcher, args,
                env => AccountRules.ApplyCommandEnvironment(account, env), timeout, ct, workDir);
            if (!run.TimedOut || attempt > 0 || ct.IsCancellationRequested || CliPaused(AgentProvider.Antigravity))
                return run;
            Log.Warn($"Antigravity 查詢（{prefix}）逾時（{timeout.TotalSeconds:0.#} 秒），重試一次");
        }
    }

    /// <summary>
    /// 短命 agy 查詢的工作資料夾（空的快取子資料夾）：agy 跑 models 與 -p /usage 仍會啟動 language server 並對工作目錄建
    /// workspace store 與 file_watcher，繼承 app 啟動目錄時會掃描大量檔案（如 current\ 的 DLL）
    /// </summary>
    private static string EnsureAntigravityEmptyWorkspace()
    {
        var dir = Path.Combine(AppPaths.CacheDirectory, "agy-empty");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>短命 agy 查詢（models、-p /usage）單次執行的時限（正常約 2～5 秒；要啟動 language server、向後端查詢，逾時會重試一次）。測試接縫</summary>
    internal TimeSpan AntigravityCommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>解析 Antigravity 設定資料夾位置（測試接縫，避免單元測試掃描真實 ~/.gemini）</summary>
    internal Func<AgentAccount, string?>? ResolveAntigravityConfigDirectory { get; set; }

    /// <summary>agy 的記錄檔裡登入的信箱（<see cref="AntigravityStreamRules.ParseSignedInEmail"/>），讀完刪掉；讀不到（沒寫出來、被鎖）回 null</summary>
    private static string? ReadSignedInEmail(string logFile)
    {
        try
        {
            if (!File.Exists(logFile))
                return null;
            using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return AntigravityStreamRules.ParseSignedInEmail(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀取 agy 的記錄失敗（這次不更新 Antigravity 的信箱）：{ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                File.Delete(logFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 刪不掉就留著（agy 的診斷記錄與信箱，沒有權杖；每次的檔名不同）
            }
        }
    }

    /// <summary>
    /// Grok：短命的 grok agent（ACP）查 _x.ai/auth/info（只讀，判斷登入與信箱）與 _x.ai/billing（額度與方案）；
    /// 模型清單在 initialize 的回應裡（不必建立對話）。不用 check_subscription：它會順便 refresh 權杖
    /// </summary>
    private async Task RefreshGrokAsync(AgentAccount account)
    {
        if (CliPaused(AgentProvider.Grok))
        {
            // Grok CLI 正在更新（安裝變動中）：不起 grok，更新完再查
            PollSoon(account.Id, CliPausedRetry);
            return;
        }
        var launcher = ResolveLauncher(AgentProvider.Grok);
        if (launcher == null)
        {
            SetStatus(account, AccountStatus.NotInstalled, null);
            return;
        }

        bool needModels = NeedsCapabilities?.Invoke(account.Id) == true;
        var lifetime = LifetimeOf(account.Id);
        var probe = await Task.Run(async () =>
        {
            await using var client = await GrokAcpClient.StartAsync(launcher, env => AccountRules.ApplyEnvironment(account, env), CancellationToken.None);
            var (info, infoError) = await client.RequestAsync(GrokAcpRules.MethodAuthInfo, new JObject(), TimeSpan.FromSeconds(20), lifetime);
            if (infoError != null)
                return new GrokProbe(AccountStatus.Error, null, null, null, infoError.Text);
            var auth = GrokAcpRules.EvaluateAuthInfo(info);
            if (auth.Status != AccountStatus.Ready)
                return new GrokProbe(auth.Status, auth.Email, null, null, null);
            var (billing, billingError) = await client.RequestAsync(GrokAcpRules.MethodBilling, new JObject(), TimeSpan.FromSeconds(30), lifetime);
            // 有信箱但權杖已失效、refresh 也失敗（例如在別處登出）：billing 回 Authentication required
            if (GrokAcpRules.IsAuthRequired(billingError))
                return new GrokProbe(AccountStatus.NotLoggedIn, auth.Email, null, null, null);
            var models = needModels ? GrokAcpRules.ParseInitializeModels(client.InitializeResult) : null;
            return new GrokProbe(AccountStatus.Ready, auth.Email, GrokAcpRules.ParsePlan(billing), billing as JObject, billingError?.Text, models);
        });
        if (disposed || Find(account.Id) == null)
            return;

        ApplyIdentity(account, probe.Email, probe.Plan);
        SetStatus(account, probe.Status, probe.Status == AccountStatus.Error ? $"檢查失敗：{probe.Error}" : null);
        if (probe.Models is { Count: > 0 })
            CapabilitiesDiscovered?.Invoke(account.Id, new AccountCapabilities(probe.Models, Array.Empty<AgentSlashCommand>()));
        if (probe.Status != AccountStatus.Ready)
            return;
        if (UsageRules.ParseGrokBilling(probe.Billing) is { } parsed)
            StoreUsage(account.Id, new UsageSnapshot(DateTimeOffset.UtcNow, "api", parsed.Windows, null, parsed.Note, null, false));
        else
            MarkStale(account.Id, probe.Error ?? "用量回應的格式無法解析");
    }

    private void ApplyIdentity(AgentAccount account, string? email, string? plan)
    {
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(email) && account.Email != email)
        {
            account.Email = email;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(plan) && account.Plan != plan)
        {
            account.Plan = plan;
            changed = true;
        }
        if (changed)
            Save();
    }

    private void SetStatus(AgentAccount account, AccountStatus status, string? text)
    {
        var state = GetState(account.Id);
        state.Status = status;
        // 自動登出之後又查到已登入（例如在 app 外重新登入；審查 F5）：「已在這台自動登出」不再成立。自動登出沒有成功的那一句留到登出成功
        if (state.RemoteLoginNote is not null && state.RemoteLoginNoteLoggedOut && status is AccountStatus.Ready or AccountStatus.ApiKey)
            ClearRemoteLoginNote(state);
        // 遠端登入身分不符的說明（D15）取代預設說明：經 link.upsert 到網頁矩陣的那一格
        state.StatusText = text ?? RemoteAccountRules.StatusTextWithNote(state.RemoteLoginNote, AccountRules.DescribeStatus(account.Provider, status));
        state.CheckedAt = DateTime.Now;
        if (status is AccountStatus.NotLoggedIn)
            usage.Remove(account.Id);
    }

    /// <summary>清掉遠端登入身分不符的說明（D15）與它「已自動登出」的標記</summary>
    private static void ClearRemoteLoginNote(AccountRuntimeState state)
    {
        state.RemoteLoginNote = null;
        state.RemoteLoginNoteLoggedOut = false;
    }

    private void StoreUsage(string accountId, UsageSnapshot snapshot)
    {
        usage[accountId] = snapshot;
        history.Record(accountId, snapshot);
        UsageObserved?.Invoke(accountId, snapshot);
    }

    private void MarkStale(string accountId, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        var last = GetUsage(accountId) ?? history.GetLastSnapshot(accountId);
        usage[accountId] = last == null
            ? UsageSnapshot.Empty(error)
            : last with { Windows = UsageRules.ApplyResets(last.Windows, now), Stale = true, Error = error, Source = "cache" };
    }

    /// <summary>對話程序回報的額度（Claude rate_limit_event、Codex rateLimits/updated）：即時合併進快照</summary>
    public void ReportSessionRateLimit(string accountId, AgentProvider provider, JObject raw)
    {
        if (Find(accountId) == null)
            return;
        List<UsageWindow> updates;
        string? credits = null;
        string? note = null;
        switch (provider)
        {
            case AgentProvider.Claude:
                updates = UsageRules.ParseClaudeRateLimitEvent(raw);
                break;
            case AgentProvider.Codex:
                (updates, credits) = UsageRules.ParseCodexRateLimits(raw);
                note = UsageRules.DescribeCodexBlock(raw);
                break;
            case AgentProvider.Antigravity:
            case AgentProvider.Grok:
                // agy、grok 的對話不從 rate_limit 解析額度（會話不發 AgentRateLimitEvent；grok 每輪結束由 ReportTurnCompleted 排一次重查）。
                // Grok 額度鎖定是輪末 _x.ai/billing 查出後的 AgentQuotaExhaustedEvent，不走這條提早 return。
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
        if (updates.Count == 0)
            return;
        var current = GetUsage(accountId);
        var merged = UsageRules.Merge(current?.Windows, updates);
        // 免費重置次數只有官方用量端點帶（rateLimits/updated 沒有），沿用上次查到的
        var next = new UsageSnapshot(DateTimeOffset.UtcNow, "session", merged, credits ?? current?.Credits, note ?? current?.Note, null, false,
            current?.ResetCredits, current?.ResetCreditExpiresAt);
        StoreUsage(accountId, next);
        // 每個 API 請求都回報一次、數字通常沒變：畫面上看得到的都一樣就不通知（以前每次都讓帳戶卡整份重算、網頁重整側欄與每個開著的對話）
        if (!UsageRules.SameForView(current, next))
            RaiseChanged();
    }

    // ───────────────────────────── 憑證檔監看 ─────────────────────────────

    private void Watch(AgentAccount account)
    {
        try
        {
            // Antigravity 的登入在認證管理員，沒有檔案可監看
            if (AccountRules.GetCredentialFile(account) is not { } file)
                return;
            var dir = Path.GetDirectoryName(file);
            if (dir == null || !Directory.Exists(dir))
                return;
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            var id = account.Id;
            long? last = null;
            void OnChange(object? sender, FileSystemEventArgs e) => post(() =>
            {
                // CLI 寫檔會連續觸發好幾次：2 秒內只處理一次（單調時鐘：系統時間往回調時不會一直被當成還在 2 秒內）
                var now = Environment.TickCount64;
                if (last is { } previous && now - previous < 2000)
                    return;
                last = now;
                _ = Task.Delay(1500).ContinueWith(_ => post(() => _ = RefreshAsync(id, includeStatus: true, skipIfRunning: false, ClaudeStatusReason.CredentialsChanged)),
                    TaskScheduler.Default);
            });
            watcher.Changed += OnChange;
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += (s, e) => OnChange(s, e);
            // 監看的資料夾被刪掉（或事件太多溢位）後監看就停了：丟掉它並重查，之後的查詢會在資料夾回來時補建
            watcher.Error += (_, _) => post(() =>
            {
                if (!watchers.TryGetValue(id, out var current) || current != watcher)
                    return;
                watchers.Remove(id);
                watcher.Dispose();
                _ = RefreshAsync(id, includeStatus: true, skipIfRunning: false, ClaudeStatusReason.CredentialsChanged);
            });
            watchers[account.Id] = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"無法監看帳戶憑證檔：{ex.Message}");
        }
    }

    // ───────────────────────────── 登入與登出 ─────────────────────────────

    /// <summary>
    /// 登入（或重新登入）。所有帳戶的登入排成一列（Codex 的登入回呼用固定的本機連接埠，同時開兩個會衝突），
    /// 排隊時先回報「等待另一個帳戶完成登入」；可用 CancelLogin 取消，移除帳戶也會取消它的登入。
    /// request＝登入選項（Claude 帶 --email 的預期信箱；Codex 可以選裝置碼登入；null＝各種類原本的登入）。
    /// 裝置碼登入只有 Codex（隔離帳戶，或本來就未登入的系統預設帳戶）：不合格就以 done:true 說明原因，不起 app-server、不落進主控台登入（D19）
    /// </summary>
    public Task LoginAsync(string accountId, LoginRequest? request = null) => LoginAsync(accountId, request, remoteStart: null);

    /// <param name="remoteStart">遠端登入（StartRemoteLoginAsync）這一次的里程碑；建立 LoginRun 時帶入，所以只會送到這一次。本機登入是 null</param>
    private async Task LoginAsync(string accountId, LoginRequest? request, TaskCompletionSource<RemoteLoginStart>? remoteStart)
    {
        var account = Find(accountId);
        if (account == null)
            return;
        var method = request?.Method ?? LoginMethod.Default;
        LastLoginRequest = (method, request?.ClaudeEmail is not null);
        if (method == LoginMethod.DeviceCode && CodexDeviceCodeRules.Refusal(account, GetState(accountId).Status) is { } refusal)
        {
            // 登入進行中時不插話（畫面上本來就沒有這個選項；done:true 會讓網頁以為進行中的登入結束了）
            if (!GetState(accountId).LoginBusy)
                LoginProgress?.Invoke(accountId, refusal, true, false);
            return;
        }
        if (CliPaused(account.Provider))
        {
            // 登入用的 app-server／主控台會擋住 CLI 換檔（7.3.1）：等更新完成
            LoginProgress?.Invoke(accountId, $"{CliInstallStabilityRules.DisplayName(account.Provider)} CLI 正在更新，請等更新完成後再登入", true, false);
            return;
        }
        var launcher = ResolveLauncher(account.Provider);
        if (launcher == null)
        {
            LoginProgress?.Invoke(accountId, AccountRules.DescribeStatus(account.Provider, AccountStatus.NotInstalled) + "，請先到設定安裝", true, false);
            return;
        }

        var state = GetState(accountId);
        if (state.LoginBusy)
            return;
        state.LoginBusy = true;
        var run = new LoginRun(LifetimeOf(accountId)) { RemoteStart = remoteStart, Expectation = request?.Remote };
        logins[accountId] = run;
        RaiseChanged();
        bool? success = null;
        bool canceled = false;
        var verdict = RemoteIdentityVerdict.Unknown;
        try
        {
            // 新的一次登入：上次遠端登入身分不符的說明不再適用（D15）
            ClearRemoteLoginNote(state);
            // 遠端登入：身分比對完之前暫停這個帳戶的查詢（D14；在 try 之內登記，finally 一定放開，提早返回不會留下）
            if (run.Expectation is not null)
                identityHold.Add(accountId);
            if (loginGate.CurrentCount == 0)
                LoginProgress?.Invoke(accountId, "等待另一個帳戶完成登入…", false, null);
            await loginGate.WaitAsync(run.Cancel.Token);
            try
            {
                success = account.Provider switch
                {
                    AgentProvider.Claude => await LoginClaudeAsync(account, launcher, request, run.Cancel.Token),
                    AgentProvider.Codex => method switch
                    {
                        LoginMethod.Default => await LoginCodexAsync(account, launcher, run.Cancel.Token),
                        LoginMethod.DeviceCode => await LoginCodexDeviceCodeAsync(account, launcher, run.Cancel.Token),
                        _ => throw new ArgumentOutOfRangeException(nameof(request), method, null),
                    },
                    AgentProvider.Antigravity => await LoginAntigravityAsync(account, launcher, run, run.Cancel.Token),
                    AgentProvider.Grok => await LoginGrokAsync(account, launcher, method, run, run.Cancel.Token),
                    _ => throw new ArgumentOutOfRangeException(nameof(accountId), account.Provider, null),
                };
            }
            finally
            {
                loginGate.Release();
            }
            // 遠端登入完成：比對身分，不符就在同一個資料夾登出（D12、D13；Codex 的 app-server 這時已經關掉，不會把登入寫回來）
            if (success == true && run.Expectation is not null)
                verdict = await VerifyRemoteIdentityAsync(account, run);
        }
        catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested)
        {
            canceled = true;
            success = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"登入 {account.Label} 失敗：{ex.Message}");
            if (Find(accountId) != null)
                LoginProgress?.Invoke(accountId, $"登入失敗：{ex.Message}", true, false);
            success = false;
        }
        finally
        {
            if (run.Expectation is not null)
                identityHold.Remove(accountId);
            // LoginBusy 留到登入後的重新查詢做完才清（下面；審查 F1）
            if (logins.TryGetValue(accountId, out var current) && current == run)
                logins.Remove(accountId);
            run.Cancel.Dispose();
            run.Closed.TrySetResult();
            // 遠端登入在里程碑之前就結束了（不支援、回應不合格、程序結束、取消）：不必等之後的重新查詢（已經有里程碑時沒有作用）
            run.RemoteStart?.TrySetResult(new RemoteLoginStart(RemoteLoginMilestone.Failed, RemoteResultCodes.Conflict,
                canceled ? RemoteAccountRules.LoginCanceledMessage : null));
        }

        // 登入期間帳戶被移除：不再查詢、不回報（也不替它建立新的狀態）
        if (disposed || Find(accountId) == null)
        {
            state.LoginBusy = false;
            return;
        }
        // 登入後的重新查詢做完才清 LoginBusy（審查 F1）：先清的話，查詢那幾秒（起 app-server、auth status）內任何一次帳戶清單送出
        // （別的帳戶查詢完、有對話在跑）都會經 link.upsert 發布「loginBusy=false＋登入前的狀態」，網頁的追蹤把成功的登入判成沒有完成。
        // 這段期間 CancelLogin 什麼都不做（logins 已經沒有這一筆）；遠端登入回 conflict、遠端登出回 busy
        try
        {
            await RefreshAsync(accountId, includeStatus: true);
        }
        finally
        {
            state.LoginBusy = false;
            RaiseChanged();
        }
        if (disposed || Find(accountId) == null)
            return;
        // 遠端登入的身分不符：結果寫明（D15；不含信箱），不算登入成功
        var mismatch = verdict == RemoteIdentityVerdict.Mismatch;
        var ok = !mismatch && GetState(accountId).Status == AccountStatus.Ready;
        var message = mismatch ? GetState(accountId).RemoteLoginNote ?? RemoteAccountRules.MismatchNote(loggedOut: false)
            : ok ? $"「{account.Label}」已登入"
            : canceled ? "已取消登入"
            : success == false ? "登入沒有完成"
            : GetState(accountId).StatusText;
        LoginProgress?.Invoke(accountId, message, true, ok);
    }

    /// <summary>取消進行中的登入：關掉登入主控台、請 app-server 取消 app 內登入；還在排隊等其他帳戶的直接取消</summary>
    public void CancelLogin(string accountId)
    {
        if (logins.TryGetValue(accountId, out var run))
            run.Cancel.Cancel();
    }

    // ───────────────────────────── 遠端登入（WP4.8） ─────────────────────────────

    /// <summary>
    /// 遠端登入前置條件的事實（D6；UI 執行緒）：受管資料夾＝<see cref="GetDeletableAccountRoot"/> 不是 null；登入已到期看
    /// AccountRuntimeState.LoginExpiresAt（Claude；Codex 一律 null＝沒到期）
    /// </summary>
    internal RemoteLoginFacts GetRemoteLoginFacts(AgentAccount account, DateTimeOffset now)
    {
        var state = GetState(account.Id);
        return new RemoteLoginFacts(account.Provider, Exists: true, account.IsSystemDefault, ManagedFolder: GetDeletableAccountRoot(account) != null, state.Status,
            LoginExpired: state.LoginExpiresAt is { } expires && expires <= now.ToUnixTimeMilliseconds(), AccountLoginBusy: state.LoginBusy,
            AnyLoginActive: HasActiveLogin, CliPaused: CliPaused(account.Provider), CliInstalled: ResolveLauncher(account.Provider) != null);
    }

    /// <summary>
    /// 遠端登出前置條件的事實（D16；UI 執行緒）：CLI 正在更新（同不起程序的 CliPaused）、這個帳戶正在登入、找得到 CLI
    /// （<see cref="ResolveLauncher"/> 不是 null＝已安裝）
    /// </summary>
    internal RemoteLogoutFacts GetRemoteLogoutFacts(AgentAccount account)
    {
        var state = GetState(account.Id);
        return new RemoteLogoutFacts(account.Provider, state.Status, CliPaused: CliPaused(account.Provider), LoginBusy: state.LoginBusy,
            CliInstalled: ResolveLauncher(account.Provider) != null);
    }

    /// <summary>要新建帳戶時的事實（D6 表下：不存在、受管、未登入、不是系統預設；新建的一律是 app 的隔離資料夾）</summary>
    internal RemoteLoginFacts GetNewAccountFacts(AgentProvider provider) => new(provider, Exists: false, IsSystemDefault: false, ManagedFolder: true,
        AccountStatus.NotLoggedIn, LoginExpired: false, AccountLoginBusy: false, AnyLoginActive: HasActiveLogin, CliPaused: CliPaused(provider),
        CliInstalled: ResolveLauncher(provider) != null);

    /// <summary>
    /// 開始遠端登入（UI 執行緒；D7、D8）：呼叫 LoginAsync，等「這次登入的里程碑、LoginAsync 的工作、期限」先到的。
    /// <list type="bullet">
    /// <item>里程碑先到：回 CodeShown（帶這一組驗證碼）或 ConsoleOpened</item>
    /// <item>LoginAsync 先結束（任何出口，含 try 之外的提早返回）或這次登入在里程碑之前結束：回 Failed(conflict, 這次期間最後一則進度)</item>
    /// <item>期限（<see cref="RemoteStartDeadline"/>）先到：取消這一次登入（只取消這一次，不碰別的登入），回 Failed(conflict, StartTimeoutMessage)</item>
    /// </list>
    /// 前置條件由呼叫端先查（RemoteAccountRules.CheckLogin）；新建帳戶的回滾也由呼叫端做
    /// </summary>
    internal async Task<RemoteLoginStart> StartRemoteLoginAsync(string accountId, LoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = Find(accountId)?.Provider;
        var started = new TaskCompletionSource<RemoteLoginStart>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 這次期間最後一則進度（失敗的原因）：只記到這次登入結束為止（之後重新查詢完的「登入沒有完成」不是原因）
        string? last = null;
        void OnProgress(string id, string message, bool done, bool? success)
        {
            if (id == accountId && !started.Task.IsCompleted)
                last = message;
        }

        LoginProgress += OnProgress;
        using var stopDeadline = new CancellationTokenSource();
        try
        {
            var login = LoginAsync(accountId, request, started);
            var deadline = Task.Delay(RemoteStartDeadline, stopDeadline.Token);
            await Task.WhenAny(started.Task, login, deadline);
            // 里程碑在前：同時完成時以里程碑為準
            if (started.Task.IsCompleted && started.Task.Result.Milestone != RemoteLoginMilestone.Failed)
                return started.Task.Result;
            if (started.Task.IsCompleted || login.IsCompleted)
            {
                var message = started.Task.IsCompleted ? started.Task.Result.Message : null;
                return new RemoteLoginStart(RemoteLoginMilestone.Failed, RemoteResultCodes.Conflict, message ?? last ?? RemoteAccountRules.LoginNotStartedMessage);
            }
            // 期限到了：只取消這一次登入（帳戶的登入已換成別的就不動）
            if (logins.TryGetValue(accountId, out var run) && run.RemoteStart == started)
                run.Cancel.Cancel();
            return new RemoteLoginStart(RemoteLoginMilestone.Failed, RemoteResultCodes.Conflict,
                provider is { } p ? RemoteAccountRules.StartTimeoutMessage(p) : RemoteAccountRules.LoginNotStartedMessage);
        }
        finally
        {
            LoginProgress -= OnProgress;
            stopDeadline.Cancel();
        }
    }

    /// <summary>遠端登入的里程碑只送給這個帳戶進行中的這一次登入（LoginRun 建立時帶入的 TCS；本機登入沒有，什麼都不做）</summary>
    private void SignalRemoteStart(string accountId, RemoteLoginStart start)
    {
        if (logins.TryGetValue(accountId, out var run))
            run.RemoteStart?.TrySetResult(start);
    }

    /// <summary>測試用：這個帳戶進行中的登入是不是遠端登入（掛著里程碑）</summary>
    internal bool HasRemoteLoginRun(string accountId) => logins.TryGetValue(accountId, out var run) && run.RemoteStart is not null;

    /// <summary>進行中的 Claude app 內登入（帳戶 id → 不送訊息的程序與這次流程）：遠端貼回的授權碼經 SubmitClaudeLoginCodeAsync 交給它</summary>
    private readonly ConcurrentDictionary<string, (ClaudeControlClient Client, ClaudeLoginFlow Flow)> claudeLoginFlows = new(StringComparer.Ordinal);

    /// <summary>
    /// Claude 登入（ClaudeLoginRules）：先試 app 內登入——不送訊息的程序 claude_authenticate，這台登入就開瀏覽器到本機回呼的網址，
    /// 遠端登入（WP4.8）把手動的網址交給網頁（里程碑 UrlShown），等 claude_oauth_wait_for_completion。CLI 不支援、程序起不來、網址不合格才退回
    /// 主控台的 claude auth login。登入完成後照舊重新檢查（預期信箱的比對與自動登出在 LoginAsync）
    /// </summary>
    private async Task<bool?> LoginClaudeAsync(AgentAccount account, AgentLauncher launcher, LoginRequest? request, CancellationToken ct)
    {
        bool remote = logins.TryGetValue(account.Id, out var run) && run.RemoteStart is not null;
        try
        {
            await using var client = await ClaudeControlClient.StartAsync(launcher, env => AccountRules.ApplyEnvironment(account, env), killWithApp: true, ct);
            var auth = await client.RequestAsync(ClaudeLoginRules.BuildAuthenticateRequest(), TimeSpan.FromSeconds(30), ct);
            if (auth is { Success: true } && ClaudeLoginRules.ParseAuthenticate(auth.Response) is { } flow)
            {
                claudeLoginFlows[account.Id] = (client, flow);
                try
                {
                    if (remote)
                    {
                        LoginProgress?.Invoke(account.Id, "已在網頁上給出 Claude 登入網址，等那邊登入後貼回授權碼…", false, null);
                        SignalRemoteStart(account.Id, new RemoteLoginStart(RemoteLoginMilestone.UrlShown, LoginUrl: flow.ManualUrl));
                    }
                    else
                    {
                        OpenBrowser(flow.AutomaticUrl);
                        LoginProgress?.Invoke(account.Id, "已開啟瀏覽器，請完成 Claude 登入…", false, null);
                    }
                    var done = await client.RequestAsync(ClaudeLoginRules.BuildWaitRequest(), LoginTimeout, ct);
                    if (done is { Success: true })
                        return true;
                    LoginProgress?.Invoke(account.Id, done == null ? "登入逾時，沒有完成" : ClaudeLoginRules.DescribeError(done.Error), false, null);
                    return false;
                }
                finally
                {
                    claudeLoginFlows.TryRemove(account.Id, out _);
                }
            }
            Log.Warn($"Claude app 內登入不可用（{auth?.Error ?? "網址不合格或沒有回應"}），改用主控台登入");
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Claude app 內登入失敗，改用主控台登入：{ex.Message}");
        }
        catch (IOException ex)
        {
            Log.Warn($"Claude app 內登入失敗，改用主控台登入：{ex.Message}");
        }

        ct.ThrowIfCancellationRequested();
        return await LoginClaudeConsoleAsync(account, launcher, request, ct);
    }

    /// <summary>
    /// 遠端登入貼回來的授權碼（accountLoginCode；UI 執行緒）：交給這個帳戶進行中的 app 內登入（claude_oauth_callback），
    /// 之後 claude_oauth_wait_for_completion 也會回來、LoginAsync 照常收尾。回傳 null＝成功，否則是給人看的原因
    /// </summary>
    public async Task<string?> SubmitClaudeLoginCodeAsync(string accountId, string? pasted)
    {
        if (!claudeLoginFlows.TryGetValue(accountId, out var active))
            return "這個帳戶沒有進行中的 Claude 登入，請重新登入";
        if (ClaudeLoginRules.ParseCode(pasted, active.Flow.State) is not { } code)
            return "授權碼的格式不對，請把登入頁上顯示的整串貼過來";
        var response = await active.Client.RequestAsync(ClaudeLoginRules.BuildCallbackRequest(code, active.Flow.State), TimeSpan.FromSeconds(60));
        if (response is { Success: true })
            return null;
        return response == null ? "Claude 沒有回應，請重新登入" : ClaudeLoginRules.DescribeError(response.Error);
    }

    /// <summary>
    /// Claude：開主控台視窗跑 claude auth login（會開瀏覽器），視窗關閉後重新檢查（app 內登入不可用時）。有預期信箱（帳戶目錄，D12）而且是單純的 ASCII 信箱時
    /// 帶 --email（npm 版的 claude 是 .cmd、cmd 會重新解析參數，不合格的一律不帶）
    /// </summary>
    private async Task<bool?> LoginClaudeConsoleAsync(AgentAccount account, AgentLauncher launcher, LoginRequest? request, CancellationToken ct)
    {
        LoginProgress?.Invoke(account.Id, "已開啟登入視窗，請在瀏覽器完成 Claude 登入，完成後視窗會自動關閉…", false, null);
        var args = new List<string> { "auth", "login", "--claudeai" };
        if (AccountIdentityRules.LoginEmailArgument(request?.ClaudeEmail) is { } email)
        {
            args.Add("--email");
            args.Add(email);
        }
        using var process = CliRunner.StartConsole(launcher, args, env => AccountRules.ApplyEnvironment(account, env), "Claude 登入");
        // 遠端登入的里程碑（D7）：主控台已在這台開啟，要有人在這台電腦前完成
        SignalRemoteStart(account.Id, new RemoteLoginStart(RemoteLoginMilestone.ConsoleOpened));
        return await WaitForLoginConsoleAsync(process, ct);
    }

    /// <summary>
    /// Codex：在 app 內以 app-server 的 account/login/start 取得登入網址、開瀏覽器，等 account/login/completed；
    /// 不支援或失敗就退回主控台 codex login
    /// </summary>
    private async Task<bool?> LoginCodexAsync(AgentAccount account, AgentLauncher launcher, CancellationToken ct)
    {
        Action<IDictionary<string, string?>> env = e => AccountRules.ApplyEnvironment(account, e);
        try
        {
            await using var client = await CodexAppServerClient.StartAsync(launcher, env, CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            var completed = new TaskCompletionSource<(bool Success, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Notification += (method, parameters) =>
            {
                if (method == "account/login/completed")
                    completed.TrySetResult((parameters.Value<bool?>("success") ?? false, parameters.Value<string>("error")));
            };
            var (start, error) = await client.RequestAsync("account/login/start", new JObject { ["type"] = "chatgpt" }, TimeSpan.FromSeconds(30), ct);
            var authUrl = start?.Value<string>("authUrl");
            if (error == null && !string.IsNullOrWhiteSpace(authUrl) && Uri.TryCreate(authUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                OpenBrowser(uri);
                LoginProgress?.Invoke(account.Id, "已開啟瀏覽器，請完成 ChatGPT 登入…", false, null);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(LoginTimeout);
                try
                {
                    // app-server 中途結束就等不到完成通知了，不必等到逾時
                    await Task.WhenAny(completed.Task, client.ExitTask).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    // 逾時或取消：請 app-server 取消這次登入（關掉它的登入回呼）
                    await client.RequestAsync("account/login/cancel", new JObject { ["loginId"] = start!.Value<string>("loginId") }, TimeSpan.FromSeconds(10));
                    ct.ThrowIfCancellationRequested();
                    return false;
                }
                if (!completed.Task.IsCompleted)
                {
                    LoginProgress?.Invoke(account.Id, "Codex 程序已結束，登入沒有完成", false, null);
                    return false;
                }
                var (ok, loginError) = await completed.Task;
                if (!ok && !string.IsNullOrWhiteSpace(loginError))
                    LoginProgress?.Invoke(account.Id, $"登入失敗：{loginError}", false, null);
                return ok;
            }
            Log.Warn($"Codex app 內登入不可用（{error ?? "沒有登入網址"}），改用主控台登入");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log.Warn($"Codex app 內登入失敗，改用主控台登入：{ex.Message}");
        }

        ct.ThrowIfCancellationRequested();
        LoginProgress?.Invoke(account.Id, "已開啟登入視窗，請在瀏覽器完成 ChatGPT 登入…", false, null);
        using var process = CliRunner.StartConsole(launcher, new[] { "login" }, env, "Codex 登入");
        return await WaitForLoginConsoleAsync(process, ct);
    }

    /// <summary>
    /// Codex 裝置碼登入（WP3.4 第 11 步；設計 6.4，第 3 階段只有本機發起、驗證碼只在這台顯示，D1）：app-server 的
    /// account/login/start {type:"chatgptDeviceCode"} 拿到驗證碼與網址（主機一定是 auth.openai.com），顯示在帳戶卡上，等 account/login/completed。
    /// <list type="bullet">
    /// <item>不開瀏覽器：使用者可能在手機或別台電腦核准</item>
    /// <item>不支援、回應不合格、失敗都不退回主控台登入；回應不合格時有 loginId 就取消，記錄檔只記欄位名稱</item>
    /// <item>取消或逾時（DeviceCodeTimeout 與驗證碼的到期取較早者）送 account/login/cancel</item>
    /// <item>驗證碼只放在 AccountRuntimeState.DeviceCode（給這台的 accountLogin 訊息），不進記錄檔、例外訊息與同步 payload（R20）</item>
    /// </list>
    /// 身分不符不在這裡處理：本機發起的只由伺服器把連結標成 identityOk=false（D8）；遠端登入（WP4.8）完成時在同一條連線讀這次的信箱，
    /// 比對與登出由 LoginAsync 在這條連線關掉之後做（D12、D13）
    /// </summary>
    private async Task<bool?> LoginCodexDeviceCodeAsync(AgentAccount account, AgentLauncher launcher, CancellationToken ct)
    {
        await using var client = await CodexAppServerClient.StartAsync(launcher, e => AccountRules.ApplyEnvironment(account, e), CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var completed = new TaskCompletionSource<(bool Success, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Notification += (method, parameters) =>
        {
            if (method == "account/login/completed")
                completed.TrySetResult((parameters["success"] is JValue { Type: JTokenType.Boolean, Value: true }, parameters["error"] is JValue { Type: JTokenType.String, Value: string e } ? e : null));
        };

        var (start, error) = await client.RequestAsync("account/login/start", CodexDeviceCodeRules.StartParams(), TimeSpan.FromSeconds(30), ct);
        if (error != null)
        {
            // 舊版 Codex 不認得 chatgptDeviceCode（也可能逾時或程序結束）：說明後結束，不退回主控台登入
            Log.Warn($"Codex 裝置碼登入無法開始：{error}");
            LoginProgress?.Invoke(account.Id, "這個版本的 Codex 不支援裝置碼登入，請更新 Codex 或改用一般登入", false, null);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (CodexDeviceCodeRules.ParseStart(start, now) is not { } code)
        {
            // 看不懂或不安全的回應（主機不是 auth.openai.com、欄位不合格）：有 loginId 就請 app-server 取消；不顯示驗證碼、不開瀏覽器
            if (start?["loginId"] is JValue { Type: JTokenType.String, Value: string loginId } && loginId.Length > 0)
                await client.RequestAsync("account/login/cancel", new JObject { ["loginId"] = loginId }, TimeSpan.FromSeconds(10));
            Log.Warn($"Codex 裝置碼登入的回應不合格，已取消（欄位：{string.Join("、", CodexDeviceCodeRules.FieldNames(start))}）");
            LoginProgress?.Invoke(account.Id, "Codex 回傳的登入資訊不合格，已取消", false, null);
            return false;
        }

        var state = GetState(account.Id);
        bool ok;
        string? failure;
        state.DeviceCode = code;
        try
        {
            LoginProgress?.Invoke(account.Id, CodexDeviceCodeRules.ProgressText(code, now), false, null);
            // 遠端登入的里程碑（D7、D9）：帶這一組驗證碼（輸出由它組成，之後狀態裡的驗證碼可能已被清掉）
            SignalRemoteStart(account.Id, new RemoteLoginStart(RemoteLoginMilestone.CodeShown, DeviceCode: code));
            var left = TimeSpan.FromMilliseconds(Math.Max(0, code.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(left < DeviceCodeTimeout ? left : DeviceCodeTimeout);
            try
            {
                // app-server 中途結束就等不到完成通知了，不必等到逾時
                await Task.WhenAny(completed.Task, client.ExitTask).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // 取消或逾時：請 app-server 撤回這次登入（驗證碼之後就不能用了）
                await client.RequestAsync("account/login/cancel", new JObject { ["loginId"] = code.LoginId }, TimeSpan.FromSeconds(10));
                ct.ThrowIfCancellationRequested();
                state.DeviceCode = null;
                LoginProgress?.Invoke(account.Id, "驗證碼已過期，登入沒有完成", false, null);
                return false;
            }
            if (!completed.Task.IsCompleted)
            {
                (ok, failure) = (false, "Codex 程序已結束，登入沒有完成");
            }
            else
            {
                var (success, loginError) = await completed.Task;
                if (!success && CodexDeviceCodeRules.Redact(loginError, code) is { } redacted)
                    Log.Warn($"Codex 裝置碼登入沒有完成：{redacted}");
                (ok, failure) = success ? (true, null) : (false, "登入沒有完成：請先在 ChatGPT 的安全性設定開啟裝置碼登入，或改用一般登入");
                // 遠端登入（D12）：在同一條連線讀這次登入的信箱，存進這次的 LoginRun；比對與登出等這條連線關掉之後才做
                if (success && logins.TryGetValue(account.Id, out var run) && run.Expectation is not null)
                    run.CodexEmail = await ReadCodexEmailAsync(client, ct);
            }
        }
        finally
        {
            state.DeviceCode = null;
        }
        if (failure != null)
            LoginProgress?.Invoke(account.Id, failure, false, null);
        return ok;
    }

    /// <summary>登入完成後這條 app-server 連線的帳戶信箱（account/read {refreshToken:false}，不換發權杖）；讀不到、不是 ChatGPT 登入回 null</summary>
    private static async Task<string?> ReadCodexEmailAsync(CodexAppServerClient client, CancellationToken ct)
    {
        var (result, error) = await client.RequestAsync("account/read", new JObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(20), ct);
        if (error != null)
            return null;
        var (status, email, _) = AccountRules.EvaluateCodexAccount(result);
        return status == AccountStatus.Ready ? email : null;
    }

    /// <summary>
    /// Grok：在 app 內以 ACP 登入——送 authenticate（grok.com；會等到登入完成才回應）的同時輪詢 _x.ai/auth/get_url，
    /// 拿到網址（裝置碼已在網址裡）就開瀏覽器；取消或逾時送 _x.ai/auth/cancel。已經登入就直接回成功。
    /// 裝置碼（遠端登入，設計 2026-10-22-grok-antigravity-sync 的 D5）不在這台開瀏覽器：網址與代碼交給發起的那一端（CodeShown），最多等 10 分鐘
    /// </summary>
    private async Task<bool?> LoginGrokAsync(AgentAccount account, AgentLauncher launcher, LoginMethod method, LoginRun run, CancellationToken ct)
    {
        if (method == LoginMethod.DeviceCode)
            return await LoginGrokDeviceCodeAsync(account, launcher, run, ct);
        await using var client = await GrokAcpClient.StartAsync(launcher, env => AccountRules.ApplyEnvironment(account, env), CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var (info, _) = await client.RequestAsync(GrokAcpRules.MethodAuthInfo, new JObject(), TimeSpan.FromSeconds(20), ct);
        if (GrokAcpRules.EvaluateAuthInfo(info).Status == AccountStatus.Ready)
            return true;

        var authenticate = client.RequestAsync("authenticate", new JObject { ["methodId"] = "grok.com" }, LoginTimeout, ct);
        try
        {
            Uri? url = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (url == null && !authenticate.IsCompleted && DateTime.UtcNow < deadline)
            {
                var (result, _) = await client.RequestAsync(GrokAcpRules.MethodAuthGetUrl, new JObject(), TimeSpan.FromSeconds(10), ct);
                url = GrokAcpRules.ParseAuthUrl(result);
                if (url == null)
                    await Task.WhenAny(authenticate, Task.Delay(500, ct));
            }
            if (url != null)
            {
                OpenBrowser(url);
                LoginProgress?.Invoke(account.Id, "已開啟瀏覽器，請完成 Grok 登入…", false, null);
            }
            else if (!authenticate.IsCompleted)
            {
                LoginProgress?.Invoke(account.Id, "Grok 沒有提供登入網址，登入沒有完成", false, null);
                await client.RequestAsync(GrokAcpRules.MethodAuthCancel, new JObject(), TimeSpan.FromSeconds(10));
                return false;
            }

            // grok 中途結束就等不到回應了（RequestAsync 會收到「已結束」的錯誤），逾時也回錯誤
            var (_, error) = await authenticate;
            if (error == null)
                return true;
            await client.RequestAsync(GrokAcpRules.MethodAuthCancel, new JObject(), TimeSpan.FromSeconds(10));
            LoginProgress?.Invoke(account.Id, $"登入失敗：{error.Text}", false, null);
            return false;
        }
        catch (OperationCanceledException)
        {
            // 使用者取消（或帳戶被移除）：請 grok 取消這次登入，免得它之後還把權杖寫回來
            await client.RequestAsync(GrokAcpRules.MethodAuthCancel, new JObject(), TimeSpan.FromSeconds(10));
            throw;
        }
    }

    /// <summary>Grok 的裝置碼登入：網址與代碼不開瀏覽器，交給發起的那一端；登入完成時在同一條連線讀信箱（遠端登入的身分比對）</summary>
    private async Task<bool?> LoginGrokDeviceCodeAsync(AgentAccount account, AgentLauncher launcher, LoginRun run, CancellationToken ct)
    {
        await using var client = await GrokAcpClient.StartAsync(launcher, env => AccountRules.ApplyEnvironment(account, env), CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var (info, _) = await client.RequestAsync(GrokAcpRules.MethodAuthInfo, new JObject(), TimeSpan.FromSeconds(20), ct);
        if (GrokAcpRules.EvaluateAuthInfo(info).Status == AccountStatus.Ready)
            return true;

        var authenticate = client.RequestAsync("authenticate", new JObject { ["methodId"] = "grok.com" }, LoginTimeout, ct);
        var state = GetState(account.Id);
        try
        {
            Uri? url = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (url == null && !authenticate.IsCompleted && DateTime.UtcNow < deadline)
            {
                var (result, _) = await client.RequestAsync(GrokAcpRules.MethodAuthGetUrl, new JObject(), TimeSpan.FromSeconds(10), ct);
                url = GrokAcpRules.ParseAuthUrl(result);
                if (url == null)
                    await Task.WhenAny(authenticate, Task.Delay(500, ct));
            }

            var now = DateTimeOffset.UtcNow;
            if (GrokAcpRules.ParseDeviceCode(url, now) is not { } code)
            {
                // 沒有網址，或主機與代碼不合格：不顯示、不開瀏覽器
                await client.RequestAsync(GrokAcpRules.MethodAuthCancel, new JObject(), TimeSpan.FromSeconds(10));
                Log.Warn(url == null ? "Grok 沒有提供登入網址" : $"Grok 的登入網址不合格（主機 {url.IdnHost}），已取消");
                LoginProgress?.Invoke(account.Id, "Grok 沒有提供可用的登入網址，登入沒有完成", false, null);
                return false;
            }

            state.DeviceCode = code;
            LoginProgress?.Invoke(account.Id, CodexDeviceCodeRules.ProgressText(code, now), false, null);
            // 遠端登入的里程碑（同 Codex：帶這一組驗證碼）
            SignalRemoteStart(account.Id, new RemoteLoginStart(RemoteLoginMilestone.CodeShown, DeviceCode: code));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GrokAcpRules.DeviceCodeLifetime);
            try
            {
                await authenticate.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await client.RequestAsync(GrokAcpRules.MethodAuthCancel, new JObject(), TimeSpan.FromSeconds(10));
                ct.ThrowIfCancellationRequested();
                LoginProgress?.Invoke(account.Id, "驗證碼已過期，登入沒有完成", false, null);
                return false;
            }

            var (_, error) = await authenticate;
            if (error != null)
            {
                LoginProgress?.Invoke(account.Id, $"登入失敗：{error.Text}", false, null);
                return false;
            }

            // 遠端登入：在同一條連線讀這次登入的信箱，比對等連線關掉之後才做（同 Codex）
            if (run.Expectation is not null)
            {
                var (after, _) = await client.RequestAsync(GrokAcpRules.MethodAuthInfo, new JObject(), TimeSpan.FromSeconds(20), ct);
                run.GrokEmail = GrokAcpRules.EvaluateAuthInfo(after).Email;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            await client.RequestAsync(GrokAcpRules.MethodAuthCancel, new JObject(), TimeSpan.FromSeconds(10));
            throw;
        }
        finally
        {
            state.DeviceCode = null;
        }
    }

    /// <summary>剛加回的本機預設帳戶（Antigravity）：先檢查狀態，沒登入才開登入視窗（已登入的不必再開 agy）</summary>
    private async Task RefreshThenLoginAsync(string accountId)
    {
        await RefreshAsync(accountId, includeStatus: true);
        if (!disposed && Find(accountId) != null && GetState(accountId).Status == AccountStatus.NotLoggedIn)
            await LoginAsync(accountId);
    }

    /// <summary>
    /// Antigravity：沒有登入子指令，開主控台視窗跑不帶參數的 agy（未登入時會走瀏覽器 Google 登入）；
    /// 登入後 agy 停在互動介面不會自己結束，所以每隔幾秒以 agy models 確認，登入完成就關掉這個視窗
    /// </summary>
    private async Task<bool?> LoginAntigravityAsync(AgentAccount account, AgentLauncher launcher, LoginRun run, CancellationToken ct)
    {
        LoginProgress?.Invoke(account.Id, "已開啟 agy 視窗，請依畫面在瀏覽器完成 Google 登入；登入完成後視窗會自動關閉…", false, null);
        using var process = CliRunner.StartConsole(launcher, Array.Empty<string>(), env => AccountRules.ApplyEnvironment(account, env), "Antigravity 登入");
        // 遠端登入的里程碑（設計 2026-10-22-grok-antigravity-sync 的 D4，同 Claude）：視窗已開，要有人在這台完成登入
        SignalRemoteStart(account.Id, new RemoteLoginStart(RemoteLoginMilestone.ConsoleOpened));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoginTimeout);
        var commandTimeout = AntigravityCommandTimeout;
        try
        {
            while (true)
            {
                var exited = await Task.WhenAny(process.WaitForExitAsync(timeout.Token), Task.Delay(AntigravityLoginPoll, timeout.Token));
                await exited;   // 取消、逾時在這裡丟出
                // 帶記錄檔：登入成功時順便取信箱（遠端登入的身分比對，D1）
                var (check, email) = await Task.Run(() =>
                    RunAntigravityWithLogAsync(launcher, account, "login", AntigravityStreamRules.ModelsArgumentsWithLog, commandTimeout, allowRetry: false, timeout.Token));
                var status = AntigravityStreamRules.EvaluateModelsCommand(check.ExitCode, check.Stdout, check.Stderr, check.TimedOut).Status;
                if (status == AccountStatus.Ready)
                {
                    run.AntigravityEmail = email;
                    await KillQuietlyAsync(process);
                    return true;
                }
                if (process.HasExited)
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            await KillQuietlyAsync(process);
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>Antigravity 登入時多久確認一次</summary>
    private static readonly TimeSpan AntigravityLoginPoll = TimeSpan.FromSeconds(5);

    /// <summary>KillQuietly 在背景執行緒做（最多等 5 秒，不能卡住 UI 執行緒）</summary>
    private static Task KillQuietlyAsync(Process process) => Task.Run(() => KillQuietly(process));

    /// <summary>結束 app 開的主控台視窗（整個程序樹）並等它真的結束；已結束或結束不了就算了</summary>
    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    /// <summary>
    /// 等登入主控台結束（最多 10 分鐘）。逾時或取消就結束整個程序樹並等它真的結束（釋放登入鎖、放開設定資料夾裡的檔案）；
    /// 取消時往上丟 OperationCanceledException
    /// </summary>
    private static async Task<bool> WaitForLoginConsoleAsync(Process process, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoginTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(exit.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
            }
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    public async Task LogoutAsync(string accountId)
    {
        var account = Find(accountId);
        if (account != null && CliPaused(account.Provider))
        {
            LoginProgress?.Invoke(accountId, $"{CliInstallStabilityRules.DisplayName(account.Provider)} CLI 正在更新，請等更新完成後再登出", true, false);
            return;
        }
        var launcher = account == null ? null : ResolveLauncher(account.Provider);
        if (account == null || launcher == null)
            return;
        switch (account.Provider)
        {
            case AgentProvider.Claude:
            case AgentProvider.Codex:
            case AgentProvider.Grok:
            {
                // 結果照舊不回報（畫面看重新查詢後的狀態）；登出成功就清掉遠端登入身分不符的說明（D15）
                var result = await RunLogoutCliAsync(account, launcher, LocalLogoutTimeout);
                if (result == RemoteLogoutResult.Done && Find(accountId) != null)
                    ClearRemoteLoginNote(GetState(accountId));
                break;
            }
            case AgentProvider.Antigravity:
                await LogoutAntigravityAsync(account, launcher);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(accountId), account.Provider, null);
        }
        SetLastIdentity(account, null);
        usage.Remove(accountId);
        await RefreshAsync(accountId, includeStatus: true);
    }

    /// <summary>本機登出等 CLI 結束的時限</summary>
    private static readonly TimeSpan LocalLogoutTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 遠端登出（WP4.8 D16；UI 執行緒）：在帳戶自己的設定資料夾跑 CLI 的登出，等它結束，最多 <see cref="RemoteLogoutDeadline"/>；
    /// 之後清身分快取與用量、在背景重新查詢（不等）。前置條件（不進同步的種類、CLI 更新中、登入中、沒安裝、本來就未登入）由呼叫端先以
    /// RemoteAccountRules.CheckLogout、IsAlreadyLoggedOut 查；帳戶不見了或找不到 CLI 回 Failed
    /// </summary>
    internal async Task<RemoteLogoutResult> LogoutForRemoteAsync(string accountId)
    {
        var account = Find(accountId);
        var launcher = account == null ? null : ResolveLauncher(account.Provider);
        if (account == null || launcher == null)
            return RemoteLogoutResult.Failed;
        var result = await RunLogoutCliAsync(account, launcher, RemoteLogoutDeadline);
        if (disposed || Find(accountId) == null)
            return result;
        if (result == RemoteLogoutResult.Done)
            ClearRemoteLoginNote(GetState(accountId));
        SetLastIdentity(account, null);
        usage.Remove(accountId);
        _ = RefreshAsync(accountId, includeStatus: true);
        return result;
    }

    /// <summary>
    /// 在帳戶自己的設定資料夾跑 CLI 的登出（ApplyCommandEnvironment(account, …)：隔離帳戶帶 CODEX_HOME／CLAUDE_CONFIG_DIR，系統預設帳戶照原本的環境），
    /// 最多 timeout：結束碼 0＝Done、其他＝Failed、逾時（程序樹已結束）＝TimedOut。Antigravity 沒有登出子指令（LogoutAntigravityAsync）
    /// </summary>
    private async Task<RemoteLogoutResult> RunLogoutCliAsync(AgentAccount account, AgentLauncher launcher, TimeSpan timeout)
    {
        string[] args = account.Provider switch
        {
            AgentProvider.Claude => ["auth", "logout"],
            AgentProvider.Codex => ["logout"],
            AgentProvider.Grok => ["logout"],
            AgentProvider.Antigravity => throw new ArgumentException("Antigravity 沒有登出子指令", nameof(account)),
            _ => throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null),
        };
        var result = await Task.Run(() => CliRunner.RunAsync(launcher, args, env => AccountRules.ApplyCommandEnvironment(account, env), timeout));
        return result.TimedOut ? RemoteLogoutResult.TimedOut
            : result.ExitCode == 0 ? RemoteLogoutResult.Done
            : RemoteLogoutResult.Failed;
    }

    /// <summary>
    /// 遠端登入完成後比對身分（WP4.8 D12、D13、D15；UI 執行緒，Codex 的 app-server 已經關掉）：Codex 以這次連線 account/read 的信箱、
    /// Claude 以 CLI 設定檔的 oauthAccount.emailAddress（只讀檔，不跑 auth status：設計 6.5）和目錄比，只比信箱。
    /// 不符就在同一個資料夾登出（最多 RemoteLogoutDeadline），記下說明（RemoteLoginNote，不含信箱）、登出成功時清掉身分快取與用量，
    /// 觸發 RemoteIdentityMismatch；任一邊不明是 Unknown，不登出（伺服器之後仍依身分鍵標示）
    /// </summary>
    private async Task<RemoteIdentityVerdict> VerifyRemoteIdentityAsync(AgentAccount account, LoginRun run)
    {
        var expected = run.Expectation ?? RemoteLoginExpectation.None;
        RemoteIdentityVerdict verdict;
        switch (account.Provider)
        {
            case AgentProvider.Codex:
                verdict = RemoteAccountRules.CheckCodexIdentity(expected.IdentityKey, run.CodexEmail);
                break;
            case AgentProvider.Claude:
            {
                var config = await Task.Run(() => ReadClaudeGlobalConfig(account));
                if (disposed || Find(account.Id) == null)
                    return RemoteIdentityVerdict.Unknown;
                verdict = RemoteAccountRules.CheckClaudeIdentity(expected, config.OAuthEmail);
                break;
            }
            case AgentProvider.Grok:
                verdict = RemoteAccountRules.CheckEmailIdentity("grok:", expected.IdentityKey, run.GrokEmail);
                break;
            case AgentProvider.Antigravity:
                verdict = RemoteAccountRules.CheckEmailIdentity("antigravity:", expected.IdentityKey, run.AntigravityEmail);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null);
        }
        if (verdict != RemoteIdentityVerdict.Mismatch)
            return verdict;

        var launcher = ResolveLauncher(account.Provider);
        // Antigravity 沒有登出子指令（D4）：不能自動登出，只記說明
        var result = launcher == null || account.Provider == AgentProvider.Antigravity
            ? RemoteLogoutResult.Failed
            : await RunLogoutCliAsync(account, launcher, RemoteLogoutDeadline);
        if (disposed || Find(account.Id) == null)
            return verdict;
        var loggedOut = result == RemoteLogoutResult.Done;
        if (loggedOut)
        {
            SetLastIdentity(account, null);
            usage.Remove(account.Id);
        }
        var noted = GetState(account.Id);
        noted.RemoteLoginNote = RemoteAccountRules.MismatchNote(loggedOut);
        noted.RemoteLoginNoteLoggedOut = loggedOut;
        Log.Warn($"遠端登入「{account.Label}」的帳號和帳戶目錄不同，{(loggedOut ? "已在同一個資料夾自動登出" : $"自動登出沒有成功（{result}）")}");
        RemoteIdentityMismatch?.Invoke(account.Id, loggedOut);
        return verdict;
    }

    /// <summary>
    /// Antigravity 沒有登出子指令（只有互動介面的 /logout）：開 agy 視窗請使用者輸入 /logout，等視窗關閉（最多 10 分鐘）再重新檢查。
    /// 會清掉這台電腦 agy 的登入（認證管理員裡的權杖），在 app 外開的 agy 也一起登出
    /// </summary>
    private async Task LogoutAntigravityAsync(AgentAccount account, AgentLauncher launcher)
    {
        // 連點兩次只開一個視窗（狀態只在 UI 執行緒改，不必加鎖）
        if (!antigravityLogouts.Add(account.Id))
            return;
        try
        {
            Process process;
            try
            {
                process = CliRunner.StartConsole(launcher, Array.Empty<string>(), env => AccountRules.ApplyEnvironment(account, env), "Antigravity 登出");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                LoginProgress?.Invoke(account.Id, $"無法開啟 agy 視窗：{ex.Message}", true, false);
                return;
            }
            using (process)
            {
                LoginProgress?.Invoke(account.Id, "已開啟 agy 視窗：請輸入 /logout 後按 Enter，再關閉視窗", false, null);
                // 帳戶被移除或 app 結束（LifetimeOf 取消）時一併關掉視窗
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LifetimeOf(account.Id));
                timeout.CancelAfter(LoginTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    await KillQuietlyAsync(process);
                }
            }
            LoginProgress?.Invoke(account.Id, "agy 視窗已關閉，重新檢查登入狀態…", true, null);
        }
        finally
        {
            antigravityLogouts.Remove(account.Id);
        }
    }

    /// <summary>開著登出視窗的 Antigravity 帳戶</summary>
    private readonly HashSet<string> antigravityLogouts = new(StringComparer.Ordinal);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        timer.Dispose();
        foreach (var watcher in watchers.Values)
            watcher.Dispose();
        watchers.Clear();
        history.Flush();
    }
}
