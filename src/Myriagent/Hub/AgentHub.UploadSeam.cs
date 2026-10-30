namespace Myriagent.Hub;

/// <summary>
/// 瀏覽器上傳的檔（形狀照 RemoteCommandShapeRules 的 uploads）。WP4.7 計畫 6.3 原本在這個檔另宣告給 WP4.4 呼叫的 <c>BeginRemoteUploads</c>；
/// WP4.4 合併時改成自己開的登記點 <c>PrepareRemoteUploads</c>（<c>AgentHub.RemoteHandlers.cs</c>），WP4.7 在 <c>AgentHub.RemoteFiles.cs</c> 實作它（計畫步 13）
/// </summary>
internal sealed record RemoteUpload(string UploadId, string Name, long Size, string Sha256);
