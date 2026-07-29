namespace XPostMonitor.Configuration;

public sealed class FlapOptions
{
    public const string SectionName = "Flap";

    public string RpcUrl { get; set; } = "https://bsc-dataseed.binance.org";
    public bool EnableRealTransactions { get; set; }
}
