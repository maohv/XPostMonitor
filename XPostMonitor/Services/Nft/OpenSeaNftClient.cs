using System.Net;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using XPostMonitor.Configuration;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Nft;

// Đọc link OpenSea công khai và mint thẳng vào SeaDrop; không cần OpenSea API key.
public sealed class OpenSeaNftClient
{
    private const long RobinhoodChainId = 4663;
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
    private readonly HttpClient httpClient;
    private readonly OpenSeaNftOptions options;

    public OpenSeaNftClient(HttpClient httpClient, OpenSeaNftOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public static string GetCollectionSlug(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || !uri.Host.Equals("opensea.io", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Link OpenSea không hợp lệ.");
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("collection", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Hãy gửi link dạng https://opensea.io/collection/ten-collection.");
        return parts[1];
    }

    public async Task<OpenSeaCollection> GetCollectionAsync(string url,
        CancellationToken cancellationToken)
    {
        string slug = GetCollectionSlug(url);
        string html = await httpClient.GetStringAsync("collection/" + Uri.EscapeDataString(slug),
            cancellationToken);
        if (!html.Contains("\"identifier\":\"robinhood\"", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Collection này không nằm trên Robinhood Chain.");

        Match addressMatch = Regex.Match(html,
            @"collection/(?<address>0x[a-fA-F0-9]{40})/image_type_logo",
            RegexOptions.IgnoreCase);
        if (!addressMatch.Success)
        {
            addressMatch = Regex.Match(html,
                @"\""address\"":\""(?<address>0x[a-fA-F0-9]{40})\"".{0,500}\""standard\"":\""ERC721\""",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        string address = addressMatch.Groups["address"].Value;
        if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(address))
            throw new InvalidOperationException("Không tìm thấy contract ERC721 trong trang OpenSea.");

        Match embeddedName = Regex.Match(html,
            @"\""name\"":\""(?<name>(?:\\.|[^\""\\])*)\"",\""description\"":",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Match title = Regex.Match(html, @"<title>(?<name>.*?)\s+-\s+Collection\s+\|\s+OpenSea</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string name = embeddedName.Success ? Regex.Unescape(embeddedName.Groups["name"].Value)
            : title.Success ? WebUtility.HtmlDecode(title.Groups["name"].Value) : slug;
        return new OpenSeaCollection(slug, name, address);
    }

    public async Task<SeaDropInfo> GetPublicDropAsync(OpenSeaCollection collection,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        string seaDropCode = await web3.Eth.GetCode.SendRequestAsync(options.SeaDropAddress)
            .WaitAsync(cancellationToken);
        if (seaDropCode == "0x") throw new InvalidOperationException("Không tìm thấy SeaDrop trên Robinhood Chain.");

        var query = web3.Eth.GetContractQueryHandler<GetPublicDropFunction>();
        GetPublicDropOutput output;
        try
        {
            output = await query.QueryDeserializingToObjectAsync<GetPublicDropOutput>(
                new GetPublicDropFunction { NftContract = collection.ContractAddress }, options.SeaDropAddress)
                .WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Collection này không dùng public SeaDrop nên bot không mint tự động.", exception);
        }

        PublicDropDto drop = output.PublicDrop;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (drop.EndTime == 0 || now < (long)drop.StartTime || now > (long)drop.EndTime)
            throw new InvalidOperationException("Public mint chưa mở hoặc đã kết thúc.");
        if (drop.MaxTotalMintableByWallet == 0)
            throw new InvalidOperationException("Collection không có public mint SeaDrop đang hoạt động.");

        string? restrictedFeeRecipient = null;
        if (drop.RestrictFeeRecipients)
        {
            var allowedQuery = web3.Eth.GetContractQueryHandler<GetAllowedFeeRecipientsFunction>();
            GetAllowedFeeRecipientsOutput allowed = await allowedQuery
                .QueryDeserializingToObjectAsync<GetAllowedFeeRecipientsOutput>(
                    new GetAllowedFeeRecipientsFunction { NftContract = collection.ContractAddress },
                    options.SeaDropAddress).WaitAsync(cancellationToken);
            restrictedFeeRecipient = allowed.Recipients.FirstOrDefault()
                ?? throw new InvalidOperationException("SeaDrop không có fee recipient hợp lệ.");
        }
        return new SeaDropInfo(collection, drop.MintPrice, drop.MaxTotalMintableByWallet,
            drop.StartTime, drop.EndTime, drop.RestrictFeeRecipients, restrictedFeeRecipient);
    }

    public async Task<NftMintResult> SendMintAsync(EvmWalletCredentials wallet,
        SeaDropInfo drop, int quantity, BigInteger nonce, CancellationToken cancellationToken)
    {
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        BigInteger value = drop.MintPriceWei * quantity;
        if (value > Web3.Convert.ToWei(options.MaxMintValuePerWalletEth))
            throw new InvalidOperationException("Giá mint vượt giới hạn an toàn đã cấu hình.");

        Account account = new Account(wallet.PrivateKey, RobinhoodChainId);
        if (!account.Address.Equals(wallet.Address, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Private key không khớp địa chỉ ví NFT.");
        Web3 web3 = new Web3(account, options.RpcUrl);
        MintPublicFunction function = new MintPublicFunction
        {
            NftContract = drop.Collection.ContractAddress,
            FeeRecipient = drop.RestrictedFeeRecipient ?? account.Address,
            MinterIfNotPayer = ZeroAddress,
            Quantity = quantity,
            AmountToSend = value,
            Nonce = nonce
        };
        var handler = web3.Eth.GetContractTransactionHandler<MintPublicFunction>();
        HexBigInteger gas;
        try
        {
            gas = await handler.EstimateGasAsync(options.SeaDropAddress, function)
                .WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(ExplainContractError(exception), exception);
        }
        HexBigInteger gasPrice = await web3.Eth.GasPrice.SendRequestAsync().WaitAsync(cancellationToken);
        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address)
            .WaitAsync(cancellationToken);
        BigInteger required = value + gas.Value * gasPrice.Value * 120 / 100;
        if (balance.Value < required) throw new InvalidOperationException("Ví không đủ ETH cho giá mint và gas.");
        if (!options.EnableRealTransactions)
            return new NftMintResult(account.Address, null, quantity, value, gas.Value, true);

        function.Gas = gas;
        function.GasPrice = new HexBigInteger(gasPrice.Value * 120 / 100);
        string hash;
        try
        {
            hash = await handler.SendRequestAsync(options.SeaDropAddress, function)
                .WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(ExplainContractError(exception), exception);
        }
        return new NftMintResult(account.Address, hash, quantity, value, gas.Value, false);
    }

    // Đổi lỗi contract thường gặp thành lý do người dùng có thể hiểu được.
    private static string ExplainContractError(Exception exception)
    {
        string? data = exception switch
        {
            SmartContractRevertException revert => revert.EncodedData,
            Nethereum.Contracts.SmartContractCustomErrorRevertException custom => custom.ExceptionEncodedData,
            Nethereum.JsonRpc.Client.RpcResponseException rpc => rpc.RpcError.Data?.ToString(),
            _ => null
        };
        data = data?.Trim('"');
        string? explained = ExplainRevertData(data);
        if (explained != null) return explained;
        return data == null ? exception.Message
            : $"Contract từ chối giao dịch: {exception.Message}. Revert data: {data}";
    }

    internal static string? ExplainRevertData(string? data)
    {
        if (data?.StartsWith("0x13da22f2", StringComparison.OrdinalIgnoreCase) == true
            && data.Length >= 10 + 64 * 3)
        {
            long current = ReadUnixTime(data.Substring(10, 64));
            long start = ReadUnixTime(data.Substring(74, 64));
            long end = ReadUnixTime(data.Substring(138, 64));
            return "Public mint không còn hoạt động. Thời gian chain: " + FormatTime(current)
                + ", mở: " + FormatTime(start) + ", đóng: " + FormatTime(end) + ".";
        }
        return null;
    }

    private static long ReadUnixTime(string hex) =>
        (long)BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string FormatTime(long unixTime) => DateTimeOffset.FromUnixTimeSeconds(unixTime)
        .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    public async Task<BigInteger> GetPendingNonceAsync(string address, CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        return (await web3.Eth.Transactions.GetTransactionCount
            .SendRequestAsync(address, Nethereum.RPC.Eth.DTOs.BlockParameter.CreatePending())
            .WaitAsync(cancellationToken)).Value;
    }

    public async Task<NftMintReceipt> WaitForReceiptAsync(string transactionHash,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            Nethereum.RPC.Eth.DTOs.TransactionReceipt receipt = await web3.TransactionReceiptPolling
                .PollForReceiptAsync(transactionHash, timeout.Token);
            if (receipt.Status?.Value != BigInteger.One)
                throw new InvalidOperationException("Giao dịch đã được xác nhận nhưng bị revert on-chain.");
            return new NftMintReceipt(receipt.BlockNumber.Value, receipt.GasUsed.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Giao dịch đã gửi nhưng chưa được xác nhận sau 90 giây.");
        }
    }

    public async Task<BigInteger> GetMintedCountAsync(string nftContract, string minter,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        var query = web3.Eth.GetContractQueryHandler<GetMintStatsFunction>();
        GetMintStatsOutput output = await query.QueryDeserializingToObjectAsync<GetMintStatsOutput>(
            new GetMintStatsFunction { Minter = minter }, nftContract).WaitAsync(cancellationToken);
        return output.MinterNumMinted;
    }

    [Function("getPublicDrop", typeof(GetPublicDropOutput))]
    private sealed class GetPublicDropFunction : FunctionMessage
    {
        [Parameter("address", "nftContract", 1)] public string NftContract { get; set; } = string.Empty;
    }

    [FunctionOutput]
    private sealed class GetPublicDropOutput : IFunctionOutputDTO
    {
        [Parameter("tuple", "publicDrop", 1)] public PublicDropDto PublicDrop { get; set; } = new();
    }

    private sealed class PublicDropDto
    {
        [Parameter("uint80", "mintPrice", 1)] public BigInteger MintPrice { get; set; }
        [Parameter("uint48", "startTime", 2)] public ulong StartTime { get; set; }
        [Parameter("uint48", "endTime", 3)] public ulong EndTime { get; set; }
        [Parameter("uint16", "maxTotalMintableByWallet", 4)] public ushort MaxTotalMintableByWallet { get; set; }
        [Parameter("uint16", "feeBps", 5)] public ushort FeeBps { get; set; }
        [Parameter("bool", "restrictFeeRecipients", 6)] public bool RestrictFeeRecipients { get; set; }
    }

    [Function("getAllowedFeeRecipients", typeof(GetAllowedFeeRecipientsOutput))]
    private sealed class GetAllowedFeeRecipientsFunction : FunctionMessage
    {
        [Parameter("address", "nftContract", 1)] public string NftContract { get; set; } = string.Empty;
    }

    [FunctionOutput]
    private sealed class GetAllowedFeeRecipientsOutput : IFunctionOutputDTO
    {
        [Parameter("address[]", "recipients", 1)] public List<string> Recipients { get; set; } = [];
    }

    [Function("mintPublic")]
    private sealed class MintPublicFunction : FunctionMessage
    {
        [Parameter("address", "nftContract", 1)] public string NftContract { get; set; } = string.Empty;
        [Parameter("address", "feeRecipient", 2)] public string FeeRecipient { get; set; } = string.Empty;
        [Parameter("address", "minterIfNotPayer", 3)] public string MinterIfNotPayer { get; set; } = string.Empty;
        [Parameter("uint256", "quantity", 4)] public BigInteger Quantity { get; set; }
    }

    [Function("getMintStats", typeof(GetMintStatsOutput))]
    private sealed class GetMintStatsFunction : FunctionMessage
    {
        [Parameter("address", "minter", 1)] public string Minter { get; set; } = string.Empty;
    }

    [FunctionOutput]
    private sealed class GetMintStatsOutput : IFunctionOutputDTO
    {
        [Parameter("uint256", "minterNumMinted", 1)] public BigInteger MinterNumMinted { get; set; }
        [Parameter("uint256", "currentTotalSupply", 2)] public BigInteger CurrentTotalSupply { get; set; }
        [Parameter("uint256", "maxSupply", 3)] public BigInteger MaxSupply { get; set; }
    }

}

public sealed record OpenSeaCollection(string Slug, string Name, string ContractAddress);
public sealed record SeaDropInfo(OpenSeaCollection Collection, BigInteger MintPriceWei,
    int MaxTotalMintableByWallet, ulong StartTime, ulong EndTime, bool RestrictFeeRecipients,
    string? RestrictedFeeRecipient);
public sealed record NftMintResult(string Wallet, string? TransactionHash, int Quantity,
    BigInteger Value, BigInteger Gas, bool IsDryRun);
public sealed record NftMintReceipt(BigInteger BlockNumber, BigInteger GasUsed);
