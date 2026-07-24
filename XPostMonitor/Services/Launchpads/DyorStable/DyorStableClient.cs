using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Net.Http.Headers;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using XPostMonitor.Configuration;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Launchpads.DyorStable;

public sealed class DyorStableClient
{
    private const long ChainId = 988;
    private const string FactoryAddress = "0xDFEf2F90F7E52609cC89b80b68Ff6a1C86C4ddc4";
    private const string PairTokenAddress = "0x817997Ca8394E26CCE3dE3A076a4889b27DbF9dE";
    private const string DryRunImageCid = "QmYwAPJzv5CZsnAzt8auVZRnGiRAzVcoHqX6NhZ9VY7K7J";

    private readonly HttpClient httpClient;
    private readonly DyorStableOptions options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> walletLocks = new();

    public DyorStableClient(HttpClient httpClient, DyorStableOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<DyorStableConnectionResult> CheckAsync(string? walletAddress,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        HexBigInteger chainId = await web3.Eth.ChainId.SendRequestAsync()
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string code = await web3.Eth.GetCode.SendRequestAsync(FactoryAddress)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        decimal? balance = null;

        if (!string.IsNullOrWhiteSpace(walletAddress))
        {
            HexBigInteger value = await web3.Eth.GetBalance.SendRequestAsync(walletAddress)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            balance = UnitConversion.Convert.FromWei(value.Value);
        }

        return new DyorStableConnectionResult(chainId.Value == ChainId, code != "0x", balance,
            !string.IsNullOrWhiteSpace(options.PinataJwt), options.EnableRealTransactions);
    }

    // This method sends a real Stable transaction when EnableRealTransactions is true.
    public async Task<DyorStableTokenResult> CreateTokenAsync(EvmWalletCredentials wallet,
        DyorStableTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.Image.Length == 0)
        {
            throw new InvalidOperationException("DYOR Swap requires a token image.");
        }

        SemaphoreSlim walletLock = walletLocks.GetOrAdd(wallet.Address, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateTokenInternalAsync(wallet, request, cancellationToken);
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<DyorStableTokenResult> CreateTokenInternalAsync(EvmWalletCredentials wallet,
        DyorStableTokenRequest request, CancellationToken cancellationToken)
    {
        Account account = new Account(wallet.PrivateKey, ChainId);
        if (!string.Equals(account.Address, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The encrypted private key does not match the EVM wallet address.");
        }

        string imageCid = options.EnableRealTransactions
            ? await UploadImageAsync(request.Image, cancellationToken)
            : DryRunImageCid;
        BigInteger buyAmount = UnitConversion.Convert.ToWei(request.BuyAmount);
        CreatePairFunction function = new CreatePairFunction
        {
            ProjectOwner = account.Address,
            PairToken = PairTokenAddress,
            AmountIn = buyAmount,
            AmountToSend = buyAmount,
            Token = new PumpTokenParameters
            {
                Name = Clean(request.Name, 100),
                Symbol = Clean(request.Symbol, 20),
                Description = Clean(request.Description, 256),
                Image = imageCid,
                Twitter = request.PostUrl
            }
        };

        Web3 web3 = new Web3(account, options.RpcUrl);
        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address);
        if (!options.EnableRealTransactions && balance.Value < buyAmount)
        {
            return new DyorStableTokenResult(null, null, null, buyAmount, null, true, false);
        }

        var handler = web3.Eth.GetContractTransactionHandler<CreatePairFunction>();
        HexBigInteger gas = await handler.EstimateGasAsync(FactoryAddress, function);
        HexBigInteger gasPrice = await web3.Eth.GasPrice.SendRequestAsync();
        BigInteger requiredBalance = buyAmount + gas.Value * gasPrice.Value;

        if (!options.EnableRealTransactions)
        {
            return new DyorStableTokenResult(null, null, null, buyAmount, gas.Value, true,
                balance.Value >= requiredBalance);
        }

        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient USDT0. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance).ToString("0.########", CultureInfo.InvariantCulture)
                + " USDT0 including initial buy and gas.");
        }

        function.Gas = gas;
        function.GasPrice = gasPrice;
        var receipt = await handler.SendRequestAndWaitForReceiptAsync(FactoryAddress, function,
            cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("DYOR Swap token transaction failed: " + receipt.TransactionHash);
        }

