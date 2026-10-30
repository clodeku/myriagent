using System.Diagnostics;
using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Usage;

namespace Myriagent.Tests.Sync.Engine;

public sealed class SyncEngineTests
{
    private const long At = 1791600000000;

    private static DeviceUpsertPayload DeviceView(string deviceId, string name) =>
        new(deviceId, name, "PC", "0.5.0", PairingRules.KeyStorageSoftware, "ABCD-EFGH", At, At, true, new DevicePolicy("none", false), null);

    private static SharedUsage Shared(double percent) =>
        new(At, UsageSyncRules.SourceApi, [new SharedUsageWindow("session", "5 小時", percent, null, false)], null, null, null, false);

    private static UsageSnapshot ApiSnapshot(DateTimeOffset at) =>
        new(at, UsageSyncRules.SourceApi, [new UsageWindow("session", "5 小時", 42, at.AddHours(2))], null, null, null, false);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("條件沒有在時限內成立");
            await Task.Delay(5);
        }
    }

    // ── 交握與身分 ──

    [Fact]
    public async Task 交握_Ok_Prove後Live_送完outbox()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(engine => h.Append(3));

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var calls = h.Server.Calls.ToList();
        calls.Take(2).Should().Equal("1:Hello", "1:Prove");
        calls.Should().Contain("1:PublishBatch").And.Contain("1:Pull");
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3);
        (await h.Inspect(v => v.Outbox)).Should().OnlyContain(e => e.State == OutboxState.Acked && e.ServerSeq != null);
        h.Host.LastStatus!.Pending.Should().Be(0);
        h.Host.LastStatus.LastSyncAt.Should().Be(h.Time.NowMs);
        var hello = h.Server.Hellos.Single();
        hello.DeviceId.Should().Be(h.DeviceId);
        hello.ObservedSpkiSha256.Should().Be(h.Server.ObservedSpki);
        hello.Cursors.Should().ContainKeys(SyncClasses.Catalog, SyncClasses.Usage);
        hello.SeenServer.Should().Be(h.Server.Instance, "第一次的 seenServer 是配對回應的 server");
        hello.Outbox.Should().Be(new OutboxPosition(4, 1, 1));
    }

    [Fact]
    public async Task Rotated_先存新pin才呼叫Prove()
    {
        await using var h = new EngineHarness();
        h.Server.RotateIdentity();
        string? pinnedWhenProving = null;
        h.Server.BeforeProveReturns = _ => pinnedWhenProving = h.SavedDevice().HubIdentitySpki;

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        pinnedWhenProving.Should().Be(h.Server.HubSpki, "改釘存檔之後才送出 Prove");
        h.SavedDevice().HubIdentitySpki.Should().Be(h.Server.HubSpki);
        h.Host.LastStatus!.HubFingerprint.Should().Be(P256KeyRules.AnchorFingerprint(h.Server.HubSpki));
    }

    [Fact]
    public async Task Rotated_存檔失敗_不Prove並退避()
    {
        await using var h = new EngineHarness();
        var oldPin = h.Device.HubIdentitySpki;
        h.Server.RotateIdentity();
        using (h.ReadOnlyDeviceFile())
        {
            await h.StartAsync();

            (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
            h.Server.Calls.Should().Contain("1:Hello").And.NotContain("1:Prove");
            h.Engine.BackoffHistory.Should().Equal(TimeSpan.FromSeconds(1));
            h.SavedDevice().HubIdentitySpki.Should().Be(oldPin);
        }

        // 存得了之後下一次重連就改釘並上線
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.SavedDevice().HubIdentitySpki.Should().Be(h.Server.HubSpki);
    }

    [Fact]
    public async Task NeedsLocalConfirmation_停住_指紋不符忽略_相符改釘並Live()
    {
        await using var h = new EngineHarness();
        var oldPin = h.Device.HubIdentitySpki;
        h.Server.LoseIdentity();

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.NeedsConfirmation);
        var fingerprint = P256KeyRules.AnchorFingerprint(h.Server.HubSpki);
        h.Host.LastStatus!.ConfirmFingerprint.Should().Be(fingerprint);
        h.Server.Calls.Should().NotContain(c => c.EndsWith(":Prove", StringComparison.Ordinal));
        h.Time.ActiveTimers.Should().Be(0, "待確認時不重連、沒有心跳");

        h.Engine.RetryNow();
        h.Engine.ConfirmHubIdentity(P256KeyRules.AnchorFingerprint(PairingTestData.NewSpki()));
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.NeedsConfirmation);
        h.Server.ConnectAttempts.Should().Be(1);
        h.SavedDevice().HubIdentitySpki.Should().Be(oldPin);

        // 大小寫、連字號不影響比對
        h.Engine.ConfirmHubIdentity(fingerprint.Replace("-", string.Empty).ToLowerInvariant());
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.SavedDevice().HubIdentitySpki.Should().Be(h.Server.HubSpki);
        h.Server.ConnectAttempts.Should().Be(2);
        h.Host.LastStatus!.ConfirmFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task hubSig錯_Invalid_退避()
    {
        await using var h = new EngineHarness();
        h.Server.ForgeHubSignature = true;

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Server.Calls.Should().NotContain("1:Prove");
        h.Host.LastStatus!.Detail.Should().Contain("伺服器身分驗證失敗");
        h.SavedDevice().HubIdentitySpki.Should().Be(h.Device.HubIdentitySpki);
        (await h.Inspect(v => v.SeenServer)).Should().Be(h.Server.Instance);
    }

    [Fact]
    public async Task 時差超過2分鐘_填ClockSkewMs_仍Live()
    {
        await using var h = new EngineHarness();
        h.Server.ClockOffsetMs = 3 * 60 * 1000;

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Host.LastStatus!.ClockSkewMs.Should().Be(180000);
    }

    [Fact]
    public async Task tokenRotation_存新權杖_存檔失敗沿用舊的()
    {
        await using var h = new EngineHarness();
        var rotated = PairingTestData.NewToken();
        h.Server.NextTokenRotation = rotated;

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        DeviceSecrets.Unprotect(h.SavedDevice().ProtectedDeviceToken).Should().Be(rotated);
        (await h.Inspect(v => v.DeviceToken)).Should().Be(rotated);

        using (h.ReadOnlyDeviceFile())
        {
            h.Server.NextTokenRotation = PairingTestData.NewToken();
            h.Server.Current!.Drop();
            await h.IdleAsync();
            await h.AdvanceAsync(1);

            (await h.StateAsync()).Should().Be(SyncEngineState.Live);
            h.Server.ConnectRequests.Last().DeviceToken.Should().Be(rotated, "重連用的是換存過的權杖");
            (await h.Inspect(v => v.DeviceToken)).Should().Be(rotated, "存檔失敗就沿用舊的");
            DeviceSecrets.Unprotect(h.SavedDevice().ProtectedDeviceToken).Should().Be(rotated);
        }
    }

    [Fact]
    public async Task Prove前收到UsageAssign_Notify_TrustChanged_不呼叫hub方法_Prove後處理()
    {
        await using var h = new EngineHarness();
        var logical = SyncIds.NewLogicalAccountId();
        h.Server.BeforeProveReturns = s =>
        {
            s.Assign(logical, 5, true);
            s.Notify(SyncClasses.Catalog);
            h.Server.RegistryRev = 2;
            s.TrustChanged(2);
        };

        await h.StartAsync();

        var calls = h.Server.Calls.ToList();
        var prove = calls.IndexOf("1:Prove");
        h.Server.Sessions.Single().CallsAtProveReturn.Should().Be(prove + 1, "ProveResponse 回來之前沒有呼叫任何 hub 方法");
        calls[prove + 1].Should().Be("1:GetTrust", "名冊版本換了：先拉名冊");
        calls.Skip(prove + 2).Should().Contain("1:Pull");
        (await h.Inspect(v => v.Assignments)).Should().ContainKey(logical).WhoseValue.Should().Be(new UsageAssignment(5, true));
        (await h.Inspect(v => v.RegistryRev)).Should().Be(2);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    // ── 啟動失敗 ──

    [Theory]
    [InlineData("Missing")]
    [InlineData("Corrupt")]
    [InlineData("DeviceMismatch")]
    [InlineData("TooNew")]
    public async Task store開不了_依原因Stopped_不丟到UI(string failure)
    {
        await using var h = new EngineHarness();
        var db = h.Paths.DatabaseFile;
        SyncStopReason expected;
        switch (failure)
        {
            case "Missing":
                File.Delete(db);
                expected = SyncStopReason.StoreMissing;
                break;
            case "Corrupt":
                File.Delete(db);
                File.WriteAllBytes(db, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
                expected = SyncStopReason.StoreCorrupt;
                break;
            case "DeviceMismatch":
                File.Delete(db);
                using (SyncStore.Create(db, SyncIds.NewDeviceId(), At, h.Server.Instance, new TrustBundle(1, [])))
                {
                }

                expected = SyncStopReason.StoreMismatch;
                break;
            default:
                using (var store = SyncStore.Open(db, h.DeviceId))
                    store.SetKv(SyncStoreSchema.SchemaMinReader, "99");
                expected = SyncStopReason.StoreTooNew;
                break;
        }

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(expected);
        h.Server.ConnectAttempts.Should().Be(0);
        h.Applier.Catalogs.Should().BeEmpty();
        h.Ui.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    public async Task store暫時打不開_被鎖_唯讀_磁碟滿_停下但不算損毀_不提示解除配對(int code)
    {
        await using var h = new EngineHarness();
        h.OpenStore = (_, _) => throw new Microsoft.Data.Sqlite.SqliteException("SQLite Error " + code, code);

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        var status = h.Host.LastStatus!;
        status.StopReason.Should().Be(SyncStopReason.None, "解除配對會刪掉 sync.db 與還沒被確認的 outbox");
        status.Detail.Should().Contain("暫時").And.Contain("SQLite " + code).And.NotContain("解除配對");
        h.Server.ConnectAttempts.Should().Be(0);
        h.Ui.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task store打不開_不是暫時性的SQLite錯誤_照樣算損毀()
    {
        await using var h = new EngineHarness();
        h.OpenStore = (_, _) => throw new Microsoft.Data.Sqlite.SqliteException("SQLite Error 1", 1);

        await h.StartAsync();

        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.StoreCorrupt);
    }

    [Fact]
    public async Task 權杖解不開_Stopped_TokenUnreadable()
    {
        await using var h = new EngineHarness();
        h.Device = h.Device with { ProtectedDeviceToken = "AAAA" };

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.TokenUnreadable);
        h.Server.ConnectAttempts.Should().Be(0);
    }

    [Fact]
    public async Task 金鑰不見_Stopped_KeyMissing()
    {
        await using var h = new EngineHarness();
        h.SignerFactory = _ => null;

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.KeyMissing);
        h.Server.ConnectAttempts.Should().Be(0);
    }

    // ── 裝置金鑰的暫時性錯誤（DeviceKeyErrorRules） ──

    [Fact]
    public async Task 開金鑰時CNG回不存在_Stopped_KeyMissing()
    {
        await using var h = new EngineHarness();
        h.SignerFactory = _ => throw new CryptographicException(DeviceKeyErrorRules.NteBadKeyset);

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.KeyMissing);
        h.Host.LastStatus.Detail.Should().Be("找不到這台的裝置金鑰，請解除配對後重新配對");
        h.Server.ConnectAttempts.Should().Be(0);
    }

    [Fact]
    public async Task 開金鑰暫時性錯誤_退避重試_恢復後Live並送出期間的事件()
    {
        await using var h = new EngineHarness();
        var opens = 0;
        h.SignerFactory = _ => ++opens <= 2 ? throw new CryptographicException(DeviceKeyErrorRules.TpmRetry) : h.Signer.Clone();

        await h.StartAsync(engine => h.Append(2));

        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Server.ConnectAttempts.Should().Be(0, "金鑰打不開就不連伺服器");
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.None);
        h.Host.LastStatus.Detail.Should().Be("裝置金鑰暫時無法使用（0x80280800），1 秒後重試", "不叫使用者重新配對");
        h.Host.LastStatus.Pending.Should().Be(2, "期間的事件照常寫進 outbox");
        h.Engine.BackoffHistory.Should().Equal(TimeSpan.FromSeconds(1));

        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        opens.Should().Be(2);
        h.Server.ConnectAttempts.Should().Be(0);
        h.Engine.BackoffHistory.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        (await h.Inspect(v => v.KeyStreak)).Should().Be(new DeviceKeyStreak(2, 0, null));

        await h.AdvanceAsync(2);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        opens.Should().Be(3);
        h.Server.ConnectAttempts.Should().Be(1);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2);
        h.Host.LastStatus!.Detail.Should().BeNull();
        (await h.Inspect(v => v.KeyStreak)).Should().Be(DeviceKeyStreak.None, "簽章成功就歸零");
    }

    [Fact]
    public async Task 不認得的金鑰錯誤_次數與時間都到門檻才停在KeyMissing()
    {
        await using var h = new EngineHarness();
        var opens = 0;
        h.SignerFactory = _ =>
        {
            opens++;
            throw new CryptographicException(unchecked((int)0x80284006));   // TBS_E_IOERROR
        };

        await h.StartAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Host.LastStatus!.Detail.Should().StartWith("裝置金鑰暫時無法使用（0x80284006）");

        // 嘗試的時間：0, 1, 3, 7, 15, 31, 47 … 秒（退避 1, 2, 4, 8, 16, 16 …），第 41 次在 591 秒：還不到 10 分鐘
        await h.AdvanceAsync(606);
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        opens.Should().Be(41);
        (await h.Inspect(v => v.KeyStreak)).Should().Be(new DeviceKeyStreak(41, 41, h.Time.GetUtcNow().AddSeconds(-606)));

        // 第 42 次在 607 秒：滿 10 分鐘、而且超過 10 次
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        opens.Should().Be(42);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.KeyMissing);
        h.Host.LastStatus.Detail.Should().Be("裝置金鑰一直無法使用（0x80284006），請解除配對後重新配對");
        h.Server.ConnectAttempts.Should().Be(0);

        await h.AdvanceAsync(60);
        opens.Should().Be(42, "停了就不再重試");
    }

    [Fact]
    public async Task 簽章時暫時性錯誤_關掉金鑰並退避_重開後Live()
    {
        await using var h = new EngineHarness();
        var opens = 0;
        FailingDeviceSigner? flaky = null;
        h.SignerFactory = _ =>
        {
            if (++opens > 1)
                return h.Signer.Clone();
            flaky = new FailingDeviceSigner(h.Signer.Clone(), new CryptographicException(DeviceKeyErrorRules.Tpm20Retry));
            return flaky;
        };

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Server.Calls.Should().Contain("1:Hello").And.NotContain("1:Prove");
        flaky!.SignAttempts.Should().Be(1);
        flaky.IsDisposed.Should().BeTrue("暫時性失敗就關掉這個金鑰，下次重開");
        (await h.Inspect(v => v.HasSigner)).Should().BeFalse();
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.None);
        h.Host.LastStatus.Detail.Should().StartWith("裝置金鑰暫時無法使用（0x80280922）");

        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        opens.Should().Be(2);
        h.Server.Calls.Should().Contain("2:Prove");
        (await h.Inspect(v => v.KeyStreak)).Should().Be(DeviceKeyStreak.None);
    }

    [Fact]
    public async Task 簽章時金鑰不存在_Stopped_KeyMissing()
    {
        await using var h = new EngineHarness();
        var signer = new FailingDeviceSigner(h.Signer.Clone(), new CryptographicException(DeviceKeyErrorRules.NteBadKeyset));
        h.SignerFactory = _ => signer;

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.KeyMissing);
        h.Server.Calls.Should().NotContain("1:Prove");
        h.Server.ConnectAttempts.Should().Be(1);
        signer.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task 金鑰暫時打不開_立即重試與暫停後恢復都重開金鑰()
    {
        await using var h = new EngineHarness();
        var opens = 0;
        var fail = true;
        h.SignerFactory = _ =>
        {
            opens++;
            return fail ? throw new CryptographicException(DeviceKeyErrorRules.TbsServiceStartPending) : h.Signer.Clone();
        };

        await h.StartAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);

        h.Engine.RetryNow();
        await h.IdleAsync();
        opens.Should().Be(2, "立即重試也重開金鑰");
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Engine.BackoffHistory.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        h.Engine.SetPaused(true);
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Paused);
        await h.AdvanceAsync(30);
        opens.Should().Be(2, "暫停時不重試");

        fail = false;
        h.Engine.SetPaused(false);
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        opens.Should().Be(3);
        h.Server.ConnectAttempts.Should().Be(1);
    }

    // ── outbox 與送出 ──

    [Fact]
    public async Task ApplyProve_StopSeqRegression_Stopped且原因正確()
    {
        await using var h = new EngineHarness();
        h.Server.NextDeviceSeq = 50;

        await h.StartAsync(engine => h.Append(2));

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.SeqRegression);
        h.Host.LastStatus.Detail.Should().Contain("倒退");
        h.Server.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task 伺服器換實例_游標歸零_清快取_published清空_resync全部重送()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        await h.StartAsync();
        h.Append(5);
        await h.AdvanceAsync(1);
        h.Server.Accepted.Should().HaveCount(5);
        (await h.Inspect(v => v.Cursors)).Should().ContainKey(SyncClasses.Catalog).WhoseValue.Should().Be(1);
        (await h.Inspect(v => v.Catalog!.Accounts.Count)).Should().Be(1);
        await h.Inspect(v =>
        {
            v.Store!.SetPublished(EngineHarness.LocalAccount, new PublishedLink("digest", null));
            return 0;
        });
        var old = h.Server.Instance;

        // 伺服器從備份還原：這台的 3、4、5 不見了、拉取紀錄回到 0
        h.Server.Restore(nextDeviceSeqAtBackup: 3, highAtBackup: 0);
        var batchesBefore = h.Server.Batches.Count;
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        h.Server.Hellos.Last().SeenServer.Should().Be(old, "對帳完成前 seen_server 不變");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        (await h.Inspect(v => v.SeenServer)).Should().Be(h.Server.Instance);
        (await h.Inspect(v => v.Reconciling)).Should().BeFalse();
        (await h.Inspect(v => v.Cursors)).Where(c => c.Value != 0).Should().BeEmpty("游標歸 0（還原後的拉取紀錄是空的）");
        (await h.Inspect(v => v.Catalog!.Accounts.Count)).Should().Be(0);
        (await h.Inspect(v => v.Store!.LoadPublished().Count)).Should().Be(0);
        h.Server.Batches.Skip(batchesBefore).SelectMany(b => b.Events).Select(e => e.DeviceSeq).Should().Equal(1, 2, 3, 4, 5);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3, 4, 5);
        h.Server.DoubleApplied.Should().Be(0);
    }

    [Fact]
    public async Task 對帳中斷電重開_seenServer未換_再次重設()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Append(3);
        await h.AdvanceAsync(1);
        var old = h.Server.Instance;
        h.Server.Restore(nextDeviceSeqAtBackup: 1, highAtBackup: 0);
        h.Server.HoldAcks = true;
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        (await h.Inspect(v => v.Reconciling)).Should().BeTrue();

        // 對帳中途關掉（BatchAck 沒收到）再開：Hello 仍帶舊的 seenServer，同樣重來一次
        await h.Engine.StopAsync(TimeSpan.FromSeconds(5));
        h.Server.Current!.DiscardAcks();
        h.Server.HoldAcks = false;
        await h.StartAsync();

        h.Server.Hellos.Last().SeenServer.Should().Be(old);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        (await h.Inspect(v => v.Reconciling)).Should().BeFalse();
        (await h.Inspect(v => v.SeenServer)).Should().Be(h.Server.Instance);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3);
        h.Server.DoubleApplied.Should().Be(0);
    }

    [Fact]
    public async Task 對帳完成才寫seenServer()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Append(2);
        await h.AdvanceAsync(1);
        var old = h.Server.Instance;
        h.Server.Restore(nextDeviceSeqAtBackup: 2, highAtBackup: 0);
        h.Server.HoldAcks = true;
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        (await h.Inspect(v => v.Reconciling)).Should().BeTrue();
        (await h.Inspect(v => v.SeenServer)).Should().Be(old);
        (await h.Inspect(v => v.Store!.GetKv(SyncStoreSchema.SeenRestoreGeneration))).Should().Be("0");

        h.Server.Current!.ReleaseAcks();
        await h.IdleAsync();

        (await h.Inspect(v => v.Reconciling)).Should().BeFalse();
        (await h.Inspect(v => v.SeenServer)).Should().Be(h.Server.Instance);
        (await h.Inspect(v => v.Store!.GetKv(SyncStoreSchema.SeenRestoreGeneration))).Should().Be("1");
    }

    [Fact]
    public async Task 對帳期間不修剪()
    {
        await using var h = new EngineHarness(heartbeat: TimeSpan.FromHours(1));
        await h.StartAsync();
        h.Append(3);
        await h.AdvanceAsync(1);
        await h.AdvanceAsync(TimeSpan.FromHours(25));
        // 兩批各一列、確認都扣著
        h.Server.HoldAcks = true;
        h.Append(1);
        await h.AdvanceAsync(0.2);
        h.Append(1);
        await h.AdvanceAsync(0.2);
        h.Server.Current!.HeldAcks.Should().Be(2);
        // 對帳開始（D15 第 1 步，這時 5 列都要完成才算對帳完成）
        await h.Inspect(v =>
        {
            v.Store!.BeginServerChange(h.Server.Instance with { RestoreGeneration = 9 });
            return 0;
        });

        h.Server.Current.ReleaseOneAck();
        await h.IdleAsync();
        (await h.Inspect(v => v.Reconciling)).Should().BeTrue();
        (await h.Inspect(v => v.Outbox)).Select(e => e.DeviceSeq).Should().Equal([1, 2, 3, 4, 5], "前 3 列已提交滿 24 小時，但對帳期間不修剪");

        h.Server.Current.ReleaseOneAck();
        await h.IdleAsync();
        (await h.Inspect(v => v.Reconciling)).Should().BeFalse();
        (await h.Inspect(v => v.Outbox)).Select(e => e.DeviceSeq).Should().Equal(4, 5);
    }

    [Fact]
    public async Task 舊世代的gap_只減在途_不Rewind()
    {
        await using var h = new EngineHarness();
        h.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 2 };
        await h.StartAsync();
        var gaps = 0;
        h.Server.BatchOverride = b => gaps++ < 4 ? new BatchAck(b.BatchId, 0, [], new BatchError(SyncErrorCodes.Gap, 1)) : null;
        h.Server.HoldAcks = true;
        h.Append(8);
        await h.AdvanceAsync(1);
        h.Server.Batches.Should().HaveCount(4);
        (await h.Inspect(v => v.InFlight)).Should().Be(4);
        var generation = await h.Inspect(v => v.SendGeneration);

        h.Server.HoldAcks = false;
        h.Server.Current!.ReleaseAcks();
        await h.IdleAsync();

        (await h.Inspect(v => v.SendGeneration)).Should().Be(generation + 1, "只有目前世代的第一個 gap 會 Rewind");
        (await h.Inspect(v => v.InFlight)).Should().Be(0);
        (await h.Inspect(v => v.Outbox)).Should().OnlyContain(e => e.State == OutboxState.Retry);

        await h.AdvanceAsync(2);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        h.Server.Batches.Should().HaveCount(8);
        h.Server.DoubleApplied.Should().Be(0);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task BatchAck逾時60秒_重連()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Server.HoldAcks = true;
        h.Append(1);
        await h.AdvanceAsync(0.2);
        await h.AdvanceAsync(59);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Host.LastStatus!.Detail.Should().Contain("太久沒有確認");
        h.Server.HoldAcks = false;
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.Sessions.Should().HaveCount(2);
        (await h.Inspect(v => v.Outbox)).Should().ContainSingle().Which.State.Should().Be(OutboxState.Acked);
        h.Server.Accepted.Should().ContainSingle();
    }

    [Fact]
    public async Task 在途最多4批_批次大小照Hello的limits()
    {
        await using var h = new EngineHarness();
        h.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 10, MaxInFlightBatches = 4 };
        await h.StartAsync();
        (await h.Inspect(v => v.Limits.MaxBatchEvents)).Should().Be(10);
        h.Server.HoldAcks = true;
        h.Append(100);

        await h.AdvanceAsync(1);

        h.Server.Batches.Should().HaveCount(4).And.OnlyContain(b => b.Events.Count == 10);
        (await h.Inspect(v => v.InFlight)).Should().Be(4);
        h.Server.HoldAcks = false;
        h.Server.Current!.ReleaseAcks();
        await h.AdvanceAsync(2);
        h.Server.Accepted.Should().HaveCount(100);
        h.Server.Batches.Should().OnlyContain(b => b.Events.Count <= 10);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 引擎重開_未確認的列重送_伺服器不重複()
    {
        await using var h = new EngineHarness();
        h.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 3 };
        await h.StartAsync();
        var batches = 0;
        h.Server.HoldAcks = true;
        // 第一批伺服器提交了、確認沒送到；第二批伺服器根本沒收到
        h.Server.BatchOverride = b => ++batches == 2 ? new BatchAck(b.BatchId, 0, [], new BatchError("lost")) : null;
        h.Append(5);
        await h.AdvanceAsync(1);
        h.Server.Batches.Should().HaveCount(2);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3);

        await h.Engine.StopAsync(TimeSpan.FromSeconds(5));
        h.Server.Current!.DiscardAcks();
        h.Server.HoldAcks = false;
        h.Server.BatchOverride = null;
        await h.StartAsync();
        await h.AdvanceAsync(1);

        h.Server.Batches.Skip(2).Should().ContainSingle().Which.Events.Select(e => e.DeviceSeq).Should().Equal(4, 5);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3, 4, 5);
        h.Server.DoubleApplied.Should().Be(0);
        (await h.Inspect(v => v.Outbox)).Should().OnlyContain(e => e.State == OutboxState.Acked);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 整批invalid_Stopped_InvalidBatch()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Server.BatchOverride = b => new BatchAck(b.BatchId, 0, [], new BatchError(SyncErrorCodes.Invalid));
        h.Append(1);

        await h.AdvanceAsync(0.2);

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.InvalidBatch);
    }

    // ── 連線世代 ──

    [Fact]
    public async Task 舊連線OnClosed晚到_不影響新連線()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        var first = h.Server.Current!;
        first.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        first.Events.OnClosed(new InvalidOperationException("舊連線的 Closed 晚到"));
        await h.IdleAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.Sessions.Should().HaveCount(2);
        h.Engine.BackoffHistory.Should().ContainSingle();
    }

    [Fact]
    public async Task 舊連線BatchAck晚到_忽略()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Server.HoldAcks = true;
        var once = 0;
        h.Server.BatchOverride = b => once++ == 0 ? new BatchAck(b.BatchId, 0, [], new BatchError(SyncErrorCodes.Gap, 1)) : null;
        h.Append(2);
        await h.AdvanceAsync(0.2);
        var first = h.Server.Current!;
        first.HeldAcks.Should().Be(1);
        first.Drop();
        h.Server.HoldAcks = false;
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var batches = h.Server.Batches.Count;
        var generation = await h.Inspect(v => v.SendGeneration);

        first.ReleaseAcks();
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        (await h.Inspect(v => v.SendGeneration)).Should().Be(generation, "舊連線的 gap 不 Rewind");
        h.Server.Batches.Should().HaveCount(batches);
        (await h.Inspect(v => v.Outbox)).Should().OnlyContain(e => e.State == OutboxState.Acked);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2);
    }

    // ── 拉取 ──

    [Fact]
    public async Task 拉取_游標每個cls各自前進_套用後才寫入()
    {
        await using var h = new EngineHarness();
        h.Server.Limits = SyncLimits.Default with { MaxPullEvents = 2 };
        h.Server.Publish(SyncEventKinds.DeviceUpsert, DeviceView(SyncIds.NewDeviceId(), "HOME-PC"));
        h.Server.Publish(SyncEventKinds.DeviceUpsert, DeviceView(SyncIds.NewDeviceId(), "LAPTOP"));
        h.Server.Publish(SyncEventKinds.PrefsSet, new PrefsSetPayload("theme", "dark", 1));
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(SyncIds.NewLogicalAccountId(), Shared(10), SyncIds.NewDeviceId(), 1));
        var seenDuringApply = new List<IReadOnlyDictionary<string, long>>();
        h.Applier.OnCatalog = apply =>
        {
            if (apply.View.Devices.Count > 0)
                seenDuringApply.Add(h.RawCursors());
        };

        await h.StartAsync();

        seenDuringApply.Should().NotBeEmpty();
        seenDuringApply[0].Should().BeEmpty("套用完才提交游標");
        var pulls = h.Server.Pulls.ToList();
        pulls[0].Cursors.Should().Equal(new PullCursor(0, SyncClasses.Catalog), new PullCursor(0, SyncClasses.Usage));
        pulls[1].Cursors.Should().Equal(new PullCursor(2, SyncClasses.Catalog), new PullCursor(0, SyncClasses.Usage));
        var expected = new Dictionary<string, long> { [SyncClasses.Catalog] = 3, [SyncClasses.Usage] = 4 };
        (await h.Inspect(v => v.Cursors)).Should().BeEquivalentTo(expected);
        h.RawCursors().Should().BeEquivalentTo(expected);
        h.Applier.Catalogs.Last().Prefs.Should().ContainSingle().Which.Key.Should().Be("theme");

        h.Server.Publish(SyncEventKinds.DeviceUpsert, DeviceView(SyncIds.NewDeviceId(), "TABLET"));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();

        h.Server.Pulls.Last().Cursors.Should().Equal(new PullCursor(3, SyncClasses.Catalog), new PullCursor(4, SyncClasses.Usage));
        (await h.Inspect(v => v.Cursors)).Should().BeEquivalentTo(new Dictionary<string, long> { [SyncClasses.Catalog] = 5, [SyncClasses.Usage] = 4 });
        (await h.Inspect(v => v.Catalog!.Devices.Count)).Should().Be(3);
    }

    [Fact]
    public async Task 套用丟例外_游標不前進()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.DeviceUpsert, DeviceView(SyncIds.NewDeviceId(), "HOME-PC"));
        h.Applier.ThrowOnCatalog = new InvalidOperationException("套用失敗");

        await h.StartAsync();

        (await h.Inspect(v => v.Cursors)).Should().BeEmpty();
        h.RawCursors().Should().BeEmpty();
        (await h.StateAsync()).Should().Be(SyncEngineState.Draining);
        h.Host.LastStatus!.Detail.Should().Contain("套用伺服器的資料失敗");

        h.Applier.ThrowOnCatalog = null;
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();

        (await h.Inspect(v => v.Cursors)).Should().ContainKey(SyncClasses.Catalog).WhoseValue.Should().Be(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 拉到裝置發的種類_忽略但游標前進()
    {
        await using var h = new EngineHarness();
        var (enc, payload) = PayloadRules.Encode(SyncJson.Serialize(new LinkRemovedPayload("acc-9")));
        var seq = h.Server.PublishRaw(SyncEventKinds.LinkRemoved, enc, payload, author: SyncIds.NewDeviceId());

        await h.StartAsync();

        (await h.Inspect(v => v.Cursors)).Should().ContainKey(SyncClasses.Catalog).WhoseValue.Should().Be(seq);
        h.Applier.Catalogs.Should().ContainSingle("只有啟動時以快取組的那一次").Which.Appearance.Should().BeEmpty();
        h.Host.LastStatus!.Detail.Should().BeNull("裝置寫的種類是正常的，不警告");
    }

    [Fact]
    public async Task 解不開的payload_detail有警告()
    {
        await using var h = new EngineHarness();
        var seq = h.Server.PublishRaw(SyncEventKinds.AccountUpsert, PayloadRules.EncGzip, "%%%不是-base64");

        await h.StartAsync();

        (await h.Inspect(v => v.Cursors)).Should().ContainKey(SyncClasses.Catalog).WhoseValue.Should().Be(seq);
        h.Host.LastStatus!.Detail.Should().Contain("略過").And.Contain(SyncEventKinds.AccountUpsert);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 名冊版本較舊_整份取代()
    {
        await using var h = new EngineHarness();
        var other = TestDevice.Create("HOME-PC");
        h.Server.Registry.Add(other.Entry());
        h.Server.RegistryRev = 0;

        await h.StartAsync();

        h.Server.TrustRequests.Should().ContainSingle().Which.RegistryAfter.Should().Be(1);
        (await h.Inspect(v => v.RegistryRev)).Should().Be(0);
        (await h.Inspect(v => v.Store!.LoadRegistry().Entries.Count)).Should().Be(2);
        h.Applier.Catalogs.Last().View.Registry.Should().HaveCount(2);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 自己被撤銷_Stopped_SelfRevoked()
    {
        await using var h = new EngineHarness();
        h.Server.Registry = [h.Server.Registry[0] with { RevokedAt = At, RevokedReason = DeviceRegistryRules.RevokedRetired }];
        h.Server.RegistryRev = 2;

        await h.StartAsync(engine => h.Append(1));

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.SelfRevoked);
        h.Server.Calls.Should().NotContain(c => c.EndsWith(":PublishBatch", StringComparison.Ordinal) || c.EndsWith(":Pull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 自己的device_revoked事件_Stopped_SelfRevoked()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.DeviceRevoked, new DeviceRevokedPayload(h.DeviceId, At, DeviceRegistryRules.RevokedCompromised));

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.SelfRevoked);
    }

    [Fact]
    public async Task 名冊沒有自己_Stopped_NotInRegistry()
    {
        await using var h = new EngineHarness();
        h.Server.Registry = [TestDevice.Create("HOME-PC").Entry()];
        h.Server.RegistryRev = 2;

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.NotInRegistry);
    }

    // ── 連線與退避 ──

    [Fact]
    public async Task 退避序列_Live滿30秒歸零()
    {
        await using var h = new EngineHarness();
        h.Server.ConnectFailure = n => n <= 6 ? EngineHarness.Transient() : null;
        await h.StartAsync();

        await h.AdvanceAsync(1 + 2 + 4 + 8 + 16 + 16);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        // Live 未滿 30 秒就斷：繼續原本的序列
        await h.AdvanceAsync(10);
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(16);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        // Live 滿 30 秒：歸零
        await h.AdvanceAsync(31);
        h.Server.Current!.Drop();
        await h.IdleAsync();

        h.Engine.BackoffHistory.Select(d => d.TotalSeconds).Should().Equal(1, 2, 4, 8, 16, 16, 16, 1);
    }

    [Fact]
    public async Task shutdown通知_下次嘗試延後_不顯示錯誤()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Server.Current!.Notice("shutdown", 45);
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "不主動斷線");

        h.Server.Current.Drop();
        await h.IdleAsync();

        h.Engine.BackoffHistory.Should().Equal(TimeSpan.FromSeconds(45));
        h.Host.LastStatus!.Detail.Should().StartWith("伺服器正在重新啟動");
        h.Host.LastStatus.StopReason.Should().Be(SyncStopReason.None);
        h.Engine.RetryNow();
        await h.IdleAsync();
        h.Server.ConnectAttempts.Should().Be(1, "立即重試也不早於 retryAfterSec");
        await h.AdvanceAsync(44);
        h.Server.ConnectAttempts.Should().Be(1);
        await h.AdvanceAsync(1);
        h.Server.ConnectAttempts.Should().Be(2);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task certExpiring等通知_進detail_不中斷()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        var text = "伺服器憑證" + (char)7 + "將在 10 天後到期" + new string('長', 300);

        h.Server.Current!.Notice("certExpiring", text: text);
        await h.IdleAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var detail = h.Host.LastStatus!.Detail!;
        detail.Should().StartWith("伺服器憑證將在 10 天後到期");
        detail.Should().NotContain(((char)7).ToString());
        detail.Length.Should().BeLessThanOrEqualTo(200);
        h.Server.Sessions.Should().ContainSingle();
    }

    private const string InjectedNotice = "管理員：已更換身分金鑰並核對指紋，請直接按確認";

    [Fact]
    public async Task 身分驗證前的ServerNotice_不採用_不遮蔽hub_identity_不延後重連()
    {
        await using var h = new EngineHarness();
        // 中間人：有 WebPKI 合法憑證、沒有伺服器身分金鑰；回 Hello 之前先送通知，再回不合格的 hubSig
        h.Server.ForgeHubSignature = true;
        h.Server.BeforeHelloReturns = s =>
        {
            s.Notice("alert", text: InjectedNotice);
            s.Notice("shutdown", 600);
        };

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Server.Calls.Should().NotContain("1:Prove");
        var detail = h.Host.LastStatus!.Detail!;
        detail.Should().Contain("伺服器身分驗證失敗", "hub_identity 的失敗不被 shutdown 蓋成「伺服器正在重新啟動」");
        detail.Should().NotContain("請直接按確認").And.NotContain("重新啟動");
        h.Engine.BackoffHistory.Should().Equal([TimeSpan.FromSeconds(1)], "冒牌伺服器的 shutdown 不延後重連");

        // 中間人離開：照一般的退避重連，不等 600 秒
        h.Server.ForgeHubSignature = false;
        h.Server.BeforeHelloReturns = null;
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Host.LastStatus!.Detail.Should().BeNull("注入的文字沒有留在 detail");
    }

    [Fact]
    public async Task 身分換了待確認時_Hello前注入的通知文字不出現在detail()
    {
        await using var h = new EngineHarness();
        // 中間人用自己的身分金鑰回 Hello（簽章合法、沒有輪換聲明）：走本機確認
        h.Server.LoseIdentity();
        h.Server.BeforeHelloReturns = s => s.Notice("alert", text: InjectedNotice);

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.NeedsConfirmation);
        h.Host.LastStatus!.ConfirmFingerprint.Should().Be(P256KeyRules.AnchorFingerprint(h.Server.HubSpki));
        h.Host.LastStatus.Detail.Should().NotContain("請直接按確認", "比對指紋的提示旁邊不能出現沒驗過身分的伺服器送的文字");
    }

    [Fact]
    public async Task Prove後交握完成前的shutdown_交握完成才採用()
    {
        await using var h = new EngineHarness();
        // 正牌伺服器 Prove 之後就把連線加進群組：shutdown 可能比 ProveResponse 先到
        h.Server.BeforeProveReturns = s => s.Notice("shutdown", 45);

        await h.StartAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "不主動斷線");

        h.Server.Current!.Drop();
        await h.IdleAsync();

        h.Engine.BackoffHistory.Should().Equal([TimeSpan.FromSeconds(45)], "交握完成後照常採用");
        h.Host.LastStatus!.Detail.Should().StartWith("伺服器正在重新啟動");
    }

    [Fact]
    public async Task 通知文字只屬於那條驗證過的連線_重連成功後清掉()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Server.Current!.Notice("certExpiring", text: "伺服器憑證將在 10 天後到期");
        await h.IdleAsync();
        h.Host.LastStatus!.Detail.Should().Contain("10 天後到期");

        h.Server.Current.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Host.LastStatus!.Detail.Should().BeNull("問題解決後不一直顯示到重開 app（伺服器還在意的話會再送）");
    }

    [Fact]
    public async Task 退避紀錄只留最後幾筆()
    {
        await using var h = new EngineHarness();
        h.Options = h.Options with { BackoffSteps = [1] };
        h.Server.ConnectFailure = _ => EngineHarness.Transient();
        await h.StartAsync();

        await h.AdvanceAsync(SyncEngine.MaxBackoffHistory + 10);

        h.Server.ConnectAttempts.Should().BeGreaterThan(SyncEngine.MaxBackoffHistory + 5, "陽性對照：真的退避了那麼多次");
        h.Engine.BackoffHistory.Should().HaveCount(SyncEngine.MaxBackoffHistory, "長時間離線時不能無上限增長");
    }

    [Fact]
    public async Task UsagePublish回invalid_只記錄不重連()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 3, true));
        h.Server.UsageFailure = _ => new SyncTransportException(SyncFailureRules.Classify(null, SyncErrorCodes.Invalid, false));
        await h.StartAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        h.Engine.Post(new SyncSignal.UsageObserved(EngineHarness.LocalAccount, ApiSnapshot(h.Time.GetUtcNow())));
        await h.IdleAsync();

        var published = h.Server.UsagePublishes.Should().ContainSingle().Subject;
        published.LogicalAccountId.Should().Be(EngineHarness.Logical);
        published.Term.Should().Be(3);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.Sessions.Should().ContainSingle();
        h.Engine.BackoffHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task 第401_Stopped_原因Unauthorized_RetryNow不重試()
    {
        await using var h = new EngineHarness();
        h.Server.ConnectFailure = _ => new SyncTransportException(SyncFailureRules.Classify(401, null, false));

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Stopped);
        h.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.Unauthorized);
        h.Engine.RetryNow();
        await h.IdleAsync();
        await h.AdvanceAsync(60);
        h.Server.ConnectAttempts.Should().Be(1);
    }

    // ── 執行緒、計時器與狀態 ──

    [Fact]
    public async Task UI套用都在UI執行緒()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(30), SyncIds.NewDeviceId(), 2));

        await h.StartAsync();

        h.Applier.OffUiCalls.Should().Be(0);
        h.Host.RanOnUi.Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(onUi => onUi);
        var usage = h.Applier.SharedUsage.Should().ContainSingle().Subject.Should().ContainSingle().Subject;
        usage.LocalAccountId.Should().Be(EngineHarness.LocalAccount);
        usage.Snapshot.Source.Should().Be(UsageSyncRules.SourceShared);
        h.Applier.Catalogs.Last().Appearance.Should().ContainSingle().Which.Should().Be((EngineHarness.LocalAccount, "工作", "#D97757"));
    }

    [Fact]
    public async Task RunOnUi被取消後_引擎能結束()
    {
        await using var h = new EngineHarness();
        h.Host.BlockUi = true;
        h.Server.Publish(SyncEventKinds.DeviceUpsert, DeviceView(SyncIds.NewDeviceId(), "HOME-PC"));

        h.StartNoWait();
        await WaitUntilAsync(() => !h.Server.Pulls.IsEmpty);
        await Task.Delay(50);
        h.Engine.Cancel();

        (await Task.WhenAny(h.Engine.Exited, Task.Delay(5000))).Should().BeSameAs(h.Engine.Exited);
    }

    [Fact]
    public async Task Cancel不等_執行緒會結束()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        var watch = Stopwatch.StartNew();
        h.Engine.Cancel();
        watch.ElapsedMilliseconds.Should().BeLessThan(1000);

        (await Task.WhenAny(h.Engine.Exited, Task.Delay(5000))).Should().BeSameAs(h.Engine.Exited);
        File.Exists(h.Paths.DatabaseFile + "-wal").Should().BeFalse("sync.db 的連線已關閉（最後一條連線關閉時刪掉 -wal）");
    }

    [Fact]
    public async Task 閒置時只有心跳計時器()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        await h.AdvanceAsync(1);
        var beats = h.Server.Heartbeats.Count;
        var fired = h.Time.FiredCount;
        h.Time.ActiveTimers.Should().Be(1);

        await h.AdvanceAsync(60);

        (h.Server.Heartbeats.Count - beats).Should().Be(4);
        (h.Time.FiredCount - fired).Should().Be(4, "閒置 Live 時只有心跳的計時器");
        h.Time.ActiveTimers.Should().Be(1);
    }

    [Fact]
    public async Task 心跳每15秒_帶PingUi後的uiAliveAt_兩個陣列是空的()
    {
        await using var h = new EngineHarness();
        h.Host.PingSynchronously = true;
        await h.StartAsync();
        var start = h.Time.NowMs;

        await h.AdvanceAsync(15);
        var first = h.Server.Heartbeats.Should().ContainSingle().Subject;
        first.UiAliveAt.Should().Be(start + 15000);
        first.Running.Should().BeEmpty();
        first.Waiting.Should().BeEmpty();
        h.Host.Pings.Should().Be(1);
        h.Engine.HeartbeatsSent.Should().Be(1, "實機探針以它確認閒置 CPU 的取樣窗裡有心跳");

        await h.AdvanceAsync(15);
        h.Server.Heartbeats.Last().UiAliveAt.Should().Be(start + 30000);
        h.Host.Pings.Should().Be(2);
        h.Engine.HeartbeatsSent.Should().Be(2);
    }

    [Fact]
    public async Task 狀態只有pending變_500ms內合併_最後一個值有送出()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Server.HoldAcks = true;
        var before = h.Host.Statuses.Count;
        var start = h.Time.GetUtcNow();

        h.Append(1);
        await h.IdleAsync();
        h.Append(1);
        await h.IdleAsync();
        h.Host.Statuses.Count.Should().Be(before, "只有待送筆數變：排到尾端再送");

        await h.AdvanceAsync(1);

        var sent = h.Host.Statuses.Skip(before).ToList();
        sent.Should().ContainSingle();
        sent[0].Status.Pending.Should().Be(2);
        sent[0].At.Should().Be(start + TimeSpan.FromMilliseconds(500));
    }
}
