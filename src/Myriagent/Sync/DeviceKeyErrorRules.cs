using System.Globalization;
using System.Security.Cryptography;

namespace Myriagent.Sync;

/// <summary>開裝置金鑰或簽章失敗的種類（<see cref="DeviceKeyErrorRules.Classify(int)"/>）</summary>
internal enum DeviceKeyErrorKind
{
    /// <summary>金鑰確定不存在：停止同步、提示重新配對（<see cref="SyncStopReason.KeyMissing"/>）</summary>
    Missing,

    /// <summary>TPM 忙碌、自我測試中、字典攻擊鎖定、資源用完、TBS 服務還沒起來、逾時：照退避重試，不限次數</summary>
    Transient,

    /// <summary>不認得的錯誤：保守地當暫時性重試，持續到門檻（<see cref="DeviceKeyErrorRules.ShouldGiveUp"/>）才停在 KeyMissing</summary>
    Unknown,
}

/// <summary>一次金鑰失敗之後怎麼做</summary>
internal enum DeviceKeyAction
{
    /// <summary>照引擎的退避步階稍後重試（重開金鑰）</summary>
    Retry,

    /// <summary>停止同步（<see cref="SyncStopReason.KeyMissing"/>）</summary>
    Stop,
}

/// <summary>
/// 這一連串金鑰失敗的紀錄（引擎執行緒的記憶體，重開 app 歸零；簽章成功就歸零）
/// </summary>
/// <param name="Failures">要重試的失敗次數（暫時性＋不認得的；記錄用）</param>
/// <param name="Unknown">其中不認得的錯誤次數</param>
/// <param name="FirstUnknownAt">第一次不認得的錯誤的時間</param>
internal sealed record DeviceKeyStreak(int Failures, int Unknown, DateTimeOffset? FirstUnknownAt)
{
    public static DeviceKeyStreak None { get; } = new(0, 0, null);
}

/// <summary>
/// 裝置金鑰錯誤的分類（設計 14.10、sync-protocol 10）：只有「金鑰確定不存在」才停在 KeyMissing、提示重新配對；
/// TPM／TBS 暫時性的錯誤照退避重試；不認得的錯誤先當暫時性，連續 <see cref="GiveUpAttempts"/> 次、而且持續
/// <see cref="GiveUpAfter"/> 以上才停（兩個條件都要：睡眠喚醒後只隔一次嘗試不算，短時間內快速失敗幾次也不算）。
/// <para>
/// 錯誤碼是 CNG（NCrypt）回的 SECURITY_STATUS／HRESULT，也就是 <see cref="CryptographicException.HResult"/>。名稱照 Windows SDK 的
/// winerror.h；值與意思以 Windows 自己的訊息表核對過（<c>certutil -error</c> 的符號名、FormatMessage 的說明；TPM 2.0 的
/// <c>TPM_20_E_*</c> 是 0x80280000 加上 TCG 規格的 <c>TPM_RC_*</c>）。
/// </para>
/// </summary>
internal static class DeviceKeyErrorRules
{
    /// <summary>不認得的錯誤至少連續這麼多次才停</summary>
    public const int GiveUpAttempts = 10;

    /// <summary>從第一次不認得的錯誤起至少持續這麼久才停（退避上限 16 秒時大約 40 次嘗試）</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

    /// <summary>退避時的失敗代碼（<see cref="SyncFailure.Code"/>）</summary>
    public const string UnavailableCode = "device_key";

    /// <summary>不認得的錯誤持續到門檻、停止時的失敗代碼（<see cref="SyncStatusRules"/> 以它換掉「找不到金鑰」的說明）</summary>
    public const string LostCode = "device_key_lost";

    // ── 金鑰不存在 ──

    /// <summary>NTE_BAD_KEYSET：Keyset does not exist（CngKey.Exists 只把這個當成「不存在」）</summary>
    public const int NteBadKeyset = unchecked((int)0x80090016);

    /// <summary>NTE_NO_KEY：Key does not exist</summary>
    public const int NteNoKey = unchecked((int)0x8009000D);

    /// <summary>NTE_NOT_FOUND：Object was not found</summary>
    public const int NteNotFound = unchecked((int)0x80090011);

    // ── 暫時性：CNG ──

    /// <summary>NTE_DEVICE_NOT_READY：提供者需要的裝置（TPM）還沒準備好</summary>
    public const int NteDeviceNotReady = unchecked((int)0x80090030);

    /// <summary>NTE_NO_MEMORY</summary>
    public const int NteNoMemory = unchecked((int)0x8009000E);

    /// <summary>
    /// NTE_AUTHENTICATION_IGNORED：字典攻擊防護觸發，提供者略過了授權。Platform Crypto Provider（pcpksp.dll）把 TPM 層的
    /// TPM_RC_LOCKOUT／TPM_E_DEFEND_LOCK_RUNNING 轉成這個再回給 NCrypt，所以字典攻擊鎖定時簽章實際看到的多半是它
    /// </summary>
    public const int NteAuthenticationIgnored = unchecked((int)0x80090031);

    // ── 暫時性：TPM 1.2 與共通 ──

