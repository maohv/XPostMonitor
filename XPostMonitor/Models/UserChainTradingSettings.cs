namespace XPostMonitor.Models;

// Lưu số tiền và slippage riêng cho từng network của một Telegram user.
public sealed class UserChainTradingSettings
{
    public long ChatId { get; set; }
    public string Chain { get; set; } = string.Empty;
    public decimal BuyAmount { get; set; }
    public decimal SlippagePercent { get; set; } = 5m;

    public TelegramUser TelegramUser { get; set; } = null!;
}
