namespace XPostMonitor.Configuration;

public sealed class EvmNetworksOptions
{
    public const string SectionName = "EvmNetworks";

    public string BscRpcUrl { get; set; } = "https://bsc-rpc.publicnode.com";
    public string BaseRpcUrl { get; set; } = "https://mainnet.base.org";
    public string RobinhoodRpcUrl { get; set; } = "https://rpc.mainnet.chain.robinhood.com";
    public string StableRpcUrl { get; set; } = "https://rpc.stable.xyz";
}