    /// <summary>TPM_E_RETRY：TPM 太忙，稍後再送</summary>
    public const int TpmRetry = unchecked((int)0x80280800);

    /// <summary>TPM_E_NEEDS_SELFTEST</summary>
    public const int TpmNeedsSelfTest = unchecked((int)0x80280801);

    /// <summary>TPM_E_DOING_SELFTEST</summary>
    public const int TpmDoingSelfTest = unchecked((int)0x80280802);

    /// <summary>TPM_E_DEFEND_LOCK_RUNNING：字典攻擊防護正在計時</summary>
    public const int TpmDefendLockRunning = unchecked((int)0x80280803);

    /// <summary>TPM_E_RESOURCES：TPM 內部資源不足</summary>
    public const int TpmResources = unchecked((int)0x80280015);

    // ── 暫時性：TPM 2.0（TPM_20_E_*） ──

    /// <summary>TPM_20_E_CONTEXT_GAP（TPM_RC_CONTEXT_GAP）</summary>
    public const int Tpm20ContextGap = unchecked((int)0x80280901);

    /// <summary>TPM_20_E_OBJECT_MEMORY（TPM_RC_OBJECT_MEMORY）</summary>
    public const int Tpm20ObjectMemory = unchecked((int)0x80280902);

    /// <summary>TPM_20_E_SESSION_MEMORY（TPM_RC_SESSION_MEMORY）</summary>
    public const int Tpm20SessionMemory = unchecked((int)0x80280903);

    /// <summary>TPM_20_E_MEMORY（TPM_RC_MEMORY）</summary>
    public const int Tpm20Memory = unchecked((int)0x80280904);

    /// <summary>TPM_20_E_SESSION_HANDLES（TPM_RC_SESSION_HANDLES：session 釋放後就能再建）。
    /// TPM_20_E_OBJECT_HANDLES（0x80280906）要重新開機才恢復，不列在這裡（當成不認得的）</summary>
    public const int Tpm20SessionHandles = unchecked((int)0x80280905);

    /// <summary>TPM_20_E_YIELDED（TPM_RC_YIELDED：暫停了指令，可以重送）</summary>
    public const int Tpm20Yielded = unchecked((int)0x80280908);

    /// <summary>TPM_20_E_CANCELED（TPM_RC_CANCELED）</summary>
    public const int Tpm20Canceled = unchecked((int)0x80280909);

    /// <summary>TPM_20_E_TESTING（TPM_RC_TESTING：自我測試中）</summary>
    public const int Tpm20Testing = unchecked((int)0x8028090A);

    /// <summary>TPM_20_E_NV_RATE（TPM_RC_NV_RATE：為了 NV 壽命限速）</summary>
    public const int Tpm20NvRate = unchecked((int)0x80280920);

    /// <summary>TPM_20_E_LOCKOUT（TPM_RC_LOCKOUT：字典攻擊鎖定中）</summary>
    public const int Tpm20Lockout = unchecked((int)0x80280921);

    /// <summary>TPM_20_E_RETRY（TPM_RC_RETRY：TPM 沒能開始這個指令）</summary>
    public const int Tpm20Retry = unchecked((int)0x80280922);

    /// <summary>TPM_20_E_NV_UNAVAILABLE（TPM_RC_NV_UNAVAILABLE：NV 暫時不能寫）</summary>
    public const int Tpm20NvUnavailable = unchecked((int)0x80280923);

    // ── 暫時性：Platform Crypto Provider ──

    /// <summary>TPM_E_PCP_DEVICE_NOT_READY：Platform Crypto Device 還沒準備好（還在佈建）</summary>
    public const int PcpDeviceNotReady = unchecked((int)0x80290401);

    /// <summary>
    /// TPM_E_PCP_AUTHENTICATION_IGNORED：PCP 為了防字典攻擊略過授權（TPM_RC_LOCKOUT 在 PCP 內的對應，對外再轉成 NTE_AUTHENTICATION_IGNORED）。
    /// TPM_E_PCP_AUTHENTICATION_FAILED（0x80290408，授權本身錯了）不列在這裡（當成不認得的）
    /// </summary>
    public const int PcpAuthenticationIgnored = unchecked((int)0x80290409);

    /// <summary>TPM_E_LOCKED_OUT：TPM 鎖定中</summary>
    public const int PcpLockedOut = unchecked((int)0x8029041B);

    // ── 暫時性：TBS（TPM Base Services） ──

    /// <summary>TBS_E_SERVICE_NOT_RUNNING</summary>
    public const int TbsServiceNotRunning = unchecked((int)0x80284008);

    /// <summary>TBS_E_TOO_MANY_TBS_CONTEXTS</summary>
    public const int TbsTooManyContexts = unchecked((int)0x80284009);

    /// <summary>TBS_E_TOO_MANY_RESOURCES</summary>
    public const int TbsTooManyResources = unchecked((int)0x8028400A);

    /// <summary>TBS_E_SERVICE_START_PENDING</summary>
    public const int TbsServiceStartPending = unchecked((int)0x8028400B);

