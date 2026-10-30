using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Rendering;

/// <summary>
/// 一組黃金檔的輸入：同一個工作資料夾下依序渲染的項目。
/// ExtraPaths＝另外要查白名單的路徑（例如 Markdown 轉換失敗、轉換時不會被找到的本機圖片）
/// </summary>
internal sealed record GoldenCorpus(string Name, string Description, string WorkingDirectory, IReadOnlyList<AgentChatItem> Items, IReadOnlyList<string> ExtraPaths);

/// <summary>
/// 項目渲染黃金檔的語料（ItemRendererGoldenTests）：涵蓋所有項目種類、狀態、工具種類與渲染分支。
/// 內容取自現有測試資料——介面探針 tools/ui-probe/fixtures.mjs 的「修正登入流程卡住」對話、ClaudeStreamParserTests 的實錄一輪
/// （思考、飲料問卷、寫入 chart.png 的工具卡與核准卡）、AgentMarkdownRendererTests 的連結與安全案例、DiffRulesTests 的差異、
/// AgentHubRenderStoreTests 的巢狀過深 Markdown、docs/web-protocol.md 第五節的 CLI 換版提示——再補上邊界情況。
/// 每次呼叫都建立新的物件；時間一律用 UTC（time 欄位與執行測試的電腦時區無關）。
/// 只用 items.json 表示得出來的狀態（伺服器收到的是 JSON，渲染的是反序列化回來的項目；ItemRendererGoldenTests 也以 ProtocolJson 往返後再渲染一次）。
/// 多行的原始字串（"""）裡的換行就是這個原始檔的換行：.gitattributes 固定以 LF 簽出，改成 CRLF 會讓黃金檔對不上
/// </summary>
internal static class ItemRendererCorpus
{
    /// <summary>介面探針的專案資料夾（fixtures.mjs 的 ROOT），也是這些對話的工作資料夾</summary>
    public const string Root = @"C:\repo\myriagent";

    /// <summary>對話資料夾：附件複製到 attachments、給模型看的縮圖與影片畫面在 attachments\.model</summary>
    private const string Attachments = @"C:\Users\Lin\AppData\Local\Myriagent\conversations\0f3c9a52b7e94d0c8a61f2d4e5b6c7d8\attachments";

    private const string ModelImages = Attachments + @"\.model";

    /// <summary>Markdig 的巢狀深度上限是 128 層，超過就丟 ArgumentException（同 AgentHubRenderStoreTests）</summary>
    private static readonly string TooDeep = new('>', 130);

    public static IReadOnlyList<GoldenCorpus> All() => new[] { Text(), Tools(), Cards(), Media(), Edge(), Grok(), Antigravity() };

    public static GoldenCorpus Get(string name) => All().Single(c => c.Name == name);

    public static IEnumerable<string> Names => All().Select(c => c.Name);

    // ───────────────────────────── 文字類項目與 Markdown ─────────────────────────────

    private static GoldenCorpus Text()
    {
        var b = new CorpusBuilder("text");
        b.Add("user-plain", AgentItemKind.User, text: "帳戶沒有登入時，按「登入」之後畫面就卡住不動。幫我找出原因、修好，並補上單元測試。附上截圖、錄影和記錄檔。");
        b.Add("user-html-chars", AgentItemKind.User, text: "a < b && c > d，\"雙引號\" 與 'single'\r\n第二行（CRLF）\n第三行 <script>alert(1)</script> **不是粗體** &amp; [不是連結](https://example.com)");
        b.Add("user-whitespace", AgentItemKind.User, text: "   ");
        b.Add("thinking", AgentItemKind.Thinking, text: "截圖顯示登入視窗關閉後主畫面仍是「登入中」。先看 `AccountManager.LoginAsync` 怎麼等登入結果，再看視窗關閉時有沒有通知中樞。\n\n錄影裡第二次按登入沒有反應，可能是第一次的等待還沒結束、`loginBusy` 一直是 true。");
        b.Add("thinking-running", AgentItemKind.Thinking, AgentItemStatus.Running, "使用者要我");
        b.Add("assistant-short", AgentItemKind.Assistant, text: "這張圖是純紅色。");
        b.Add("assistant-rich", AgentItemKind.Assistant, text: RichReply);
        b.Add("assistant-local-files", AgentItemKind.Assistant, text: LocalFilesReply);
        b.Add("assistant-unsafe", AgentItemKind.Assistant, text: UnsafeReply);
        b.Add("assistant-cjk-autolinks", AgentItemKind.Assistant, text: CjkReply);
        b.Add("assistant-crlf", AgentItemKind.Assistant, text: "第一行\r\n第二行（軟換行照樣換行）\r\n\r\n新段落：`" + Root + @"\README.md`");
        b.Add("assistant-streaming", AgentItemKind.Assistant, AgentItemStatus.Running, "我會把縮圖產生搬到專用的 STA 背景執行緒：\n\n```cs\nvar queue = new BlockingCollection<Job>();");
        b.Add("assistant-empty", AgentItemKind.Assistant, text: string.Empty);
        b.Add("plan-running", AgentItemKind.Plan, AgentItemStatus.Running, string.Empty, i => i.PlanSteps = Steps(
            ("找出登入卡住的原因", "completed"),
            ("在 LoginAsync 加上逾時與取消", "completed"),
            ("登入視窗關閉時立即重新檢查", "inProgress"),
            ("補上 4 個單元測試", "pending"),
            ("跑完整的 dotnet test", "pending")));
        b.Add("plan-done-explained", AgentItemKind.Plan, AgentItemStatus.Done, "先修 **逾時**，再補測試（Codex turn/plan 的說明）", i => i.PlanSteps = Steps(
            ("修正逾時", "completed"),
            ("補測試", "completed")));
        b.Add("notice", AgentItemKind.Notice, text: "已自動壓縮較早的對話內容，保留最近 80 則");
        b.Add("notice-stopped", AgentItemKind.Notice, text: "已停止這一輪（排隊的訊息暫停，按送出繼續）");
        b.Add("notice-cli-continued", AgentItemKind.Notice, text: "已用 Claude Code 2.1.297 自動接續（中斷時正在執行：npm test，已執行 42 秒）",
            setup: i => i.NoticeKind = AgentNoticeKind.CliUpdate);
        b.Add("notice-cli-not-continued", AgentItemKind.Notice, text: "CLI 已更新到 2.1.297；這一輪沒有自動接續，原因：上一個操作〈git push〉結果未知，請確認後按「繼續」或送出新訊息",
            setup: i => i.NoticeKind = AgentNoticeKind.CliUpdate);
        b.Add("notice-cli-failed", AgentItemKind.Notice, text: "以新版 CLI 重新啟動失敗：找不到 `claude.exe`（系統找不到指定的檔案）；按送出繼續",
            setup: i => i.NoticeKind = AgentNoticeKind.CliUpdate);
        b.Add("error", AgentItemKind.Error, text: "Claude 程序意外結束（結束碼 1）：`ECONNRESET`。已用前文自動重新連線。");
        b.Add("error-quota", AgentItemKind.Error, text: "Claude 訂閱額度已用完，10/15 18:10 重置；可以把這個對話換到其他帳戶繼續");
        b.Add("notice-app-continued", AgentItemKind.Notice, text: "Clode Myriagent 已由 0.2.1523 更新為 0.2.1530；已請代理從中斷處繼續", setup: i => i.NoticeKind = AgentNoticeKind.AppUpdate);
        // 加在最後：前面各項的編號與時間不變
        b.Add("user-remote", AgentItemKind.User, text: "從手機問：報表做好了嗎？\n\n（附 1 張圖片，請在 claude.ai 或 Claude App 查看）",
            setup: i => i.Source = AgentItemSource.Remote);
        b.Add("notice-clear", AgentItemKind.Notice, text: "已清除對話：代理看不到這之前的內容，接下來的訊息從新的工作階段開始。", setup: i => i.NoticeKind = AgentNoticeKind.Clear);
        b.Add("notice-account-switch", AgentItemKind.Notice,
            text: "「Claude 主帳戶」的額度已用完（10/24 06:10 重置），已自動改用「Claude 副帳戶」（2 小時 5 分後重置）繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。",
            setup: i => i.NoticeKind = AgentNoticeKind.AccountSwitch);
        // 第 5 階段（WP5.1 加的種類；WP5.7、WP5.8 寫）：加在最後，前面各項的編號與時間不變
        b.Add("notice-handoff", AgentItemKind.Notice, text: "已從 OFFICE-PC 交接到 HOME-PC：這台以完整的 CLI 紀錄續接，下一則訊息會先附換機說明。",
            setup: i => i.NoticeKind = AgentNoticeKind.Handoff);
        b.Add("notice-fork", AgentItemKind.Notice, text: "離線期間在 OFFICE-PC 的 3 輪另存成這個分支（原對話已在 HOME-PC 被接手）。",
            setup: i => i.NoticeKind = AgentNoticeKind.Fork);
        // Codex 的背景指令跑完、代理接著處理（2026-10-23 加的種類）：加在最後，前面各項的編號與時間不變
        b.Add("notice-background-task", AgentItemKind.Notice, text: "背景指令已結束：`npm test`（結束碼 1，執行了 27 秒），代理接著處理。",
            setup: i => i.NoticeKind = AgentNoticeKind.BackgroundTask);
        // 代理另開的背景程式自己結束了（2026-10-24 加的種類）：加在最後，前面各項的編號與時間不變
        b.Add("notice-background-program", AgentItemKind.Notice, text: "代理另開的程式「python worker6.py」已結束（結束碼 1，執行了 12 分鐘）",
            setup: i => i.NoticeKind = AgentNoticeKind.BackgroundProgram);
        // API 過載排定了自動重試（2026-10-24 加的種類；網頁倒數）：加在最後，前面各項的編號與時間不變
        b.Add("notice-overload-retry", AgentItemKind.Notice, text: "API 伺服器過載（529 Overloaded），60 秒後自動重試（第 1 次，最多 10 次）；在這之前送出訊息就改送你的訊息",
            setup: i => i.NoticeKind = AgentNoticeKind.OverloadRetry);
        // /btw 旁支提問的問與答（2026-10-24 加的種類）：加在最後，前面各項的編號與時間不變
        b.Add("notice-side-question", AgentItemKind.Notice, text: "> 剛才改了哪些檔案？\n\n改了 `ClaudeStreamRules.cs` 與兩個測試檔。",
            setup: i => i.NoticeKind = AgentNoticeKind.SideQuestion);
        return b.Build("text", "文字類項目（使用者〔含從 claude.ai 或 Claude App 送來的〕、思考、回覆、待辦、提示、錯誤）與 Markdown 的各種寫法：表格、程式碼、本機與外部連結、危險連結、中文標點的自動連結", Root);
    }

