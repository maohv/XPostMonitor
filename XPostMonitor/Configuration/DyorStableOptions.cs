namespace XPostMonitor.Configuration;

public sealed class DyorStableOptions
{
    public const string SectionName = "DyorStable";

    public string RpcUrl { get; set; } = "https://rpc.stable.xyz";
    public string PinataJwt { get; set; } = string.Empty;
    public bool EnableRealTransactions { get; set; }
}
