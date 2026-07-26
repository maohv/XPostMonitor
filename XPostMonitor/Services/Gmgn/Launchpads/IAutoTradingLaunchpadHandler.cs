using System.Numerics;

namespace XPostMonitor.Services.Gmgn.Launchpads;

// Mỗi launchpad tự chuẩn bị vị thế và cách bán của chính nó.
// Nhờ vậy sửa Pons sẽ không làm thay đổi FourMeme.
public interface IAutoTradingLaunchpadHandler
{
    string GmgnChain { get; }
    string RpcUrl { get; }

    bool Supports(string chain, string launchpad);
    Task<GmgnTokenPosition> GetPositionAsync(GmgnCredentials credentials, string walletAddress,
        string tokenAddress, CancellationToken cancellationToken);
    Task<string> GetSellQuoteTokenAsync(GmgnCredentials credentials, string tokenAddress,
        CancellationToken cancellationToken);
    Task<BigInteger> GetTokenBalanceAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken);
    Task<BigInteger?> GetSellAmountAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken);
    Task<decimal?> GetTakeProfitGasPriceGweiAsync(GmgnCredentials credentials,
        CancellationToken cancellationToken);
}
