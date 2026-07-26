using System.Numerics;

namespace XPostMonitor.Services.Gmgn.Chains;

public interface IAutoTradingChainHandler
{
    string GmgnChain { get; }
    string RpcUrl { get; }

    bool Supports(string chain, string launchpad);
    Task<string> GetSellQuoteTokenAsync(GmgnCredentials credentials, string tokenAddress,
        CancellationToken cancellationToken);
    Task<BigInteger> GetTokenBalanceAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken);
    Task<BigInteger?> GetSellAmountAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken);
    Task<decimal?> GetTakeProfitGasPriceGweiAsync(GmgnCredentials credentials,
        CancellationToken cancellationToken);
}
