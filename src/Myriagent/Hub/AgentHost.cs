using Myriagent.Speech;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>一個顯示介面的視窗（主視窗或彈出視窗）裡的 WebView2</summary>
internal interface IAgentView
{
    string Id { get; }

    bool IsPopout { get; }

    /// <summary>送一則訊息給網頁（UI 執行緒呼叫）</summary>
    void PostToWeb(JObject message);
}

/// <summary>
/// 完整介面的視窗（像 Chrome 的多個視窗：主視窗與之後另外開的視窗）裡的 WebView2。各視窗有自己的分頁與窗格版面（以 WindowKey 存），
/// 分頁可以在視窗之間拖曳搬移（AgentHub.Windows.cs）。彈出視窗與遠端、同步的檢視不是
/// </summary>
internal interface IWindowView : IAgentView
{
    /// <summary>視窗的鍵：主視窗是 <see cref="WindowRules.PrimaryKey"/>，其他視窗是 settings.json 的 <c>Windows[].Key</c></summary>
    string WindowKey { get; }

    /// <summary>主視窗（第一個視窗：app 更新的接續、啟動提醒只送它；關掉時照設定縮到系統匣）</summary>
    bool IsPrimaryWindow { get; }
}

/// <summary>放開拖曳的分頁時，游標落在哪裡（宿主以游標位置判斷）</summary>
internal enum TabDropKind
{
    /// <summary>不處理：還在來源視窗、落在彈出視窗、按了 Esc 取消</summary>
    None,

    /// <summary>落在 app 的另一個視窗：搬過去（ViewId 是那個視窗的 view）</summary>
    Window,

    /// <summary>落在 app 的視窗以外（桌面、其他程式）：在游標的位置開新視窗，分頁搬進去</summary>
    NewWindow,
}

internal readonly record struct TabDropTarget(TabDropKind Kind, string? ViewId = null);

/// <summary>多視窗的宿主能力（AppController 以 AgentHub.AttachWindows 接上；中樞預設沒有，視窗指令一律不處理）</summary>
internal interface IWindowHost
{
    /// <summary>
    /// 以 <paramref name="key"/> 開新的完整視窗（新視窗的網頁就緒時中樞以 WindowKey 認出它）；開不了（正在結束）回 false。
    /// atCursor＝放在游標的位置（拖出分頁）；否則從 sourceViewId 的視窗錯開一點
    /// </summary>
    bool OpenWindow(string key, string? sourceViewId, bool atCursor);

    /// <summary>放開拖曳的分頁時游標所在（sourceViewId＝分頁原本所在的視窗）</summary>
    TabDropTarget LocateTabDrop(string sourceViewId);

    /// <summary>把這個 view 的視窗帶到前面（分頁搬進去之後）</summary>
    void ActivateWindow(string viewId);

    /// <summary>關掉這個 view 的視窗（只關另外開的視窗；主視窗不關）</summary>
    void CloseWindow(string viewId);

    /// <summary>
    /// 各視窗的外框（鍵是 view id；WindowBorderRules）：中樞只在有變的時候送，宿主照 view 找到視窗設顏色；
    /// 沒有視窗的 view（遠端、同步的檢視）略過
    /// </summary>
    void UpdateWindowBorders(IReadOnlyDictionary<string, WindowBorder> borders);
}

/// <summary>宿主（WinForms 外殼）提供給中樞的能力</summary>
internal interface IAgentHost
{
    /// <summary>排到 UI 執行緒執行（保持先後順序）</summary>
    void BeginInvoke(Action action);

    /// <summary>選檔對話框（可多選）；取消回空清單</summary>
    IReadOnlyList<string> PickFiles(IAgentView view);

    /// <summary>選資料夾對話框；取消回 null</summary>
    string? PickFolder(IAgentView view, string? initialDirectory);

    /// <summary>另存新檔對話框（預設檔名 <paramref name="fileName"/>）；取消回 null</summary>
    string? PickSaveFile(IAgentView view, string fileName);

    void OpenWithShell(string pathOrUrl);

    void RevealInExplorer(string path);

    void CopyText(string text);

    /// <summary>把檔案放進剪貼簿（檔案清單，貼到檔案總管或其他程式就是複製這個檔）；剪貼簿被別的程式佔著時回 false</summary>
    bool CopyFile(string path);

    /// <summary>在新視窗開這個對話</summary>
    void OpenPopout(string conversationId);

    /// <summary>對話被刪除（關掉顯示它的彈出視窗）</summary>
    void ConversationDeleted(string conversationId);

    /// <summary>這個 view 所在的視窗是使用者正在操作的前景視窗（作用中、看得到、沒有最小化；藏到系統匣或最小化都不算）</summary>
    bool IsViewForeground(string viewId);

    /// <summary>系統通知（點了回到該對話）；視窗在前景時由呼叫端決定不送</summary>
    void Notify(string title, string message, string? conversationId);

    /// <summary>閃爍工作列提醒：閃顯示這個對話的視窗（viewIds）；沒有任何看得到的視窗顯示它時閃主視窗</summary>
    void RequestAttention(IReadOnlyCollection<string> viewIds);

    /// <summary>
    /// 語音通知：以 Windows 內建語音、照 <paramref name="tuning"/> 的語速與音調念出這句（依序念，佇列滿了或同一句剛念過就略過；設定由呼叫端檢查）。
    /// <paramref name="started"/>＝聲音開始出來時在 UI 執行緒呼叫一次（中樞讓那個對話的標頭閃爍）；略過或念不出來就不呼叫
    /// </summary>
    void Speak(string text, VoiceTuning tuning, Action? started);

    /// <summary>停下正在念的語音通知、清掉還沒念的（關掉語音通知時）</summary>
    void StopSpeaking();

    /// <summary>主題改變（標題列顏色跟著換）</summary>
    void ApplyTheme(string theme);

    /// <summary>系統匣提示文字與執行中數量更新</summary>
    void UpdateTray(string tooltip, int running, int waiting);
}
