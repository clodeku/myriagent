using Myriagent.Sync;
using MyriagentHub.Data;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Web;

/// <summary>
/// 擁有者漫遊偏好的主題（sync.db 的 prefs，鍵 theme）：/app/ 的 index.html 給已登入的擁有者淺色版用。
/// 只在已登入的 index 請求時讀一列、不快取（唯讀短連線，不經寫入者）
/// </summary>
internal sealed class PrefsThemeSource(SyncWriter writer, ILogger<PrefsThemeSource> log)
{
    /// <summary>dark／light／system；沒有設定、不合格（PrefsRules）或讀取失敗回 null（失敗記警告）</summary>
    public string? GetTheme()
    {
        try
        {
            using var connection = writer.OpenRead();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value_json FROM prefs WHERE key = 'theme'";
            if (cmd.ExecuteScalar() is not string json)
                return null;
            var value = JToken.Parse(json);
            if (!PrefsRules.IsValid("theme", value))
            {
                log.LogWarning("sync.db 的 theme 偏好不合格，改用預設");
                return null;
            }
            return (string?)value;
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException or IOException)
        {
            log.LogWarning(ex, "讀不到漫遊的主題偏好");
            return null;
        }
    }
}
