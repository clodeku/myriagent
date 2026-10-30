using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Update;

/// <summary>
/// manifest 的 minRuntime 從哪裡來（PIPE-1）：發佈工具打包時讀發佈資料夾的 <see cref="RuntimeConfigFileName"/>，不再寫死 10.0.0——
/// 桌面改了目標框架（例如 net11.0-windows）或帶進要求較新執行階段的套件時，manifest 照實際的建置寫，
/// 只裝了舊執行階段的電腦就會照 minRuntime 提示先安裝（設計 8.4 第 3 步），而不是裝上之後起不來、被健康閘門退回
/// </summary>
internal static class ReleaseRuntimeRules
{
    /// <summary>桌面 framework-dependent 發佈的執行階段設定檔（dotnet publish 寫在 entry 旁邊）</summary>
    public const string RuntimeConfigFileName = "Myriagent.runtimeconfig.json";

    /// <summary>桌面需要的共用框架（WinForms＋WebView2；裝了它就一定有同版的 Microsoft.NETCore.App）</summary>
    public const string DesktopFramework = "Microsoft.WindowsDesktop.App";

    /// <summary>runtimeconfig.json 合理的大小上限（實際約 600 位元組）：超過就不讀</summary>
    public const int MaxRuntimeConfigBytes = 64 * 1024;

    /// <summary>
    /// 從 runtimeconfig.json 的 <c>runtimeOptions.frameworks</c>（或單一的 <c>runtimeOptions.framework</c>）取出 <see cref="DesktopFramework"/> 的版本。
    /// 只收 framework-dependent：有 <c>includedFrameworks</c>（self-contained）、沒有或有兩個桌面框架、版本不是三段的正式版（例如預覽版「10.0.0-rc.2…」）
    /// 一律回 false 與原因（中文），不丟例外
    /// </summary>
    public static bool TryReadMinRuntime(string? json, [NotNullWhen(true)] out ReleaseRuntime? runtime, out string? error)
    {
        runtime = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxRuntimeConfigBytes)
            return Fail("runtimeconfig.json 是空的或太大", out error);
        JObject root;
        try
        {
            if (JToken.Parse(json) is not JObject parsed)
                return Fail("runtimeconfig.json 不是 JSON 物件", out error);
            root = parsed;
        }
        catch (JsonException ex)
        {
            return Fail($"runtimeconfig.json 不是合法的 JSON：{ex.Message}", out error);
        }
        if (root["runtimeOptions"] is not JObject options)
            return Fail("runtimeconfig.json 沒有 runtimeOptions", out error);
        if (options["includedFrameworks"] is not null)
            return Fail("發佈是 self-contained（runtimeconfig.json 有 includedFrameworks）：桌面版一律 framework-dependent", out error);

        var frameworks = new List<JObject>();
        if (options["frameworks"] is JArray array)
            frameworks.AddRange(array.OfType<JObject>());
        if (options["framework"] is JObject single)
            frameworks.Add(single);
        var desktop = frameworks.Where(f => f["name"] is JValue { Type: JTokenType.String } name && (string?)name == DesktopFramework).ToList();
        if (desktop.Count != 1)
            return Fail(desktop.Count == 0
                ? $"runtimeconfig.json 沒有列 {DesktopFramework}（不是桌面 app 的發佈？）"
                : $"runtimeconfig.json 列了 {desktop.Count} 個 {DesktopFramework}", out error);
        var versionText = desktop[0]["version"] is JValue { Type: JTokenType.String } v ? (string?)v : null;
        if (!ReleaseVersion.TryParse(versionText, out var version))
            return Fail($"{DesktopFramework} 的版本不是三段的正式版：{versionText}", out error);
        runtime = new ReleaseRuntime(DesktopFramework, version);
        error = null;
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
