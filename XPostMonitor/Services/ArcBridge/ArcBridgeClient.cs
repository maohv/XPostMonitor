using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using XPostMonitor.Configuration;
using XPostMonitor.Models;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.ArcBridge;

// Thuc hien duy nhat luong bridge USDC tu Base sang Arc Mainnet qua DYOR va Circle Gateway.
public sealed class ArcBridgeClient
{
    private const long BaseChainId = 8453;
    private const string BaseUsdcAddress = "0x833589fCD6eDb6E08f4C7C32D4f71b54bdA02913";
    private const string DyorRouterAddress = "0xE2bcdb651A1f991e3B099D1d9982926B241D1DD0";
    private const string BaseExplorer = "https://basescan.org/tx/";
    private const string ArcExplorer = "https://arc.exploreme.pro/tx/";
    private const int PlatformFeePercent = 3;

    private const string Erc20Abi = """
        [
          {"type":"function","name":"balanceOf","stateMutability":"view","inputs":[{"name":"account","type":"address"}],"outputs":[{"name":"","type":"uint256"}]},
          {"type":"function","name":"allowance","stateMutability":"view","inputs":[{"name":"owner","type":"address"},{"name":"spender","type":"address"}],"outputs":[{"name":"","type":"uint256"}]},
          {"type":"function","name":"approve","stateMutability":"nonpayable","inputs":[{"name":"spender","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[{"name":"","type":"bool"}]}
        ]
        """;

    private const string RouterAbi = """
        [
          {"type":"function","name":"deposit","stateMutability":"nonpayable","inputs":[{"name":"grossAmount","type":"uint256"},{"name":"gatewayFeeReserve","type":"uint256"}],"outputs":[{"name":"gatewayDeposit","type":"uint256"}]}
        ]
        """;

