using System.Globalization;
using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Web3;
using XPostMonitor.Configuration;

namespace XPostMonitor.Services.Gmgn.Launchpads.Pons;

// Pons tự đọc giá từ pool. GMGN chỉ được gọi khi giá đã đạt TP và cần bán.
public sealed class PonsAutoTradingHandler : ILocalTakeProfitHandler
{
    // WETH chính thức trên Robinhood Chain theo tài liệu của Pons.
    private const string PonsWethAddress = "0x0Bd7D308f8E1639FAb988df18A8011f41EAcAD73";
    // GMGN dùng địa chỉ 0 để biểu diễn ETH native nhận về sau khi bán.
    private const string NativeEthAddress = "0x0000000000000000000000000000000000000000";
    private readonly GmgnClient gmgnClient;
    private readonly EvmNetworksOptions networks;

    public PonsAutoTradingHandler(GmgnClient gmgnClient, EvmNetworksOptions networks)
    {
        this.gmgnClient = gmgnClient;
        this.networks = networks;
    }

    public string GmgnChain => "robinhood";
    public string RpcUrl => networks.RobinhoodRpcUrl;
    public string LocalOrderPrefix => "local:pons:";

    public bool Supports(string chain, string launchpad)
    {
        return string.Equals(chain, "robinhood", StringComparison.OrdinalIgnoreCase)
            && string.Equals(launchpad, "pons", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GmgnTokenPosition> GetPositionAsync(GmgnCredentials credentials,
        string walletAddress, string tokenAddress, CancellationToken cancellationToken)
    {
        // Ví GMGN phải trùng với ví đã dùng để tạo token Pons.
        string linkedWallet = await gmgnClient.GetWalletAddressAsync(credentials.ApiKey, GmgnChain,
            cancellationToken);
        if (!string.Equals(linkedWallet, walletAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The GMGN wallet does not match the Pons launch wallet.");
        }

        decimal entryPrice = await GetCurrentPriceAsync(tokenAddress, cancellationToken);
        return new GmgnTokenPosition(linkedWallet, PonsWethAddress, entryPrice);
    }

    // Đọc giá token/WETH trực tiếp từ slot0 của pool Uniswap V3 do Pons tạo.
    public async Task<decimal> GetCurrentPriceAsync(string tokenAddress,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(RpcUrl);
        string poolAddress = await web3.Eth.GetContractQueryHandler<LiquidityPoolFunction>()
            .QueryAsync<string>(tokenAddress, new LiquidityPoolFunction()).WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(poolAddress)
            || poolAddress == "0x0000000000000000000000000000000000000000")
        {
            throw new InvalidOperationException("Pons liquidity pool was not found.");
        }

        Slot0Output slot = await web3.Eth.GetContractQueryHandler<Slot0Function>()
            .QueryDeserializingToObjectAsync<Slot0Output>(new Slot0Function(), poolAddress)
            .WaitAsync(cancellationToken);
        if (slot.SqrtPriceX96 <= 0)
        {
            throw new InvalidOperationException("Pons pool has no valid price yet.");
        }

        double sqrtRatio = (double)slot.SqrtPriceX96 / Math.Pow(2d, 96d);
        double token1PerToken0 = sqrtRatio * sqrtRatio;
        bool tokenIsToken0 = ReadAddressNumber(tokenAddress) < ReadAddressNumber(PonsWethAddress);
        double priceInWeth = tokenIsToken0 ? token1PerToken0 : 1d / token1PerToken0;
        if (double.IsNaN(priceInWeth) || double.IsInfinity(priceInWeth)
            || priceInWeth <= 0d || priceInWeth > (double)decimal.MaxValue)
        {
            throw new InvalidOperationException("Pons pool returned an invalid token price.");
        }

        return (decimal)priceInWeth;
    }

    private static BigInteger ReadAddressNumber(string address)
    {
        string value = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        return BigInteger.Parse("0" + value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    public Task<string> GetSellQuoteTokenAsync(GmgnCredentials credentials, string tokenAddress,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(NativeEthAddress);
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

    [Function("liquidityPool", "address")]
    private sealed class LiquidityPoolFunction : FunctionMessage
    {
    }

    [Function("slot0", typeof(Slot0Output))]
    private sealed class Slot0Function : FunctionMessage
    {
    }

    [FunctionOutput]
    private sealed class Slot0Output : IFunctionOutputDTO
    {
        [Parameter("uint160", "sqrtPriceX96", 1)] public BigInteger SqrtPriceX96 { get; set; }
        [Parameter("int24", "tick", 2)] public int Tick { get; set; }
        [Parameter("uint16", "observationIndex", 3)] public ushort ObservationIndex { get; set; }
        [Parameter("uint16", "observationCardinality", 4)] public ushort ObservationCardinality { get; set; }
        [Parameter("uint16", "observationCardinalityNext", 5)] public ushort ObservationCardinalityNext { get; set; }
        [Parameter("uint8", "feeProtocol", 6)] public byte FeeProtocol { get; set; }
        [Parameter("bool", "unlocked", 7)] public bool Unlocked { get; set; }
    }
}
