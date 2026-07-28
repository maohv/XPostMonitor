namespace XPostMonitor.Models;

public sealed class UserTradingSettings
{
    public long ChatId { get; set; }
    public bool EnableTokenCreation { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
}
