namespace XPostMonitor.Configuration;

public sealed class FlapRobinhoodOptions
{
    public const string SectionName = "FlapRobinhood";

    public string RpcUrl { get; set; } = "https://rpc.mainnet.chain.robinhood.com";

    // Mặc định tắt để lần đầu chỉ dry-run, không gửi giao dịch và không tiêu ETH.
    public bool EnableRealTransactions { get; set; }
}
