namespace XPostMonitor.Models;

public sealed class TelegramUser
{
    public long ChatId { get; set; }
    public long TelegramUserId { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public bool IsPremium { get; set; }
    public string LanguageCode { get; set; } = "en";

    public ICollection<WatchlistEntry> Watchlist { get; set; } = new List<WatchlistEntry>();
    public ICollection<UserChainTradingSettings> ChainTradingSettings { get; set; } = new List<UserChainTradingSettings>();
    public UserTradingSettings? TradingSettings { get; set; }
}
