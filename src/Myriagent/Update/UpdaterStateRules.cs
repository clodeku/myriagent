using Myriagent.Core;

namespace Myriagent.Update;

internal enum LaunchAction
{
    Proceed,
    RollBack,
}

/// <summary>更新器狀態的純函式規則（設計 6.6、8.5、8.6、8.7）：要不要裝、閃退保護、健康閘門失敗、換版、清理</summary>
internal static class UpdaterStateRules
{
    public const int LaunchFailureThreshold = 3;
    public const int GateFailuresBeforeBad = 2;
    public const int KeepVersions = 3;

    public static bool ShouldInstall(UpdaterState state, ReleaseIdentity candidate, ReleaseIdentity current)
        => candidate.Version > current.Version && !state.Bad.Contains(candidate.Release, StringComparer.Ordinal);

    /// <summary>
    /// Program.Main 最前面：這一版已經連續 3 次啟動後沒到健康就結束，而且有前一版 → 切回前一版（這版標成壞版）；
    /// 否則把次數加一照常啟動（到健康時 OnHealthy 歸零）
    /// </summary>
    public static (UpdaterState State, LaunchAction Action) OnLaunch(UpdaterState state, string release)
    {
        state.LaunchFailures.TryGetValue(release, out var failures);
        if (failures >= LaunchFailureThreshold && state.Current == release && state.Previous is { } previous && previous != release)
        {
            state.LaunchFailures.Remove(release);
            if (!state.Bad.Contains(release))
                state.Bad.Add(release);
            state.Current = previous;
            state.Previous = null;
            state.Staged = null;
            state.LastResult = $"{VersionOf(release)} 連續啟動失敗，已退回 {VersionOf(previous)}";
            state.LastResultAt = DateTime.Now;
            return (state, LaunchAction.RollBack);
        }
        state.LaunchFailures[release] = failures + 1;
        return (state, LaunchAction.Proceed);
    }

    public static UpdaterState OnHealthy(UpdaterState state, string release)
    {
        state.LaunchFailures.Remove(release);
        state.GateFailures.Remove(release);
        return state;
    }

    /// <summary>健康閘門失敗、已切回 restored：第一次只作廢這次的備妥（下次檢查會再試一次），第二次才列壞版</summary>
    public static UpdaterState OnGateFailed(UpdaterState state, string failed, string restored)
    {
        state.GateFailures.TryGetValue(failed, out var count);
        count++;
        state.GateFailures[failed] = count;
        if (count >= GateFailuresBeforeBad && !state.Bad.Contains(failed))
            state.Bad.Add(failed);
        state.LaunchFailures.Remove(failed);
        state.Current = restored;
        state.Staged = null;
        state.LastResult = $"新版 {VersionOf(failed)} 啟動失敗，已退回 {VersionOf(restored)}";
        state.LastResultAt = DateTime.Now;
        return state;
    }

    /// <summary>
    /// 換版完成：前一版改成 from。from 與 to 相同（current 早就指向 to，例如換版的空窗中別的實例的幫手已經換過）時前一版不動：
    /// Previous==Current 會讓閃退保護沒有版本可以退回
    /// </summary>
    public static UpdaterState OnSwitched(UpdaterState state, string from, string to)
    {
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            state.Previous = from;
        state.Current = to;
        state.Staged = null;
        state.LastResult = $"已更新到 {VersionOf(to)}";
        state.LastResultAt = DateTime.Now;
        return state;
    }

    public static UpdaterState OnStaged(UpdaterState state, string release)
    {
        state.Staged = release;
        return state;
    }

    /// <summary>
    /// 可以刪的版本：保留目前版、前一版、已備妥的、正在執行的，再加上最新的幾版湊滿 KeepVersions；名稱不是 release 的不動。
    /// 壞版不占湊數的名額（不會再裝、也不會切回它）：閃退保護退回之後沒有前一版，名額要留給較舊的好版本
    /// </summary>
    public static IReadOnlyList<string> VersionsToDelete(UpdaterState state, IReadOnlyList<string> installed, string? running)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in new[] { state.Current, state.Previous, state.Staged, running })
        {
            if (!string.IsNullOrWhiteSpace(r))
                keep.Add(r);
        }
        var releases = installed
            .Select(name => ReleaseIdentity.TryParseRelease(name, out var id) ? id : null)
            .OfType<ReleaseIdentity>()
            .OrderByDescending(id => id.Version)
            .ToList();
        int keptOlder = releases.Count(id => keep.Contains(id.Release) && id.Release != state.Staged);
        var delete = new List<string>();
        foreach (var id in releases)
        {
            if (keep.Contains(id.Release))
                continue;
            if (keptOlder < KeepVersions && !state.Bad.Contains(id.Release, StringComparer.Ordinal))
            {
                keptOlder++;
                continue;
            }
            delete.Add(id.Release);
        }
        return delete;
    }

    /// <summary>刪除版本資料夾；被占用（例如 --data 的探針實例還從舊版執行）就跳過、下次再刪。回傳真的刪掉的</summary>
    public static IReadOnlyList<string> DeleteVersions(InstallLayout layout, IReadOnlyList<string> releases)
    {
        var deleted = new List<string>();
        foreach (var release in releases)
        {
            var dir = layout.VersionDirectory(release);
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                deleted.Add(release);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪不掉舊版 {release}（可能還有程式在用），下次再試：{ex.Message}");
            }
        }
        return deleted;
    }

    /// <summary>release 名稱（0.2.1-aaaaaa1）的版本部分；不是 release 名稱就原樣回傳（其他地方經 ResumeIntentRules.VersionOfRelease 用）</summary>
    internal static string VersionOf(string release) => ReleaseIdentity.TryParseRelease(release, out var id) ? id!.Version.ToString() : release;
}
