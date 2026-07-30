namespace XPostMonitor.Configuration;

// Cau hinh rieng cho chuc nang bridge USDC Base sang Arc.
public sealed class ArcBridgeOptions
{
    public const string SectionName = "ArcBridge";

    public bool Enabled { get; set; } = true;
    public string BaseRpcUrl { get; set; } = "https://mainnet.base.org";
    public string ArcRpcUrl { get; set; } = "https://rpc.blockdaemon.mainnet.arc.io";
    public int PollIntervalSeconds { get; set; } = 5;
    public int PollTimeoutMinutes { get; set; } = 120;
}
