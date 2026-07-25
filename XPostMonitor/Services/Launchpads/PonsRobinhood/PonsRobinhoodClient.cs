using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using XPostMonitor.Configuration;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Launchpads.PonsRobinhood;

public sealed class PonsRobinhoodClient
{
    private const long ChainId = 4663;
    private const int LaunchConfigId = 0;
    private const int DexId = 0;
    private const string FactoryAddress = "0xA5aAb3F0c6EeadF30Ef1D3Eb997108E976351feB";
    private const string DryRunLogo = "ipfs://bafkreidw2jltlq6iracbff6kezkytfartob7djta6a6omsbtde3tevy3eq";

    private readonly HttpClient httpClient;
    private readonly PonsRobinhoodOptions options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> walletLocks = new();

    public PonsRobinhoodClient(HttpClient httpClient, PonsRobinhoodOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<PonsRobinhoodConnectionResult> CheckAsync(CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        HexBigInteger chainId = await web3.Eth.ChainId.SendRequestAsync()
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string code = await web3.Eth.GetCode.SendRequestAsync(FactoryAddress)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        bool launchEnabled = await web3.Eth.GetContractQueryHandler<LaunchEnabledFunction>()
            .QueryAsync<bool>(FactoryAddress, new LaunchEnabledFunction()).WaitAsync(cancellationToken);
        return new PonsRobinhoodConnectionResult(chainId.Value == ChainId, code != "0x", launchEnabled);
    }

    public async Task<PonsRobinhoodTokenResult> CreateTokenAsync(EvmWalletCredentials wallet,
        PonsRobinhoodTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.Image.Length == 0)
        {
            throw new InvalidOperationException("pons requires a token image.");
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

    private async Task<PonsRobinhoodTokenResult> CreateTokenInternalAsync(EvmWalletCredentials wallet,
        PonsRobinhoodTokenRequest request, CancellationToken cancellationToken)
    {
        Account account = new Account(wallet.PrivateKey, ChainId);
        if (!string.Equals(account.Address, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The encrypted private key does not match the EVM wallet address.");
        }

        Web3 web3 = new Web3(account, options.RpcUrl);
        bool launchEnabled = await web3.Eth.GetContractQueryHandler<LaunchEnabledFunction>()
            .QueryAsync<bool>(FactoryAddress, new LaunchEnabledFunction()).WaitAsync(cancellationToken);
        if (!launchEnabled)
        {
            throw new InvalidOperationException("pons launches are currently disabled.");
        }

        BigInteger launchFee = await web3.Eth.GetContractQueryHandler<LaunchFeeFunction>()
            .QueryAsync<BigInteger>(FactoryAddress, new LaunchFeeFunction()).WaitAsync(cancellationToken);
        BigInteger buyAmount = UnitConversion.Convert.ToWei(request.BuyAmount);
        BigInteger transactionValue = launchFee + buyAmount;
        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address)
            .WaitAsync(cancellationToken);

        if (options.EnableRealTransactions && balance.Value < transactionValue)
        {
            throw new InvalidOperationException("Insufficient ETH for the pons launch fee and initial buy.");
        }

        string logo = options.EnableRealTransactions
            ? await UploadImageAsync(request.Image, cancellationToken)
            : DryRunLogo;
        LaunchTokenFunction function = BuildLaunchFunction(account.Address, logo, request);
        function.AmountToSend = transactionValue;

        if (!options.EnableRealTransactions && balance.Value < transactionValue)
        {
            return new PonsRobinhoodTokenResult(null, null, transactionValue, null, true, false);
        }

        var handler = web3.Eth.GetContractTransactionHandler<LaunchTokenFunction>();
        HexBigInteger gas = await handler.EstimateGasAsync(FactoryAddress, function)
            .WaitAsync(cancellationToken);
        HexBigInteger currentGasPrice = await web3.Eth.GasPrice.SendRequestAsync().WaitAsync(cancellationToken);
        HexBigInteger gasPrice = new HexBigInteger(currentGasPrice.Value * 120 / 100);
        BigInteger requiredBalance = transactionValue + gas.Value * gasPrice.Value;

        if (!options.EnableRealTransactions)
        {
            return new PonsRobinhoodTokenResult(null, null, transactionValue, gas.Value, true,
                balance.Value >= requiredBalance);
        }
        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient ETH. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance).ToString("0.########", CultureInfo.InvariantCulture)
                + " ETH including launch fee, initial buy and gas.");
        }

        function.Gas = gas;
        function.GasPrice = gasPrice;
        TransactionReceipt receipt = await handler.SendRequestAndWaitForReceiptAsync(FactoryAddress, function,
            cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("pons token transaction failed: " + receipt.TransactionHash);
        }

