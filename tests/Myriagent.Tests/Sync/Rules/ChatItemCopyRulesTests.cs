using System.Collections;
using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 給引擎的淺快照（WP4.3 計畫 D1）：每個屬性都複製；容器與可變元素是新的實例（UI 之後改原物件不影響快照）；字串共用同一個參照
/// </summary>
public sealed class ChatItemCopyRulesTests
{
    /// <summary>
    /// 每個型別可寫的公開屬性數（新增屬性先讓這個測試失敗，再決定快照要不要帶它）。<c>AgentChatItem.Source</c> 快照照樣複製
    /// （快照是項目的完整副本），不上線由投影負責（MaskItem 不複製、TryItem 不輸出，計畫 D34）
    /// </summary>
    [Theory]
    [InlineData(typeof(AgentChatItem), 35)]
    [InlineData(typeof(AgentAttachment), 9)]
    [InlineData(typeof(AgentQuestion), 7)]
    [InlineData(typeof(AgentQuestionOption), 3)]
    [InlineData(typeof(AgentPlanStep), 2)]
    [InlineData(typeof(AgentMediaRef), 3)]
    [InlineData(typeof(AgentConversation), 36)]
    public void 屬性數釘住(Type type, int count)
        => PublishTestItems.Properties(type).Should().HaveCount(count, type.Name);

    [Fact]
    public void 每個可寫的公開屬性都複製()
    {
        var item = PublishTestItems.Full();
        var fresh = new AgentChatItem();
        foreach (var property in PublishTestItems.Properties(typeof(AgentChatItem)))
            IsDefaultLike(property.GetValue(item), property.GetValue(fresh)).Should().BeFalse($"樣本的 {property.Name} 要不是預設值，才驗得出有沒有複製");

        var copy = ChatItemCopyRules.Copy(item);

        copy.Should().NotBeSameAs(item);
        copy.Should().BeEquivalentTo(item, options => options.WithStrictOrdering());
        foreach (var property in PublishTestItems.Properties(typeof(AgentChatItem)))
            property.GetValue(copy).Should().BeEquivalentTo(property.GetValue(item), options => options.WithStrictOrdering().RespectingRuntimeTypes(), property.Name);
    }

    [Fact]
    public void 清單字典與可變元素都是新的實例()
    {
        var item = PublishTestItems.Full();

        var copy = ChatItemCopyRules.Copy(item);

        copy.Diff.Should().NotBeSameAs(item.Diff);
        copy.Diff![0].Should().NotBeSameAs(item.Diff![0]);
        copy.Attachments.Should().NotBeSameAs(item.Attachments);
        copy.Attachments[0].Should().NotBeSameAs(item.Attachments[0]);
        copy.Attachments[0].ModelImagePaths.Should().NotBeSameAs(item.Attachments[0].ModelImagePaths);
        copy.Attachments[0].FrameSeconds.Should().NotBeSameAs(item.Attachments[0].FrameSeconds);
        copy.Questions.Should().NotBeSameAs(item.Questions);
        copy.Questions[0].Should().NotBeSameAs(item.Questions[0]);
        copy.Questions[0].Options.Should().NotBeSameAs(item.Questions[0].Options);
        copy.Questions[0].Options[0].Should().NotBeSameAs(item.Questions[0].Options[0]);
        copy.Answers.Should().NotBeSameAs(item.Answers);
        copy.Answers["q1"].Should().NotBeSameAs(item.Answers["q1"]);
        copy.MediaPaths.Should().NotBeSameAs(item.MediaPaths);
        copy.PlanSteps.Should().NotBeSameAs(item.PlanSteps);
        copy.PlanSteps[0].Should().NotBeSameAs(item.PlanSteps[0]);
        copy.MediaRefs.Should().NotBeSameAs(item.MediaRefs);
        copy.MediaRefs![0].Should().NotBeSameAs(item.MediaRefs![0]);
        copy.KeptMedia.Should().NotBeSameAs(item.KeptMedia).And.Equal(item.KeptMedia);
    }

    [Fact]
    public void 改原本的PlanStep狀態或Answers內層_複本不變()
    {
        var item = PublishTestItems.Full();
        var copy = ChatItemCopyRules.Copy(item);

        item.PlanSteps[0].Status = "completed";
        item.PlanSteps.Add(new AgentPlanStep { Text = "新的" });
        item.Answers["q1"].Add("丙");
        item.Answers["q2"] = ["丁"];
        item.Questions[0].IsSecret = false;
        item.Questions[0].Options[0].Label = "改";
        item.Attachments[0].ModelImagePaths.Add(@"C:\x.png");
        item.MediaRefs![0].Ref = "changed";
        item.Diff!.Add(new AgentDiffLine("del", "x"));
        item.MediaPaths.Clear();

        copy.Should().BeEquivalentTo(PublishTestItems.Full(), options => options.WithStrictOrdering(), "快照停在複製的那一刻");
    }

