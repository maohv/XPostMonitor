namespace XPostMonitor.Models;

public sealed class WatchlistEntry
{
    public long ChatId { get; set; }
    public string XUserId { get; set; } = string.Empty;
    public string? TokenChain { get; set; }
    public string? TokenDex { get; set; }
    public string? TokenAnchor { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
    public XAccount XAccount { get; set; } = null!;
}
