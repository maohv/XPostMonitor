namespace XPostMonitor.Models;

// Cấu hình riêng cho việc user tự gửi link X vào bot.
// Bảng này không liên quan đến Auto Create của từng username trong WatchlistEntries.
public sealed class LinkTokenSettings
{
    public long ChatId { get; set; }
    public bool EnableAutoCreate { get; set; }
    public string Chain { get; set; } = "bsc";
    public string Launchpad { get; set; } = "fourmeme";
    public string? Anchor { get; set; }
    public int CreatorTaxPercent { get; set; }
    public bool EnableAutoTrading { get; set; }
    public string WorkerSlots { get; set; } = "1";
    public decimal BuyAmount { get; set; } = 0.05m;
    public decimal SlippagePercent { get; set; } = 5m;
    public DateTime UpdatedAtUtc { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
}
