namespace XPostMonitor.Configuration;

public sealed class PonsRobinhoodOptions
{
    public const string SectionName = "PonsRobinhood";

    public string RpcUrl { get; set; } = "https://rpc.mainnet.chain.robinhood.com";
    public string PinataJwt { get; set; } = string.Empty;
    public bool EnableRealTransactions { get; set; }
}