    [Fact]
    public void 字串是同一個參照()
    {
        var item = PublishTestItems.Full();
        item.Text = new string('長', 100_000);
        item.Output = new string('出', 50_000);

        var copy = ChatItemCopyRules.Copy(item);

        foreach (var property in PublishTestItems.Properties(typeof(AgentChatItem)).Where(p => p.PropertyType == typeof(string)))
            ReferenceEquals(property.GetValue(copy), property.GetValue(item)).Should().BeTrue($"{property.Name}：不複製文字");
        ReferenceEquals(copy.Answers["q1"][0], item.Answers["q1"][0]).Should().BeTrue();
        ReferenceEquals(copy.Questions[0].Text, item.Questions[0].Text).Should().BeTrue();
        ReferenceEquals(copy.Attachments[0].StoredPath, item.Attachments[0].StoredPath).Should().BeTrue();
    }

    [Fact]
    public void 清單是null或含null_照樣複製()
    {
        var item = PublishTestItems.Full();
        item.Diff = null;
        item.MediaRefs = null;
        item.Attachments = [null!, item.Attachments[0]];
        item.PlanSteps = [null!];
        item.Answers["empty"] = null!;
        item.MediaPaths = null!;

        var copy = ChatItemCopyRules.Copy(item);

        copy.Diff.Should().BeNull();
        copy.MediaRefs.Should().BeNull();
        copy.MediaPaths.Should().BeNull();
        copy.Attachments.Should().HaveCount(2);
        copy.Attachments[0].Should().BeNull();
        copy.Attachments[1].Should().BeEquivalentTo(item.Attachments[1]);
        copy.PlanSteps.Should().ContainSingle().Which.Should().BeNull();
        copy.Answers.Should().ContainKey("empty").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void Answers保留原本的比較方式()
    {
        var item = PublishTestItems.Full();
        item.Answers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["Q1"] = ["甲"] };

        ChatItemCopyRules.Copy(item).Answers.Comparer.Should().BeSameAs(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyMeta同上()
    {
        var meta = new AgentConversation
        {
            Id = "0123456789abcdef0123456789abcdef",
            Title = "標題",
            AccountId = "acc",
            Provider = AgentProvider.Codex,
            SessionId = "s1",
            SessionStarted = true,
            SessionAccountId = "acc0",
            Model = "m",
            Effort = "high",
            Agent = "reviewer",
            OutputStyle = "Explanatory",
            CodexServiceTier = "priority",
            CodexMode = "plan",
            CodexSkills = [new("test", "C:/skills/test/SKILL.md")],
            ResumeAt = "7e7e44d7-c310-428c-8c7b-6074ae338563",
            QuotaWait = new QuotaWait("acc", new DateTimeOffset(2026, 10, 26, 15, 1, 0, TimeSpan.FromHours(8))),
            PermissionMode = AgentPermissionMode.FullAccess,
            WorkingDirectory = @"C:\work",
            LastCliVersion = "1.2.3",
            RemoteControlSessionId = "rc",
            DelegatedAccountIds = ["acc-codex"],
            ParentConversationId = "fedcba9876543210fedcba9876543211",
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
            UpdatedAt = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Local),
            SyncPolicy = "metadata",
            OwnerDeviceId = "dev",
            LeaseEpoch = 3,
            NextOrd = 129,
            WorkingDirectoryRef = new PathRef(PathRefKinds.Git, "git:github.com/a/b", "src", "b"),
            ForkedFrom = "fedcba9876543210fedcba9876543210",
            ForkedFromTitle = "原標題",
            ForkPointSeq = 81234,
            ForkSourceTranscript = "C:/data/fork-source/s1.jsonl",
            PendingHandoffNote = "換機說明",
            QuotaHome = new QuotaHomeAccount("acc0", new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.FromHours(8)), new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(8)),
                "opus", "max", AgentPermissionMode.AcceptEdits, null, null, "sonnet", "high"),
            DeferredAccount = new DeferredAccountSwitch("acc2", RememberAccount: true),
        };
        var fresh = new AgentConversation();
        foreach (var property in PublishTestItems.Properties(typeof(AgentConversation)))
            IsDefaultLike(property.GetValue(meta), property.GetValue(fresh)).Should().BeFalse($"樣本的 {property.Name} 要不是預設值");

        var copy = ChatItemCopyRules.CopyMeta(meta);

        copy.Should().NotBeSameAs(meta);
        foreach (var property in PublishTestItems.Properties(typeof(AgentConversation)))
        {
            if (property.GetValue(meta) is ICollection list)
            {
                // 容器（Codex 的技能、帳戶委派的選取清單）：內容相同、實例是新的
                property.GetValue(copy).Should().BeEquivalentTo(list, property.Name).And.NotBeSameAs(list);
                continue;
            }
            property.GetValue(copy).Should().Be(property.GetValue(meta), property.Name);
            if (property.PropertyType == typeof(string))
                ReferenceEquals(property.GetValue(copy), property.GetValue(meta)).Should().BeTrue($"{property.Name}：字串共用");
        }

        meta.Title = "改了";
        meta.DelegatedAccountIds.Add("acc-other");
        copy.Title.Should().Be("標題");
        meta.CodexSkills.Clear();
        copy.CodexSkills.Should().ContainSingle("快照與原始清單不能共用可變的集合");
        copy.DelegatedAccountIds.Should().Equal("acc-codex");
    }

    /// <summary>null、空字串、空的清單或字典、與新實例相同的值都算預設</summary>
    private static bool IsDefaultLike(object? value, object? fresh) => value switch
    {
        null => true,
        string s => s.Length == 0 || s == (string?)fresh,
        ICollection collection => collection.Count == 0,
        _ => value.Equals(fresh),
    };
}
