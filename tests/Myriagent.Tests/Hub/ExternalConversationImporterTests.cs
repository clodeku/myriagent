using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 匯入本機外部 CLI 歷史對話（ExternalConversationRules／ExternalConversationImporter／AgentHub.ExternalImport）：
/// 驗證 Claude Code、Codex、Antigravity、Grok 的唯讀掃描、排除子代理與已匯入對話、完整匯入對話紀錄並支援原生續接
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ExternalConversationImporterTests
{
    [Fact]
    public void 掃描四家CLI歷史對話_排除子代理與已匯入工作階段_並解析工具與回覆()
    {
        using var temp = new TempDirectory("ext-import");
        var workDir = temp.CreateDirectory("workspace-demo");

        // 1. Claude Code 主對話與應被排除的 agent-* 子代理檔
        var claudeConfig = temp.CreateDirectory("claude-home");
        var claudeProj = Path.Combine(claudeConfig, "projects", "C--workspace-demo");
        Directory.CreateDirectory(claudeProj);
        var claudeSessionId = "11111111-2222-3333-4444-555555555555";
        File.WriteAllLines(Path.Combine(claudeProj, $"{claudeSessionId}.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                type = "user",
                sessionId = claudeSessionId,
                cwd = workDir,
                timestamp = "2026-10-24T01:00:00Z",
                message = new { role = "user", content = "幫我檢查 API 路由" },
            }),
            JsonConvert.SerializeObject(new
            {
                type = "assistant",
                sessionId = claudeSessionId,
                cwd = workDir,
                timestamp = "2026-10-24T01:00:05Z",
                message = new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "thinking", thinking = "先讀取路由定義檔" },
                        new { type = "tool_use", id = "tu_1", name = "Bash", input = new { command = "dotnet test", description = "執行測試" } },
                        new { type = "text", text = "測試已通過，路由正常。" },
                    },
                },
            }),
            JsonConvert.SerializeObject(new
            {
                type = "user",
                sessionId = claudeSessionId,
                timestamp = "2026-10-24T01:00:06Z",
                message = new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "tool_result", tool_use_id = "tu_1", content = "Passed! 42 tests" },
                    },
                },
            }),
        });
        File.WriteAllLines(Path.Combine(claudeProj, "agent-sub123.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new { type = "user", sessionId = "agent-sub123", message = new { role = "user", content = "子代理任務" } }),
        });

        // 2. Codex 主對話 rollout 與應排除的 subagent rollout
        var codexHome = temp.CreateDirectory("codex-home");
        var codexDay = Path.Combine(codexHome, "sessions", "2026", "10", "24");
        Directory.CreateDirectory(codexDay);
        var codexThreadId = "019a0000-1111-7222-8333-444444444444";
        File.WriteAllLines(Path.Combine(codexDay, $"rollout-2026-10-24T09-10-00-{codexThreadId}.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T01:10:00Z",
                type = "session_meta",
                payload = new { id = codexThreadId, cwd = workDir, source = "cli" },
            }),
            JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T01:10:01Z",
                type = "response_item",
                payload = new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = "<environment_context>\n<cwd>" + workDir + "</cwd>\n</environment_context>" },
                        new { type = "input_text", text = "重構快取模組" },
                    },
                },
            }),
            JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T01:10:05Z",
                type = "response_item",
                payload = new
                {
                    type = "message",
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "output_text", text = "已完成快取模組重構。" },
                    },
                },
            }),
        });

        var codexSubThreadId = "019a0000-9999-7222-8333-999999999999";
        File.WriteAllLines(Path.Combine(codexDay, $"rollout-2026-10-24T09-11-00-{codexSubThreadId}.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T01:11:00Z",
                type = "session_meta",
                payload = new { id = codexSubThreadId, cwd = workDir, source = new { subagent = "review" } },
            }),
            JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T01:11:01Z",
                type = "response_item",
                payload = new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "子代理審查" } } },
            }),
        });

        // 3. Antigravity 主對話 brain 與其派生的子代理 brain
        var agyHome = temp.CreateDirectory("agy-home");
        var agyMainId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var agySubId = "ffffffff-0000-1111-2222-333333333333";
        var agyMainLogs = Path.Combine(agyHome, "brain", agyMainId, ".system_generated", "logs");
        var agySubLogs = Path.Combine(agyHome, "brain", agySubId, ".system_generated", "logs");
        Directory.CreateDirectory(agyMainLogs);
        Directory.CreateDirectory(agySubLogs);

        File.WriteAllLines(Path.Combine(agyMainLogs, "transcript_full.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-10-24T01:20:00Z",
                content = $"<user_information>\nCommand Working Directory: {workDir}\n</user_information>\n<USER_REQUEST>\n分析系統效能瓶頸\n</USER_REQUEST>",
            }),
            JsonConvert.SerializeObject(new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                created_at = "2026-10-24T01:20:05Z",
                thinking = "委派研究子代理檢查資料庫查詢",
                tool_calls = new object[]
                {
                    new { name = "invoke_subagent", args = new { TypeName = "research", Role = "DB Researcher", Prompt = "查慢查詢" } },
                },
                content = "已完成效能瓶頸分析報告。",
            }),
            JsonConvert.SerializeObject(new
            {
                step_index = 2,
                source = "SYSTEM",
                type = "TOOL_RESPONSE",
                created_at = "2026-10-24T01:20:06Z",
                content = JsonConvert.SerializeObject(new { spawnStepIndex = 1, result = new { conversationId = agySubId } }),
            }),
            JsonConvert.SerializeObject(new
            {
                step_index = 3,
                source = "SYSTEM",
                type = "USER_INPUT",
                created_at = "2026-10-24T01:20:10Z",
                content = "<SYSTEM_MESSAGE>background wakeup</SYSTEM_MESSAGE>",
            }),
        });

        File.WriteAllLines(Path.Combine(agySubLogs, "transcript_full.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-10-24T01:20:06Z",
                content = "<USER_REQUEST>查慢查詢</USER_REQUEST>",
            }),
        });

        // 4. Grok 明文 JSON 對話檔
        var grokHome = temp.CreateDirectory("grok-home");
        var grokSessions = Path.Combine(grokHome, "sessions");
        Directory.CreateDirectory(grokSessions);
        File.WriteAllText(Path.Combine(grokSessions, "grok-sess-1.json"), JsonConvert.SerializeObject(new
        {
            sessionId = "grok-sess-1",
            cwd = workDir,
            title = "Grok 終端機對話",
            updatedAt = "2026-10-24T01:30:00Z",
            messages = new object[]
            {
                new { role = "user", content = "你好 Grok" },
                new { role = "assistant", content = "你好！需要什麼協助？" },
            },
        }));

        var targets = new[]
        {
            new ExternalAccountScanTarget("acc-claude", "Claude（本機）", AgentProvider.Claude, claudeConfig),
            new ExternalAccountScanTarget("acc-codex", "Codex（本機）", AgentProvider.Codex, codexHome),
            new ExternalAccountScanTarget("acc-agy", "Antigravity（本機）", AgentProvider.Antigravity, agyHome),
            new ExternalAccountScanTarget("acc-grok", "Grok（本機）", AgentProvider.Grok, grokHome),
        };

        var scanned = ExternalConversationImporter.Scan(targets, Array.Empty<(AgentProvider, string)>());
        scanned.Select(c => (c.Provider, c.SessionId)).Should().BeEquivalentTo(new[]
        {
            (AgentProvider.Grok, "grok-sess-1"),
            (AgentProvider.Antigravity, agyMainId),
            (AgentProvider.Codex, codexThreadId),
            (AgentProvider.Claude, claudeSessionId),
        }, "四家主對話都應掃出，且排除 Claude agent-*、Codex subagent 與 Antigravity invoke_subagent 子代理");

        var agyCand = scanned.Single(c => c.Provider == AgentProvider.Antigravity);
        agyCand.Title.Should().Be("分析系統效能瓶頸");
        agyCand.WorkingDirectory.Should().Be(workDir);
        agyCand.MessageCount.Should().Be(2, "<SYSTEM_MESSAGE> 喚醒不計入使用者訊息");

        var claudeLoaded = ExternalConversationImporter.Load(scanned.Single(c => c.Provider == AgentProvider.Claude));
        claudeLoaded.Should().NotBeNull();
        claudeLoaded!.Items.Select(i => i.Kind).Should().Equal(
            AgentItemKind.User,
            AgentItemKind.Thinking,
            AgentItemKind.Tool,
            AgentItemKind.Assistant);
        claudeLoaded.Items.Single(i => i.Kind == AgentItemKind.Tool).Output.Should().Contain("Passed! 42 tests");

        // 已存在於 Myriagent 的工作階段應自動排除
        var filtered = ExternalConversationImporter.Scan(targets, new[] { (AgentProvider.Claude, claudeSessionId) });
        filtered.Should().NotContain(c => c.SessionId == claudeSessionId);
    }

    [Fact]
    public async Task AgentHub列出並匯入外部對話_立即開啟存檔且後續送出可直接續接()
    {
        using var h = new HubHarness();
        using var extTemp = new TempDirectory("ext-import-hub");
        var workDir = extTemp.CreateDirectory("my-project");
        var claudeConfig = extTemp.CreateDirectory("claude-ext");
        var projDir = Path.Combine(claudeConfig, "projects", "C--my-project");
        Directory.CreateDirectory(projDir);

        var sessionId = "99999999-8888-7777-6666-555555555555";
        File.WriteAllLines(Path.Combine(projDir, $"{sessionId}.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                type = "user",
                sessionId,
                cwd = workDir,
                timestamp = "2026-10-24T02:00:00Z",
                message = new { role = "user", content = "終端機裡先問過的第一題" },
            }),
            JsonConvert.SerializeObject(new
            {
                type = "assistant",
                sessionId,
                cwd = workDir,
                timestamp = "2026-10-24T02:00:03Z",
                message = new
                {
                    role = "assistant",
                    content = new object[] { new { type = "text", text = "這是終端機裡的第一則回答。" } },
                },
            }),
        });

        h.Ui.Invoke(() => h.Hub.ExternalScanAccountsOverride = () => new[]
        {
            new ExternalAccountScanTarget(h.Claude.Id, h.Claude.Label, AgentProvider.Claude, claudeConfig),
        });

        var markList = h.Main.Mark();
        h.Command(h.Main, "listExternalConversations", new { requestId = "ext1" });

        var listMsg = await h.Main.WaitForAsync("externalConversations", m => m.Value<string>("requestId") == "ext1", markList, timeoutMs: 5000);
        var items = (JArray)listMsg["items"]!;
        items.Should().HaveCount(1);
        var sourceKey = items[0]!.Value<string>("sourceKey")!;
        items[0]!.Value<string>("title").Should().Be("終端機裡先問過的第一題");

        var markImport = h.Main.Mark();
        h.Command(h.Main, "importExternalConversation", new { sourceKey, paneId = "p1" });

        var convMsg = await h.Main.WaitForAsync("conversation", m => m["header"]?.Value<string>("title") == "終端機裡先問過的第一題", markImport, timeoutMs: 5000);
        var importedConvId = convMsg.Value<string>("conversationId")!;
        convMsg["header"]!.Value<bool>("isDraft").Should().BeFalse();
        ((JArray)convMsg["items"]!).Should().HaveCount(2);

        var savedMeta = h.Conversation(importedConvId);
        savedMeta.Should().NotBeNull();
        savedMeta!.SessionId.Should().Be(sessionId);
        savedMeta.SessionStarted.Should().BeTrue();
        savedMeta.WorkingDirectory.Should().Be(workDir);

        // 在匯入後的對話直接送出下一則訊息：應以 SessionStarted=true 原生續接，不重複貼前文節錄
        h.Send(h.Main, importedConvId, "接著問第二題");
        await h.WaitUntilAsync(() => h.Sessions.Count == 1 && h.Sessions[0].Sent.Count == 1, "建立會話並送出第二題");
        var sent = h.Sessions[0].Sent.All.Should().ContainSingle().Subject;
        sent.Input.Text.Should().Be("接著問第二題");
        sent.Settings.SessionId.Should().Be(sessionId);
        sent.Settings.SessionStarted.Should().BeTrue();
    }

    [Fact]
    public async Task 真實Grok資料夾結構_Codex標題索引_Antigravity子代理標記_超大檔首尾保留_與已配對同步Ord發布()
    {
        using var temp = new TempDirectory("ext-import-real");
        var workDir = temp.CreateDirectory("grok-repo");

        // 1. 真實 Grok CLI 工作階段目錄：sessions/<encoded-cwd>/<uuid>/chat_history.jsonl + summary.json + prompt_context.json
        var grokHome = temp.CreateDirectory("grok-real-home");
        var grokUuid = "22222222-3333-4444-5555-666666666666";
        var grokDir = Path.Combine(grokHome, "sessions", "C--grok-repo", grokUuid);
        Directory.CreateDirectory(grokDir);
        File.WriteAllText(Path.Combine(grokDir, "summary.json"), JsonConvert.SerializeObject(new
        {
            session_id = grokUuid,
            title = "Grok 資料庫索引最佳化",
            updated_at = "2026-10-24T03:00:00Z",
        }));
        File.WriteAllText(Path.Combine(grokDir, "prompt_context.json"), JsonConvert.SerializeObject(new
        {
            working_directory = workDir,
        }));
        File.WriteAllLines(Path.Combine(grokDir, "chat_history.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new { role = "user", content = "請檢查查詢索引" }),
            JsonConvert.SerializeObject(new
            {
                role = "assistant",
                reasoning_summary = "先用 grep 找索引宣告",
                tool_calls = new object[]
                {
                    new { id = "call_g1", function = new { name = "grep", arguments = JsonConvert.SerializeObject(new { pattern = "CREATE INDEX" }) } },
                },
                content = "已新增複合索引。",
            }),
            JsonConvert.SerializeObject(new { role = "tool", tool_call_id = "call_g1", content = "schema.sql:12: CREATE INDEX idx_a" }),
        });
        // 同層級的 prompt_history.jsonl 不應被當成獨立對話
        File.WriteAllLines(Path.Combine(grokHome, "sessions", "C--grok-repo", "prompt_history.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new { prompt = "請檢查查詢索引" }),
        });

        // 2. Antigravity 帶 <subagent_reminder> 的獨立子代理 brain（即使父對話不在場也應排除）
        var agyHome = temp.CreateDirectory("agy-real-home");
        var subOnlyId = "77777777-8888-9999-aaaa-bbbbbbbbbbbb";
        var subOnlyLogs = Path.Combine(agyHome, "brain", subOnlyId, ".system_generated", "logs");
        Directory.CreateDirectory(subOnlyLogs);
        File.WriteAllLines(Path.Combine(subOnlyLogs, "transcript.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                content = "<subagent_reminder>You are a research subagent.</subagent_reminder>\n調查任務",
            }),
        });

        // 3. Codex session_index.jsonl 對應標題＋超過 8 MB 超大 rollout 檔保留開頭 session_meta 與結尾最新對話
        var codexHome = temp.CreateDirectory("codex-real-home");
        var codexThreadId = "019b0000-1111-7222-8333-555555555555";
        var codexParentSubId = "019b0000-1111-7222-8333-666666666666";
        File.WriteAllLines(Path.Combine(codexHome, "session_index.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new { id = codexThreadId, thread_name = "超大專案重構紀錄" }),
        });
        var codexDay = Path.Combine(codexHome, "sessions", "2026", "10", "24");
        Directory.CreateDirectory(codexDay);
        var bigRollout = Path.Combine(codexDay, $"rollout-2026-10-24T10-00-00-{codexThreadId}.jsonl");
        using (var writer = new StreamWriter(bigRollout, append: false, new System.Text.UTF8Encoding(false)))
        {
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T02:00:00Z",
                type = "session_meta",
                payload = new { id = codexThreadId, cwd = workDir, source = "cli" },
            }));
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T02:00:01Z",
                type = "response_item",
                payload = new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "最初的第一則問題" } } },
            }));
            var filler = new string('x', 64 * 1024);
            for (int i = 0; i < 135; i++)
            {
                writer.WriteLine(JsonConvert.SerializeObject(new
                {
                    timestamp = "2026-10-24T02:05:00Z",
                    type = "response_item",
                    payload = new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text = filler } } },
                }));
            }
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T02:59:00Z",
                type = "response_item",
                payload = new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "超過8MB尾端最後一則問題" } } },
            }));
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T02:59:05Z",
                type = "response_item",
                payload = new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text = "超過8MB尾端最後一則回覆" } } },
            }));
        }
        new FileInfo(bigRollout).Length.Should().BeGreaterThan(8 * 1024 * 1024);

        // 帶 parent_thread_id 的 Codex 子執行緒應排除
        File.WriteAllLines(Path.Combine(codexDay, $"rollout-2026-10-24T10-01-00-{codexParentSubId}.jsonl"), new[]
        {
            JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T02:10:00Z",
                type = "session_meta",
                payload = new { id = codexParentSubId, parent_thread_id = codexThreadId, cwd = workDir, source = "cli" },
            }),
            JsonConvert.SerializeObject(new
            {
                timestamp = "2026-10-24T02:10:01Z",
                type = "response_item",
                payload = new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "子執行緒" } } },
            }),
        });

        var targets = new[]
        {
            new ExternalAccountScanTarget("acc-grok", "Grok", AgentProvider.Grok, grokHome),
            new ExternalAccountScanTarget("acc-agy", "Antigravity", AgentProvider.Antigravity, agyHome),
            new ExternalAccountScanTarget("acc-codex", "Codex", AgentProvider.Codex, codexHome),
        };
        var scanned = ExternalConversationImporter.Scan(targets, Array.Empty<(AgentProvider, string)>());
        scanned.Select(c => (c.Provider, c.SessionId)).Should().BeEquivalentTo(new[]
        {
            (AgentProvider.Grok, grokUuid),
            (AgentProvider.Codex, codexThreadId),
        });

        var grokCand = scanned.Single(c => c.Provider == AgentProvider.Grok);
        grokCand.Title.Should().Be("Grok 資料庫索引最佳化");
        grokCand.WorkingDirectory.Should().Be(workDir);
        var grokLoaded = ExternalConversationImporter.Load(grokCand)!;
        grokLoaded.Items.Select(i => i.Kind).Should().Equal(
            AgentItemKind.User,
            AgentItemKind.Thinking,
            AgentItemKind.Tool,
            AgentItemKind.Assistant);
        grokLoaded.Items.Single(i => i.Kind == AgentItemKind.Tool).Output.Should().Contain("CREATE INDEX idx_a");

        var codexCand = scanned.Single(c => c.Provider == AgentProvider.Codex);
        codexCand.Title.Should().Be("超大專案重構紀錄");
        codexCand.WorkingDirectory.Should().Be(workDir);
        var codexLoaded = ExternalConversationImporter.Load(codexCand)!;
        codexLoaded.Items.Last().Text.Should().Be("超過8MB尾端最後一則回覆");

        // 4. 驗證已配對同步掛鉤（IConversationChangeSink）下匯入外部對話時，每筆項目皆配發遞增 Ord 並觸發 OnItemChanged
        using var h = new HubHarness();
        var itemHooks = new List<(string ConvId, string ItemId, long Ord)>();
        var sink = new SyncOrdProbeSink((conv, item) => itemHooks.Add((conv.Meta.Id, item.Id, item.Ord ?? 0)));
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachConversationSink(sink);
            h.Hub.ExternalScanAccountsOverride = () => new[]
            {
                new ExternalAccountScanTarget(h.Codex.Id, h.Codex.Label, AgentProvider.Codex, codexHome),
            };
        });

        var markList = h.Main.Mark();
        h.Command(h.Main, "listExternalConversations", new { requestId = "ext-sync" });
        var listMsg = await h.Main.WaitForAsync("externalConversations", m => m.Value<string>("requestId") == "ext-sync", markList, timeoutMs: 5000);
        var sourceKey = listMsg["items"]![0]!.Value<string>("sourceKey")!;

        var markImport = h.Main.Mark();
        h.Command(h.Main, "importExternalConversation", new { sourceKey });
        var convMsg = await h.Main.WaitForAsync("conversation", m => m["header"]?.Value<string>("title") == "超大專案重構紀錄", markImport, timeoutMs: 5000);
        var importedId = convMsg.Value<string>("conversationId")!;

        var hooksForConv = itemHooks.Where(x => x.ConvId == importedId).ToList();
        hooksForConv.Should().NotBeEmpty();
        hooksForConv.Select(x => x.Ord).Should().OnlyContain(ord => ord > 0).And.BeInAscendingOrder();
    }

    private sealed class SyncOrdProbeSink(Action<ConversationHookInfo, AgentChatItem> onItem) : IConversationChangeSink
    {
        public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item) => onItem(conversation, item);
        public void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item) { }
        public void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved) { }
        public void OnConversationDeleting(ConversationHookInfo conversation) { }
        public void OnHeaderChanged(ConversationHookInfo conversation) { }
        public void OnRunningCountsChanged() { }
        public void OnQueueChanged(ConversationHookInfo conversation) { }
        public void OnComposerChanged(ConversationHookInfo conversation) { }
        public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli) { }
        public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage) { }
        public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech) { }
    }
}
