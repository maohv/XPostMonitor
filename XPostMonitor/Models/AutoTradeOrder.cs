namespace XPostMonitor.Models;

// Lưu một lệnh TP đã tạo trên GMGN.
public sealed class AutoTradeOrder
{
    public long Id { get; set; }
    public long AutoTradeId { get; set; }
    public string GmgnOrderId { get; set; } = string.Empty;
    public decimal ProfitPercent { get; set; }
    public decimal SellPercent { get; set; }
    public decimal TargetPrice { get; set; }
    public string Status { get; set; } = "open";
    public string? TransactionHash { get; set; }
    public decimal? RealizedProfitUsd { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime? NotifiedAtUtc { get; set; }

    public AutoTrade AutoTrade { get; set; } = null!;
}
