namespace XPostMonitor.Models;

// Ví NFT nằm riêng hoàn toàn với ví tạo token và ví Bridge.
public sealed class NftWallet
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public int SlotNumber { get; set; } // 0 = ví chính; từ 1 trở lên = ví mint.
    public NftWalletGroup MintGroup { get; set; } = NftWalletGroup.Free;
    public string WalletAddress { get; set; } = string.Empty;
    public string EncryptedPrivateKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public TelegramUser TelegramUser { get; set; } = null!;
}

public enum NftWalletGroup
{
    Free = 0,
    Paid = 1
}
