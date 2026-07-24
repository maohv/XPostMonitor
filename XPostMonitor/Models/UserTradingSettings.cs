namespace XPostMonitor.Models;

public sealed class UserTradingSettings
{
    public long ChatId { get; set; }
    public string EncryptedGmgnApiKey { get; set; } = string.Empty;
    public string EncryptedGmgnPrivateKey { get; set; } = string.Empty;
    public string EvmWalletAddress { get; set; } = string.Empty;
    public string EncryptedEvmPrivateKey { get; set; } = string.Empty;
    public bool EnableTokenCreation { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
}