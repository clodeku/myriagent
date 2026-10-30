namespace MyriagentHub.Auth;

/// <summary>擁有者帳號與密碼的純函式規則（設計 14.4）</summary>
internal static class PasswordRules
{
    /// <summary>新密碼最短長度（預設密碼也是 8 字）</summary>
    public const int MinLength = 8;

    /// <summary>密碼上限：雜湊（PBKDF2）前就擋掉，超長的輸入不花運算</summary>
    public const int MaxLength = 128;

    public const int MaxUserNameLength = 64;

    /// <summary>送來驗證的密碼（登入、再驗證、目前的密碼）：格式不對就當驗證失敗，不拿去雜湊</summary>
    public static bool IsCandidate(string? password) => !string.IsNullOrEmpty(password) && password.Length <= MaxLength;

    /// <summary>登入的帳號：去頭尾空白後 1～64 字；不合格回 null</summary>
    public static string? CleanUserName(string? userName)
    {
        var trimmed = userName?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxUserNameLength ? null : trimmed;
    }

    /// <summary>新密碼：null＝合格，否則是錯誤代碼（8～128 字、不含控制字元、不能和目前的相同）</summary>
    public static string? CheckNew(string? password, string? current)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return "password_too_short";
        if (password.Length > MaxLength)
            return "password_too_long";
        if (password.Any(char.IsControl))
            return "password_invalid";
        if (string.Equals(password, current, StringComparison.Ordinal))
            return "password_unchanged";
        return null;
    }
}
