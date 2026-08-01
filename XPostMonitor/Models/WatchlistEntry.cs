namespace XPostMonitor.Models;

public sealed class WatchlistEntry
{
    public long ChatId { get; set; }
    public string XUserId { get; set; } = string.Empty;
    public string? TokenChain { get; set; }
    public string? TokenDex { get; set; }
    public string? TokenAnchor { get; set; }
    public int CreatorTaxPercent { get; set; }
    public bool EnableAutoTrading { get; set; }
    public int ParallelTokenCount { get; set; } = 1;
    public bool CreateTokenOnPost { get; set; } = true;
    public bool CreateTokenOnReply { get; set; } = true;
    public bool CreateTokenOnQuote { get; set; } = true;
    public bool CreateTokenOnRepost { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
    public XAccount XAccount { get; set; } = null!;
}