    /// <summary>TBS_E_COMMAND_CANCELED</summary>
    public const int TbsCommandCanceled = unchecked((int)0x8028400D);

    // ── 暫時性：逾時與忙碌（HRESULT_FROM_WIN32） ──

    /// <summary>HRESULT_FROM_WIN32(ERROR_TIMEOUT)</summary>
    public const int Win32Timeout = unchecked((int)0x800705B4);

    /// <summary>HRESULT_FROM_WIN32(WAIT_TIMEOUT)</summary>
    public const int Win32WaitTimeout = unchecked((int)0x80070102);

    /// <summary>HRESULT_FROM_WIN32(ERROR_BUSY)</summary>
    public const int Win32Busy = unchecked((int)0x800700AA);

    /// <summary>
    /// 錯誤碼 → 種類。不在兩張清單裡的（例如 NTE_DEVICE_NOT_FOUND、TBS_E_TPM_NOT_FOUND、TBS_E_SERVICE_DISABLED：TPM 被停用或拿掉，
    /// 恢復之後金鑰還在；TBS_E_IOERROR、TPM_20_E_OBJECT_HANDLES；TPM 清除之後載入金鑰的各種錯誤）一律 <see cref="DeviceKeyErrorKind.Unknown"/>
    /// </summary>
    public static DeviceKeyErrorKind Classify(int hresult) => hresult switch
    {
        NteBadKeyset or NteNoKey or NteNotFound => DeviceKeyErrorKind.Missing,
        NteDeviceNotReady or NteNoMemory or NteAuthenticationIgnored
            or TpmRetry or TpmNeedsSelfTest or TpmDoingSelfTest or TpmDefendLockRunning or TpmResources
            or Tpm20ContextGap or Tpm20ObjectMemory or Tpm20SessionMemory or Tpm20Memory or Tpm20SessionHandles
            or Tpm20Yielded or Tpm20Canceled or Tpm20Testing or Tpm20NvRate or Tpm20Lockout or Tpm20Retry or Tpm20NvUnavailable
            or PcpDeviceNotReady or PcpAuthenticationIgnored or PcpLockedOut
            or TbsServiceNotRunning or TbsTooManyContexts or TbsTooManyResources or TbsServiceStartPending or TbsCommandCanceled
            or Win32Timeout or Win32WaitTimeout or Win32Busy => DeviceKeyErrorKind.Transient,
        _ => DeviceKeyErrorKind.Unknown,
    };

    /// <summary>
    /// 例外 → 種類：<see cref="CryptographicException"/> 看 HResult（CNG 的錯誤碼）；其他例外（金鑰的格式不對、提供者的程式錯誤……）
    /// 當成不認得的
    /// </summary>
    public static DeviceKeyErrorKind Classify(Exception error) =>
        error is CryptographicException ? Classify(error.HResult) : DeviceKeyErrorKind.Unknown;

    /// <summary>不認得的錯誤已經連續 <see cref="GiveUpAttempts"/> 次、而且從第一次起滿 <see cref="GiveUpAfter"/>（時鐘倒退時不算滿）</summary>
    public static bool ShouldGiveUp(DeviceKeyStreak streak, DateTimeOffset now) =>
        streak.Unknown >= GiveUpAttempts && streak.FirstUnknownAt is { } first && now - first >= GiveUpAfter;

    /// <summary>
    /// 一次失敗之後的紀錄與動作：不存在→停止；暫時性→重試（不增加不認得的次數，也不歸零）；不認得→次數加一，到門檻停止
    /// </summary>
    public static (DeviceKeyStreak Streak, DeviceKeyAction Action) OnFailure(DeviceKeyStreak streak, DeviceKeyErrorKind kind, DateTimeOffset now)
    {
        switch (kind)
        {
            case DeviceKeyErrorKind.Missing:
                return (streak, DeviceKeyAction.Stop);
            case DeviceKeyErrorKind.Transient:
                return (streak with { Failures = streak.Failures + 1 }, DeviceKeyAction.Retry);
            case DeviceKeyErrorKind.Unknown:
            {
                var next = new DeviceKeyStreak(streak.Failures + 1, streak.Unknown + 1, streak.FirstUnknownAt ?? now);
                return (next, ShouldGiveUp(next, now) ? DeviceKeyAction.Stop : DeviceKeyAction.Retry);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "不認得的金鑰錯誤種類");
        }
    }

    /// <summary>錯誤碼的十六進位寫法（0x 加 8 位大寫）</summary>
    public static string Hex(int hresult) => "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>退避時顯示的原因（sync.detail 再接「N 秒後重試」）：不叫使用者重新配對</summary>
    public static SyncFailure Unavailable(int hresult) =>
        new(SyncFailureKind.Transient, UnavailableCode, $"裝置金鑰暫時無法使用（{Hex(hresult)}）");

    /// <summary>不認得的錯誤持續到門檻、停止時的原因</summary>
    public static SyncFailure Lost(int hresult) =>
        new(SyncFailureKind.Transient, LostCode, $"裝置金鑰一直無法使用（{Hex(hresult)}），請解除配對後重新配對");
}
