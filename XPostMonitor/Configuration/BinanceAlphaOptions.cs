namespace XPostMonitor.Configuration;

// Cấu hình riêng cho luồng theo dõi token mới trên Binance Alpha.
public sealed class BinanceAlphaOptions
{
    public const string SectionName = "BinanceAlpha";

    public bool Enabled { get; set; }
    public long TelegramChannelId { get; set; }
    public int RestCheckIntervalSeconds { get; set; } = 10;
}
