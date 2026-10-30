using Myriagent.Agents;
using Myriagent.Cli;

namespace Myriagent.Tests.TestSupport;

/// <summary>測試用的 CLI 執行檔身分（路徑照真實安裝的位置；fileId 不同＝換過檔）</summary>
internal static class CliIdentities
{
    public const string ClaudeNativeExe = @"C:\Users\U\.local\bin\claude.exe";

    public const string CodexNpmExe = @"C:\Users\U\AppData\Roaming\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

    private static readonly DateTime Written = new(2026, 10, 9, 14, 49, 39, DateTimeKind.Utc);

    public static CliBinaryIdentity Claude(string version, ulong fileId = 1, long size = 259_509_920)
        => new(AgentProvider.Claude, CliInstallKind.Native, ClaudeNativeExe, version, 0x5A3C_91E0, fileId, size, Written.AddMinutes((long)fileId));

    public static CliBinaryIdentity Codex(string version, ulong fileId = 1, long size = 326_875_440)
        => new(AgentProvider.Codex, CliInstallKind.Npm, CodexNpmExe, version, 0x5A3C_91E0, fileId, size, Written.AddMinutes((long)fileId));

    public const string GrokNativeExe = @"C:\Users\U\.grok\bin\grok.exe";

    public static CliBinaryIdentity Grok(string version, ulong fileId = 1, long size = 98_304_000)
        => new(AgentProvider.Grok, CliInstallKind.Other, GrokNativeExe, version, 0x5A3C_91E0, fileId, size, Written.AddMinutes((long)fileId));

    public const string AntigravityNativeExe = @"C:\Users\U\AppData\Local\agy\bin\agy.exe";

    public static CliBinaryIdentity Antigravity(string version, ulong fileId = 1, long size = 191_000_000)
        => new(AgentProvider.Antigravity, CliInstallKind.Other, AntigravityNativeExe, version, 0x5A3C_91E0, fileId, size, Written.AddMinutes((long)fileId));
}
