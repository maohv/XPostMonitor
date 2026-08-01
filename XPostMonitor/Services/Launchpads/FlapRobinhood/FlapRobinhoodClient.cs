using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Numerics;
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
using XPostMonitor.Services.Launchpads.Flap;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Launchpads.FlapRobinhood;

// Tạo Tax Token V3 trên Flap Robinhood.
// File này tách riêng để mọi thay đổi không ảnh hưởng Flap BSC đang chạy ổn.
public sealed class FlapRobinhoodClient
{
    // Các giá trị dưới đây lấy từ Robinhood Chain Integration Guide của Flap.
    private const long ChainId = 4663;
    private const string PortalAddress = "0x26605f322f7fF986f381bB9A6e3f5DAb0bEaEb09";
    private const string TaxTokenV3Address = "0x7777C8743C88B3aff3cf262135beF2c8b2e83333";
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
    private const string DryRunMetadata = "bafkreidw2jltlq6iracbff6kezkytfartob7djta6a6omsbtde3tevy3eq";

    // Tax tồn tại một năm. Trong giờ đầu, cơ chế anti-farmer của Flap được bật.
    private const ulong TaxDuration = 365UL * 24 * 60 * 60;
    private const ulong AntiFarmerDuration = 60UL * 60;

    private readonly HttpClient httpClient;
    private readonly FlapRobinhoodOptions options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> walletLocks = new();

