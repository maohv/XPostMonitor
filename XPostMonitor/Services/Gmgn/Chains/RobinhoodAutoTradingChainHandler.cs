using System.Numerics;
using Nethereum.Web3;
using XPostMonitor.Configuration;

namespace XPostMonitor.Services.Gmgn.Chains;

public sealed class RobinhoodAutoTradingChainHandler : IAutoTradingChainHandler
{
    private readonly GmgnClient gmgnClient;
    private readonly EvmNetworksOptions networks;

    public RobinhoodAutoTradingChainHandler(GmgnClient gmgnClient, EvmNetworksOptions networks)
    {
        this.gmgnClient = gmgnClient;
        this.networks = networks;
    }

    public string GmgnChain => "robinhood";
    public string RpcUrl => networks.RobinhoodRpcUrl;

    public bool Supports(string chain, string launchpad)
    {
        return string.Equals(chain, "robinhood", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(launchpad, "pons", StringComparison.OrdinalIgnoreCase)
                || string.Equals(launchpad, "long", StringComparison.OrdinalIgnoreCase));
    }

    public Task<string> GetSellQuoteTokenAsync(GmgnCredentials credentials, string tokenAddress,
        CancellationToken cancellationToken)
    {
        return gmgnClient.GetQuoteTokenAsync(credentials, GmgnChain, tokenAddress, cancellationToken);
    }

    public async Task<BigInteger?> GetSellAmountAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken)
    {
        return await GetTokenBalanceAsync(walletAddress, tokenAddress, cancellationToken);
    }

    public async Task<BigInteger> GetTokenBalanceAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(RpcUrl);
        return await web3.Eth.ERC20.GetContractService(tokenAddress).BalanceOfQueryAsync(walletAddress)
            .WaitAsync(cancellationToken);
    }

    public Task<decimal?> GetTakeProfitGasPriceGweiAsync(GmgnCredentials credentials,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<decimal?>(null);
    }
}
