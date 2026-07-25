namespace XPostMonitor.Models;

// Một mức chốt lời dùng chung cho các token của một Telegram user.
public sealed class TakeProfitSetting
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public decimal ProfitPercent { get; set; }
    public decimal SellPercent { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
}
