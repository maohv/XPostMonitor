using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using XPostMonitor.Configuration;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Launchpads.FourMeme;

public sealed class FourMemeClient
{
    private const string TokenManagerAddress = "0x5c952063c7fc8610FFDB798152D69F0B9550762b";
    private const string TokenManagerAbi = """
        [
          {"inputs":[],"name":"_launchFee","outputs":[{"type":"uint256"}],"stateMutability":"view","type":"function"},
          {"inputs":[],"name":"_tradingFeeRate","outputs":[{"type":"uint256"}],"stateMutability":"view","type":"function"},
          {"inputs":[{"name":"args","type":"bytes"},{"name":"signature","type":"bytes"}],"name":"createToken","outputs":[],"stateMutability":"payable","type":"function"}
        ]
        """;

    private readonly HttpClient httpClient;
    private readonly FourMemeOptions options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> walletLocks = new();

    public FourMemeClient(HttpClient httpClient, FourMemeOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<FourMemeConnectionResult> CheckAsync(string? walletAddress,
        CancellationToken cancellationToken)
    {
        JsonElement raisedToken = await GetRaisedTokenAsync(cancellationToken);
        Web3 web3 = new Web3(options.RpcUrl);
        var contract = web3.Eth.GetContract(TokenManagerAbi, TokenManagerAddress);
        BigInteger launchFee = await contract.GetFunction("_launchFee").CallAsync<BigInteger>();
        decimal? balance = null;

        if (!string.IsNullOrWhiteSpace(walletAddress))
        {
            HexBigInteger value = await web3.Eth.GetBalance.SendRequestAsync(walletAddress);
            balance = UnitConversion.Convert.FromWei(value.Value);
        }

        return new FourMemeConnectionResult(
            raisedToken.GetProperty("symbol").GetString() ?? "BNB",
            UnitConversion.Convert.FromWei(launchFee),
            balance,
            options.EnableRealTransactions);
    }

    // This method sends a real BSC transaction. The caller must confirm with the user first.
    public async Task<FourMemeTokenResult> CreateTokenAsync(EvmWalletCredentials wallet,
        FourMemeTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.Image.Length == 0)
        {
            throw new InvalidOperationException("FourMeme requires a token image.");
        }
        if (request.CreatorTaxPercent is not (0 or 1 or 3 or 5 or 10))
        {
            throw new InvalidOperationException("FourMeme creator tax must be 0%, 1%, 3%, 5% or 10%.");
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

    private async Task<FourMemeTokenResult> CreateTokenInternalAsync(EvmWalletCredentials wallet,
        FourMemeTokenRequest request, CancellationToken cancellationToken)
    {
        Account account = new Account(wallet.PrivateKey, 56);
        if (!string.Equals(account.Address, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The encrypted private key does not match the EVM wallet address.");
        }

        string accessToken = await LoginAsync(account, cancellationToken);
        string imageUrl = await UploadImageAsync(accessToken, request.Image, cancellationToken);
        JsonElement raisedToken = await GetRaisedTokenAsync(cancellationToken);
        FourMemePreparedToken prepared = await PrepareTokenAsync(accessToken, raisedToken, imageUrl,
            account.Address, request, cancellationToken);

        Web3 web3 = new Web3(account, options.RpcUrl);
        var contract = web3.Eth.GetContract(TokenManagerAbi, TokenManagerAddress);
        BigInteger launchFee = await contract.GetFunction("_launchFee").CallAsync<BigInteger>();
        BigInteger tradingFeeRate = await contract.GetFunction("_tradingFeeRate").CallAsync<BigInteger>();
        BigInteger buyAmount = UnitConversion.Convert.ToWei(request.BuyAmount);
        BigInteger tradingFee = buyAmount * tradingFeeRate / 10000;
        BigInteger transactionValue = launchFee + buyAmount + tradingFee;

        byte[] createArg = DecodeBytes(prepared.CreateArg);
        byte[] signature = DecodeBytes(prepared.Signature);
        var createFunction = contract.GetFunction("createToken");
        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address);
        if (!options.EnableRealTransactions && balance.Value < transactionValue)
        {
            return new FourMemeTokenResult(null, null, transactionValue, null, true, false);
        }

        HexBigInteger gas = await createFunction.EstimateGasAsync(account.Address, null,
            new HexBigInteger(transactionValue), createArg, signature);
        HexBigInteger gasPrice = await web3.Eth.GasPrice.SendRequestAsync();
        BigInteger requiredBalance = transactionValue + gas.Value * gasPrice.Value;

        if (!options.EnableRealTransactions)
        {
            return new FourMemeTokenResult(null, null, transactionValue, gas.Value, true,
                balance.Value >= requiredBalance);
        }

        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient BNB. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance).ToString("0.########", CultureInfo.InvariantCulture)
                + " BNB including launch fee, initial buy and gas.");
        }

