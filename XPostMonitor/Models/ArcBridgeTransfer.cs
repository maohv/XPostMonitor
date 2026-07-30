namespace XPostMonitor.Models;

// Lưu giao dịch Bridge để bot có thể tiếp tục nếu VPS khởi động lại trong lúc chờ Circle.
public sealed class ArcBridgeTransfer
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public string WalletAddress { get; set; } = string.Empty;
    public string Status { get; set; } = ArcBridgeTransferStatus.Preparing;
    public string GrossAmountAtomic { get; set; } = string.Empty;
    public string ReceiveAmountAtomic { get; set; } = string.Empty;
    public string MaxFeeAtomic { get; set; } = string.Empty;
    public string BurnIntentJson { get; set; } = string.Empty;
    public string? DepositTransactionHash { get; set; }
    public string? DepositBlockNumber { get; set; }
    public string? MintTransactionHash { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public ArcBridgeWallet Wallet { get; set; } = null!;
}

public static class ArcBridgeTransferStatus
{
    public const string Preparing = "Preparing";
    public const string Depositing = "Depositing";
    public const string WaitingCircle = "WaitingCircle";
    public const string Minting = "Minting";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
