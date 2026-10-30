using System.Globalization;

namespace Myriagent.Update;

/// <summary>
/// 桌面 app 的發佈版本：正好三段十進位數字「主.次.修訂」（修訂＝main 上的提交數，設計 6.1）。
/// 不收前導零、預發布字尾與第四段：同一個版本只有一種寫法，比較只看數字
/// </summary>
internal readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>每段最多 9 位數（int 不會溢位）</summary>
    private const int MaxDigits = 9;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 3)
            return false;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > MaxDigits || (part.Length > 1 && part[0] == '0') || !part.All(char.IsAsciiDigit))
                return false;
            numbers[i] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0)
            return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
