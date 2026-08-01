namespace XPostMonitor.Models;

// Lưu token Alpha đã thấy để bot không gửi trùng khi restart hoặc mất kết nối.
public sealed class BinanceAlphaToken
{
    public string TokenId { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string ChainName { get; set; } = string.Empty;
    public string ContractAddress { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string? AlphaId { get; set; }
    public string? IconUrl { get; set; }
    public DateTime? ListingTimeUtc { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime? NotifiedAtUtc { get; set; }
}