        string transactionHash = await createFunction.SendTransactionAsync(account.Address, gas,
            new HexBigInteger(transactionValue), createArg, signature);
        var receipt = await web3.TransactionReceiptPolling.PollForReceiptAsync(transactionHash, cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("FourMeme token transaction failed: " + transactionHash);
        }

        string? tokenAddress = receipt.DecodeAllEvents<TransferEventDto>()
            .FirstOrDefault(item => item.Event.From == ZeroAddress)?.Log.Address;
        return new FourMemeTokenResult(transactionHash, tokenAddress, transactionValue, gas.Value, false, true);
    }

    private async Task<string> LoginAsync(Account account, CancellationToken cancellationToken)
    {
        JsonElement nonceData = await PostJsonAsync("private/user/nonce/generate", new
        {
            accountAddress = account.Address,
            verifyType = "LOGIN",
            networkCode = "BSC"
        }, null, "Nonce", cancellationToken);

        string nonce = nonceData.GetString()
            ?? throw new JsonException("FourMeme returned an empty nonce.");
        string message = "You are sign in Meme " + nonce;
        string signature = new EthereumMessageSigner().EncodeUTF8AndSign(message,
            new EthECKey(account.PrivateKey));

        JsonElement loginData = await PostJsonAsync("private/user/login/dex", new
        {
            region = "WEB",
            langType = "EN",
            loginIp = "",
            inviteCode = "",
            verifyInfo = new
            {
                address = account.Address,
                networkCode = "BSC",
                signature,
                verifyType = "LOGIN"
            },
            walletName = "MetaMask"
        }, null, "Login", cancellationToken);

        return loginData.GetString()
            ?? throw new JsonException("FourMeme returned an empty access token.");
    }

    private async Task<string> UploadImageAsync(string accessToken, byte[] image,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form = new MultipartFormDataContent();
        using ByteArrayContent file = new ByteArrayContent(image);
        bool isJpeg = image.Length >= 2 && image[0] == 0xFF && image[1] == 0xD8;
        file.Headers.ContentType = new MediaTypeHeaderValue(isJpeg ? "image/jpeg" : "image/png");
        form.Add(file, "file", isJpeg ? "token.jpg" : "token.png");

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "private/token/upload");
        request.Headers.Add("meme-web-access", accessToken);
        request.Content = form;
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        JsonElement data = await ReadResponseAsync(response, "Image upload", cancellationToken);

        return data.GetString()
            ?? throw new JsonException("FourMeme returned an empty image URL.");
    }

    private async Task<JsonElement> GetRaisedTokenAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync("public/config", cancellationToken);
        JsonElement data = await ReadResponseAsync(response, "Public config", cancellationToken);
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("FourMeme public config did not contain raised tokens.");
        }

        JsonElement? firstPublished = null;
        foreach (JsonElement item in data.EnumerateArray())
        {
            bool published = ReadString(item, "status") == "PUBLISH";
            if (published && ReadString(item, "symbol") == "BNB")
            {
                return item.Clone();
            }
            if (published && firstPublished == null)
            {
                firstPublished = item.Clone();
            }
        }

        return firstPublished ?? throw new InvalidOperationException("FourMeme has no published raised token.");
    }

    private async Task<FourMemePreparedToken> PrepareTokenAsync(string accessToken, JsonElement raisedToken,
        string imageUrl, string creatorWalletAddress, FourMemeTokenRequest request,
        CancellationToken cancellationToken)
    {
        string raisedSymbol = ReadString(raisedToken, "symbol") ?? "BNB";
        Dictionary<string, object?> body = new Dictionary<string, object?>
        {
            ["name"] = Clean(request.Name, 20),
            ["shortName"] = Clean(request.Symbol, 20),
            ["desc"] = Clean(request.Description, 500),
            ["totalSupply"] = ReadNumber(raisedToken, "totalAmount", 1_000_000_000m),
            ["raisedAmount"] = ReadNumber(raisedToken, "totalBAmount", 24m),
            ["saleRate"] = ReadNumber(raisedToken, "saleRate", 0.8m),
            ["reserveRate"] = 0,
            ["imgUrl"] = imageUrl,
            ["raisedToken"] = raisedToken,
            ["launchTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["funGroup"] = false,
            ["label"] = "Meme",
            ["lpTradingFee"] = 0.0025m,
            ["preSale"] = request.BuyAmount.ToString(CultureInfo.InvariantCulture),
            ["clickFun"] = false,
            ["symbol"] = raisedSymbol,
            ["dexType"] = "PANCAKE_SWAP",
            ["rushMode"] = false,
            ["onlyMPC"] = false,
            ["feePlan"] = false,
            ["twitterUrl"] = request.PostUrl
        };

        if (request.CreatorTaxPercent > 0)
        {
            body["tokenTaxInfo"] = new
            {
                feeRate = request.CreatorTaxPercent,
                burnRate = 0,
                divideRate = 0,
                liquidityRate = 0,
                recipientAddress = creatorWalletAddress,
                recipientRate = 100,
                minSharing = 100000
            };
        }

        JsonElement data = await PostJsonAsync("private/token/create", body, accessToken,
            "Create API", cancellationToken);
        string createArg = ReadString(data, "createArg")
            ?? throw new JsonException("FourMeme returned an empty createArg.");
        string signature = ReadString(data, "signature")
            ?? throw new JsonException("FourMeme returned an empty signature.");
        return new FourMemePreparedToken(createArg, signature);
    }

    private async Task<JsonElement> PostJsonAsync(string url, object body, string? accessToken, string stage,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Add("meme-web-access", accessToken);
        }
        request.Content = JsonContent.Create(body);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadResponseAsync(response, stage, cancellationToken);
    }

    private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, string stage,
        CancellationToken cancellationToken)
    {
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(stage + " failed: HTTP " + (int)response.StatusCode + " "
                + Shorten(json), null, response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string code = root.TryGetProperty("code", out JsonElement codeValue) ? codeValue.ToString() : string.Empty;
        if (code != "0")
        {
            throw new InvalidOperationException(stage + " failed: " + Shorten(json));
        }
        if (!root.TryGetProperty("data", out JsonElement data))
        {
            throw new JsonException(stage + " did not return data.");
        }

        return data.Clone();
    }

    private static byte[] DecodeBytes(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.FromHexString(value[2..]);
        }
        if (value.All(Uri.IsHexDigit))
        {
            return Convert.FromHexString(value);
        }

        return Convert.FromBase64String(value);
    }

    private static string Clean(string value, int maximumLength)
    {
        string clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("FourMeme token metadata cannot be empty.");
        }

        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
    }

    private static decimal ReadNumber(JsonElement element, string name, decimal fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }
        if (decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture,
            out decimal parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private sealed record FourMemePreparedToken(string CreateArg, string Signature);

    [Event("Transfer")]
    private sealed class TransferEventDto : IEventDTO
    {
        [Parameter("address", "from", 1, true)]
        public string From { get; set; } = string.Empty;

        [Parameter("address", "to", 2, true)]
        public string To { get; set; } = string.Empty;

        [Parameter("uint256", "value", 3, false)]
        public BigInteger Value { get; set; }
    }

    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
}

public sealed record FourMemeTokenRequest(string Name, string Symbol, string Description, byte[] Image,
    string PostUrl, decimal BuyAmount, int CreatorTaxPercent);

public sealed record FourMemeTokenResult(string? TransactionHash, string? TokenAddress,
    BigInteger TransactionValueWei, BigInteger? EstimatedGas, bool IsDryRun, bool HasEnoughBalance);

public sealed record FourMemeConnectionResult(string RaisedToken, decimal LaunchFee, decimal? Balance,
    bool EnableRealTransactions);
