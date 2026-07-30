namespace XPostMonitor.Configuration;

public sealed class FlapOptions
{
    public const string SectionName = "Flap";

    public string RpcUrl { get; set; } = "https://bsc-dataseed.binance.org";
    public string BundleRpcUrl { get; set; } = "https://puissant-builder.48.club/";
    public bool EnableRealTransactions { get; set; }
}
