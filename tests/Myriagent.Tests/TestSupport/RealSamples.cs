namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 2026-10-09 在這台電腦以唯讀探針實測的 CLI／端點回應（Claude Code 2.1.295、codex-cli 0.160.1；格式說明見 docs/protocol-notes.md）。
/// 只遮蔽了 email、帳戶與組織識別碼，欄位與數值形狀維持原樣。
/// </summary>
internal static class RealSamples
{
    /// <summary>GET https://api.anthropic.com/api/oauth/usage（Max 方案）</summary>
    public const string ClaudeUsage = """
        {
         "five_hour": {"utilization": 12.0, "resets_at": "2026-10-09T18:10:00.320796+00:00", "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null},
         "seven_day": {"utilization": 32.0, "resets_at": "2026-10-14T22:00:00.320819+00:00", "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null},
         "seven_day_oauth_apps": null,
         "seven_day_opus": null,
         "seven_day_sonnet": null,
         "seven_day_cowork": null,
         "seven_day_omelette": null,
         "tangelo": null,
         "iguana_necktie": {"utilization": 0.4438968, "resets_at": "2026-11-05T07:59:00+00:00", "limit_dollars": 100, "used_dollars": 2.5, "remaining_dollars": 97.5, "locked_reason": null},
         "omelette_promotional": null,
         "extra_usage": {"is_enabled": false, "monthly_limit": null, "used_credits": null, "utilization": null, "currency": null, "decimal_places": null, "disabled_reason": null, "user_disabled": true, "spend_limit_reached": false, "credits_ever_enabled": true, "daily": null, "weekly": null},
         "limits": [
          {"kind": "session", "group": "session", "percent": 12, "severity": "normal", "resets_at": "2026-10-09T18:10:00.320796+00:00", "scope": null, "is_active": false},
          {"kind": "weekly_all", "group": "weekly", "percent": 32, "severity": "normal", "resets_at": "2026-10-14T22:00:00.320819+00:00", "scope": null, "is_active": true},
          {"kind": "weekly_scoped", "group": "weekly", "percent": 0, "severity": "normal", "resets_at": "2026-10-14T22:00:00+00:00", "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": false}
         ],
         "spend": {"used": {"amount_minor": 0, "currency": "USD", "exponent": 2}, "limit": null, "percent": 0, "severity": "normal", "enabled": false, "disabled_reason": null, "cap": null, "balance": null, "auto_reload": null, "can_purchase_credits": false, "can_toggle": false},
         "member_dashboard_available": false,
         "seven_day_breakdown": {"as_of": "2026-10-09T15:57:09.364813+00:00", "window_started_at": "2026-10-07T22:00:00.320819+00:00", "rows": [{"key": "claude_code", "display_name": "Claude Code", "percent": 100}, {"key": "chat", "display_name": "Chats", "percent": 0}]},
         "weekly_scoped_shares": [{"limit_index": 2, "used_percent_of_weekly": 0, "allowance_percent_of_weekly": 50}]
        }
        """;

    /// <summary>codex app-server 的 account/rateLimits/read 結果（Pro 方案：只有 primary＝10080 分鐘）</summary>
    public const string CodexRateLimitsRead = """
        {"ordinaryUsageAllowed": true,
         "rateLimits": {"limitId": "codex", "limitName": null, "normalModelSlug": null, "primary": {"usedPercent": 9, "windowDurationMins": 10080, "resetsAt": 1791959804}, "secondary": null, "credits": {"hasCredits": true, "unlimited": false, "balance": "12345.6789000000"}, "individualLimit": null, "spendControlReached": false, "planType": "pro", "rateLimitReachedType": null},
         "rateLimitsByLimitId": {"codex": {"limitId": "codex", "limitName": null, "normalModelSlug": null, "primary": {"usedPercent": 9, "windowDurationMins": 10080, "resetsAt": 1791959804}, "secondary": null, "credits": {"hasCredits": true, "unlimited": false, "balance": "12345.6789000000"}, "individualLimit": null, "spendControlReached": false, "planType": "pro", "rateLimitReachedType": null}},
         "rateLimitResetCredits": {"availableCount": 1, "credits": [{"id": "RateLimitResetCredit_x", "resetType": "codexRateLimits", "status": "available", "grantedAt": 1791415837, "expiresAt": 1794007837, "title": "Full reset"}]},
         "accountId": "00000000-0000-0000-0000-000000000000", "rateLimitUpsell": null}
        """;

