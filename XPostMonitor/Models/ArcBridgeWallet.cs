namespace XPostMonitor.Models;

// Ví riêng cho chức năng Bridge miễn phí. Không liên quan đến ví Trading hoặc GMGN.
public sealed class ArcBridgeWallet
{
    public long ChatId { get; set; }
    public string WalletAddress { get; set; } = string.Empty;
    public string EncryptedPrivateKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
    public ICollection<ArcBridgeTransfer> Transfers { get; set; } = new List<ArcBridgeTransfer>();
}
