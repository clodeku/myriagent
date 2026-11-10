using Myriagent.Agents;
using Myriagent.Git;

namespace Myriagent.Hub;

internal sealed partial class AgentHub
{
    private IGitTurnReader? gitTurnReader;
    internal void AttachGitTurnReader(IGitTurnReader reader) => gitTurnReader = reader;

    private sealed class TurnDiffState
    {
        public string? TurnId;
        public GitTurnSnapshot? Before;
        public string? BaselineNote;
        public List<TurnFileDiff> Native = [];
        public AgentChatItem? Card;
        public bool Completed;
    }

    private sealed partial class ConversationState
    {
        public TurnDiffState? TurnDiff;
        public Task? TurnDiffFinishing;
    }

    private async Task BeginTurnDiffAsync(ConversationState c, string cwd)
    {
        if (c.Meta.Provider != AgentProvider.Codex) return;
        if (c.TurnDiffFinishing != null) await c.TurnDiffFinishing;
        var diff = new TurnDiffState();
        c.TurnDiff = diff;
        if (gitTurnReader == null) return;
        try { diff.Before = await gitTurnReader.CaptureAsync(cwd, doctorCancel.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { diff.BaselineNote = "未取得 Git 基準，僅顯示 CLI 回報的差異。"; }
    }

    private void UpdateTurnDiff(ConversationState c, AgentTurnDiffEvent change)
    {
        if (c.TurnDiff == null || c.TurnDiff.Completed || c.TurnDiff.TurnId != null && c.TurnDiff.TurnId != change.TurnId)
            c.TurnDiff = new TurnDiffState();
        var state = c.TurnDiff;
        state.TurnId = change.TurnId;
        state.Native = TurnDiffRules.Parse(change.Diff.Length > 2_000_000 ? change.Diff[..2_000_000] : change.Diff);
        RenderTurnDiff(c, state, state.Native, "CLI 回報的本輪修改；結束後再核對 Git。" + state.BaselineNote);
    }

    private void RenderTurnDiff(ConversationState c, TurnDiffState state, IReadOnlyList<TurnFileDiff> files, string note)
    {
        if (files.Count == 0 && state.Card == null) return;
        if (state.Card == null)
        {
            state.Card = new AgentChatItem { Kind = AgentItemKind.Tool, ToolKind = "edit", ToolName = "整輪變更彙整" };
            AddItem(c, state.Card);
        }
        state.Card.Title = $"本輪變更 · {files.Count} 個檔案";
        state.Card.Detail = note + "\n\n" + string.Join("\n", files.Take(TurnDiffRules.MaxFiles).Select(f => f.Path));
        state.Card.Diff = TurnDiffRules.Display(files);
        state.Card.Status = state.Completed ? AgentItemStatus.Done : AgentItemStatus.Running;
        MarkItemDirty(c, state.Card);
    }

    private void FinishTurnDiff(ConversationState c)
    {
        if (c.TurnDiff is not { Completed: false } state) return;
        state.Completed = true;
        RenderTurnDiff(c, state, state.Native, state.Before != null ? "正在核對 Git…" : state.BaselineNote ?? "本輪由 CLI 回報的差異；沒有可用的 Git 開始前快照。");
        if (state.Before != null && gitTurnReader != null) c.TurnDiffFinishing = CompareTurnDiffAsync(c, state, gitTurnReader);
    }

    private async Task CompareTurnDiffAsync(ConversationState c, TurnDiffState state, IGitTurnReader reader)
    {
        GitTurnChanges result;
        try { result = await reader.CompareAsync(state.Before!, doctorCancel.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { result = new([], "Git 核對失敗，保留 CLI 回報的差異。"); }
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c) || !c.ItemsLoaded
            || state.Card != null && !c.Items.Contains(state.Card)) return;
        // Git 涵蓋的檔案用前後比對結果；CLI 另回報的忽略檔或 repo 外修改仍保留。
        var files = result.Files.ToList();
        foreach (var native in state.Native)
        {
            var path = native.Path;
            if (Path.IsPathFullyQualified(path)) path = Path.GetRelativePath(state.Before!.Root, path).Replace('\\', '/');
            if (result.ComparedPaths?.Contains(path) != true && !files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) files.Add(native);
        }
        RenderTurnDiff(c, state, files, result.Note);
        SaveConversation(c, saveItems: true);
    }
}
