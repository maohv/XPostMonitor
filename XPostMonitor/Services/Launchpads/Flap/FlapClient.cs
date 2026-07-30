using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

namespace XPostMonitor.Services.Launchpads.Flap;

public sealed class FlapClient
{
    private const long ChainId = 56;
    private const string PortalAddress = "0xe2cE6ab80874Fa9Fa2aAE65D277Dd6B8e65C9De0";
    private const string TaxTokenV3Address = "0x024f18294970B5c76c0691b87f138A0317156422";
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
    private static readonly BigInteger Erc20DeployValue = BigInteger.Pow(10, 9); // 1 gwei BNB theo frontend Flap.
    private const long RwaBuyGasLimit = 1_500_000;
    private const int BundleLifetimeSeconds = 60;
    private const string DryRunMetadata = "bafkreidw2jltlq6iracbff6kezkytfartob7djta6a6omsbtde3tevy3eq";
    private const ulong TaxDuration = 365UL * 24 * 60 * 60;
    private const ulong AntiFarmerDuration = 60UL * 60;

    private readonly HttpClient httpClient;
    private readonly FlapOptions options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> walletLocks = new();

    public FlapClient(HttpClient httpClient, FlapOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<FlapConnectionResult> CheckAsync(CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        HexBigInteger chainId = await web3.Eth.ChainId.SendRequestAsync()
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string code = await web3.Eth.GetCode.SendRequestAsync(PortalAddress)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return new FlapConnectionResult(chainId.Value == ChainId, code != "0x",
            options.EnableRealTransactions);
    }

    public async Task<FlapTokenResult> CreateTokenAsync(EvmWalletCredentials wallet,
        FlapTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.Image.Length == 0)
        {
            throw new InvalidOperationException("Flap requires a token image.");
        }
        if (request.CreatorTaxPercent is not (1 or 3 or 5 or 10))
        {
            throw new InvalidOperationException("Flap creator tax must be 1%, 3%, 5% or 10%.");
        }
        if (LaunchpadCatalog.FindFlapBscPaymentToken(request.PaymentToken) == null)
        {
            throw new InvalidOperationException("Flap BSC payment token is not supported.");
        }

        SemaphoreSlim walletLock = walletLocks.GetOrAdd(wallet.Address, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            await FlapDiagnosticLog.WriteAsync("START | Wallet=" + wallet.Address + " | Post="
                + request.PostUrl + " | Name=" + request.Name + " | Symbol=" + request.Symbol
                + " | Buy=" + request.BuyAmount.ToString(CultureInfo.InvariantCulture)
                + " | Payment=" + LaunchpadCatalog.FindFlapBscPaymentToken(request.PaymentToken)!.Code
                + " | CreatorTax=" + request.CreatorTaxPercent);
            FlapTokenResult result = await CreateTokenInternalAsync(wallet, request, cancellationToken);
            await FlapDiagnosticLog.WriteAsync("SUCCESS | Wallet=" + wallet.Address + " | Token="
                + (result.TokenAddress ?? "dry-run") + " | Transaction="
                + (result.TransactionHash ?? "dry-run"));
            return result;
        }
        catch (Exception exception)
        {
            await FlapDiagnosticLog.WriteAsync("ERROR | Wallet=" + wallet.Address + " | Post="
                + request.PostUrl + " | " + FlapDiagnosticLog.Describe(exception));
            throw;
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<FlapTokenResult> CreateTokenInternalAsync(EvmWalletCredentials wallet,
        FlapTokenRequest request, CancellationToken cancellationToken)
    {
        Account account = new Account(wallet.PrivateKey, ChainId);
        if (!string.Equals(account.Address, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The encrypted private key does not match the EVM wallet address.");
        }

        Web3 web3 = new Web3(account, options.RpcUrl);
        string portalCode = await web3.Eth.GetCode.SendRequestAsync(PortalAddress)
            .WaitAsync(cancellationToken);
        if (portalCode == "0x")
        {
            throw new InvalidOperationException("Flap Portal contract was not found on BSC.");
        }

        Task<string> metadataTask = options.EnableRealTransactions
            ? UploadMetadataAsync(account.Address, request, cancellationToken)
            : Task.FromResult(DryRunMetadata);
        Task<FlapVanitySalt> saltTask = FindVanitySaltAsync(cancellationToken);
        await Task.WhenAll(metadataTask, saltTask);

        FlapVanitySalt vanity = await saltTask;
        await FlapDiagnosticLog.WriteAsync("PREPARED | Wallet=" + account.Address + " | Metadata="
            + await metadataTask + " | Salt=0x" + Convert.ToHexString(vanity.Salt).ToLowerInvariant()
            + " | PredictedToken=" + vanity.TokenAddress);

        FlapPaymentToken paymentToken = LaunchpadCatalog.FindFlapBscPaymentToken(request.PaymentToken)!;
        bool usesBnb = paymentToken.TokenAddress == ZeroAddress;
        // Số tiền mua trong Settings luôn là BNB. Với RWA, Portal tự đổi BNB sang quote token.
        BigInteger buyAmount = UnitConversion.Convert.ToWei(request.BuyAmount);
        NewTokenV6Function function = BuildFunction(account.Address, await metadataTask, vanity,
            usesBnb ? buyAmount : BigInteger.Zero, paymentToken, request);

        if (!usesBnb)
        {
            return await CreateRwaTokenWithBnbAsync(web3, account, function, vanity.TokenAddress,
                paymentToken, buyAmount, cancellationToken);
        }

        BigInteger transactionValue = usesBnb ? buyAmount : Erc20DeployValue;
        function.AmountToSend = transactionValue;

        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address)
            .WaitAsync(cancellationToken);
        if (!options.EnableRealTransactions && balance.Value < transactionValue)
        {
            return new FlapTokenResult(null, null, transactionValue, null, true, false);
        }
        if (options.EnableRealTransactions && balance.Value <= transactionValue)
        {
            throw new InvalidOperationException("Insufficient BNB in wallet " + account.Address
                + ". Current balance: "
                + UnitConversion.Convert.FromWei(balance.Value).ToString("0.########", CultureInfo.InvariantCulture)
                + " BNB. BNB is still required for deploy and gas.");
        }

        HexBigInteger currentGasPrice = await web3.Eth.GasPrice.SendRequestAsync().WaitAsync(cancellationToken);
        HexBigInteger gasPrice = new HexBigInteger(currentGasPrice.Value * 120 / 100);

        var handler = web3.Eth.GetContractTransactionHandler<NewTokenV6Function>();
        await FlapDiagnosticLog.WriteAsync("ESTIMATE START | Wallet=" + account.Address
            + " | DexThresh=" + function.Params.DexThresh + " | MigratorType="
            + function.Params.MigratorType + " | TokenVersion=" + function.Params.TokenVersion
            + " | Quote=" + paymentToken.Code + " | BuyAtomic=" + buyAmount);
        HexBigInteger gas = await handler.EstimateGasAsync(PortalAddress, function)
            .WaitAsync(cancellationToken);
        await FlapDiagnosticLog.WriteAsync("ESTIMATE OK | Wallet=" + account.Address
            + " | Gas=" + gas.Value);
        BigInteger requiredBalance = transactionValue + gas.Value * gasPrice.Value;

        if (!options.EnableRealTransactions)
        {
            return new FlapTokenResult(null, null, transactionValue, gas.Value, true,
                balance.Value >= requiredBalance);
        }
        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient BNB. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance).ToString("0.########", CultureInfo.InvariantCulture)
                + " BNB including initial buy and gas.");
        }