    /// <summary>介面探針 i12 的回覆（原本是轉好的 HTML，這裡寫回 Markdown 原文）</summary>
    private const string RichReply = """
        ### 找到原因了

        帳戶沒有登入時，`WaitForLoginAsync` 沒有逾時；登入視窗被關掉後，它就一直等下去，`loginBusy` 也不會清掉。我做了三件事：

        1. 加上 **3 分鐘**逾時，逾時回傳 `LoginResult.TimedOut`
        2. 登入視窗關閉時立即重新檢查狀態
        3. 補上 4 個單元測試，涵蓋逾時、關閉視窗與成功三種情況

        | 情境 | 修改前 | 修改後 |
        |---|---|---|
        | 關掉登入視窗 | 一直顯示「登入中」 | 立即回報「登入沒有完成」 |
        | 3 分鐘沒有完成 | 一直等待 | 逾時並可以重新登入 |
        | 正常登入 | 正常 | 正常 |

        ```csharp
        using var cts = new CancellationTokenSource(LoginTimeout);
        var ok = await WaitForLoginAsync(account, cts.Token);
        if (!ok)
        {
            return LoginResult.TimedOut(account.Id);
        }
        ```

        參考：[受控執行緒的取消](https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads)。修改的檔案：[AccountManager.cs](C:\repo\myriagent\src\Myriagent\Accounts\AccountManager.cs)

        > 逾時時間之後可以在設定檔調整。

        - 巢狀清單
          - 第二層 *斜體* 與 ~~刪除~~
        """;

    /// <summary>本機圖片、影片與檔案連結：完整路徑、相對路徑、file 網址、行號錨點、反斜線接標點、UNC 與資料流、文字與行內程式碼裡的路徑（AgentMarkdownRendererTests 的案例）</summary>
    private const string LocalFilesReply = """
        圖表與截圖：

        ![圖表](C:\repo\myriagent\assets\用量圖表-本週.png)
        ![報告](C:/repo/myriagent/docs/報告.pdf)
        ![修正後](assets/登入畫面-修正後.png)
        ![示範影片](C:\repo\myriagent\assets\登入流程示範.mp4)
        ![shot](C:\repo\app\.screenshots\home.png)
        ![含空白](<C:\my files\.cache\a b.png>)
        ![file 網址](file:///C:/out/a%20b.png)
        ![再一次圖表](C:\repo\myriagent\assets\用量圖表-本週.png)
        ![網路共用](\\evil\share\a.png)
        ![上一層](../shared/logo.png)

        連結：[app.js:42](src/Web/app.js#L42)、[上層 README](../README.md)、[a.cs:12](C:/repo/a.cs#L12)、[ci.yml](C:\repo\app\.github\workflows\ci.yml)、[同層](./docs/a%20b.md#L3-L5)、[資料流](a.txt:secret)、[章節](#安裝)、[根目錄](/etc/passwd)

        文字裡的路徑：截圖存在 C:\repo\myriagent\assets\用量圖表-本週.png，設定在 C:\repo\app\.github\workflows\ci.yml、錯誤在 C:/repo/a.cs:42:7。
        行內程式碼 `C:\my files\報表 v2.xlsx` 與指令 `C:\tools\x.exe --help`；**C:\out\my_file_name.txt**、網址 https://example.com/C:/x 與 [C:\out\a.txt](C:\out\a.txt) 不重複
        """;

    /// <summary>外部圖片不自動載入、危險連結中和、原始 HTML 當文字、本頁來源不當外站（AgentMarkdownRendererTests 的案例）</summary>
    private const string UnsafeReply = """
        外部圖片：![秘密](https://evil.example/x.png?d=secret) ![](https://evil.example/y.png)

        危險連結：[點我](javascript:alert(1)) [資料](data:text/html,hi) <javascript:alert(2)> ![x](javascript:alert(3))

        原始 HTML：<script>alert(1)</script> <img src=x onerror=alert(1)> <b>粗體？</b>

        本頁來源：[x](https://myriagent.local/media?path=C%3A%5Cwin.ini) ![y](https://myriagent.local/media?path=C%3A%5Cwin.ini)

        其他：[官網](https://example.com) [信](mailto:a@b.c) <https://example.org/autolink> <a@b.c> [協定相對](//evil.example/x)
        """;

