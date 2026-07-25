namespace XPostMonitor.Models;

// Lưu một token đã giao cho GMGN quản lý để bot không mất dấu sau khi restart.
public sealed class AutoTrade
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public string PostId { get; set; } = string.Empty;
    public string Chain { get; set; } = string.Empty;
    public string TokenAddress { get; set; } = string.Empty;
    public string TokenName { get; set; } = string.Empty;
    public string TokenSymbol { get; set; } = string.Empty;
    public string WalletAddress { get; set; } = string.Empty;
    public string QuoteTokenAddress { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public string Status { get; set; } = "preparing";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
    public ICollection<AutoTradeOrder> Orders { get; set; } = new List<AutoTradeOrder>();
}