    public FlapRobinhoodClient(HttpClient httpClient, FlapRobinhoodOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    // Kiểm tra đúng Robinhood Chain và Portal có tồn tại.
    public async Task<FlapRobinhoodConnectionResult> CheckAsync(CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        HexBigInteger chainId = await web3.Eth.ChainId.SendRequestAsync()
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string code = await web3.Eth.GetCode.SendRequestAsync(PortalAddress)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        return new FlapRobinhoodConnectionResult(chainId.Value == ChainId, code != "0x",
            options.EnableRealTransactions);
    }

    // Khóa theo từng ví để hai worker không dùng cùng nonce trong một thời điểm.
    public async Task<FlapRobinhoodTokenResult> CreateTokenAsync(EvmWalletCredentials wallet,
        FlapRobinhoodTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.Image.Length == 0)
        {
            throw new InvalidOperationException("Flap Robinhood requires a token image.");
        }
        if (request.CreatorTaxPercent is not (1 or 3 or 5 or 10))
        {
            throw new InvalidOperationException("Flap Robinhood creator tax must be 1%, 3%, 5% or 10%.");
        }

        SemaphoreSlim walletLock = walletLocks.GetOrAdd(wallet.Address, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            await FlapDiagnosticLog.WriteAsync("ROBINHOOD START | Wallet=" + wallet.Address
                + " | Post=" + request.PostUrl + " | Name=" + request.Name
                + " | Symbol=" + request.Symbol + " | Buy="
                + request.BuyAmount.ToString(CultureInfo.InvariantCulture)
                + " | CreatorTax=" + request.CreatorTaxPercent);

            FlapRobinhoodTokenResult result = await CreateTokenInternalAsync(wallet, request,
                cancellationToken);

            await FlapDiagnosticLog.WriteAsync("ROBINHOOD SUCCESS | Wallet=" + wallet.Address
                + " | Token=" + (result.TokenAddress ?? "dry-run")
                + " | Transaction=" + (result.TransactionHash ?? "dry-run"));
            return result;
        }
        catch (Exception exception)
        {
            await FlapDiagnosticLog.WriteAsync("ROBINHOOD ERROR | Wallet=" + wallet.Address
                + " | Post=" + request.PostUrl + " | " + FlapDiagnosticLog.Describe(exception));
            throw;
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<FlapRobinhoodTokenResult> CreateTokenInternalAsync(EvmWalletCredentials wallet,
        FlapRobinhoodTokenRequest request, CancellationToken cancellationToken)
    {
        Account account = new Account(wallet.PrivateKey, ChainId);
        if (!string.Equals(account.Address, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The encrypted private key does not match the EVM wallet address.");
        }

        Web3 web3 = new Web3(account, options.RpcUrl);
        string portalCode = await web3.Eth.GetCode.SendRequestAsync(PortalAddress)
            .WaitAsync(cancellationToken);
        if (portalCode == "0x")
        {
            throw new InvalidOperationException("Flap Portal contract was not found on Robinhood Chain.");
        }

        // Upload metadata và tìm địa chỉ đuôi 7777 chạy song song để giảm thời gian chờ.
        Task<string> metadataTask = options.EnableRealTransactions
            ? UploadMetadataAsync(account.Address, request, cancellationToken)
            : Task.FromResult(DryRunMetadata);
        Task<FlapRobinhoodVanitySalt> saltTask = FindVanitySaltAsync(cancellationToken);
        await Task.WhenAll(metadataTask, saltTask);

        FlapRobinhoodVanitySalt vanity = await saltTask;
        BigInteger buyAmount = UnitConversion.Convert.ToWei(request.BuyAmount);
        NewTokenV6Function function = BuildFunction(account.Address, await metadataTask, vanity,
            buyAmount, request);

        // Quote token là address(0), vì vậy tiền mua ban đầu được gửi bằng ETH native.
        function.AmountToSend = buyAmount;

        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address)
            .WaitAsync(cancellationToken);
        if (!options.EnableRealTransactions && balance.Value < buyAmount)
        {
            return new FlapRobinhoodTokenResult(null, null, buyAmount, null, true, false);
        }
        if (options.EnableRealTransactions && balance.Value <= buyAmount)
        {
            throw new InvalidOperationException("Insufficient ETH in wallet " + account.Address
                + ". Initial buy is " + request.BuyAmount.ToString("0.########",
                    CultureInfo.InvariantCulture) + " ETH, plus gas.");
        }

        var handler = web3.Eth.GetContractTransactionHandler<NewTokenV6Function>();
        HexBigInteger gas = await handler.EstimateGasAsync(PortalAddress, function)
            .WaitAsync(cancellationToken);
        HexBigInteger currentGasPrice = await web3.Eth.GasPrice.SendRequestAsync()
            .WaitAsync(cancellationToken);
        HexBigInteger gasPrice = new HexBigInteger(currentGasPrice.Value * 120 / 100);
        BigInteger requiredBalance = buyAmount + gas.Value * gasPrice.Value;

        if (!options.EnableRealTransactions)
        {
            return new FlapRobinhoodTokenResult(null, null, buyAmount, gas.Value, true,
                balance.Value >= requiredBalance);
        }
        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient ETH. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance)
                    .ToString("0.########", CultureInfo.InvariantCulture)
                + " ETH including initial buy and gas.");
        }

        function.Gas = gas;
        function.GasPrice = gasPrice;
        TransactionReceipt receipt = await handler.SendRequestAndWaitForReceiptAsync(
            PortalAddress, function, cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("Flap Robinhood token transaction failed: "
                + receipt.TransactionHash);
        }

        string tokenAddress = vanity.TokenAddress;
        string tokenCode = await web3.Eth.GetCode.SendRequestAsync(tokenAddress)
            .WaitAsync(cancellationToken);
        if (tokenCode == "0x")
        {
            throw new InvalidOperationException(
                "Flap created the transaction but token address was not found.");
        }