        function.Gas = gas;
        function.GasPrice = gasPrice;
        await FlapDiagnosticLog.WriteAsync("SEND START | Wallet=" + account.Address
            + " | Gas=" + gas.Value + " | GasPrice=" + gasPrice.Value);
        TransactionReceipt receipt = await handler.SendRequestAndWaitForReceiptAsync(PortalAddress, function,
            cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("Flap token transaction failed: " + receipt.TransactionHash);
        }

        string tokenAddress = (await saltTask).TokenAddress;
        string tokenCode = await web3.Eth.GetCode.SendRequestAsync(tokenAddress).WaitAsync(cancellationToken);
        if (tokenCode == "0x")
        {
            throw new InvalidOperationException("Flap created the transaction but token address was not found.");
        }
        return new FlapTokenResult(receipt.TransactionHash, tokenAddress, transactionValue, gas.Value, false, true);
    }

    // Tạo RWA không mua trước, sau đó mua ngay bằng BNB trong cùng private bundle.
    // Hai transaction vẫn do chính ví Worker ký nên token mua được nằm thẳng trong Worker.
    private async Task<FlapTokenResult> CreateRwaTokenWithBnbAsync(Web3 web3, Account account,
        NewTokenV6Function launch, string tokenAddress, FlapPaymentToken paymentToken,
        BigInteger buyAmount, CancellationToken cancellationToken)
    {
        QuoteTokenConfiguration configuration = await GetQuoteTokenConfigurationAsync(web3,
            paymentToken.TokenAddress, cancellationToken);
        if (configuration.Enabled == 0 || configuration.NativeToQuoteSwapType == 0)
        {
            throw new InvalidOperationException(paymentToken.Code
                + " does not currently support buying with BNB on Flap.");
        }
        await FlapDiagnosticLog.WriteAsync("RWA CONFIG OK | Wallet=" + account.Address
            + " | Quote=" + paymentToken.Code + " | SwapType="
            + configuration.NativeToQuoteSwapType + " | DexId=" + configuration.DexId);

        launch.AmountToSend = Erc20DeployValue;
        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address)
            .WaitAsync(cancellationToken);
        if (!options.EnableRealTransactions && balance.Value < Erc20DeployValue)
        {
            return new FlapTokenResult(null, null, Erc20DeployValue + buyAmount,
                null, true, false);
        }
        var launchHandler = web3.Eth.GetContractTransactionHandler<NewTokenV6Function>();
        HexBigInteger estimatedLaunchGas = await launchHandler.EstimateGasAsync(PortalAddress, launch)
            .WaitAsync(cancellationToken);
        BigInteger launchGas = estimatedLaunchGas.Value * 120 / 100;

        HexBigInteger chainGasPrice = await web3.Eth.GasPrice.SendRequestAsync()
            .WaitAsync(cancellationToken);
        BigInteger builderGasPrice = await GetBuilderGasPriceAsync(cancellationToken);
        BigInteger gasPrice = BigInteger.Max(chainGasPrice.Value * 120 / 100, builderGasPrice);
        BigInteger totalValue = Erc20DeployValue + buyAmount;
        BigInteger requiredBalance = totalValue + (launchGas + RwaBuyGasLimit) * gasPrice;

        await FlapDiagnosticLog.WriteAsync("RWA BUNDLE PREPARED | Wallet=" + account.Address
            + " | Token=" + tokenAddress + " | Quote=" + paymentToken.Code
            + " | BuyBNBWei=" + buyAmount + " | LaunchGas=" + launchGas
            + " | BuyGasLimit=" + RwaBuyGasLimit + " | GasPrice=" + gasPrice);

        if (!options.EnableRealTransactions)
        {
            return new FlapTokenResult(null, null, totalValue, launchGas + RwaBuyGasLimit,
                true, balance.Value >= requiredBalance);
        }
        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient BNB for Flap RWA atomic launch. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance).ToString("0.########",
                    CultureInfo.InvariantCulture) + " BNB including initial buy and gas.");
        }

        HexBigInteger nonce = await web3.Eth.Transactions.GetTransactionCount
            .SendRequestAsync(account.Address, BlockParameter.CreatePending())
            .WaitAsync(cancellationToken);
        launch.Nonce = nonce;
        launch.Gas = new HexBigInteger(launchGas);
        launch.GasPrice = new HexBigInteger(gasPrice);

        SwapExactInputFunction buy = new SwapExactInputFunction
        {
            FromAddress = account.Address,
            AmountToSend = buyAmount,
            Nonce = new HexBigInteger(nonce.Value + 1),
            Gas = new HexBigInteger(RwaBuyGasLimit),
            GasPrice = new HexBigInteger(gasPrice),
            Params = new ExactInputParams
            {
                InputToken = ZeroAddress,
                OutputToken = tokenAddress,
                InputAmount = buyAmount,
                // Bundle không xuất hiện trong public mempool nên không dùng minOutput ở lần mua đầu.
                MinOutputAmount = BigInteger.Zero,
                PermitData = []
            }
        };

        string signedLaunch = await launchHandler.SignTransactionAsync(PortalAddress, launch)
            .WaitAsync(cancellationToken);
        var buyHandler = web3.Eth.GetContractTransactionHandler<SwapExactInputFunction>();
        string signedBuy = await buyHandler.SignTransactionAsync(PortalAddress, buy)
            .WaitAsync(cancellationToken);
        string launchHash = CalculateTransactionHash(signedLaunch);
        string buyHash = CalculateTransactionHash(signedBuy);
        string bundleHash = await SendBundleAsync(web3, signedLaunch, signedBuy, cancellationToken);

        await FlapDiagnosticLog.WriteAsync("RWA BUNDLE ACCEPTED | Wallet=" + account.Address
            + " | Bundle=" + bundleHash + " | LaunchTx=" + launchHash + " | BuyTx=" + buyHash);

        (TransactionReceipt launchReceipt, TransactionReceipt buyReceipt) = await WaitForBundleAsync(
            web3, launchHash, buyHash, cancellationToken);
        if (launchReceipt.Status.Value != 1 || buyReceipt.Status.Value != 1)
        {
            throw new InvalidOperationException("Flap RWA private bundle was mined but a transaction failed. "
                + "Launch=" + launchHash + ", Buy=" + buyHash + ".");
        }

        string tokenCode = await web3.Eth.GetCode.SendRequestAsync(tokenAddress).WaitAsync(cancellationToken);
        if (tokenCode == "0x")
        {
            throw new InvalidOperationException("Flap RWA bundle was mined but token address was not found.");
        }
        await FlapDiagnosticLog.WriteAsync("RWA BUNDLE MINED | Wallet=" + account.Address
            + " | Token=" + tokenAddress + " | LaunchTx=" + launchHash + " | BuyTx=" + buyHash);
        return new FlapTokenResult(launchHash, tokenAddress, totalValue,
            launchReceipt.GasUsed.Value + buyReceipt.GasUsed.Value, false, true);
    }

    private static NewTokenV6Function BuildFunction(string walletAddress, string metadata,
        FlapVanitySalt vanity, BigInteger buyAmount, FlapPaymentToken paymentToken, FlapTokenRequest request)
    {
        ushort taxRate = checked((ushort)(request.CreatorTaxPercent * 100));
        return new NewTokenV6Function
        {
            Params = new NewTokenV6Params
            {
                Name = Clean(request.Name, 100),
                Symbol = Clean(request.Symbol, 20),
                Meta = metadata,
                DexThresh = 1,
                Salt = vanity.Salt,
                MigratorType = 1,
                QuoteToken = paymentToken.TokenAddress,
                QuoteAmount = buyAmount,
                Beneficiary = walletAddress,
                PermitData = [],
                ExtensionId = new byte[32],
                ExtensionData = [],
                DexId = 0,
                LpFeeProfile = 0,
                BuyTaxRate = taxRate,
                SellTaxRate = taxRate,
                TaxDuration = TaxDuration,
                AntiFarmerDuration = AntiFarmerDuration,
                MarketingBps = 10000,
                DeflationBps = 0,
                DividendBps = 0,
                LpBps = 0,
                MinimumShareBalance = BigInteger.Zero,
                // Portal mới yêu cầu ERC-20 quote phải khai báo chính quote token ở đây.
                DividendToken = paymentToken.TokenAddress,
                CommissionReceiver = walletAddress,
                TokenVersion = 6
            }
        };
    }

    // Đọc cấu hình để chắc chắn Flap hỗ trợ đổi BNB sang RWA đã chọn.
    private static async Task<QuoteTokenConfiguration> GetQuoteTokenConfigurationAsync(Web3 web3,
        string quoteToken, CancellationToken cancellationToken)
    {
        var handler = web3.Eth.GetContractQueryHandler<GetQuoteTokenConfigurationFunction>();
        GetQuoteTokenConfigurationOutput result = await handler
            .QueryDeserializingToObjectAsync<GetQuoteTokenConfigurationOutput>(
                new GetQuoteTokenConfigurationFunction { QuoteToken = quoteToken }, PortalAddress)
            .WaitAsync(cancellationToken);
        return result.Configuration;
    }

    // Builder có mức gas tối thiểu riêng. Lấy trực tiếp để bundle không bị từ chối.
    private async Task<BigInteger> GetBuilderGasPriceAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, options.BundleRpcUrl)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "eth_gasPrice", @params = Array.Empty<object>() })
        };
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(json);
        ThrowIfRpcError(document.RootElement, "get builder gas price");
        string value = document.RootElement.GetProperty("result").GetString()
            ?? throw new InvalidOperationException("48 Club returned an empty gas price.");
        return new HexBigInteger(value).Value;
    }

    // Gửi hai transaction đã ký theo đúng thứ tự và không công khai ra mempool.
    private async Task<string> SendBundleAsync(Web3 web3, string signedLaunch, string signedBuy,
        CancellationToken cancellationToken)
    {
        HexBigInteger currentBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()
            .WaitAsync(cancellationToken);
        long maximumBlock = checked((long)currentBlock.Value + 40);
        long maximumTimestamp = DateTimeOffset.UtcNow.AddSeconds(BundleLifetimeSeconds).ToUnixTimeSeconds();
        object body = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_sendBundle",
            @params = new object[]
            {
                new
                {
                    txs = new[] { EnsureHexPrefix(signedLaunch), EnsureHexPrefix(signedBuy) },
                    maxBlockNumber = maximumBlock,
                    maxTimestamp = maximumTimestamp,
                    revertingTxHashes = Array.Empty<string>(),
                    noMerge = true
                }
            }
        };
        using HttpRequestMessage request = new(HttpMethod.Post, options.BundleRpcUrl)
        {
            Content = JsonContent.Create(body)
        };
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(json);
        ThrowIfRpcError(document.RootElement, "submit Flap RWA bundle");
        return document.RootElement.GetProperty("result").GetString()
            ?? throw new InvalidOperationException("48 Club accepted the request but returned no bundle hash.");
    }

    // Chỉ báo thành công khi cả transaction tạo và transaction mua đều đã lên chain.
    private static async Task<(TransactionReceipt Launch, TransactionReceipt Buy)> WaitForBundleAsync(
        Web3 web3, string launchHash, string buyHash, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(BundleLifetimeSeconds);
        TransactionReceipt? launchReceipt = null;
        TransactionReceipt? buyReceipt = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            launchReceipt ??= await web3.Eth.Transactions.GetTransactionReceipt
                .SendRequestAsync(launchHash).WaitAsync(cancellationToken);
            buyReceipt ??= await web3.Eth.Transactions.GetTransactionReceipt
                .SendRequestAsync(buyHash).WaitAsync(cancellationToken);
            if (launchReceipt != null && buyReceipt != null)
            {
                return (launchReceipt, buyReceipt);
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException("Flap RWA bundle was not mined within " + BundleLifetimeSeconds
            + " seconds. Launch=" + launchHash + ", Buy=" + buyHash + ".");
    }

    private static string CalculateTransactionHash(string signedTransaction)
    {
        string clean = signedTransaction.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? signedTransaction[2..] : signedTransaction;
        byte[] hash = Sha3Keccack.Current.CalculateHash(Convert.FromHexString(clean));
        return "0x" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    // 48 Club yêu cầu raw transaction luôn bắt đầu bằng 0x.
    private static string EnsureHexPrefix(string value)
    {
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : "0x" + value;
    }

    private static void ThrowIfRpcError(JsonElement root, string operation)
    {
        if (root.TryGetProperty("error", out JsonElement error))
        {
            throw new InvalidOperationException("48 Club failed to " + operation + ": " + Shorten(error.ToString()));
        }
    }

    private async Task<string> UploadMetadataAsync(string walletAddress, FlapTokenRequest request,
        CancellationToken cancellationToken)
    {
        string operations = JsonSerializer.Serialize(new
        {
            query = "mutation Create($file: Upload!, $meta: MetadataInput!) { create(file: $file, meta: $meta) }",
            variables = new
            {
                file = (object?)null,
                meta = new
                {
                    website = (string?)null,
                    twitter = request.PostUrl,
                    telegram = (string?)null,
                    description = Clean(request.Description, 500, true),
                    creator = walletAddress
                }
            }
        });

        string boundary = "----XPostMonitor" + Guid.NewGuid().ToString("N");
        using MultipartFormDataContent form = new MultipartFormDataContent(boundary);
        form.Headers.ContentType!.Parameters.First().Value = boundary;

        using StringContent operationsContent = new StringContent(operations, Encoding.UTF8);
        operationsContent.Headers.ContentType = null;
        operationsContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"operations\""
        };
        form.Add(operationsContent);

        using StringContent mapContent = new StringContent("{\"0\":[\"variables.file\"]}", Encoding.UTF8);
        mapContent.Headers.ContentType = null;
        mapContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"map\""
        };
        form.Add(mapContent);

        using ByteArrayContent file = new ByteArrayContent(request.Image);
        bool isJpeg = request.Image.Length >= 2 && request.Image[0] == 0xFF && request.Image[1] == 0xD8;
        file.Headers.ContentType = new MediaTypeHeaderValue(isJpeg ? "image/jpeg" : "image/png");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"0\"",
            FileName = isJpeg ? "\"token.jpg\"" : "\"token.png\""
        };
        form.Add(file);

        using HttpResponseMessage response = await httpClient.PostAsync("api/upload", form, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Flap metadata upload failed: HTTP "
                + (int)response.StatusCode + " " + Shorten(json), null, response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("errors", out JsonElement errors))
        {
            throw new InvalidOperationException("Flap metadata upload failed: " + Shorten(errors.ToString()));
        }
        return document.RootElement.GetProperty("data").GetProperty("create").GetString()
            ?? throw new JsonException("Flap returned an empty metadata CID.");
    }

    private static Task<FlapVanitySalt> FindVanitySaltAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            byte[] salt = RandomNumberGenerator.GetBytes(32);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string tokenAddress = PredictTokenAddress(salt);
                if (tokenAddress.EndsWith("7777", StringComparison.OrdinalIgnoreCase))
                {
                    return new FlapVanitySalt(salt, tokenAddress);
                }
                salt = Sha3Keccack.Current.CalculateHash(salt);
            }
        }, cancellationToken);
    }

    private static string PredictTokenAddress(byte[] salt)
    {
        byte[] portal = Convert.FromHexString(PortalAddress[2..]);
        byte[] implementation = Convert.FromHexString(TaxTokenV3Address[2..]);
        byte[] prefix = Convert.FromHexString("3d602d80600a3d3981f3363d3d373d3d3d363d73");
        byte[] suffix = Convert.FromHexString("5af43d82803e903d91602b57fd5bf3");
        byte[] bytecode = [.. prefix, .. implementation, .. suffix];
        byte[] bytecodeHash = Sha3Keccack.Current.CalculateHash(bytecode);
        byte[] create2 = [0xff, .. portal, .. salt, .. bytecodeHash];
        byte[] addressHash = Sha3Keccack.Current.CalculateHash(create2);
        return "0x" + Convert.ToHexString(addressHash[^20..]).ToLowerInvariant();
    }

    private static string Clean(string value, int maximumLength, bool allowEmpty = false)
    {
        string clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("Flap token name and symbol cannot be empty.");
        }
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    [Function("newTokenV6", "address")]
    private sealed class NewTokenV6Function : FunctionMessage
    {
        [Parameter("tuple", "params", 1)] public NewTokenV6Params Params { get; set; } = new NewTokenV6Params();
    }

    [Function("swapExactInput", "uint256")]
    private sealed class SwapExactInputFunction : FunctionMessage
    {
        [Parameter("tuple", "params", 1)] public ExactInputParams Params { get; set; } = new ExactInputParams();
    }

    [Struct("ExactInputParams")]
    private sealed class ExactInputParams
    {
        [Parameter("address", "inputToken", 1)] public string InputToken { get; set; } = string.Empty;
        [Parameter("address", "outputToken", 2)] public string OutputToken { get; set; } = string.Empty;
        [Parameter("uint256", "inputAmount", 3)] public BigInteger InputAmount { get; set; }
        [Parameter("uint256", "minOutputAmount", 4)] public BigInteger MinOutputAmount { get; set; }
        [Parameter("bytes", "permitData", 5)] public byte[] PermitData { get; set; } = [];
    }

    [Function("getQuoteTokenConfiguration", typeof(GetQuoteTokenConfigurationOutput))]
    private sealed class GetQuoteTokenConfigurationFunction : FunctionMessage
    {
        [Parameter("address", "quoteToken", 1)] public string QuoteToken { get; set; } = string.Empty;
    }

    [FunctionOutput]
    private sealed class GetQuoteTokenConfigurationOutput : IFunctionOutputDTO
    {
        [Parameter("tuple", "config", 1)] public QuoteTokenConfiguration Configuration { get; set; } = new();
    }

    [Struct("QuoteTokenConfiguration")]
    private sealed class QuoteTokenConfiguration
    {
        [Parameter("uint8", "enabled", 1)] public byte Enabled { get; set; }
        [Parameter("uint8", "defaultCurve", 2)] public byte DefaultCurve { get; set; }
        [Parameter("uint8", "alternativeCurve", 3)] public byte AlternativeCurve { get; set; }
        [Parameter("uint8", "nativeToQuoteSwapType", 4)] public byte NativeToQuoteSwapType { get; set; }
        [Parameter("uint8", "dexId", 5)] public byte DexId { get; set; }
    }

    [Struct("NewTokenV6Params")]
    private sealed class NewTokenV6Params
    {
        [Parameter("string", "name", 1)] public string Name { get; set; } = string.Empty;
        [Parameter("string", "symbol", 2)] public string Symbol { get; set; } = string.Empty;
        [Parameter("string", "meta", 3)] public string Meta { get; set; } = string.Empty;
        [Parameter("uint8", "dexThresh", 4)] public byte DexThresh { get; set; }
        [Parameter("bytes32", "salt", 5)] public byte[] Salt { get; set; } = [];
        [Parameter("uint8", "migratorType", 6)] public byte MigratorType { get; set; }
        [Parameter("address", "quoteToken", 7)] public string QuoteToken { get; set; } = string.Empty;
        [Parameter("uint256", "quoteAmt", 8)] public BigInteger QuoteAmount { get; set; }
        [Parameter("address", "beneficiary", 9)] public string Beneficiary { get; set; } = string.Empty;
        [Parameter("bytes", "permitData", 10)] public byte[] PermitData { get; set; } = [];
        [Parameter("bytes32", "extensionID", 11)] public byte[] ExtensionId { get; set; } = [];
        [Parameter("bytes", "extensionData", 12)] public byte[] ExtensionData { get; set; } = [];
        [Parameter("uint8", "dexId", 13)] public byte DexId { get; set; }
        [Parameter("uint8", "lpFeeProfile", 14)] public byte LpFeeProfile { get; set; }
        [Parameter("uint16", "buyTaxRate", 15)] public ushort BuyTaxRate { get; set; }
        [Parameter("uint16", "sellTaxRate", 16)] public ushort SellTaxRate { get; set; }
        [Parameter("uint64", "taxDuration", 17)] public ulong TaxDuration { get; set; }
        [Parameter("uint64", "antiFarmerDuration", 18)] public ulong AntiFarmerDuration { get; set; }
        [Parameter("uint16", "mktBps", 19)] public ushort MarketingBps { get; set; }
        [Parameter("uint16", "deflationBps", 20)] public ushort DeflationBps { get; set; }
        [Parameter("uint16", "dividendBps", 21)] public ushort DividendBps { get; set; }
        [Parameter("uint16", "lpBps", 22)] public ushort LpBps { get; set; }
        [Parameter("uint256", "minimumShareBalance", 23)] public BigInteger MinimumShareBalance { get; set; }
        [Parameter("address", "dividendToken", 24)] public string DividendToken { get; set; } = string.Empty;
        [Parameter("address", "commissionReceiver", 25)] public string CommissionReceiver { get; set; } = string.Empty;
        [Parameter("uint8", "tokenVersion", 26)] public byte TokenVersion { get; set; }
    }

    private sealed record FlapVanitySalt(byte[] Salt, string TokenAddress);
}

public sealed record FlapTokenRequest(string Name, string Symbol, string Description, byte[] Image,
    string PostUrl, decimal BuyAmount, int CreatorTaxPercent, string? PaymentToken);

public sealed record FlapTokenResult(string? TransactionHash, string? TokenAddress,
    BigInteger TransactionValueWei, BigInteger? EstimatedGas, bool IsDryRun, bool HasEnoughBalance);

public sealed record FlapConnectionResult(bool CorrectChain, bool PortalFound, bool RealTransactionsEnabled);
