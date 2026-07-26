using System.Numerics;
using Nethereum.Web3;
using XPostMonitor.Configuration;

namespace XPostMonitor.Services.Gmgn.Launchpads.FourMeme;

// Toàn bộ cách đặt TP và bán của FourMeme nằm riêng trong file này.
// Logic được giữ nguyên như trước khi tách launchpad.
public sealed class FourMemeAutoTradingHandler : IAutoTradingLaunchpadHandler
{
    private const string NativeBnbAddress = "0x0000000000000000000000000000000000000000";
    private readonly GmgnClient gmgnClient;
    private readonly EvmNetworksOptions networks;

    public FourMemeAutoTradingHandler(GmgnClient gmgnClient, EvmNetworksOptions networks)
    {
        this.gmgnClient = gmgnClient;
        this.networks = networks;
    }

    public string GmgnChain => "bsc";
    public string RpcUrl => networks.BscRpcUrl;

    public bool Supports(string chain, string launchpad)
    {
        return string.Equals(chain, "bsc", StringComparison.OrdinalIgnoreCase)
            && string.Equals(launchpad, "fourmeme", StringComparison.OrdinalIgnoreCase);
    }

    public Task<GmgnTokenPosition> GetPositionAsync(GmgnCredentials credentials, string walletAddress,
        string tokenAddress, CancellationToken cancellationToken)
    {
        return gmgnClient.GetTokenPositionAsync(credentials, GmgnChain, walletAddress, tokenAddress,
            cancellationToken);
    }

    public Task<string> GetSellQuoteTokenAsync(GmgnCredentials credentials, string tokenAddress,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(NativeBnbAddress);
    }

    public Task<BigInteger?> GetSellAmountAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken)
    {
        // GMGN dùng 100% số dư khi amount là null trên BSC.
        return Task.FromResult<BigInteger?>(null);
    }

    public async Task<BigInteger> GetTokenBalanceAsync(string walletAddress, string tokenAddress,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(RpcUrl);
        return await web3.Eth.ERC20.GetContractService(tokenAddress).BalanceOfQueryAsync(walletAddress)
            .WaitAsync(cancellationToken);
    }

    public async Task<decimal?> GetTakeProfitGasPriceGweiAsync(GmgnCredentials credentials,
        CancellationToken cancellationToken)
    {
        decimal gasPrice = await gmgnClient.GetAverageGasPriceGweiAsync(credentials, GmgnChain,
            cancellationToken);
        return Math.Max(gasPrice, 0.05m);
    }
}
