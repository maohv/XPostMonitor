namespace XPostMonitor.Models;

public sealed class UserTradingSettings
{
    public long ChatId { get; set; }
    public bool EnableTokenCreation { get; set; }
    // Flap chá»‰ cáº§n lÆ°u pháº§n chia cho holder. Pháº§n cá»§a Dev luÃ´n báº±ng 100 - giÃ¡ trá»‹ nÃ y.
    public int FlapHolderPercent { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
}