        return new FlapRobinhoodTokenResult(receipt.TransactionHash, tokenAddress, buyAmount,
            gas.Value, false, true);
    }

    // Tạo đúng NewTokenV6Params cho Tax Token V3 theo tài liệu Flap Robinhood.
    private static NewTokenV6Function BuildFunction(string walletAddress, string metadata,
        FlapRobinhoodVanitySalt vanity, BigInteger buyAmount, FlapRobinhoodTokenRequest request)
    {
        ushort taxRate = checked((ushort)(request.CreatorTaxPercent * 100));
        return new NewTokenV6Function
        {
            Params = new NewTokenV6Params
            {
                Name = CleanTokenText(request.Name, 100),
                Symbol = CleanTokenText(request.Symbol, 20),
                Meta = metadata,
                DexThresh = 1, // FOUR_FIFTHS: tốt nghiệp khi bán 80% supply.
                Salt = vanity.Salt,
                MigratorType = 1, // V2_MIGRATOR: loại duy nhất Robinhood hỗ trợ.
                QuoteToken = ZeroAddress, // Native ETH.
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
                MarketingBps = 10000, // 100% phần tax phân phối về ví creator.
                DeflationBps = 0,
                DividendBps = 0,
                LpBps = 0,
                MinimumShareBalance = BigInteger.Zero,
                DividendToken = ZeroAddress,
                CommissionReceiver = walletAddress,
                TokenVersion = 6 // TOKEN_TAXED_V3.
            }
        };
    }

    // Flap dùng API này để lưu ảnh và metadata trước khi gọi contract.
    private async Task<string> UploadMetadataAsync(string walletAddress,
        FlapRobinhoodTokenRequest request, CancellationToken cancellationToken)
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

        using HttpResponseMessage response = await httpClient.PostAsync("api/upload", form,
            cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Flap metadata upload failed: HTTP "
                + (int)response.StatusCode + " " + Shorten(json), null, response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("errors", out JsonElement errors))
        {
            throw new InvalidOperationException("Flap metadata upload failed: "
                + Shorten(errors.ToString()));
        }

        return document.RootElement.GetProperty("data").GetProperty("create").GetString()
            ?? throw new JsonException("Flap returned an empty metadata CID.");
    }

    // Tìm salt để địa chỉ Tax Token V3 kết thúc bằng 7777.
    private static Task<FlapRobinhoodVanitySalt> FindVanitySaltAsync(
        CancellationToken cancellationToken)
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
                    return new FlapRobinhoodVanitySalt(salt, tokenAddress);
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
        string clean = new string(value.Where(character => !char.IsControl(character)).ToArray())
            .Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("Flap token name and symbol cannot be empty.");
        }

        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    // Giữ nguyên nội dung, emoji và ký tự đặc biệt; chỉ chuẩn hóa dấu nháy kiểu Word.
    private static string CleanTokenText(string value, int maximumLength)
    {
        string normalized = value
            .Replace("‘", "'")
            .Replace("’", "'")
            .Replace("“", "\"")
            .Replace("”", "\"")
            .Replace("＇", "'")
            .Replace("＂", "\"");
        return Clean(normalized, maximumLength);
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    [Function("newTokenV6", "address")]
    private sealed class NewTokenV6Function : FunctionMessage
    {
        [Parameter("tuple", "params", 1)]
        public NewTokenV6Params Params { get; set; } = new();
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
        [Parameter("uint256", "minimumShareBalance", 23)]
        public BigInteger MinimumShareBalance { get; set; }
        [Parameter("address", "dividendToken", 24)]
        public string DividendToken { get; set; } = string.Empty;
        [Parameter("address", "commissionReceiver", 25)]
        public string CommissionReceiver { get; set; } = string.Empty;
        [Parameter("uint8", "tokenVersion", 26)] public byte TokenVersion { get; set; }
    }

    private sealed record FlapRobinhoodVanitySalt(byte[] Salt, string TokenAddress);
}

public sealed record FlapRobinhoodTokenRequest(string Name, string Symbol, string Description,
    byte[] Image, string PostUrl, decimal BuyAmount, int CreatorTaxPercent);

public sealed record FlapRobinhoodTokenResult(string? TransactionHash, string? TokenAddress,
    BigInteger TransactionValueWei, BigInteger? EstimatedGas, bool IsDryRun, bool HasEnoughBalance);

public sealed record FlapRobinhoodConnectionResult(bool CorrectChain, bool PortalFound,
    bool RealTransactionsEnabled);
