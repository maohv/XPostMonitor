namespace XPostMonitor.Configuration;

public sealed class LongRobinhoodOptions
{
    public const string SectionName = "LongRobinhood";

    public string RpcUrl { get; set; } = "https://rpc.mainnet.chain.robinhood.com";
    public string LogRpcUrl { get; set; } = "https://rpc.mainnet.chain.robinhood.com";
    public string PinataJwt { get; set; } = string.Empty;
    public bool EnableRealTransactions { get; set; }
}