    private readonly HttpClient httpClient;
    private readonly ArcBridgeOptions options;
    private readonly ArcBridgeTransferService transferService;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> walletLocks = new();
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ArcBridgeClient(HttpClient httpClient, ArcBridgeOptions options,
        ArcBridgeTransferService transferService)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.transferService = transferService;
    }

    // Doc so du va bao gia. Ham nay chi doc du lieu, khong ky va khong gui giao dich.
    public async Task<ArcBridgeWalletStatus> GetWalletStatusAsync(EvmWalletCredentials wallet,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        Web3 web3 = new Web3(options.BaseRpcUrl);
        var usdc = web3.Eth.GetContract(Erc20Abi, BaseUsdcAddress);

        Task<BigInteger> usdcTask = usdc.GetFunction("balanceOf")
            .CallAsync<BigInteger>(wallet.Address).WaitAsync(cancellationToken);
        Task<HexBigInteger> ethTask = web3.Eth.GetBalance.SendRequestAsync(wallet.Address)
            .WaitAsync(cancellationToken);
        Task<BigInteger> gatewayTask = GetGatewayBalanceAsync(wallet.Address, cancellationToken);
        await Task.WhenAll(usdcTask, ethTask, gatewayTask);
        return new ArcBridgeWalletStatus(wallet.Address, ToUsdc(await usdcTask),
            Nethereum.Util.UnitConversion.Convert.FromWei((await ethTask).Value),
            ToUsdc(await gatewayTask));
    }

    // Tính phí chính xác cho số lượng user vừa nhập nhưng chưa gửi giao dịch.
    public async Task<ArcBridgeStatus> GetStatusAsync(EvmWalletCredentials wallet,
        decimal grossAmount, CancellationToken cancellationToken)
    {
        BigInteger grossAmountAtomic = ToAtomic(grossAmount);
        BigInteger platformFeeAtomic = grossAmountAtomic * PlatformFeePercent / 100;
        BigInteger receiveAmountAtomic = grossAmountAtomic - platformFeeAtomic;
        if (receiveAmountAtomic <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grossAmount));
        }

        Task<ArcBridgeWalletStatus> walletTask = GetWalletStatusAsync(wallet,
            cancellationToken);
        Task<ArcBridgeQuote> quoteTask = GetQuoteAsync(wallet.Address, receiveAmountAtomic,
            cancellationToken);
        await Task.WhenAll(walletTask, quoteTask);
        ArcBridgeWalletStatus walletStatus = await walletTask;
        ArcBridgeQuote quote = await quoteTask;
        return new ArcBridgeStatus(
            walletStatus.WalletAddress,
            walletStatus.BaseUsdc,
            walletStatus.BaseEth,
            walletStatus.GatewayUsdc,
            ToUsdc(grossAmountAtomic),
            ToUsdc(platformFeeAtomic),
            ToUsdc(quote.MaxFeeAtomic),
            ToUsdc(receiveAmountAtomic),
            ToUsdc(grossAmountAtomic + quote.MaxFeeAtomic));
    }

    // Báo phí cho số USDC đã nằm trong Circle Gateway và có thể nhận trên Arc.
    public async Task<ArcBridgeClaimStatus> GetClaimStatusAsync(EvmWalletCredentials wallet,
        CancellationToken cancellationToken)
    {
        BigInteger gatewayBalance = await GetGatewayBalanceAsync(wallet.Address,
            cancellationToken);
        if (gatewayBalance <= 11_000)
        {
            return new ArcBridgeClaimStatus(ToUsdc(gatewayBalance), 0, 0);
        }

        (ArcBridgeQuote quote, BigInteger receiveAmount) = await GetClaimQuoteAsync(
            wallet.Address, gatewayBalance, cancellationToken);
        return new ArcBridgeClaimStatus(ToUsdc(gatewayBalance),
            ToUsdc(quote.MaxFeeAtomic), ToUsdc(receiveAmount));
    }

    // Gui approve, deposit, cho Circle xac nhan, ky BurnIntent va nhan USDC tren Arc.
    public async Task<ArcBridgeResult> BridgeAsync(long chatId, EvmWalletCredentials wallet,
        decimal requestedGrossAmount,
        Func<ArcBridgeStage, Task> reportStage, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        SemaphoreSlim walletLock = walletLocks.GetOrAdd(wallet.Address,
            _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        ArcBridgeTransfer? transfer = null;
        bool depositWasSent = false;
        try
        {
            transfer = await transferService.GetPendingAsync(chatId, cancellationToken);
            BurnIntentData burnIntent;
            BigInteger maxFeeAtomic;
            BigInteger receiveAmountAtomic;

            if (transfer == null)
            {
                BigInteger grossAmountAtomic = ToAtomic(requestedGrossAmount);
                BigInteger platformFeeAtomic = grossAmountAtomic * PlatformFeePercent / 100;
                receiveAmountAtomic = grossAmountAtomic - platformFeeAtomic;
                if (receiveAmountAtomic <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(requestedGrossAmount));
                }

                ArcBridgeQuote quote = await GetQuoteAsync(wallet.Address, receiveAmountAtomic,
                    cancellationToken);
                burnIntent = quote.BurnIntent;
                maxFeeAtomic = quote.MaxFeeAtomic;
                transfer = await transferService.CreateAsync(chatId, wallet.Address,
                    grossAmountAtomic.ToString(CultureInfo.InvariantCulture),
                    receiveAmountAtomic.ToString(CultureInfo.InvariantCulture),
                    maxFeeAtomic.ToString(CultureInfo.InvariantCulture),
                    JsonSerializer.Serialize(burnIntent, jsonOptions), cancellationToken);
            }
            else
            {
                if (!string.Equals(transfer.WalletAddress, wallet.Address,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The pending Bridge belongs to a different wallet.");
                }
                burnIntent = JsonSerializer.Deserialize<BurnIntentData>(
                    transfer.BurnIntentJson, jsonOptions)
                    ?? throw new JsonException("Saved Circle authorization is invalid.");
                maxFeeAtomic = ParseAtomic(transfer.MaxFeeAtomic, "Circle fee");
                receiveAmountAtomic = ParseAtomic(transfer.ReceiveAmountAtomic, "receive amount");
            }

            if (!string.IsNullOrWhiteSpace(transfer.MintTransactionHash))
            {
                await reportStage(ArcBridgeStage.Minting);
                await WaitForArcReceiptAsync(transfer.MintTransactionHash, cancellationToken);
                await transferService.CompleteAsync(transfer.Id, cancellationToken);
                return BuildResult(transfer.DepositTransactionHash,
                    transfer.MintTransactionHash, receiveAmountAtomic);
            }

            Account account = CreateAccount(wallet);
            Web3 web3 = new Web3(account, options.BaseRpcUrl);
            TransactionReceipt depositReceipt;
            if (string.IsNullOrWhiteSpace(transfer.DepositTransactionHash))
            {
                BigInteger grossAmountAtomic = ParseAtomic(transfer.GrossAmountAtomic,
                    "Bridge amount");
                BigInteger requiredUsdc = grossAmountAtomic + maxFeeAtomic;
                var usdc = web3.Eth.GetContract(Erc20Abi, BaseUsdcAddress);
                BigInteger balance = await usdc.GetFunction("balanceOf")
                    .CallAsync<BigInteger>(wallet.Address).WaitAsync(cancellationToken);
                if (balance < requiredUsdc)
                {
                    throw new InvalidOperationException("Insufficient Base USDC. Required "
                        + ToUsdc(requiredUsdc).ToString("0.######",
                            CultureInfo.InvariantCulture) + " USDC.");
                }

                BigInteger allowance = await usdc.GetFunction("allowance")
                    .CallAsync<BigInteger>(wallet.Address, DyorRouterAddress)
                    .WaitAsync(cancellationToken);
                if (allowance < requiredUsdc)
                {
                    await reportStage(ArcBridgeStage.Approving);
                    await SendContractAsync(web3, wallet.Address, BaseUsdcAddress,
                        usdc.GetFunction("approve").GetData(DyorRouterAddress, requiredUsdc),
                        null, cancellationToken);
                }

                await reportStage(ArcBridgeStage.Depositing);
                var router = web3.Eth.GetContract(RouterAbi, DyorRouterAddress);
                depositReceipt = await SendContractAsync(web3, wallet.Address,
                    DyorRouterAddress,
                    router.GetFunction("deposit").GetData(grossAmountAtomic, maxFeeAtomic),
                    async transactionHash =>
                    {
                        depositWasSent = true;
                        transfer.DepositTransactionHash = transactionHash;
                        await transferService.SaveDepositSubmittedAsync(transfer.Id,
                            transactionHash, cancellationToken);
                    }, cancellationToken);
            }
            else
            {
                depositWasSent = true;
                depositReceipt = await WaitForBaseReceiptAsync(web3,
                    transfer.DepositTransactionHash, cancellationToken);
            }

            transfer.DepositBlockNumber = depositReceipt.BlockNumber.Value
                .ToString(CultureInfo.InvariantCulture);
            await transferService.SaveDepositAsync(transfer.Id, depositReceipt.TransactionHash,
                transfer.DepositBlockNumber, cancellationToken);

            await reportStage(ArcBridgeStage.WaitingCircle);
            await WaitForCircleAsync(wallet.Address, receiveAmountAtomic + maxFeeAtomic,
                depositReceipt.TransactionHash, depositReceipt.BlockNumber.Value, cancellationToken);

            await reportStage(ArcBridgeStage.Signing);
            ArcBridgeMintResult mint = await TransferAndMintAsync(wallet, burnIntent,
                cancellationToken);
            await transferService.SaveMintAsync(transfer.Id, mint.MintTransactionHash,
                cancellationToken);
            await reportStage(ArcBridgeStage.Minting);
            await WaitForArcReceiptAsync(mint.MintTransactionHash, cancellationToken);
            await transferService.CompleteAsync(transfer.Id, cancellationToken);
            return BuildResult(depositReceipt.TransactionHash, mint.MintTransactionHash,
                receiveAmountAtomic);
        }
        catch (Exception exception)
        {
            if (transfer != null)
            {
                try
                {
                    if (exception is ArcMintRevertedException)
                    {
                        await transferService.ResetMintAsync(transfer.Id, exception.Message,
                            cancellationToken);
                    }
                    else
                    {
                        bool recoverableDeposit = (depositWasSent
                                || !string.IsNullOrWhiteSpace(
                                    transfer.DepositTransactionHash))
                            && exception is not BaseTransactionRevertedException;
                        await transferService.SaveErrorAsync(transfer.Id, recoverableDeposit,
                            exception.Message, cancellationToken);
                    }
                }
                catch
                {
                    // Giữ lỗi gốc của giao dịch thay vì thay bằng lỗi ghi log DB.
                }
            }
            throw;
        }
        finally
        {
            walletLock.Release();
        }
    }

    // Nhan lai so USDC dang nam trong Gateway neu bot da dung sau khi deposit.
    public async Task<ArcBridgeResult> ClaimAvailableAsync(EvmWalletCredentials wallet,
        Func<ArcBridgeStage, Task> reportStage, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        SemaphoreSlim walletLock = walletLocks.GetOrAdd(wallet.Address,
            _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            BigInteger gatewayBalance = await GetGatewayBalanceAsync(wallet.Address,
                cancellationToken);
            if (gatewayBalance <= 11_000)
            {
                throw new InvalidOperationException("No claimable USDC was found in Circle Gateway.");
            }

            (ArcBridgeQuote quote, BigInteger receiveAmount) = await GetClaimQuoteAsync(
                wallet.Address, gatewayBalance, cancellationToken);

            await reportStage(ArcBridgeStage.Signing);
            ArcBridgeMintResult mint = await TransferAndMintAsync(wallet, quote.BurnIntent,
                cancellationToken);
            await reportStage(ArcBridgeStage.Minting);
            await WaitForArcReceiptAsync(mint.MintTransactionHash, cancellationToken);

            return new ArcBridgeResult(null, mint.MintTransactionHash, ToUsdc(receiveAmount),
                null, ArcExplorer + mint.MintTransactionHash);
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<ArcBridgeQuote> GetQuoteAsync(string address, BigInteger amountAtomic,
        CancellationToken cancellationToken)
    {
        string salt = "0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        EstimateResponse result = await PostAsync<EstimateResponse>(new
        {
            action = "estimate",
            address,
            amountAtomic = amountAtomic.ToString(CultureInfo.InvariantCulture),
            salt
        }, cancellationToken);

        if (!BigInteger.TryParse(result.BurnIntent.MaxFee, CultureInfo.InvariantCulture,
                out BigInteger maxFee))
        {
            throw new JsonException("DYOR returned an invalid Circle fee.");
        }
        return new ArcBridgeQuote(result.BurnIntent, maxFee);
    }

    private async Task<(ArcBridgeQuote Quote, BigInteger ReceiveAmount)> GetClaimQuoteAsync(
        string address, BigInteger gatewayBalance, CancellationToken cancellationToken)
    {
        BigInteger candidateAmount = gatewayBalance - 11_000;
        ArcBridgeQuote quote = await GetQuoteAsync(address, candidateAmount, cancellationToken);
        BigInteger receiveAmount = gatewayBalance - quote.MaxFeeAtomic;
        if (receiveAmount <= 0)
        {
            throw new InvalidOperationException(
                "Circle fee is greater than the Gateway balance.");
        }
        if (receiveAmount != candidateAmount)
        {
            quote = await GetQuoteAsync(address, receiveAmount, cancellationToken);
            receiveAmount = gatewayBalance - quote.MaxFeeAtomic;
        }
        if (receiveAmount + quote.MaxFeeAtomic > gatewayBalance)
        {
            throw new InvalidOperationException(
                "Gateway balance no longer covers the Circle fee.");
        }
        return (quote, receiveAmount);
    }

    private async Task<BigInteger> GetGatewayBalanceAsync(string address,
        CancellationToken cancellationToken)
    {
        BalanceResponse result = await PostAsync<BalanceResponse>(new
        {
            action = "balance",
            address
        }, cancellationToken);
        return BigInteger.TryParse(result.BalanceAtomic, CultureInfo.InvariantCulture,
            out BigInteger balance)
            ? balance
            : throw new JsonException("DYOR returned an invalid Gateway balance.");
    }

    private async Task WaitForCircleAsync(string address, BigInteger minimumAtomic,
        string depositTransactionHash, BigInteger depositBlock, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(Math.Max(1, options.PollTimeoutMinutes));
        while (DateTime.UtcNow < deadline)
        {
            ProgressResponse progress = await PostAsync<ProgressResponse>(new
            {
                action = "progress",
                address,
                minimumAtomic = minimumAtomic.ToString(CultureInfo.InvariantCulture),
                depositTx = depositTransactionHash,
                depositBlock = depositBlock.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);

            if (progress.Ready)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)),
                cancellationToken);
        }

        throw new TimeoutException("Circle has not confirmed the Base deposit yet. "
            + "Open the Bridge menu later and use Claim pending USDC.");
    }

    private async Task<ArcBridgeMintResult> TransferAndMintAsync(EvmWalletCredentials wallet,
        BurnIntentData burnIntent, CancellationToken cancellationToken)
    {
        string signature = SignBurnIntent(wallet, burnIntent);
        TransferResponse transfer = await PostAsync<TransferResponse>(new
        {
            action = "transfer",
            burnIntent,
            signature
        }, cancellationToken);

        if (string.IsNullOrWhiteSpace(transfer.SponsorToken))
        {
            throw new InvalidOperationException("DYOR did not provide Arc gas sponsorship.");
        }

        SponsoredMintResponse mint = await PostAsync<SponsoredMintResponse>(new
        {
            action = "sponsoredMint",
            attestation = transfer.Attestation,
            signature = transfer.Signature,
            sponsorToken = transfer.SponsorToken
        }, cancellationToken);

        if (string.IsNullOrWhiteSpace(mint.MintTx))
        {
            throw new JsonException("DYOR did not return the Arc transaction hash.");
        }
        return new ArcBridgeMintResult(mint.MintTx);
    }

    private string SignBurnIntent(EvmWalletCredentials wallet, BurnIntentData burnIntent)
    {
        var typedData = new
        {
            domain = new { name = "GatewayWallet", version = "1" },
            primaryType = "BurnIntent",
            types = new Dictionary<string, object>
            {
                ["EIP712Domain"] = new[]
                {
                    new { name = "name", type = "string" },
                    new { name = "version", type = "string" }
                },
                ["TransferSpec"] = new[]
                {
                    new { name = "version", type = "uint32" },
                    new { name = "sourceDomain", type = "uint32" },
                    new { name = "destinationDomain", type = "uint32" },
                    new { name = "sourceContract", type = "bytes32" },
                    new { name = "destinationContract", type = "bytes32" },
                    new { name = "sourceToken", type = "bytes32" },
                    new { name = "destinationToken", type = "bytes32" },
                    new { name = "sourceDepositor", type = "bytes32" },
                    new { name = "destinationRecipient", type = "bytes32" },
                    new { name = "sourceSigner", type = "bytes32" },
                    new { name = "destinationCaller", type = "bytes32" },
                    new { name = "value", type = "uint256" },
                    new { name = "salt", type = "bytes32" },
                    new { name = "hookData", type = "bytes" }
                },
                ["BurnIntent"] = new[]
                {
                    new { name = "maxBlockHeight", type = "uint256" },
                    new { name = "maxFee", type = "uint256" },
                    new { name = "spec", type = "TransferSpec" }
                }
            },
            message = burnIntent
        };

        string json = JsonSerializer.Serialize(typedData, jsonOptions);
        string cleanPrivateKey = wallet.PrivateKey.StartsWith("0x",
            StringComparison.OrdinalIgnoreCase) ? wallet.PrivateKey[2..] : wallet.PrivateKey;
        EthECKey key = new EthECKey(cleanPrivateKey);
        Eip712TypedDataSigner signer = new Eip712TypedDataSigner();
        string signature = signer.SignTypedDataV4(json, key);
        string recovered = signer.RecoverFromSignatureV4(json, signature);
        if (!string.Equals(recovered, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Circle authorization signature is invalid.");
        }
        return signature;
    }

    private static Account CreateAccount(EvmWalletCredentials wallet)
    {
        Account account = new Account(wallet.PrivateKey, BaseChainId);
        if (!string.Equals(account.Address, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The private key does not match the EVM wallet.");
        }
        return account;
    }

    private static async Task<TransactionReceipt> SendContractAsync(Web3 web3, string from,
        string contractAddress, string data, Func<string, Task>? transactionSubmitted,
        CancellationToken cancellationToken)
    {
        TransactionInput transaction = new TransactionInput
        {
            From = from,
            To = contractAddress,
            Data = data,
            Value = new HexBigInteger(BigInteger.Zero)
        };
        HexBigInteger gas = await web3.Eth.Transactions.EstimateGas.SendRequestAsync(transaction)
            .WaitAsync(cancellationToken);
        HexBigInteger currentGasPrice = await web3.Eth.GasPrice.SendRequestAsync()
            .WaitAsync(cancellationToken);
        HexBigInteger gasPrice = new HexBigInteger(currentGasPrice.Value * 120 / 100);
        HexBigInteger nativeBalance = await web3.Eth.GetBalance.SendRequestAsync(from)
            .WaitAsync(cancellationToken);
        BigInteger requiredGas = gas.Value * gasPrice.Value;
        if (nativeBalance.Value < requiredGas)
        {
            throw new InvalidOperationException("Insufficient ETH on Base for gas.");
        }

        transaction.Gas = gas;
        transaction.GasPrice = gasPrice;
        string transactionHash = await web3.TransactionManager.SendTransactionAsync(transaction)
            .WaitAsync(cancellationToken);
        if (transactionSubmitted != null)
        {
            await transactionSubmitted(transactionHash);
        }
        TransactionReceipt receipt = await WaitForBaseReceiptAsync(web3, transactionHash,
            cancellationToken);
        return receipt;
    }

    private static async Task<TransactionReceipt> WaitForBaseReceiptAsync(Web3 web3,
        string transactionHash, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            TransactionReceipt? receipt = await web3.Eth.Transactions.GetTransactionReceipt
                .SendRequestAsync(transactionHash).WaitAsync(cancellationToken);
            if (receipt == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }
        if (receipt.Status.Value != 1)
        {
            throw new BaseTransactionRevertedException("Base transaction failed: "
                + receipt.TransactionHash);
        }
        return receipt;
        }
        throw new TimeoutException("Base transaction was submitted but confirmation is pending: "
            + transactionHash);
    }

    private async Task WaitForArcReceiptAsync(string transactionHash,
        CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.ArcRpcUrl);
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            TransactionReceipt? receipt = await web3.Eth.Transactions.GetTransactionReceipt
                .SendRequestAsync(transactionHash).WaitAsync(cancellationToken);
            if (receipt != null)
            {
                if (receipt.Status.Value != 1)
                {
                    throw new ArcMintRevertedException("Arc claim transaction failed: "
                        + transactionHash);
                }
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("Arc transaction was submitted but confirmation is still pending: "
            + transactionHash);
    }

    private async Task<T> PostAsync<T>(object body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/gateway", body,
            jsonOptions, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string error = json;
            try
            {
                error = JsonDocument.Parse(json).RootElement.GetProperty("error").GetString() ?? json;
            }
            catch
            {
                // Neu response khong phai JSON thi giu nguyen noi dung loi.
            }
            throw new HttpRequestException("DYOR Arc Bridge: " + Shorten(error), null,
                response.StatusCode);
        }
        return JsonSerializer.Deserialize<T>(json, jsonOptions)
            ?? throw new JsonException("DYOR Arc Bridge returned an empty response.");
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Arc Bridge is disabled.");
        }
    }

    private static decimal ToUsdc(BigInteger atomic)
    {
        return decimal.Parse(atomic.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture) / 1_000_000m;
    }

    private static BigInteger ToAtomic(decimal amount)
    {
        if (amount <= 0 || amount > 1_000_000m
            || decimal.Round(amount, 6, MidpointRounding.ToZero) != amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }
        return new BigInteger(amount * 1_000_000m);
    }

    private static BigInteger ParseAtomic(string value, string fieldName)
    {
        return BigInteger.TryParse(value, CultureInfo.InvariantCulture, out BigInteger result)
            ? result
            : throw new JsonException("Saved " + fieldName + " is invalid.");
    }

    private static ArcBridgeResult BuildResult(string? baseTransactionHash,
        string arcTransactionHash, BigInteger receiveAmountAtomic)
    {
        return new ArcBridgeResult(baseTransactionHash, arcTransactionHash,
            ToUsdc(receiveAmountAtomic),
            string.IsNullOrWhiteSpace(baseTransactionHash)
                ? null
                : BaseExplorer + baseTransactionHash,
            ArcExplorer + arcTransactionHash);
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private sealed class EstimateResponse
    {
        public BurnIntentData BurnIntent { get; set; } = new();
    }

    private sealed class BalanceResponse
    {
        public string BalanceAtomic { get; set; } = "0";
    }

    private sealed class ProgressResponse
    {
        public bool Ready { get; set; }
    }

    private sealed class TransferResponse
    {
        public string Attestation { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string SponsorToken { get; set; } = string.Empty;
        public string TransferId { get; set; } = string.Empty;
    }

    private sealed class SponsoredMintResponse
    {
        public string MintTx { get; set; } = string.Empty;
    }

    private sealed class BaseTransactionRevertedException : Exception
    {
        public BaseTransactionRevertedException(string message) : base(message)
        {
        }
    }

    private sealed class ArcMintRevertedException : Exception
    {
        public ArcMintRevertedException(string message) : base(message)
        {
        }
    }

    public sealed class BurnIntentData
    {
        public string MaxBlockHeight { get; set; } = string.Empty;
        public string MaxFee { get; set; } = string.Empty;
        public TransferSpecData Spec { get; set; } = new();
    }

    public sealed class TransferSpecData
    {
        public uint Version { get; set; }
        public uint SourceDomain { get; set; }
        public uint DestinationDomain { get; set; }
        public string SourceContract { get; set; } = string.Empty;
        public string DestinationContract { get; set; } = string.Empty;
        public string SourceToken { get; set; } = string.Empty;
        public string DestinationToken { get; set; } = string.Empty;
        public string SourceDepositor { get; set; } = string.Empty;
        public string DestinationRecipient { get; set; } = string.Empty;
        public string SourceSigner { get; set; } = string.Empty;
        public string DestinationCaller { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public string HookData { get; set; } = "0x";
    }
}

public enum ArcBridgeStage
{
    Approving,
    Depositing,
    WaitingCircle,
    Signing,
    Minting
}

public sealed record ArcBridgeStatus(string WalletAddress, decimal BaseUsdc, decimal BaseEth,
    decimal GatewayUsdc, decimal GrossAmount, decimal PlatformFee, decimal CircleMaxFee,
    decimal ExpectedReceive, decimal RequiredBaseUsdc);

public sealed record ArcBridgeWalletStatus(string WalletAddress, decimal BaseUsdc,
    decimal BaseEth, decimal GatewayUsdc);

public sealed record ArcBridgeClaimStatus(decimal GatewayUsdc, decimal CircleMaxFee,
    decimal ExpectedReceive);

public sealed record ArcBridgeQuote(ArcBridgeClient.BurnIntentData BurnIntent,
    BigInteger MaxFeeAtomic);

public sealed record ArcBridgeMintResult(string MintTransactionHash);

public sealed record ArcBridgeResult(string? BaseTransactionHash, string ArcTransactionHash,
    decimal ReceivedUsdc, string? BaseExplorerUrl, string ArcExplorerUrl);
