namespace Myriagent.Sync;

/// <summary>
/// 第 5 階段的桌面開關（計畫 docs/plans/2026-10-23-phase5-handoff.md 4.1、D1）：做到一半的第 5 階段在正式的桌面必須休眠——
/// 開關關著時 Hello 不宣告 <c>log1</c>／<c>hand1</c>，所以沒有任何連線協商得出，新的程式一律不跑。引擎建立時讀（<see cref="SyncEngineOptions.Phase5"/>，
/// 沒給是 <see cref="Default"/>）；測試與 FakeDevice 以引擎選項打開。伺服器那一邊是 <c>Hub:Logs:Declare</c>、<c>Hub:Handoff:Declare</c>，
/// 和這裡同一個工作包改預設（log1：WP5.3；hand1：WP5.10，伺服器先部署、桌面後發佈）
/// </summary>
/// <param name="Log1">宣告逐字稿與檔案日誌（上傳、回補、下載）</param>
/// <param name="Hand1">宣告租約與交接（只在 <paramref name="Log1"/> 也打開時有效：<see cref="DeclaresHand1"/>）</param>
internal sealed record SyncPhase5Switches(bool Log1, bool Hand1)
{
    /// <summary>正式組建的預設：兩個都關</summary>
    public static SyncPhase5Switches Default { get; } = new(Log1: false, Hand1: false);

    /// <summary>兩個都開（測試用）</summary>
    public static SyncPhase5Switches All { get; } = new(Log1: true, Hand1: true);

    /// <summary>要宣告 hand1：兩個都開（hand1 要靠日誌驗證逐字稿）</summary>
    public bool DeclaresHand1 => Log1 && Hand1;
}