    /// <summary>自動連結遇到全形標點就結束、只有雙波浪號是刪除線、待辦清單（AgentMarkdownRendererTests 的案例）</summary>
    private const string CjkReply = """
        詳見 https://example.com/docs，請參考。
        官方文件：https://example.com/docs。
        「https://example.com/q」與 https://zh.wikipedia.org/wiki/臺灣 很好；也可以看 www.example.com/a

        大約需要 3~5 天，O(n^2) 降到 O(n^1.5)，C++20 與 a==b，這是 ~~刪除線~~

        - [x] 已完成的待辦
        - [ ] 還沒做的待辦
        """;

    // ───────────────────────────── 工具卡 ─────────────────────────────

    private static GoldenCorpus Tools()
    {
        var b = new CorpusBuilder("tools");
        b.Tool("bash-running", AgentItemStatus.Running, "bash", "執行指令", "dotnet test Myriagent.slnx -c Release", i =>
        {
            i.Detail = "dotnet test Myriagent.slnx -c Release";
            i.Output = """
                  正在判斷要還原的專案...
                  已還原 C:\repo\myriagent\tests\Myriagent.Tests\Myriagent.Tests.csproj
                  Myriagent -> C:\repo\myriagent\src\Myriagent\bin\Release\net10.0-windows\win-x64\Myriagent.dll
                  測試回合 Myriagent.Tests.dll (.NETCoreApp,Version=v10.0)
                  正在啟動測試執行，請稍候...
                """;
        });
        b.Tool("bash-failed", AgentItemStatus.Failed, "bash", "執行指令", "dotnet build Myriagent.slnx -c Release", i =>
        {
            i.Detail = "dotnet build Myriagent.slnx -c Release";
            i.Output = """
                  正在判斷要還原的專案...
                  所有專案都在最新狀態，可進行還原。
                C:\repo\myriagent\src\Myriagent\Accounts\AccountManager.cs(147,58): error CS0103: 名稱 'LoginTimeout' 不存在於目前的內容中

                建置失敗。
                    0 個警告
                    1 個錯誤
                """;
        });
        var esc = ((char)0x1b).ToString();
        b.Tool("bash-ansi-trimmed", AgentItemStatus.Done, "bash", "執行指令", "npm test", i =>
        {
            i.Detail = "npm test";
            i.Output = $"…（前 1,234 字省略）\n{esc}[32m✓{esc}[0m 42 個測試通過\n{esc}[31m✗{esc}[0m 1 個失敗：<LoginTimeout> & \"逾時\"\n✅ 完成 🎉";
        });
        b.Tool("powershell", AgentItemStatus.Done, "bash", "執行 PowerShell", "Get-ChildItem -Recurse -Filter *.cs | Measure-Object", i =>
        {
            i.Detail = "Get-ChildItem -Recurse -Filter *.cs | Measure-Object\n# 第二行說明";
            i.Output = "Count    : 142\nAverage  :\nSum      :";
        });
        b.Tool("read", AgentItemStatus.Done, "read", "讀取檔案", @"src\Myriagent\Accounts\AccountManager.cs", i =>
        {
            i.FilePath = Root + @"\src\Myriagent\Accounts\AccountManager.cs";
            i.Detail = "第 120～220 行";
            i.Output = string.Empty;
        });
        b.Tool("read-image", AgentItemStatus.Done, "image", "讀取檔案", Root + @"\assets\登入畫面-修正後.png", i => i.FilePath = Root + @"\assets\登入畫面-修正後.png");
        b.Tool("write-new-file", AgentItemStatus.Done, "write", "寫入檔案", @"C:\work\chart.png", i =>
        {
            i.FilePath = @"C:\work\chart.png";
            i.Diff = DiffRules.FromNewFile("...");
            i.Output = "File created";
        });
        b.Tool("edit", AgentItemStatus.Done, "edit", "修改檔案", @"src\Myriagent\Accounts\AccountManager.cs", i =>
        {
            i.FilePath = Root + @"\src\Myriagent\Accounts\AccountManager.cs";
            i.Diff = DiffRules.FromEdit(
                "        var status = await CheckStatusAsync(account);\n        if (status == AccountStatus.NotLoggedIn)\n            await WaitForLoginAsync(account);\n        return LoginResult.Ok(account.Id);\n    }",
                "        var status = await CheckStatusAsync(account);\n        if (status == AccountStatus.NotLoggedIn)\n        {\n            using var cts = new CancellationTokenSource(LoginTimeout);\n            var ok = await WaitForLoginAsync(account, cts.Token);\n            if (!ok)\n            {\n                return LoginResult.TimedOut(account.Id);\n            }\n        }\n\n        account.LoginBusy = false;\n        return LoginResult.Ok(account.Id);\n    }");
        });
        b.Tool("multi-edit", AgentItemStatus.Done, "edit", "修改檔案", @"src\Myriagent\Hub\AgentHub.Accounts.cs", i =>
        {
            i.FilePath = Root + @"\src\Myriagent\Hub\AgentHub.Accounts.cs";
            i.Diff = DiffRules.FromEdits(new (string?, string?)[]
            {
                ("        store.Save(accounts);\n        return account;", "        store.Save(accounts);\n        PushAccounts();\n        return account;"),
                ("// TODO: 通知側欄", "// 新增帳戶後立即送出 accounts 訊息"),
            });
        });
        b.Tool("notebook-edit", AgentItemStatus.Done, "edit", "修改筆記本", Root + @"\notebooks\分析.ipynb", i =>
        {
            i.FilePath = Root + @"\notebooks\分析.ipynb";
            i.Diff = DiffRules.FromNewFile("print('hello')\nprint('世界')\n");
        });
        b.Tool("codex-unified", AgentItemStatus.Done, "edit", "修改檔案", Root + @"\src\a.cs", i =>
        {
            i.FilePath = Root + @"\src\a.cs";
            i.Diff = DiffRules.FromUnified(string.Join("\n",
                "diff --git a/a.cs b/a.cs",
                "index 83db48f..bf269f4 100644",
                "--- a/a.cs",
                "+++ b/a.cs",
                "@@ -1,3 +1,3 @@",
                " using System;",
                "-int a = 1;",
                "+int a = 2;",
                "\\ No newline at end of file",
                "沒有前置空白的行",
                "@@ -10,2 +10,3 @@",
                "--- 註解（刪掉的「-- 註解」）",
                "+++i;",
                " }"));
        });
        b.Tool("codex-add-file", AgentItemStatus.Done, "edit", "新增檔案", Root + @"\docs\新檔案.md", i =>
        {
            i.FilePath = Root + @"\docs\新檔案.md";
            i.Diff = DiffRules.FromUnified("# 標題\n- 清單\n---\n+原文就是加號開頭", "add");
        });
        b.Tool("codex-delete-file", AgentItemStatus.Done, "edit", "刪除檔案", Root + @"\docs\舊檔案.md", i =>
        {
            i.FilePath = Root + @"\docs\舊檔案.md";
            i.Diff = DiffRules.FromUnified("bye", "delete");
        });
        b.Tool("diff-truncated", AgentItemStatus.Done, "edit", "修改檔案", Root + @"\data\大檔案.csv", i =>
        {
            i.FilePath = Root + @"\data\大檔案.csv";
            i.Diff = new List<AgentDiffLine>
            {
                new("hunk", "@@ -1,3 +1,3 @@"),
                new("ctx", "id,name"),
                new("del", "1,舊名稱"),
                new("add", "1,新名稱"),
                new("hunk", "⋯ 還有 1,234 行未顯示（+1,000 −234）"),
            };
        });
        b.Tool("search", AgentItemStatus.Done, "search", "搜尋", "LoginAsync|WaitForLoginAsync", i =>
        {
            i.Detail = "pattern: LoginAsync|WaitForLoginAsync\npath: src";
            i.Output = """
                src\Myriagent\Accounts\AccountManager.cs:142:    public async Task<LoginResult> LoginAsync(AgentAccount account)
                src\Myriagent\Accounts\AccountManager.cs:188:    private async Task<bool> WaitForLoginAsync(AgentAccount account)
                src\Myriagent\Hub\AgentHub.Accounts.cs:57:        var result = await accounts.LoginAsync(account);
                """;
        });
        b.Tool("web-declined", AgentItemStatus.Declined, "web", "讀取網頁", "https://learn.microsoft.com/dotnet/api/system.threading.cancellationtokensource",
            i => i.Detail = "整理 CancellationTokenSource 的逾時用法");
        b.Tool("web-search", AgentItemStatus.Done, "web", "網路搜尋", "CancellationTokenSource 逾時 用法");
        b.Tool("agent-cancelled", AgentItemStatus.Cancelled, "agent", "子代理", "檢查其他呼叫 LoginAsync 的地方",
            i => i.Detail = "找出所有呼叫 LoginAsync 的地方，確認逾時回傳不會讓呼叫端出錯。");
        b.Tool("mcp", AgentItemStatus.Done, "mcp", "MCP：github", "search_issues", i =>
        {
            i.Detail = "{\n  \"query\": \"login hang repo:example-user/myriagent\"\n}";
            i.Output = "找到 1 個相關議題：#12 登入視窗關掉後卡在登入中";
        });
        b.Tool("todo", AgentItemStatus.Done, "todo", "更新待辦", "2/5 完成，進行中：登入視窗關閉時立即重新檢查");
        b.Tool("image-generated", AgentItemStatus.Done, "image", "產生圖片", "儀表板示意圖 ×3", i => i.MediaPaths = new List<string>
        {
            Root + @"\assets\儀表板草稿-A.png",
            Root + @"\assets\儀表板草稿-B.png",
            Root + @"\assets\儀表板草稿-C.png",
        });
        b.Tool("plan-tool", AgentItemStatus.Done, "plan", "確認執行計畫", "代理已規劃好做法，請確認是否照計畫執行");
        b.Tool("other-json", AgentItemStatus.Done, "other", "SomeNewTool", "第一個字串參數", i => i.Detail = "{\n  \"value\": \"第一個字串參數\",\n  \"count\": 3\n}");
        b.Tool("null-fields", AgentItemStatus.Done, null, null, null);
        b.Tool("empty-diff", AgentItemStatus.Done, "edit", "修改檔案", Root + @"\same.txt", i =>
        {
            i.FilePath = Root + @"\same.txt";
            i.Diff = new List<AgentDiffLine>();
        });
        b.Tool("extra-fields", AgentItemStatus.Done, "bash", "執行指令", "npm test", i =>
        {
            i.Text = "工具卡不顯示 Text";
            i.Decision = "allow";
            i.CanAllowForSession = false;
            i.AllowSessionLabel = "本對話都允許 Bash(npm test:*)";
        });
        return b.Build("tools", "工具卡：各種工具種類（bash、read、write、edit、search、web、agent、mcp、todo、image、plan、other、未指定）、各種狀態、差異（LCS、多段、筆記本、unified、新增與刪除檔案、截斷提示）與產出的媒體", Root);
    }

