namespace XPostMonitor.Configuration;

public sealed class OpenSeaNftOptions
{
    public const string SectionName = "OpenSeaNft";

    public string RpcUrl { get; set; } = "https://rpc.mainnet.chain.robinhood.com";
    public string SeaDropAddress { get; set; } = "0x00005EA00Ac477B1030CE78506496e8C2dE24bf5";
    public bool EnableRealTransactions { get; set; }
    public int MaxWallets { get; set; } = 30;
    public int SendDelayMs { get; set; } = 150;
    public decimal MaxMintValuePerWalletEth { get; set; } = 0.05m;
}
