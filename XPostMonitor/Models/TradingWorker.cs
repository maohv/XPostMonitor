namespace XPostMonitor.Models;

// Một worker gồm một ví EVM và một kết nối GMGN riêng.
public sealed class TradingWorker
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public int SlotNumber { get; set; }
    public string EvmWalletAddress { get; set; } = string.Empty;
    public string EncryptedEvmPrivateKey { get; set; } = string.Empty;
    public string EncryptedGmgnApiKey { get; set; } = string.Empty;
    public string EncryptedGmgnPrivateKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
    public ICollection<AutoTrade> AutoTrades { get; set; } = new List<AutoTrade>();
}