        EventLog<TokenLaunchedEventDto>? launched = receipt.DecodeAllEvents<TokenLaunchedEventDto>()
            .FirstOrDefault();
        string tokenAddress = launched?.Event.Token
            ?? throw new InvalidOperationException("pons created the token but its address was not found.");
        return new PonsRobinhoodTokenResult(receipt.TransactionHash, tokenAddress, transactionValue,
            gas.Value, false, true);
    }

    private static LaunchTokenFunction BuildLaunchFunction(string walletAddress, string logo,
        PonsRobinhoodTokenRequest request)
    {
        return new LaunchTokenFunction
        {
            Params = new TokenParamsData
            {
                Name = Clean(request.Name, 100),
                Symbol = Clean(request.Symbol, 20),
                Logo = logo,
                Description = Clean(request.Description, 500, true),
                Socials = new SocialsData { Twitter = request.PostUrl },
                FeeWallet = walletAddress
            },
            LaunchConfigId = LaunchConfigId,
            DexId = DexId,
            Salt = RandomNumberGenerator.GetBytes(32)
        };
    }

    private async Task<string> UploadImageAsync(byte[] image, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PinataJwt))
        {
            throw new InvalidOperationException("PonsRobinhood:PinataJwt is required for a real token.");
        }

        using MultipartFormDataContent form = new MultipartFormDataContent();
        using ByteArrayContent file = new ByteArrayContent(image);
        bool isJpeg = image.Length >= 2 && image[0] == 0xFF && image[1] == 0xD8;
        file.Headers.ContentType = new MediaTypeHeaderValue(isJpeg ? "image/jpeg" : "image/png");
        form.Add(file, "file", isJpeg ? "token.jpg" : "token.png");

        using HttpRequestMessage uploadRequest = new HttpRequestMessage(HttpMethod.Post, "pinning/pinFileToIPFS");
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.PinataJwt);
        uploadRequest.Content = form;
        using HttpResponseMessage response = await httpClient.SendAsync(uploadRequest, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Pinata image upload failed: HTTP " + (int)response.StatusCode + " "
                + Shorten(json), null, response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string cid = document.RootElement.GetProperty("IpfsHash").GetString()
            ?? throw new JsonException("Pinata returned an empty image hash.");
        return "ipfs://" + cid;
    }

    private static string Clean(string value, int maximumLength, bool allowEmpty = false)
    {
        string clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("pons token name and symbol cannot be empty.");
        }

        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    [Function("launchEnabled", "bool")]
    private sealed class LaunchEnabledFunction : FunctionMessage
    {
    }

    [Function("launchFee", "uint256")]
    private sealed class LaunchFeeFunction : FunctionMessage
    {
    }

    [Function("launchToken", "address")]
    private sealed class LaunchTokenFunction : FunctionMessage
    {
        [Parameter("tuple", "params", 1)] public TokenParamsData Params { get; set; } = new TokenParamsData();
        [Parameter("uint256", "launchConfigId", 2)] public BigInteger LaunchConfigId { get; set; }
        [Parameter("uint256", "dexId", 3)] public BigInteger DexId { get; set; }
        [Parameter("bytes32", "salt", 4)] public byte[] Salt { get; set; } = [];
    }

    [Struct("TokenParams")]
    private sealed class TokenParamsData
    {
        [Parameter("string", "name", 1)] public string Name { get; set; } = string.Empty;
        [Parameter("string", "symbol", 2)] public string Symbol { get; set; } = string.Empty;
        [Parameter("string", "logo", 3)] public string Logo { get; set; } = string.Empty;
        [Parameter("string", "description", 4)] public string Description { get; set; } = string.Empty;
        [Parameter("tuple", "socials", 5)] public SocialsData Socials { get; set; } = new SocialsData();
        [Parameter("address", "feeWallet", 6)] public string FeeWallet { get; set; } = string.Empty;
    }

    [Struct("Socials")]
    private sealed class SocialsData
    {
        [Parameter("string", "twitter", 1)] public string Twitter { get; set; } = string.Empty;
        [Parameter("string", "telegram", 2)] public string Telegram { get; set; } = string.Empty;
        [Parameter("string", "discord", 3)] public string Discord { get; set; } = string.Empty;
        [Parameter("string", "website", 4)] public string Website { get; set; } = string.Empty;
        [Parameter("string", "farcaster", 5)] public string Farcaster { get; set; } = string.Empty;
    }

    [Event("TokenLaunched")]
    private sealed class TokenLaunchedEventDto : IEventDTO
    {
        [Parameter("address", "token", 1, true)] public string Token { get; set; } = string.Empty;
        [Parameter("address", "deployer", 2, true)] public string Deployer { get; set; } = string.Empty;
        [Parameter("address", "dexFactory", 3, true)] public string DexFactory { get; set; } = string.Empty;
        [Parameter("address", "pairToken", 4, false)] public string PairToken { get; set; } = string.Empty;
        [Parameter("address", "pool", 5, false)] public string Pool { get; set; } = string.Empty;
        [Parameter("uint256", "dexId", 6, false)] public BigInteger DexId { get; set; }
        [Parameter("uint256", "launchConfigId", 7, false)] public BigInteger LaunchConfigId { get; set; }
        [Parameter("uint256", "positionId", 8, false)] public BigInteger PositionId { get; set; }
        [Parameter("uint256", "restrictionsEndBlock", 9, false)] public BigInteger RestrictionsEndBlock { get; set; }
        [Parameter("uint256", "initialBuyAmount", 10, false)] public BigInteger InitialBuyAmount { get; set; }
    }
}

public sealed record PonsRobinhoodTokenRequest(string Name, string Symbol, string Description, byte[] Image,
    string PostUrl, decimal BuyAmount);

public sealed record PonsRobinhoodTokenResult(string? TransactionHash, string? TokenAddress,
    BigInteger TransactionValueWei, BigInteger? EstimatedGas, bool IsDryRun, bool HasEnoughBalance);

public sealed record PonsRobinhoodConnectionResult(bool CorrectChain, bool FactoryFound, bool LaunchEnabled);
