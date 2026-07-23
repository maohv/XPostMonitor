namespace XPostMonitor.Models;

public sealed class UserTradingSettings
{
    public long ChatId { get; set; }
    public string WalletAddress { get; set; } = string.Empty;
    public string EncryptedPrivateKey { get; set; } = string.Empty;
    public string EncryptedGmgnApiKey { get; set; } = string.Empty;
    public decimal BuyAmount { get; set; } = 0.005m;
    public decimal SlippagePercent { get; set; } = 5m;
    public bool EnableTokenCreation { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
}
