using System.Numerics;
using Nethereum.Web3;
using XPostMonitor.Configuration;

namespace XPostMonitor.Services.Gmgn.Launchpads.FlapRobinhood;

// Auto trading của Flap Robinhood nằm riêng, không dùng handler Flap BSC.
public sealed class FlapRobinhoodAutoTradingHandler : IAutoTradingLaunchpadHandler
{
    private const string NativeEthAddress = "0x0000000000000000000000000000000000000000";
    private readonly GmgnClient gmgnClient;
    private readonly EvmNetworksOptions networks;

    public FlapRobinhoodAutoTradingHandler(GmgnClient gmgnClient, EvmNetworksOptions networks)
    {
        this.gmgnClient = gmgnClient;
        this.networks = networks;
    }

    public string GmgnChain => "robinhood";
    public string RpcUrl => networks.RobinhoodRpcUrl;

    public bool Supports(string chain, string launchpad)
    {
        return string.Equals(chain, "robinhood", StringComparison.OrdinalIgnoreCase)
            && string.Equals(launchpad, "flap", StringComparison.OrdinalIgnoreCase);
    }

    public Task<GmgnTokenPosition> GetPositionAsync(GmgnCredentials credentials,
        string walletAddress, string tokenAddress, CancellationToken cancellationToken)
    {
        return gmgnClient.GetTokenPositionAsync(credentials, GmgnChain, walletAddress,
            tokenAddress, cancellationToken);
    }

    public Task<string> GetSellQuoteTokenAsync(GmgnCredentials credentials, string tokenAddress,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(NativeEthAddress);
    }

    // Robinhood gửi số lượng token cụ thể cho GMGN khi bán.
    public async Task<BigInteger?> GetSellAmountAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken)
    {
        return await GetTokenBalanceAsync(walletAddress, tokenAddress, cancellationToken);
    }

    public async Task<BigInteger> GetTokenBalanceAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(RpcUrl);
        return await web3.Eth.ERC20.GetContractService(tokenAddress)
            .BalanceOfQueryAsync(walletAddress).WaitAsync(cancellationToken);
    }

    public Task<decimal?> GetTakeProfitGasPriceGweiAsync(GmgnCredentials credentials,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<decimal?>(null);
    }
}
