namespace Myriagent.Update;

/// <summary>發佈資料夾裡的一個檔案（相對路徑用斜線）</summary>
internal sealed record ReleaseFile(string Path, long Size, string Sha256);

/// <summary>需要的共用執行階段（Microsoft.WindowsDesktop.App 10.0.0 以上）</summary>
internal sealed record ReleaseRuntime(string Name, ReleaseVersion Version);

/// <summary>桌面版的發佈 manifest（設計 6.2；原文位元組才是簽章對象，這是驗證後的解析結果）</summary>
internal sealed record ReleaseManifest(
    ReleaseIdentity Identity,
    string Commit,
    string Entry,
    ReleaseRuntime MinRuntime,
    int ProtocolVersion,
    string Zip,
    string ZipSha256,
    long TotalBytes,
    IReadOnlyList<ReleaseFile> Files);

/// <summary>manifest 的簽章（.manifest.sig 的內容）：kid＝公鑰 SPKI 的 SHA-256 前 16 個十六進位字，sig＝P1363 64 位元組的 base64url</summary>
internal sealed record ReleaseSignature(string Kid, string Sig);

/// <summary>信任的發佈公鑰</summary>
internal sealed record ReleaseKey(string Kid, string Spki);
