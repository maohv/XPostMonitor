using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
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

namespace XPostMonitor.Services.Launchpads.DyorStable;

public sealed class DyorStableClient
{
    private const long ChainId = 988;
    private const string FactoryAddress = "0x80b42aed46d73f47119dc444bea28a9e68f32bf4";
    private const string ApiPath = "api/stable/v1/";
    private const string DryRunMetadataUri = "https://example.com/dyor-stable-dry-run.json";

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
        using HttpResponseMessage apiResponse = await httpClient.GetAsync(ApiPath + "integration/config",
            cancellationToken);
        decimal? balance = null;

        if (!string.IsNullOrWhiteSpace(walletAddress))
        {
            HexBigInteger value = await web3.Eth.GetBalance.SendRequestAsync(walletAddress)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            balance = UnitConversion.Convert.FromWei(value.Value);
        }

        return new DyorStableConnectionResult(chainId.Value == ChainId, code != "0x", balance,
            apiResponse.IsSuccessStatusCode, options.EnableRealTransactions);
    }

    public async Task<DyorStableTokenResult> CreateTokenAsync(EvmWalletCredentials wallet,
        DyorStableTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.Image.Length == 0)
        {
            throw new InvalidOperationException("DYOR Stable requires a token image.");
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

        string name = Clean(request.Name, 60);
        string symbol = Clean(request.Symbol, 20);
        string metadataUri = options.EnableRealTransactions
            ? await CreateMetadataAsync(name, symbol, request, cancellationToken)
            : DryRunMetadataUri;
        PreparedTransaction prepared = await PrepareLaunchAsync(account.Address, name, symbol,
            metadataUri, request.BuyAmount, cancellationToken);
        if (prepared.ChainId != ChainId
            || !string.Equals(prepared.To, FactoryAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DYOR Stable returned an unexpected chain or factory.");
        }

        BigInteger transactionValue = BigInteger.Parse(prepared.Value, CultureInfo.InvariantCulture);
        Web3 web3 = new Web3(account, options.RpcUrl);
        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address)
            .WaitAsync(cancellationToken);
        TransactionInput transaction = new TransactionInput
        {
            From = account.Address,
            To = prepared.To,
            Data = prepared.Data,
            Value = new HexBigInteger(transactionValue)
        };
        HexBigInteger gas = await web3.Eth.Transactions.EstimateGas.SendRequestAsync(transaction)
            .WaitAsync(cancellationToken);
        HexBigInteger currentGasPrice = await web3.Eth.GasPrice.SendRequestAsync().WaitAsync(cancellationToken);
        HexBigInteger gasPrice = new HexBigInteger(currentGasPrice.Value * 120 / 100);
        BigInteger requiredBalance = transactionValue + gas.Value * gasPrice.Value;

        if (!options.EnableRealTransactions)
        {
            return new DyorStableTokenResult(null, null, null, transactionValue, gas.Value, true,
                balance.Value >= requiredBalance);
        }
        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient USDT0. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance).ToString("0.########",
                    CultureInfo.InvariantCulture) + " USDT0 including launch fee, initial buy and gas.");
        }

        transaction.Gas = gas;
        transaction.GasPrice = gasPrice;
        TransactionReceipt receipt = await web3.TransactionManager
            .SendTransactionAndWaitForReceiptAsync(transaction, cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("DYOR Stable token transaction failed: "
                + receipt.TransactionHash);
        }

        EventLog<TokenLaunchedEventDto>? launched = receipt.DecodeAllEvents<TokenLaunchedEventDto>()
            .FirstOrDefault();
        if (launched == null)
        {
            throw new InvalidOperationException("DYOR Stable created the token but its address was not found.");
        }
        return new DyorStableTokenResult(receipt.TransactionHash, launched.Event.Token,
            launched.Event.Pool, transactionValue, gas.Value, false, true);
    }

    private async Task<string> CreateMetadataAsync(string name, string symbol,
        DyorStableTokenRequest request, CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form = new MultipartFormDataContent();
        using ByteArrayContent file = new ByteArrayContent(request.Image);
        (string contentType, string extension) = GetImageFormat(request.Image);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        file.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
        {
            Name = "\"image\"",
            FileName = "\"token." + extension + "\""
        };
        form.Add(file);
        using HttpResponseMessage imageResponse = await httpClient.PostAsync(ApiPath + "images", form,
            cancellationToken);
        string imageJson = await ReadSuccessAsync(imageResponse, cancellationToken);
        using JsonDocument imageDocument = JsonDocument.Parse(imageJson);
        string imageUrl = imageDocument.RootElement.GetProperty("url").GetString()
            ?? throw new JsonException("DYOR Stable returned an empty image URL.");

        using HttpResponseMessage metadataResponse = await httpClient.PostAsJsonAsync(ApiPath + "metadata", new
        {
            name,
            symbol,
            description = Clean(request.Description, 256, true),
            image = imageUrl,
            x = request.PostUrl
        }, cancellationToken);
        string metadataJson = await ReadSuccessAsync(metadataResponse, cancellationToken);
        using JsonDocument metadataDocument = JsonDocument.Parse(metadataJson);
        return metadataDocument.RootElement.GetProperty("uri").GetString()
            ?? throw new JsonException("DYOR Stable returned an empty metadata URI.");
    }

    private async Task<PreparedTransaction> PrepareLaunchAsync(string walletAddress, string name,
        string symbol, string metadataUri, decimal buyAmount, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(ApiPath + "launch/prepare", new
        {
            name,
            symbol,
            metadataUri,
            feeRecipient = walletAddress,
            sender = walletAddress,
            initialBuyEth = buyAmount.ToString("0.######", CultureInfo.InvariantCulture),
            minTokensOut = "0"
        }, cancellationToken);
        string json = await ReadSuccessAsync(response, cancellationToken);
        return JsonSerializer.Deserialize<PreparedTransaction>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new JsonException("DYOR Stable returned an empty prepared transaction.");
    }

    private static async Task<string> ReadSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("DYOR Stable API failed: HTTP " + (int)response.StatusCode
                + " " + Shorten(json), null, response.StatusCode);
        }
        return json;
    }

    private static string Clean(string value, int maximumLength, bool allowEmpty = false)
    {
        string clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("DYOR Stable token metadata cannot be empty.");
        }
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static (string ContentType, string Extension) GetImageFormat(byte[] image)
    {
        if (image.Length >= 8 && image[0] == 0x89 && image[1] == 0x50
            && image[2] == 0x4E && image[3] == 0x47)
        {
            return ("image/png", "png");
        }
        if (image.Length >= 2 && image[0] == 0xFF && image[1] == 0xD8)
        {
            return ("image/jpeg", "jpg");
        }
        if (image.Length >= 12 && image[0] == 0x52 && image[1] == 0x49
            && image[2] == 0x46 && image[3] == 0x46 && image[8] == 0x57
            && image[9] == 0x45 && image[10] == 0x42 && image[11] == 0x50)
        {
            return ("image/webp", "webp");
        }
        if (image.Length >= 6 && image[0] == 0x47 && image[1] == 0x49
            && image[2] == 0x46 && image[3] == 0x38)
        {
            return ("image/gif", "gif");
        }
        throw new InvalidOperationException("DYOR Stable received an unsupported image format.");
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    [Event("TokenLaunched")]
    private sealed class TokenLaunchedEventDto : IEventDTO
    {
        [Parameter("address", "token", 1, true)] public string Token { get; set; } = string.Empty;
        [Parameter("address", "deployer", 2, true)] public string Deployer { get; set; } = string.Empty;
        [Parameter("address", "dexFactory", 3, true)] public string DexFactory { get; set; } = string.Empty;
        [Parameter("address", "pairToken", 4, false)] public string PairToken { get; set; } = string.Empty;
        [Parameter("address", "pool", 5, false)] public string Pool { get; set; } = string.Empty;
        [Parameter("uint256", "positionId", 6, false)] public BigInteger PositionId { get; set; }
        [Parameter("uint256", "restrictionsEndBlock", 7, false)] public BigInteger RestrictionsEndBlock { get; set; }
        [Parameter("uint256", "initialBuyAmount", 8, false)] public BigInteger InitialBuyAmount { get; set; }
        [Parameter("string", "metadataUri", 9, false)] public string MetadataUri { get; set; } = string.Empty;
        [Parameter("address", "feeRecipient", 10, false)] public string FeeRecipient { get; set; } = string.Empty;
    }

    private sealed class PreparedTransaction
    {
        public string Chain { get; set; } = string.Empty;
        public long ChainId { get; set; }
        public string To { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string LaunchFee { get; set; } = string.Empty;
        public string InitialBuy { get; set; } = string.Empty;
    }
}

public sealed record DyorStableTokenRequest(string Name, string Symbol, string Description, byte[] Image,
    string PostUrl, decimal BuyAmount);

public sealed record DyorStableTokenResult(string? TransactionHash, string? TokenAddress, string? LaunchAddress,
    BigInteger TransactionValueWei, BigInteger? EstimatedGas, bool IsDryRun, bool HasEnoughBalance);

public sealed record DyorStableConnectionResult(bool CorrectChain, bool FactoryFound, decimal? Balance,
    bool MetadataApiReady, bool EnableRealTransactions);