    /// <summary>codex app-server 的 account/read 結果（已登入 ChatGPT）</summary>
    public const string CodexAccountRead = """
        {"account": {"type": "chatgpt", "email": "user@example.com", "planType": "pro"}, "requiresOpenaiAuth": true,
         "workspaceRouting": {"chatgptAccountId": "00000000-0000-0000-0000-000000000000", "backendOrigin": "https://chatgpt.com", "accountRoutingOverride": "NO_CONSTRAINT"}}
        """;

    /// <summary>claude auth status --json（已登入訂閱，結束碼 0）</summary>
    public const string ClaudeAuthStatusLoggedIn = """
        {
          "loggedIn": true,
          "authMethod": "claude.ai",
          "apiProvider": "firstParty",
          "analyticsDisabled": false,
          "projectsDirectory": "C:\\Users\\User\\.claude\\projects",
          "configDirectory": "C:\\Users\\User\\.claude",
          "email": "user@example.com",
          "orgId": "00000000-0000-0000-0000-000000000000",
          "orgName": "user@example.com's Organization",
          "subscriptionType": "max"
        }

        """;

    /// <summary>claude auth status --json（CLAUDE_CONFIG_DIR 指向空資料夾，結束碼 1）</summary>
    public const string ClaudeAuthStatusEmptyConfig = """
        {
          "loggedIn": false,
          "authMethod": "none",
          "apiProvider": "firstParty",
          "analyticsDisabled": false,
          "projectsDirectory": "C:\\Users\\User\\AppData\\Local\\Temp\\myriagent-probe\\projects",
          "configDirectory": "C:\\Users\\User\\AppData\\Local\\Temp\\myriagent-probe"
        }

        """;

    /// <summary>.credentials.json 的形狀（權杖換成假值）</summary>
    public const string ClaudeCredentials = """
        {"claudeAiOauth": {"accessToken": "sk-ant-oat01-假權杖", "refreshToken": "sk-ant-ort01-假權杖", "expiresAt": 1791600000000,
          "refreshTokenExpiresAt": 1794000000000, "scopes": ["user:inference", "user:profile", "user:sessions:claude_code"],
          "subscriptionType": "max", "rateLimitTier": "default_claude_max_20x"},
         "mcpOAuth": {}, "organizationUuid": "00000000-0000-0000-0000-000000000000"}
        """;

    /// <summary>Claude control_request initialize 的回應（response 內層；commands 只留幾個，含實測會出現的「__」開頭內部指令）</summary>
    public const string ClaudeInitializeResponse = """
        {"commands": [
           {"name": "deep-research", "description": "Research a topic across multiple sources", "argumentHint": ""},
           {"name": "compact", "description": "Clear conversation history but keep a summary in context", "argumentHint": "<optional custom summarization instructions>"},
           {"name": "__remote-workflow", "description": "internal", "argumentHint": ""},
           {"name": "/model", "description": "Set the AI model for Claude Code", "argumentHint": "[model]"},
           {"name": "Compact", "description": "重複（不分大小寫）", "argumentHint": ""},
           {"name": "", "description": "沒有名稱"}
         ],
         "agents": [],
         "output_style": "default",
         "available_output_styles": ["default", "Proactive", "Concise", "Explanatory", "Learning"],
         "models": [
           {"value": "default", "displayName": "Default (recommended)", "description": "Opus 5.5", "supportedEffortLevels": ["low", "medium", "high", "xhigh", "max"]},
           {"value": "haiku", "displayName": "Haiku 5.5", "supportedEffortLevels": ["low", "medium", "high", "xhigh", "max"]},
           {"value": "claude-haiku-4-5-20251001", "displayName": "Haiku 4.5"}
         ],
         "account": {"email": "user@example.com", "organization": "user@example.com's Organization", "subscriptionType": "Claude Max", "apiProvider": "firstParty"},
         "pid": 183948,
         "current_permission_mode": "default",
         "session_state": "idle",
         "capabilities": ["ui_surface_v1"]}
        """;
}
