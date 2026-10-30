using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎測試的共用環境：暫存資料夾裡已配對的一台裝置（device.json、sync.db）、假伺服器、可撥快的時鐘、測試的 UI 執行緒與宿主。
/// 引擎每次建立都拿同一把私鑰的新實例（停止時引擎會 Dispose 它）。退避步階 1, 2, 4, 8, 16 秒、抖動固定 0.5（延遲剛好是步階）
/// </summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public const string LocalAccount = "acc-1";

    public static readonly string Logical = SyncIds.NewLogicalAccountId();

    /// <param name="conversations">對話的盒子（WP4.3 計畫步 10；null＝不發布對話的引擎，WP4.1 的行為）</param>
    /// <param name="dlpVersion">引擎宣告 conv1 時比對的權杖 DLP 版本（null＝正式的 CredentialLeakRules.Version）</param>
    public EngineHarness(TimeSpan? heartbeat = null, ConversationChangeBox? conversations = null, int? dlpVersion = null)
    {
        Conversations = conversations;
        DlpVersion = dlpVersion;
        Time = new ManualTimeProvider();
        Dir = new TempDirectory("sync-engine");
        Paths = new SyncPaths(Dir.Combine("sync"));
        Directory.CreateDirectory(Paths.Directory);
        Signer = new InMemoryDeviceSigner();
        var deviceId = SyncIds.NewDeviceId();
        Server = new FakeSyncServer(Time, deviceId, Signer.PublicSpki);
        Token = PairingTestData.NewToken();
        Device = PairingTestData.Device(deviceId, devicePubKey: Signer.PublicSpki, hubSpki: Server.HubSpki, token: Token);
        DeviceFile.Save(Paths.DeviceFile, Device);
        using (SyncStore.Create(Paths.DatabaseFile, deviceId, Device.PairedAt, Server.Instance, new TrustBundle(Server.RegistryRev, Server.Registry)))
        {
        }

        Ui = new UiThread();
        Host = new FakeHostPort(Ui, Time);
        Applier = new RecordingApplier(Ui);
        Options = new SyncEngineOptions([1, 2, 4, 8, 16], () => 0.5, heartbeat ?? TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));
        SignerFactory = _ => Signer.Clone();
        Engine = CreateEngine();
    }

    public ManualTimeProvider Time { get; }

    public TempDirectory Dir { get; }

    public SyncPaths Paths { get; }

    public InMemoryDeviceSigner Signer { get; }

    public FakeSyncServer Server { get; }

    public string Token { get; }

    /// <summary>交給引擎的 device.json 內容（測試可以先改再 <see cref="StartAsync"/>）</summary>
    public DeviceFileData Device { get; set; }

    public UiThread Ui { get; }

    public FakeHostPort Host { get; }

    /// <summary>交給引擎的宿主（null＝<see cref="Host"/>；遠端指令的測試換成同時實作 IRemoteCommandPort 的 FakeCommandPort，WP4.4 D35）</summary>
    public ISyncHostPort? HostPort { get; set; }

    public RecordingApplier Applier { get; }

    public SyncEngineOptions Options { get; set; }

    public Func<DeviceFileData, IDeviceSigner?> SignerFactory { get; set; }

    /// <summary>引擎開 sync.db 的函式（預設 SyncStore.Open；測試可以換成丟例外的）</summary>
    public Func<string, string, SyncStore> OpenStore { get; set; } = SyncStore.Open;

    public SyncEngine Engine { get; private set; }

    public string DeviceId => Device.DeviceId;

    /// <summary>之後建立的引擎帶的對話盒子（null＝不發布對話）</summary>
    public ConversationChangeBox? Conversations { get; set; }

    /// <summary>之後建立的引擎宣告 conv1 時比對的權杖 DLP 版本（null＝正式的值）</summary>
    public int? DlpVersion { get; set; }

    /// <summary>之後建立的引擎的 app 版本（程式錯誤的封鎖到換版為止）</summary>
    public string AppVersion { get; set; } = "0.5.0";

    /// <summary>之後建立的引擎的遠端政策（Hello 的 policy；WP4.7 的媒體轉送依它決定原檔給不給）</summary>
    public DevicePolicy Policy { get; set; } = new("none", false);

    public SyncEngine CreateEngine()
    {
        var engine = new SyncEngine(new SyncEngineArgs(
            Paths,
            Device,
            SignerFactory,
            OpenStore,
            Server,
            HostPort ?? Host,
            Applier,
            Time,
            Options,
            Policy,
            null,
            false,
            AppVersion,
            Guid.NewGuid().ToString("N"),
            "OFFICE-PC",
            "user",
            Conversations));
        if (DlpVersion is { } version)
            engine.DlpVersion = version;
        engine.TrimDlpCache = TrimDlpCache;
        return engine;
    }

    /// <summary>
    /// 之後建立的引擎閒置時釋放 DLP 樣式（WP4.9 步 5）：預設關掉——樣式是全程序共用的，量記憶體的測試與平行的測試不受影響；
    /// 測釋放的測試（<c>SyncEngineDlpTrimTests</c>，循序執行）自己打開
    /// </summary>
    public bool TrimDlpCache { get; set; }

    /// <summary>以目前的設定重建引擎（還沒啟動的那個直接丟掉）、啟動、等它閒下來</summary>
    public async Task StartAsync(Action<SyncEngine>? beforeStart = null)
    {
        Engine = CreateEngine();
        beforeStart?.Invoke(Engine);
        Engine.Start();
        await IdleAsync();
    }

    /// <summary>建立並啟動，不等它閒下來（UI 工作卡住時用）</summary>
    public void StartNoWait()
    {
        Engine = CreateEngine();
        Engine.Start();
    }

    /// <summary>正常停止目前的引擎，再以同一份資料啟動新的</summary>
    public async Task RestartAsync()
    {
        await Engine.StopAsync(TimeSpan.FromSeconds(5));
        await StartAsync();
    }

    public async Task IdleAsync()
    {
        await Engine.WhenIdleAsync();
        await Ui.DrainAsync(1);
        await Engine.WhenIdleAsync();
    }

    /// <summary>撥快時鐘；每觸發一個計時器就等引擎閒下來</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        await Time.AdvanceAsync(by, () => Engine.WhenIdleAsync());
        await IdleAsync();
    }

    public Task AdvanceAsync(double seconds) => AdvanceAsync(TimeSpan.FromSeconds(seconds));

    public Task<T> Inspect<T>(Func<SyncEngine.EngineView, T> read) => Engine.InvokeAsync(read);

    public Task<SyncEngineState> StateAsync() => Inspect(v => v.State);

    /// <summary>直接寫進 outbox 的事件（cli.state 的 JSON，不合併）</summary>
    public void Append(int count = 1, string? coalesceKey = null)
    {
        for (var i = 0; i < count; i++)
            Engine.Post(new SyncSignal.Append(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, $$"""{"providers":{},"n":{{Guid.NewGuid().GetHashCode()}}}""", coalesceKey)));
    }

    /// <summary>這台連結到 <see cref="Logical"/> 的 account.upsert（連結 ready、身分相符）</summary>
    public AccountUpsertPayload LinkedAccount(string label = "工作", int sortOrder = 0, long version = 1, string? deviceId = null) =>
        new(Logical, "Claude", "claude:org:me@example.com", label, "#D97757", sortOrder, "me@example.com", "org", "Claude Max", version,
        [
            new AccountLinkView(deviceId ?? DeviceId, LocalAccount, CatalogSyncRules.ConfigKindIsolated, "ready", null, "me@example.com", "org", "Claude Max",
                null, null, false, true, null, null, false),
        ]);

    /// <summary>另開一條連線讀 sync.db（WAL：引擎的長連線開著也讀得到已提交的內容）</summary>
    public IReadOnlyDictionary<string, long> RawCursors()
    {
        using var connection = new SqliteConnection(SyncStore.ConnectionString(Paths.DatabaseFile, SyncStoreOpenMode.Open));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT cls, seq FROM cursors";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    public DeviceFileData SavedDevice() => DeviceFile.Load(Paths.DeviceFile).Data!;

    /// <summary>device.json 設成唯讀（之後的存檔失敗）；回傳還原用的物件</summary>
    public IDisposable ReadOnlyDeviceFile()
    {
        File.SetAttributes(Paths.DeviceFile, File.GetAttributes(Paths.DeviceFile) | FileAttributes.ReadOnly);
        return new Restore(() => File.SetAttributes(Paths.DeviceFile, File.GetAttributes(Paths.DeviceFile) & ~FileAttributes.ReadOnly));
    }

    public static SyncTransportException Transient() => new(SyncFailureRules.Classify(null, null, false));

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAsync(TimeSpan.FromSeconds(5));
        if (File.Exists(Paths.DeviceFile))
            File.SetAttributes(Paths.DeviceFile, FileAttributes.Normal);
        Ui.Errors.Should().BeEmpty("UI 執行緒上不該有沒接住的例外");
        Ui.Dispose();
        Server.Dispose();
        Signer.Dispose();
        Dir.Dispose();
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
