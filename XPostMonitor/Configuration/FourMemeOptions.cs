namespace XPostMonitor.Configuration;

public sealed class FourMemeOptions
{
    public const string SectionName = "FourMeme";

    public string RpcUrl { get; set; } = "https://bsc-dataseed.binance.org";
    public bool EnableRealTransactions { get; set; }
}