        EventLog<PairCreatedEventDto>? created = receipt.DecodeAllEvents<PairCreatedEventDto>().FirstOrDefault();
        string? tokenAddress = created == null ? null : FindCreatedToken(created.Event);
        return new DyorStableTokenResult(receipt.TransactionHash, tokenAddress, created?.Event.Pair,
            buyAmount, gas.Value, false, true);
    }

    private async Task<string> UploadImageAsync(byte[] image, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PinataJwt))
        {
            throw new InvalidOperationException("DyorStable:PinataJwt is required for a real token.");
        }

        using MultipartFormDataContent form = new MultipartFormDataContent();
        using ByteArrayContent file = new ByteArrayContent(image);
        bool isJpeg = image.Length >= 2 && image[0] == 0xFF && image[1] == 0xD8;
        file.Headers.ContentType = new MediaTypeHeaderValue(isJpeg ? "image/jpeg" : "image/png");
        form.Add(file, "file", isJpeg ? "token.jpg" : "token.png");

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "pinning/pinFileToIPFS");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.PinataJwt);
        request.Content = form;
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Pinata upload failed: HTTP " + (int)response.StatusCode + " "
                + Shorten(json), null, response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("IpfsHash").GetString()
            ?? throw new JsonException("Pinata returned an empty IPFS hash.");
    }

    private static string FindCreatedToken(PairCreatedEventDto created)
    {
        return string.Equals(created.Token0, PairTokenAddress, StringComparison.OrdinalIgnoreCase)
            ? created.Token1
            : created.Token0;
    }

    private static string Clean(string value, int maximumLength)
    {
        string clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("DYOR Swap token metadata cannot be empty.");
        }

        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    [Function("createPair", "address")]
    private sealed class CreatePairFunction : FunctionMessage
    {
        [Parameter("address", "_projectOwner", 1)]
        public string ProjectOwner { get; set; } = string.Empty;

        [Parameter("address", "_pairToken", 2)]
        public string PairToken { get; set; } = string.Empty;

        [Parameter("uint256", "_amountIn", 3)]
        public BigInteger AmountIn { get; set; }

        [Parameter("tuple", "params", 4)]
        public PumpTokenParameters Token { get; set; } = new PumpTokenParameters();
    }

    [Struct("PumpTokenStruct")]
    private sealed class PumpTokenParameters
    {
        [Parameter("string", "name", 1)] public string Name { get; set; } = string.Empty;
        [Parameter("string", "symbol", 2)] public string Symbol { get; set; } = string.Empty;
        [Parameter("string", "description", 3)] public string Description { get; set; } = string.Empty;
        [Parameter("string", "image", 4)] public string Image { get; set; } = string.Empty;
        [Parameter("string", "website", 5)] public string Website { get; set; } = string.Empty;
        [Parameter("string", "telegram", 6)] public string Telegram { get; set; } = string.Empty;
        [Parameter("string", "twitter", 7)] public string Twitter { get; set; } = string.Empty;
        [Parameter("string", "meta", 8)] public string Meta { get; set; } = string.Empty;
        [Parameter("uint256", "totalSupply", 9)] public BigInteger TotalSupply { get; set; }
        [Parameter("uint256", "realEthReserves", 10)] public BigInteger RealEthReserves { get; set; }
        [Parameter("uint256", "realTokenReserves", 11)] public BigInteger RealTokenReserves { get; set; }
        [Parameter("uint256", "liquidityEth", 12)] public BigInteger LiquidityEth { get; set; }
        [Parameter("uint256", "liquidityToken", 13)] public BigInteger LiquidityToken { get; set; }
        [Parameter("uint256", "initialVirtualTokenSlippage", 14)] public BigInteger InitialVirtualTokenSlippage { get; set; }
        [Parameter("uint256", "initialVirtualEthReserves", 15)] public BigInteger InitialVirtualEthReserves { get; set; }
        [Parameter("uint256", "initialVirtualTokenReserves", 16)] public BigInteger InitialVirtualTokenReserves { get; set; }
    }

    [Event("PairCreated")]
    private sealed class PairCreatedEventDto : IEventDTO
    {
        [Parameter("address", "token0", 1, true)] public string Token0 { get; set; } = string.Empty;
        [Parameter("address", "token1", 2, true)] public string Token1 { get; set; } = string.Empty;
        [Parameter("address", "pair", 3, false)] public string Pair { get; set; } = string.Empty;
        [Parameter("uint256", "", 4, false)] public BigInteger Index { get; set; }
    }
}

public sealed record DyorStableTokenRequest(string Name, string Symbol, string Description, byte[] Image,
    string PostUrl, decimal BuyAmount);

public sealed record DyorStableTokenResult(string? TransactionHash, string? TokenAddress, string? LaunchAddress,
    BigInteger TransactionValueWei, BigInteger? EstimatedGas, bool IsDryRun, bool HasEnoughBalance);

public sealed record DyorStableConnectionResult(bool CorrectChain, bool FactoryFound, decimal? Balance,
    bool PinataConfigured, bool EnableRealTransactions);
