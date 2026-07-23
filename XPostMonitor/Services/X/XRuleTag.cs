namespace XPostMonitor.Services.X;

// Tạo và đọc tag để biết Post mới thuộc tài khoản X nào trong database.
public static class XRuleTag
{
    private const string Prefix = "xpost:";

    // Ghép X user ID vào prefix cố định khi tạo Filtered Stream rule.
    public static string Create(string xUserId)
    {
        return Prefix + xUserId;
    }

    // Lấy X user ID ra khỏi tag; trả false nếu tag không thuộc ứng dụng này.
    public static bool TryGetXUserId(string? tag, out string xUserId)
    {
        if (tag != null && tag.StartsWith(Prefix, StringComparison.Ordinal))
        {
            xUserId = tag[Prefix.Length..];
            return true;
        }

        xUserId = string.Empty;
        return false;
    }
}
