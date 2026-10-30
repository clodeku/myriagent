namespace Myriagent.Sync;

/// <summary>
/// 連結進來的 src/MyriagentHub/Data/SyncSchema.cs 用到的 Protocol 型別（Protocol 的那一份是 internal，量測程式看不到）：
/// 形狀照 src/Myriagent.Protocol/Sync/DeviceHubMessages.cs 的 <c>ServerInstance</c>。B6 只用 SyncSchema 建結構，不讀它
/// </summary>
internal sealed record ServerInstance(string InstanceId, long RestoreGeneration);
