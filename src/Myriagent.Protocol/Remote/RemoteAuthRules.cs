namespace Myriagent.Remote;

/// <summary>
/// 遠端操作對伺服器工作階段的要求（設計 8.10、14.11 D4、Q2、Q5）：伺服器以工作階段最後一次輸入密碼的時間（<c>LastUvAt</c>）判斷，
/// 簽進信封的 <c>auth.uvAt</c> 讓電腦以同一組常數重查（閘門 5f）。時間一律是伺服器時間（Unix 毫秒）：<c>uvAt</c> 與指令建立的
/// <c>created</c> 都由伺服器寫，比的是兩者的差，不用電腦自己的時鐘。
/// <para>
/// 擁有者的回答（2026-10-18）：Q2「都不用輸入」——兩個時間窗都是 null（只要瀏覽器工作階段有效：閒置 12 小時、最長 7 天）；
/// Q5「照常可用、常駐警告」——擁有者密碼還是預設值時不限制任何層級（<see cref="DefaultPasswordMaxTier"/> 是 2），旗標只進遠端操作紀錄與網頁的警告。
/// 檢查與信封的欄位照樣保留，日後要收緊只改這裡的常數（伺服器與電腦共用）
/// </para>
/// </summary>
internal static class RemoteAuthRules
{
    /// <summary>層級 1 要求多久內輸入過密碼（毫秒）；null＝不要求，只要工作階段有效（Q2）</summary>
    public static long? Tier1WindowMs => null;

    /// <summary>層級 2 要求多久內輸入過密碼（毫秒）；null＝不要求，只要工作階段有效（Q2）</summary>
    public static long? Tier2WindowMs => null;

    /// <summary>擁有者密碼還是預設值時最高能做到哪個層級；2＝不限制（Q5-B：照常可用、常駐警告）</summary>
    public const int DefaultPasswordMaxTier = 2;

    /// <summary>這個層級的時間窗（層級 0 只要工作階段有效）</summary>
    public static long? WindowFor(int tier) => WindowFor(tier, Tier1WindowMs, Tier2WindowMs);

    /// <summary>以目前的常數判斷（閘門 5f、伺服器簽信封之前）</summary>
    public static bool IsFresh(int tier, long? uvAt, long created) => IsFresh(tier, uvAt, created, Tier1WindowMs, Tier2WindowMs);

    /// <summary>
    /// 近期輸入過密碼：時間窗是 null 時一律是；否則 <paramref name="uvAt"/> 不是 null、不晚於 <paramref name="created"/>，
    /// 而且 <c>created − uvAt</c> ≤ 時間窗（邊界算新鮮）。層級超出 0～2 丟例外
    /// </summary>
    public static bool IsFresh(int tier, long? uvAt, long created, long? tier1WindowMs, long? tier2WindowMs)
    {
        if (WindowFor(tier, tier1WindowMs, tier2WindowMs) is not { } window)
            return true;
        return uvAt is { } at && at <= created && created - at <= window;
    }

    /// <summary>擁有者密碼還是預設值時，這個層級能不能做（閘門 5g；目前一律可以，只記錄與警告）</summary>
    public static bool DefaultPasswordAllows(int tier, bool defaultPassword)
    {
        CheckTier(tier);
        return !defaultPassword || tier <= DefaultPasswordMaxTier;
    }

    private static long? WindowFor(int tier, long? tier1WindowMs, long? tier2WindowMs)
    {
        CheckTier(tier);
        return tier switch
        {
            0 => null,
            1 => tier1WindowMs,
            _ => tier2WindowMs,
        };
    }

    private static void CheckTier(int tier)
    {
        if (tier is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "層級是 0～2");
    }
}