    // ───────────────────────────── 核准、問卷、計畫確認 ─────────────────────────────

    private static GoldenCorpus Cards()
    {
        var b = new CorpusBuilder("cards");
        b.Add("approval-bash-waiting", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "dotnet test tests/Myriagent.Tests -c Release --filter \"FullyQualifiedName~Login\"");
            i.Detail = "在 tests/Myriagent.Tests 執行登入相關的單元測試（約 20 秒），不會連線到外部服務。";
            i.AllowSessionLabel = "本對話同樣的指令都允許";
        });
        b.Add("approval-write-waiting", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "write", "寫入檔案", @"C:\work\chart.png");
            i.FilePath = @"C:\work\chart.png";
            i.Diff = DiffRules.FromNewFile("...");
            i.AllowSessionLabel = "本對話改為自動接受編輯（檔案修改與 mkdir、rm、mv 等檔案指令不再詢問）";
        });
        b.Add("approval-allow", AgentItemKind.Approval, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "edit", "修改檔案", @"tests\Myriagent.Tests\AccountRulesTests.cs");
            i.Decision = "allow";
        });
        b.Add("approval-allow-session", AgentItemKind.Approval, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "edit", "修改檔案", @"tests\Myriagent.Tests\AccountRulesTests.cs");
            i.Decision = "allowSession";
        });
        b.Add("approval-deny-feedback", AgentItemKind.Approval, AgentItemStatus.Declined, string.Empty, i =>
        {
            Card(i, "edit", "修改檔案", Root + @"\src\A.cs");
            i.FilePath = Root + @"\src\A.cs";
            i.Diff = DiffRules.FromEdit("public int Count;", "public long Count;");
            i.Decision = "deny";
            i.Output = "你的說明：不要改 A 的公開介面";
        });
        b.Add("approval-invalidated", AgentItemKind.Approval, AgentItemStatus.Cancelled, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "git push origin main");
            i.Output = "CLI 已重新啟動，這張卡已失效；代理會視需要重新提出";
        });
        b.Add("approval-mcp-no-session", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "mcp", "MCP：github", "create_issue");
            i.Detail = "{\n  \"title\": \"登入逾時\"\n}";
            i.CanAllowForSession = false;
        });
        b.Add("question-waiting", AgentItemKind.Question, AgentItemStatus.Waiting, string.Empty, i => i.Questions = new List<AgentQuestion>
        {
            Question("q1", "逾時", "登入要等多久才算逾時？", false, true, false,
                Option("3 分鐘（建議）", "和 CLI 預設的登入等待時間一致"),
                Option("5 分鐘", "給需要兩步驟驗證的帳戶多一點時間"),
                Option("不要逾時", "維持現在的行為，只修正關閉視窗的情況")),
            Question("q2", "測試", "要補哪些測試？", true, false, false,
                Option("逾時回報"),
                Option("關閉視窗後重新檢查"),
                Option("多個帳戶同時登入", "需要模擬兩個設定資料夾", "AccountManagerTests.LoginTwoAccountsAtOnce()")),
            Question("q3", "金鑰", "整合測試用的測試帳號 API 金鑰（只存在這台電腦）", false, true, true),
        });
        b.Add("question-answered", AgentItemKind.Question, AgentItemStatus.Done, string.Empty, i =>
        {
            i.Questions = new List<AgentQuestion>
            {
                Question("qa", "分支", "修改要放在哪個分支？", false, true, false, Option("直接改 main"), Option("開新分支 fix/login-timeout")),
            };
            i.Answers = new Dictionary<string, List<string>> { ["qa"] = new() { "開新分支 fix/login-timeout" } };
            i.Decision = "answered";
        });
        b.Add("question-recorded-turn", AgentItemKind.Question, AgentItemStatus.Done, string.Empty, i =>
        {
            i.Questions = new List<AgentQuestion>
            {
                Question("你想喝哪種飲料？", "飲料選擇", "你想喝哪種飲料？", false, true, false, Option("咖啡", "選擇咖啡"), Option("紅茶", "選擇紅茶")),
            };
            i.Answers = new Dictionary<string, List<string>> { ["你想喝哪種飲料？"] = new() { "紅茶" } };
            i.Decision = "answered";
        });
        b.Add("question-skipped", AgentItemKind.Question, AgentItemStatus.Declined, string.Empty, i =>
        {
            i.Questions = new List<AgentQuestion> { Question("q1", null, "要不要順便更新 README？", false, true, false, Option("要"), Option("不要")) };
            i.Decision = "skip";
        });
        b.Add("question-async-codex", AgentItemKind.Question, AgentItemStatus.Waiting, string.Empty, i =>
        {
            i.Questions = new List<AgentQuestion> { Question("m1:0", "格式", "要哪種格式？", false, true, false, Option("CSV"), Option("Excel")) };
            i.AnswerAsMessage = true;
        });
        b.Add("question-secret-answered", AgentItemKind.Question, AgentItemStatus.Done, string.Empty, i =>
        {
            i.Questions = new List<AgentQuestion> { Question("token", "權杖", "請輸入部署權杖", false, true, true) };
            i.Answers = new Dictionary<string, List<string>> { ["token"] = new() { "（已隱藏）" } };
            i.Decision = "answered";
        });
        b.Add("question-multi-answers", AgentItemKind.Question, AgentItemStatus.Done, string.Empty, i =>
        {
            i.Questions = new List<AgentQuestion>
            {
                Question("q2", "測試", "要補哪些測試？", true, true, false, Option("逾時回報"), Option("關閉視窗後重新檢查"), Option("多個帳戶同時登入")),
            };
            i.Answers = new Dictionary<string, List<string>>
            {
                ["q2"] = new() { "逾時回報", "多個帳戶同時登入", "另外：網路斷線時的重試" },
                ["已不在題目裡的鍵"] = new(),
            };
            i.Decision = "answered";
        });
        b.Add("plan-approval-waiting", AgentItemKind.PlanApproval, AgentItemStatus.Waiting, "PlanApproval 的 Text 不顯示", i =>
        {
            PlanCard(i);
            i.Detail = PlanDetail;
        });
        b.Add("plan-approval-accepted", AgentItemKind.PlanApproval, AgentItemStatus.Done, string.Empty, i =>
        {
            PlanCard(i);
            i.Detail = "1. 修正逾時\n2. 補測試";
            i.Decision = "acceptEdits";
        });
        b.Add("plan-approval-manual", AgentItemKind.PlanApproval, AgentItemStatus.Done, string.Empty, i =>
        {
            PlanCard(i);
            i.Detail = "照計畫執行，每次改檔案仍詢問";
            i.Decision = "manual";
        });
        b.Add("plan-approval-keep-planning", AgentItemKind.PlanApproval, AgentItemStatus.Declined, string.Empty, i =>
        {
            PlanCard(i);
            i.Detail = "把 `LoginTimeout` 移到設定";
            i.Decision = "keepPlanning";
            i.Output = "你的說明：先不要動設定檔";
        });
        b.Add("plan-approval-no-detail", AgentItemKind.PlanApproval, AgentItemStatus.Waiting, string.Empty, PlanCard);
        // MCP 伺服器的 elicitation（2026-10-24 加的；toolName 是來源、detailHtml 是說明與網址）：加在最後，前面各項的編號與時間不變
        b.Add("question-mcp-elicitation", AgentItemKind.Question, AgentItemStatus.Waiting, string.Empty, i =>
        {
            i.ToolName = "MCP：probe";
            i.Detail = "MCP 伺服器「probe」要你開啟網址：\n\n請完成授權\n\n<https://example.test/authorize?x=1>";
            i.Questions = new List<AgentQuestion> { Question("__url", "網址", "在瀏覽器開啟上面的網址、照網頁的指示完成後，按「已完成」", false, false, false, Option("已完成", "已經在網頁上完成"), Option("不要", "不開這個網址（婉拒）")) };
        });
        return b.Build("cards", "等你回應的卡片：核准（各種決定、本對話都允許的說明、拒絕說明、CLI 重開後失效）、問卷（單選、多選、密碼、非同步提問、略過）與計畫確認（含計畫內容的 Markdown 與本機圖片）", Root);
    }

    /// <summary>介面探針 i18 的計畫（Markdown 原文）：含相對路徑的本機圖片與檔案連結</summary>
    private const string PlanDetail = """
        #### 補測試與收尾的計畫

        1. 在 `AccountRulesTests` 新增逾時、視窗關閉、成功三組測試
        2. 把 `LoginTimeout` 移到設定，預設 3 分鐘
        3. 跑 `dotnet test` 確認全部通過

        ![流程圖](docs/images/login-flow.png)

        預計修改 3 個檔案（見[規格](docs/登入流程規格.md)），不會動到其他帳戶的設定資料夾。
        """;

    private static void Card(AgentChatItem item, string toolKind, string toolName, string title)
    {
        item.ToolKind = toolKind;
        item.ToolName = toolName;
        item.Title = title;
    }

    /// <summary>Claude 的 ExitPlanMode 核准卡（ClaudeAgentSession 送出的固定名稱與說明）</summary>
    private static void PlanCard(AgentChatItem item)
    {
        Card(item, "plan", "確認執行計畫", "代理已規劃好做法，請確認是否照計畫執行");
        item.CanAllowForSession = false;
    }

    private static AgentQuestion Question(string id, string? header, string text, bool multi, bool allowOther, bool secret, params AgentQuestionOption[] options)
        => new() { Id = id, Header = header, Text = text, MultiSelect = multi, AllowOther = allowOther, IsSecret = secret, Options = options.ToList() };

    private static AgentQuestionOption Option(string label, string? description = null, string? preview = null)
        => new() { Label = label, Description = description, Preview = preview };

    // ───────────────────────────── 附件與媒體 ─────────────────────────────

    private static GoldenCorpus Media()
    {
        var b = new CorpusBuilder("media");
        b.Add("user-attachments", AgentItemKind.User, text: "幫我看這三個檔案", setup: i => i.Attachments = new List<AgentAttachment>
        {
            Attachment("x1", "登入卡住的畫面.png", Attachments + @"\登入卡住的畫面.png", AgentAttachmentKind.Image, 186_368,
                new[] { Attachments + @"\登入卡住的畫面.png" }),
            Attachment("x2", "重現步驟.mp4", Attachments + @"\重現步驟.mp4", AgentAttachmentKind.Video, 1_258_291,
                Enumerable.Range(1, 6).Select(n => $@"{ModelImages}\重現步驟_{n}.jpg"), 3.4),
            Attachment("x3", "agent.log", Attachments + @"\agent.log", AgentAttachmentKind.Document, 12_288),
        });
        b.Add("user-attachment-only", AgentItemKind.User, text: string.Empty, setup: i => i.Attachments = new List<AgentAttachment>
        {
            Attachment("y1", "設定畫面.png", Attachments + @"\設定畫面.png", AgentAttachmentKind.Image, 98_304, new[] { ModelImages + @"\設定畫面_縮圖.jpg" }),
        });
        b.Add("user-attachment-kinds", AgentItemKind.User, text: "各種格式與大小", setup: i => i.Attachments = new List<AgentAttachment>
        {
            Attachment("k1", "掃描.tif", Attachments + @"\掃描.tif", AgentAttachmentKind.Image, 5_505_024, new[] { ModelImages + @"\掃描.jpg" }),
            Attachment("k2", "照片.webp", Attachments + @"\照片.webp", AgentAttachmentKind.Image, 1024, new[] { Attachments + @"\照片.webp" }),
            Attachment("k3", "螢幕錄影.avi", Attachments + @"\螢幕錄影.avi", AgentAttachmentKind.Video, 2L * 1024 * 1024 * 1024,
                Enumerable.Range(1, 6).Select(n => $@"{ModelImages}\螢幕錄影_{n}.jpg"), 3725.5),
            Attachment("k4", "沒有畫面.mov", Attachments + @"\沒有畫面.mov", AgentAttachmentKind.Video, 1536),
            Attachment("k5", "登入流程規格.pdf", Attachments + @"\登入流程規格.pdf", AgentAttachmentKind.Document, 1_468_006),
            Attachment("k6", "圖示.svg", Attachments + @"\圖示.svg", AgentAttachmentKind.Document, 0),
            Attachment("k7", "iPhone.heic", Attachments + @"\iPhone.heic", AgentAttachmentKind.Document, 1023),
            Attachment("k8", "favicon.ico", Attachments + @"\favicon.ico", AgentAttachmentKind.Document, 1024),
            Attachment("k9", "a+b #1 100%&.png", Attachments + @"\a+b #1 100%&.png", AgentAttachmentKind.Image, 1_572_864, new[] { Attachments + @"\a+b #1 100%&.png" }),
            Attachment("k10", "備份.zip", Attachments + @"\備份.zip", AgentAttachmentKind.Document, 3_489_660_928),
        });
        b.Add("assistant-media", AgentItemKind.Assistant, text: "重現步驟的畫面和修正後的樣子整理在下面，最後一段是修正後的操作錄影：", setup: i => i.MediaPaths = new List<string>
        {
            Root + @"\assets\用量圖表-本週.png",
            Root + @"\assets\登入畫面-修正後.png",
            Root + @"\assets\登入畫面-逾時提示.jpg",
            Root + @"\assets\登入流程示範.mp4",
            Root + @"\assets\動畫.gif",
            Root + @"\assets\短片.webm",
            Root + @"\assets\手機錄影.MOV",
            Root + @"\assets\舊圖.bmp",
            "C:/repo/myriagent/assets/../out/chart.png",
            @"c:\repo\myriagent\out\小寫磁碟代號.png",
            @"\\server\share\網路共用.png",
        });
        b.Add("assistant-media-only", AgentItemKind.Assistant, text: string.Empty, setup: i => i.MediaPaths = new List<string> { Root + @"\out\產出.png" });
        return b.Build("media", "附件（圖片、影片、文件；有沒有給模型看的圖、影片長度與畫面數、各種大小與特殊字元的檔名）與代理產出的媒體（圖片、可播放與不可播放的影片、正斜線與 .. 的路徑、UNC）", Root);
    }

    private static AgentAttachment Attachment(string id, string fileName, string storedPath, AgentAttachmentKind kind, long size,
        IEnumerable<string>? modelImages = null, double? duration = null)
    {
        var images = modelImages?.ToList() ?? new List<string>();
        return new AgentAttachment
        {
            Id = id,
            FileName = fileName,
            StoredPath = storedPath,
            Kind = kind,
            SizeBytes = size,
            ModelImagePaths = images,
            FrameSeconds = kind == AgentAttachmentKind.Video ? AgentAttachmentRules.GetFrameSeconds(duration, images.Count).Take(images.Count).ToList() : new List<double>(),
            DurationSeconds = duration,
        };
    }

    // ───────────────────────────── 病態與邊界 ─────────────────────────────

    private static GoldenCorpus Edge()
    {
        var b = new CorpusBuilder("edge");
        var nestedImage = Root + @"\out\巢狀.png";
        var planImage = Root + @"\out\計畫圖.png";
        b.Add("deep-nesting", AgentItemKind.Assistant, text: TooDeep + " 引用");
        b.Add("deep-nesting-image", AgentItemKind.Assistant, text: $"{TooDeep} ![巢狀]({nestedImage})");
        b.Add("deep-nesting-thinking", AgentItemKind.Thinking, text: TooDeep + " 思考");
        b.Add("deep-nesting-plan-approval", AgentItemKind.PlanApproval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            PlanCard(i);
            i.Detail = $"{TooDeep} ![計畫圖]({planImage})";
        });
        b.Add("deep-nesting-with-media", AgentItemKind.Assistant, text: TooDeep + " 下面的圖照樣顯示", setup: i => i.MediaPaths = new List<string> { Root + @"\out\照樣顯示.png" });
        b.Add("whitespace-markdown", AgentItemKind.Assistant, text: "   \n\t\n");
        b.Add("html-entities", AgentItemKind.Assistant, text: "R&amp;D &lt;tag&gt; &copy; &#x1F600; &nbsp;結尾 \\*不是斜體\\*");
        var c = (int code) => ((char)code).ToString();
        b.Add("control-chars", AgentItemKind.Assistant, text: $"控制字元：{c(0)}{c(1)}{c(0x1b)}[1m粗{c(0x1b)}[0m{c(7)}{c(0x0c)} 定位\t字元、表情 ✅🎉");
        b.Add("attachments-null-element", AgentItemKind.User, text: "附件清單裡有 null（損毀的 items.json）", setup: i => i.Attachments = new List<AgentAttachment>
        {
            Attachment("n1", "前面的圖.png", Attachments + @"\前面的圖.png", AgentAttachmentKind.Image, 2048, new[] { Attachments + @"\前面的圖.png" }),
            null!,
        });
        b.Add("diff-null-text", AgentItemKind.Tool, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "edit", "修改檔案", Root + @"\broken.cs");
            i.Diff = new List<AgentDiffLine> { new("add", "x"), new("hunk", null!) };
        });
        b.Add("media-null-element", AgentItemKind.Assistant, text: "媒體清單中間有 null", setup: i => i.MediaPaths = new List<string>
        {
            Root + @"\out\null之前.png",
            null!,
            Root + @"\out\null之後.mp4",
        });
        b.Add("time-min", AgentItemKind.Notice, text: "建立時間是 0001-01-01（壞檔）", setup: i => i.CreatedAt = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
        b.Add("time-max", AgentItemKind.Notice, text: "建立時間是 9999-12-31", setup: i => i.CreatedAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
        b.Add("time-1970", AgentItemKind.Notice, text: "1971 年以前視為無效", setup: i => i.CreatedAt = new DateTime(1970, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        // 1971-01-01 14:00 UTC：任何時區（UTC-12～+14）的本機時間都還在 1971 年——ProtocolJson 往返後是本機時間，年份檢查看的是本機的年
        b.Add("time-1971", AgentItemKind.Notice, text: "1971 年（任何時區都是）", setup: i => i.CreatedAt = new DateTime(1971, 1, 1, 14, 0, 0, DateTimeKind.Utc));
        b.Add("undefined-enums", (AgentItemKind)99, (AgentItemStatus)42, "未知的種類照樣以 **Markdown** 顯示", i => i.NoticeKind = (AgentNoticeKind)5);
        b.Add("notice-kind-on-error", AgentItemKind.Error, text: "其他種類的項目也照樣帶 noticeKind", setup: i => i.NoticeKind = AgentNoticeKind.CliUpdate);
        b.Add("undefined-attachment-kind", AgentItemKind.User, text: "未知的附件種類", setup: i => i.Attachments = new List<AgentAttachment>
        {
            Attachment("u1", "未知種類.png", Attachments + @"\未知種類.png", (AgentAttachmentKind)7, 4096),
        });
        b.Add("question-empty", AgentItemKind.Question, AgentItemStatus.Waiting, string.Empty);
        return b.Build("edge", "病態與邊界：巢狀過深的 Markdown 退回純文字（其中的本機圖片不登記）、控制字元、清單裡的 null（整筆改成錯誤說明）、轉換不了的時間、未定義的列舉值", Root,
            nestedImage, planImage);
    }

    // ───────────────────────────── Grok 與 Antigravity 的對話 ─────────────────────────────

    /// <summary>實錄的工作資料夾（GrokRulesTests、AntigravityStreamParserTests 的樣本都在 C:\w）</summary>
    private const string ProbeRoot = @"C:\w";

    /// <summary>工具參數照 grok／agy 的寫法縮排成 JSON（GrokAcpRules、AntigravityStepRules 的細節）</summary>
    private static string Json(JObject value) => value.ToString(Newtonsoft.Json.Formatting.Indented);

    /// <summary>
    /// Grok Build（ACP）對話（設計 2026-10-22-grok-antigravity-sync 第 10 節第 4 項）：欄位照 GrokUpdateParser、GrokAcpRules 與 GrokAgentSession 實際送出的事件，
    /// 再由中樞建成項目。工具卡和實錄經解讀器、中樞建的逐欄相同（ProviderCorpusShapeTests）；思考、待辦（ACP plan：沒有說明、依序）、核准卡
    /// （HandlePermission：AllowSessionLabel 依 ACP kind）、問卷（HandleQuestion：題目就是 id、沒有標頭、可自填）、計畫確認卡（HandlePlan：「計畫」「照這個計畫執行？」、
    /// 沒有 planContent 時的說明）與提示、錯誤照各自的程式碼
    /// </summary>
    private static GoldenCorpus Grok()
    {
        var b = new CorpusBuilder("grok");
        b.Add("user", AgentItemKind.User, text: @"在 C:\w 建 a.txt、跑一次指令確認，再找 TODO");
        b.Add("thinking", AgentItemKind.Thinking, text: "想一下：先看資料夾裡有什麼，再決定要不要改 `a.cs`");
        b.Add("plan-acp", AgentItemKind.Plan, AgentItemStatus.Running, string.Empty, i => i.PlanSteps = Steps(
            ("讀設定", "completed"),
            ("寫測試", "inProgress"),
            ("跑測試", "pending")));
        b.Add("assistant", AgentItemKind.Assistant, text: "我會執行");
        b.Tool("exec", AgentItemStatus.Done, "bash", "執行指令", "Write-Output hello-myriagent; Write-Output line-two", i =>
        {
            i.Detail = "Print two lines\n\nWrite-Output hello-myriagent; Write-Output line-two";
            i.Output = "hello-myriagent\nline-two\n";
        });
        b.Tool("write-new", AgentItemStatus.Done, "write", "寫入檔案", ProbeRoot + @"\a.txt", i =>
        {
            i.FilePath = ProbeRoot + @"\a.txt";
            i.Diff = DiffRules.FromNewFile("a");
        });
        b.Tool("write-rejected", AgentItemStatus.Declined, "write", "寫入檔案", ProbeRoot + @"\reject.txt", i =>
        {
            i.FilePath = ProbeRoot + @"\reject.txt";
            i.Diff = DiffRules.FromNewFile("nope\n");
        });
        b.Tool("exec-exit-code", AgentItemStatus.Failed, "bash", "執行指令", "exit 2", i =>
        {
            i.Detail = "exit 2";
            i.Output = "bad\n";
        });
        b.Tool("grep", AgentItemStatus.Done, "search", "搜尋內容", $"TODO{(char)0x3000}src", i =>
        {
            i.Detail = Json(new JObject { ["pattern"] = "TODO", ["path"] = "src" });
            i.Output = @"src\a.cs:3:// TODO 補測試" + "\n";
        });
        b.Tool("search-replace", AgentItemStatus.Done, "edit", "修改檔案", ProbeRoot + @"\a.cs", i =>
        {
            i.FilePath = ProbeRoot + @"\a.cs";
            i.Diff = DiffRules.FromEdit("int a = 1;", "int a = 2;");
        });
        b.Tool("read-file", AgentItemStatus.Done, "read", "讀取檔案", ProbeRoot + @"\hello.txt", i =>
        {
            i.FilePath = ProbeRoot + @"\hello.txt";
            i.Output = "hello-myriagent\nline-two\n";
        });
        b.Tool("mcp", AgentItemStatus.Done, "mcp", "MCP：mcp__github__search", "login hang", i =>
        {
            i.Detail = Json(new JObject { ["query"] = "login hang" });
            i.Output = "找到 1 個議題：#12 登入卡住";
        });
        b.Tool("x-search", AgentItemStatus.Done, "web", "X Search", "Clode Myriagent", i =>
        {
            i.Detail = Json(new JObject { ["query"] = "Clode Myriagent" });
            i.Output = "3 則貼文";
        });
        b.Tool("task", AgentItemStatus.Done, "agent", "子代理", "檢查呼叫端", i =>
        {
            i.Detail = Json(new JObject { ["description"] = "檢查呼叫端", ["prompt"] = "找出所有呼叫 LoginAsync 的地方" });
            i.Output = "共 3 處，都已處理逾時";
        });
        b.Tool("stopped", AgentItemStatus.Cancelled, "bash", "執行指令", "npm test", i =>
        {
            i.Detail = "npm test";
            i.Output = "partial\n\n已停止";
        });
        b.Add("approval-exec", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "Write-Output hi");
            i.Detail = "Print hi\n\nWrite-Output hi";
            i.AllowSessionLabel = "本對話的指令都不再詢問";
        });
        b.Add("approval-edit", AgentItemKind.Approval, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "write", "寫入檔案", ProbeRoot + @"\hello.txt");
            i.FilePath = ProbeRoot + @"\hello.txt";
            i.Diff = DiffRules.FromNewFile("hi-myriagent\n");
            i.AllowSessionLabel = "本對話的編輯都允許";
            i.Decision = "allowSession";
        });
        b.Add("approval-no-session", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "web", "讀取網頁", "https://example.com/docs");
            i.Detail = Json(new JObject { ["url"] = "https://example.com/docs" });
            i.CanAllowForSession = false;
        });
        b.Add("question", AgentItemKind.Question, AgentItemStatus.Waiting, string.Empty, i => i.Questions = new List<AgentQuestion>
        {
            Question("你偏好哪個顏色？", null, "你偏好哪個顏色？", false, true, false, Option("藍色", "冷色系"), Option("紅色", null, "#ff0000")),
        });
        b.Add("plan-approval", AgentItemKind.PlanApproval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            GrokPlanCard(i);
            i.Detail = "# 計畫\n\n1. 改 `a.cs` 的逾時\n2. 補測試\n\n見[說明](docs/README.md)";
        });
        b.Add("plan-approval-in-reply", AgentItemKind.PlanApproval, AgentItemStatus.Done, string.Empty, i =>
        {
            GrokPlanCard(i);
            i.Detail = "（計畫內容在上面的回覆裡）";
            i.Decision = "acceptEdits";
        });
        b.Add("notice-rejected", AgentItemKind.Notice, text: "你拒絕了這個操作，這一輪已結束");
        b.Add("notice-limit", AgentItemKind.Notice, text: "Grok 的回覆達到輸出長度上限而中斷");
        b.Add("notice-session-lost", AgentItemKind.Notice, text: "grok 找不到這個對話先前的紀錄，已改開新對話並附上前文節錄");
        b.Add("error-refusal", AgentItemKind.Error, text: "Grok 拒絕回應這個要求");
        b.Add("error-failed", AgentItemKind.Error, text: "Grok 執行失敗：Internal error: model overloaded");
        return b.Build("grok", "Grok Build（ACP）對話：工具卡照實錄經 GrokUpdateParser 與中樞建的（指令、新檔案的差異、被拒的寫入、結束碼非 0、搜尋的樣式與資料夾、修改、讀檔、MCP、X 搜尋、子代理、停下時的部分輸出），"
            + "思考、待辦、核准卡（本對話都允許的說明依 ACP kind）、問卷（題目即 id）、計畫確認卡與 Grok 的提示、錯誤", ProbeRoot);
    }

    /// <summary>Grok 的 exit_plan_mode 核准卡（GrokAgentSession.HandlePlan 送出的固定名稱與標題）</summary>
    private static void GrokPlanCard(AgentChatItem item)
    {
        Card(item, "plan", "計畫", "照這個計畫執行？");
        item.CanAllowForSession = false;
    }

    /// <summary>
    /// Antigravity（agy stream-json）對話：工具卡照實錄經 AntigravityStreamParser 與中樞建的（逐欄相同，ProviderCorpusShapeTests）——
    /// 帶內容的寫檔算出紅綠差異、只有路徑的 1.4.6 步驟（無伴讀逐字稿時）不列空細節；被自動拒絕的操作以提示說明可改用「自動（沙箱）」或「完全自動」
    /// </summary>
    private static GoldenCorpus Antigravity()
    {
        var b = new CorpusBuilder("antigravity");
        b.Add("user", AgentItemKind.User, text: "列出資料夾裡的檔案");
        b.Tool("command-denied", AgentItemStatus.Failed, "bash", "執行指令", "Get-ChildItem", i =>
        {
            i.Detail = "Get-ChildItem";
            i.Output = "permission check failed for command \"Get-ChildItem\": user denied permission to run command:\nGet-ChildItem\nDo not attempt to circumvent this denial.";
        });
        b.Add("notice-denied", AgentItemKind.Notice, text: "agy 自動拒絕了需要核准的操作（RunCommand）：Antigravity 在這裡無法於對話中詢問。要讓代理執行指令，請把權限模式改成「自動（沙箱）」或「完全自動」。");
        b.Add("user-2", AgentItemKind.User, text: "第 2 輪");
        b.Tool("view-file", AgentItemStatus.Done, "read", "讀取檔案", ProbeRoot + @"\hello.txt", i =>
        {
            i.FilePath = ProbeRoot + @"\hello.txt";
            i.Output = "2 lines, 17 bytes";
        });
        b.Tool("command", AgentItemStatus.Done, "bash", "執行指令", "Write-Output agy-probe", i =>
        {
            i.Detail = "Write-Output agy-probe";
            i.Output = "agy-probe\n";
        });
        b.Tool("write-to-file", AgentItemStatus.Done, "write", "寫入檔案", ProbeRoot + @"\a.txt", i =>
        {
            i.FilePath = ProbeRoot + @"\a.txt";
            i.Diff = DiffRules.FromNewFile("hello");
        });
        b.Tool("grep-search", AgentItemStatus.Done, "search", "搜尋內容", "TODO", i =>
        {
            i.Detail = Json(new JObject { ["Query"] = "TODO", ["SearchPath"] = ProbeRoot });
            i.Output = "Found 2 results";
        });
        b.Tool("command-denied-silently", AgentItemStatus.Failed, "bash", "執行指令", "npm test", i =>
        {
            i.Detail = "npm test";
            i.Output = "agy 自動拒絕了這個指令（需要核准，但在這裡無法詢問），沒有執行";
        });
        // agy 1.4.6 的檔案工具（2026-10-23 實測）：步驟名稱是 edit_file、write_file，stream-json 參數只有 Path（未提供伴讀逐字稿時沒有內容、輸出與差異）
        b.Tool("edit-file-v146", AgentItemStatus.Done, "edit", "修改檔案", "C:/w/b.txt", i => i.FilePath = "C:/w/b.txt");
        b.Tool("write-file-v146", AgentItemStatus.Done, "write", "寫入檔案", ProbeRoot + @"\c.txt", i => i.FilePath = ProbeRoot + @"\c.txt");
        b.Add("assistant", AgentItemKind.Assistant, text: "Hello from probe\n");
        b.Add("user-3", AgentItemKind.User, text: "第 3 輪");
        b.Tool("command-aborted", AgentItemStatus.Cancelled, "bash", "執行指令", "npm run dev", i =>
        {
            i.Detail = "npm run dev";
            i.Output = "agy 程序已結束，這個工具被中斷，結果未知";
        });
        b.Add("error-exit", AgentItemKind.Error, text: "agy 程序意外結束（結束碼 1）");
        b.Add("notice-conversation-lost", AgentItemKind.Notice, text: "agy 找不到這個對話先前的紀錄，已改開新對話並附上前文節錄");
        b.Add("error-result", AgentItemKind.Error, text: "agy 回報這一輪失敗（ERROR）");
        return b.Build("antigravity", "Antigravity（agy stream-json）對話：工具卡照實錄經 AntigravityStreamParser 與中樞建的（被自動拒絕的指令、讀檔、執行、帶紅綠差異的寫檔、搜尋、只報 DONE 的被拒指令、"
            + "程序結束時還在跑的指令），檔案工具的細節不重複列路徑；被拒的操作提示可改用「自動（沙箱）」或「完全自動」", ProbeRoot);
    }

    private static List<AgentPlanStep> Steps(params (string Text, string Status)[] steps)
        => steps.Select(s => new AgentPlanStep { Text = s.Text, Status = s.Status }).ToList();

    /// <summary>依序建立項目：Id＝「語料-序號-名稱」、Seq＝序號、建立時間從 2026-07-15 01:00 UTC 起每筆加一分鐘</summary>
    private sealed class CorpusBuilder
    {
        private static readonly DateTime Start = new(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc);

        private readonly string prefix;
        private readonly List<AgentChatItem> items = new();

        public CorpusBuilder(string prefix) => this.prefix = prefix;

        public AgentChatItem Add(string name, AgentItemKind kind, AgentItemStatus status = AgentItemStatus.Done, string text = "", Action<AgentChatItem>? setup = null)
        {
            int n = items.Count + 1;
            var time = Start.AddMinutes(n);
            var item = new AgentChatItem
            {
                Id = $"{prefix}-{n:00}-{name}",
                Kind = kind,
                Status = status,
                Text = text,
                CreatedAt = time,
                UpdatedAt = time,
                Seq = n,
            };
            setup?.Invoke(item);
            items.Add(item);
            return item;
        }

        public AgentChatItem Tool(string name, AgentItemStatus status, string? toolKind, string? toolName, string? title, Action<AgentChatItem>? setup = null)
            => Add(name, AgentItemKind.Tool, status, string.Empty, i =>
            {
                i.ToolKind = toolKind;
                i.ToolName = toolName;
                i.Title = title;
                setup?.Invoke(i);
            });

        public GoldenCorpus Build(string name, string description, string workingDirectory, params string[] extraPaths)
            => new(name, description, workingDirectory, items, extraPaths);
    }
}
