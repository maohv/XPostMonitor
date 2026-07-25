using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Nethereum.ABI;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using XPostMonitor.Configuration;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Launchpads.LongRobinhood;

public sealed class LongRobinhoodClient
{
    private const long ChainId = 4663;
    private const int FeeDecaySeconds = 10;
    private const int DynamicFeeFlag = 0x800000;
    private const long InitialBuyGasLimit = 1000000;
    private const string LauncherAddress = "0x22e99278308B393ea1260859B181AD7E78f5eeED";
    private const string TokenFactoryAddress = "0x1B37D3a72082029c44B35B604Ea473617580b69a";
    private const string GovernanceFactoryAddress = "0x85f37f74Ef2478A770318bc810177a9835911aD7";
    private const string PoolInitializerAddress = "0x4e3468951D49f2EEa976eD0D6e75fFCb44a9a544";
    private const string LiquidityMigratorAddress = "0xba2F330EDb16cD8056f5988d8CE19BbC63475A0e";
    private const string IntegratorAddress = "0x92d435C96E63c43E12d6D0AB28f6b0B04072F765";
    private const string DopplerHookAddress = "0x6f02324d20cc679d0e585290caa6b16bacbc0f77";
    private const string LaunchCreatedTopic = "0xadc6f1f726f7c710f77ec06adc75f3bb964e5be19581b072c67f7b9b4039267b";
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
    private const string WrappedEthAddress = "0x0bd7d308f8e1639fab988df18a8011f41eacad73";
    private const string UniswapV2FactoryAddress = "0x8bceaa40b9acdfaedf85adf4ff01f5ad6517937f";
    private const string UniversalRouterAddress = "0x8876789976decbfcbbbe364623c63652db8c0904";
    private const string DryRunTokenUri = "ipfs://bafkreidw2jltlq6iracbff6kezkytfartob7djta6a6omsbtde3tevy3eq";

    private readonly HttpClient httpClient;
    private readonly LongRobinhoodOptions options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> walletLocks = new();

    public LongRobinhoodClient(HttpClient httpClient, LongRobinhoodOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<LongRobinhoodConnectionResult> CheckAsync(string? walletAddress, CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(options.RpcUrl);
        HexBigInteger chainId = await web3.Eth.ChainId.SendRequestAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string code = await web3.Eth.GetCode.SendRequestAsync(LauncherAddress).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        bool paused = await web3.Eth.GetContractQueryHandler<PausedFunction>().QueryAsync<bool>(LauncherAddress, new PausedFunction()).WaitAsync(cancellationToken);
        decimal? balance = null;

        if (!string.IsNullOrWhiteSpace(walletAddress))
        {
            HexBigInteger value = await web3.Eth.GetBalance.SendRequestAsync(walletAddress).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            balance = UnitConversion.Convert.FromWei(value.Value);
        }

        return new LongRobinhoodConnectionResult(chainId.Value == ChainId, code != "0x", paused, balance, options.EnableRealTransactions);
    }

    public async Task<LongRobinhoodTokenResult> CreateTokenAsync(EvmWalletCredentials wallet, LongRobinhoodTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.Image.Length == 0)
        {
            throw new InvalidOperationException("Long requires a token image.");
        }

        LaunchpadAnchor anchor = LaunchpadCatalog.FindLongAnchor(request.Anchor)
            ?? throw new InvalidOperationException("Long stock anchor is not supported.");
        string symbol = CleanSymbol(request.Symbol);

        SemaphoreSlim walletLock = walletLocks.GetOrAdd(wallet.Address, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateTokenInternalAsync(wallet, request, anchor, symbol, cancellationToken);
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<LongRobinhoodTokenResult> CreateTokenInternalAsync(EvmWalletCredentials wallet, LongRobinhoodTokenRequest request, LaunchpadAnchor anchor, string symbol, CancellationToken cancellationToken)
    {
        Account account = new Account(wallet.PrivateKey, ChainId);
        if (!string.Equals(account.Address, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The encrypted private key does not match the EVM wallet address.");
        }

        Web3 web3 = new Web3(account, options.RpcUrl);
        bool available = await web3.Eth.GetContractQueryHandler<IsTickerAvailableFunction>()
            .QueryAsync<bool>(LauncherAddress, new IsTickerAvailableFunction { Ticker = symbol }).WaitAsync(cancellationToken);
        if (!available)
        {
            throw new InvalidOperationException("Long ticker " + symbol + " is reserved for 24 hours.");
        }

        HexBigInteger balance = await web3.Eth.GetBalance.SendRequestAsync(account.Address).WaitAsync(cancellationToken);
        BigInteger buyAmount = UnitConversion.Convert.ToWei(request.BuyAmount);
        if (options.EnableRealTransactions && balance.Value < buyAmount)
        {
            throw new InvalidOperationException("Insufficient ETH for the initial buy.");
        }

        Web3 logWeb3 = new Web3(options.LogRpcUrl);
        LongCurve curve = await GetCurrentCurveAsync(logWeb3, anchor.TokenAddress, cancellationToken);
        string tokenUri = options.EnableRealTransactions
            ? await UploadMetadataAsync(account.Address, request, symbol, cancellationToken)
            : DryRunTokenUri;
        CreateFunction function = BuildCreateFunction(account.Address, anchor.TokenAddress, request, symbol, tokenUri, curve);
        var handler = web3.Eth.GetContractTransactionHandler<CreateFunction>();
        HexBigInteger gas = await handler.EstimateGasAsync(LauncherAddress, function).WaitAsync(cancellationToken);
        HexBigInteger currentGasPrice = await web3.Eth.GasPrice.SendRequestAsync().WaitAsync(cancellationToken);
        HexBigInteger gasPrice = new HexBigInteger(AddGasBuffer(currentGasPrice.Value));
        BigInteger requiredBalance = buyAmount + gas.Value * gasPrice.Value * 3;

        if (!options.EnableRealTransactions)
        {
            return new LongRobinhoodTokenResult(null, null, gas.Value, true, balance.Value >= requiredBalance);
        }
        if (balance.Value < requiredBalance)
        {
            throw new InvalidOperationException("Insufficient ETH. Required about "
                + UnitConversion.Convert.FromWei(requiredBalance).ToString("0.########", CultureInfo.InvariantCulture)
                + " ETH for gas.");
        }

        function.Gas = gas;
        function.GasPrice = gasPrice;
        TransactionReceipt receipt = await handler.SendRequestAndWaitForReceiptAsync(LauncherAddress, function,
            cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("Long token transaction failed: " + receipt.TransactionHash);
        }

        EventLog<LaunchCreatedEventDto>? created = receipt.DecodeAllEvents<LaunchCreatedEventDto>().FirstOrDefault();
        string tokenAddress = created?.Event.Asset
            ?? throw new InvalidOperationException("Long created the token but its address was not found. Launch transaction: "
                + receipt.TransactionHash);
        try
        {
            await WaitForFeeDropAsync(web3, receipt.BlockNumber, cancellationToken);
            BigInteger minimumStockAmount = await GetMinimumStockAmountAsync(web3, anchor.TokenAddress,
                buyAmount, request.SlippagePercent, cancellationToken);
            await SendInitialBuyAsync(web3, anchor.TokenAddress, tokenAddress, buyAmount,
                minimumStockAmount, curve, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Token " + tokenAddress
                + " was created, but the initial buy failed. Launch transaction: " + receipt.TransactionHash
                + ". Error: " + exception.Message, exception);
        }

        return new LongRobinhoodTokenResult(receipt.TransactionHash, tokenAddress, gas.Value, false, true);
    }

    private static async Task WaitForFeeDropAsync(Web3 web3, HexBigInteger launchBlockNumber, CancellationToken cancellationToken)
    {
        BlockWithTransactions launchBlock = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber
            .SendRequestAsync(new BlockParameter(launchBlockNumber)).WaitAsync(cancellationToken);
        // Pending block can still use the last decay fee, so wait one extra chain second.
        BigInteger feeEndsAt = launchBlock.Timestamp.Value + FeeDecaySeconds + 1;

        while (true)
        {
            BlockWithTransactions latestBlock = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber
                .SendRequestAsync(BlockParameter.CreateLatest()).WaitAsync(cancellationToken);
            if (latestBlock.Timestamp.Value >= feeEndsAt)
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private static async Task<BigInteger> GetMinimumStockAmountAsync(Web3 web3, string stockAddress, BigInteger buyAmount, decimal slippagePercent, CancellationToken cancellationToken)
    {
        string pairAddress = await web3.Eth.GetContractQueryHandler<GetPairFunction>()
            .QueryAsync<string>(UniswapV2FactoryAddress, new GetPairFunction
            {
                TokenA = WrappedEthAddress,
                TokenB = stockAddress
            }).WaitAsync(cancellationToken);
        if (string.Equals(pairAddress, ZeroAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No Uniswap V2 pool was found for the selected stock token.");
        }

        GetReservesOutput reserves = await web3.Eth.GetContractQueryHandler<GetReservesFunction>()
            .QueryDeserializingToObjectAsync<GetReservesOutput>(new GetReservesFunction(), pairAddress)
            .WaitAsync(cancellationToken);
        string token0 = await web3.Eth.GetContractQueryHandler<Token0Function>()
            .QueryAsync<string>(pairAddress, new Token0Function()).WaitAsync(cancellationToken);
        BigInteger reserveIn = string.Equals(token0, WrappedEthAddress, StringComparison.OrdinalIgnoreCase)
            ? reserves.Reserve0 : reserves.Reserve1;
        BigInteger reserveOut = string.Equals(token0, WrappedEthAddress, StringComparison.OrdinalIgnoreCase)
            ? reserves.Reserve1 : reserves.Reserve0;

        if (reserveIn <= 0 || reserveOut <= 0)
        {
            throw new InvalidOperationException("The selected stock pool has no liquidity.");
        }

        BigInteger amountWithFee = buyAmount * 997;
        BigInteger expectedStockAmount = amountWithFee * reserveOut / (reserveIn * 1000 + amountWithFee);
        return ApplySlippage(expectedStockAmount, slippagePercent);
    }

    private static async Task SendInitialBuyAsync(Web3 web3, string stockAddress, string tokenAddress, BigInteger buyAmount, BigInteger minimumStockAmount, LongCurve curve, CancellationToken cancellationToken)
    {
        PoolKeyData poolKey = BuildPoolKey(stockAddress, tokenAddress, curve);
        bool zeroForOne = string.Equals(poolKey.Currency0, stockAddress, StringComparison.OrdinalIgnoreCase);
        // RPC nodes cannot quote a new pool immediately. V2 still keeps the user's slippage limit.
        ExecuteFunction function = BuildBuyFunction(stockAddress, tokenAddress, buyAmount,
            minimumStockAmount, BigInteger.One, poolKey, zeroForOne);

        var handler = web3.Eth.GetContractTransactionHandler<ExecuteFunction>();
        HexBigInteger currentGasPrice = await web3.Eth.GasPrice.SendRequestAsync().WaitAsync(cancellationToken);
        function.Gas = new HexBigInteger(InitialBuyGasLimit);
        function.GasPrice = new HexBigInteger(AddGasBuffer(currentGasPrice.Value));

        TransactionReceipt receipt = await handler.SendRequestAndWaitForReceiptAsync(UniversalRouterAddress,
            function, cancellationToken);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException("Long initial buy failed: " + receipt.TransactionHash);
        }
    }

    private static PoolKeyData BuildPoolKey(string stockAddress, string tokenAddress, LongCurve curve)
    {
        bool stockFirst = string.Compare(stockAddress, tokenAddress, StringComparison.OrdinalIgnoreCase) < 0;
        return new PoolKeyData
        {
            Currency0 = stockFirst ? stockAddress : tokenAddress,
            Currency1 = stockFirst ? tokenAddress : stockAddress,
            Fee = DynamicFeeFlag,
            TickSpacing = curve.TickSpacing,
            Hooks = PoolInitializerAddress
        };
    }

    private static ExecuteFunction BuildBuyFunction(string stockAddress, string tokenAddress, BigInteger buyAmount, BigInteger minimumStockAmount, BigInteger minimumTokenAmount, PoolKeyData poolKey, bool zeroForOne)
    {
        ABIEncode encoder = new ABIEncode();
        byte[] wrapInput = encoder.GetABIParamsEncoded(new WrapEthData
        {
            Recipient = UniversalRouterAddress,
            Amount = buyAmount
        });
        byte[] v2Input = encoder.GetABIParamsEncoded(new V2SwapExactInputData
        {
            Recipient = UniversalRouterAddress,
            AmountIn = buyAmount,
            AmountOutMinimum = minimumStockAmount,
            Path = [WrappedEthAddress, stockAddress],
            PayerIsUser = false
        });
        byte[] swapInput = encoder.GetABIParamsEncoded(new ExactInputSingleWrapper
        {
            Params = new ExactInputSingleData
            {
                PoolKey = poolKey,
                ZeroForOne = zeroForOne,
                AmountIn = BigInteger.Zero,
                AmountOutMinimum = minimumTokenAmount
            }
        });
        byte[] settleInput = encoder.GetABIParamsEncoded(new SettleData
        {
            Currency = stockAddress,
            Amount = BigInteger.One << 255,
            PayerIsUser = false
        });
        byte[] takeInput = encoder.GetABIParamsEncoded(new TakeAllData
        {
            Currency = tokenAddress,
            MinimumAmount = minimumTokenAmount
        });
        byte[] v4Input = encoder.GetABIParamsEncoded(new V4SwapData
        {
            Actions = [0x0b, 0x06, 0x0f],
            Parameters = [settleInput, swapInput, takeInput]
        });

        return new ExecuteFunction
        {
            Commands = [0x0b, 0x08, 0x10],
            Inputs = [wrapInput, v2Input, v4Input],
            AmountToSend = buyAmount
        };
    }

    private static BigInteger ApplySlippage(BigInteger amount, decimal slippagePercent)
    {
        int slippageBps = (int)Math.Clamp(slippagePercent * 100m, 1m, 5000m);
        return amount * (10000 - slippageBps) / 10000;
    }

    private static CreateFunction BuildCreateFunction(string walletAddress, string numeraire, LongRobinhoodTokenRequest request, string symbol, string tokenUri, LongCurve curve)
    {
        ABIEncode encoder = new ABIEncode();
        byte[] tokenFactoryData = encoder.GetABIParamsEncoded(new TokenFactoryData
        {
            Name = Clean(request.Name, 64),
            Symbol = symbol,
            TokenUri = tokenUri
        });
        byte[] hookData = encoder.GetABIParamsEncoded(new HookDataWrapper
        {
            Data = new HookData
            {
                Numeraire = numeraire,
                BuybackDestination = IntegratorAddress,
                StartFee = 800000,
                EndFee = 8000,
                DurationSeconds = FeeDecaySeconds,
                FeeDistribution = new FeeDistributionData
                {
                    AssetFeesToNumeraireBuyback = BigInteger.Pow(10, 18),
                    NumeraireFeesToNumeraireBuyback = BigInteger.Pow(10, 18)
                }
            }
        });
        List<PoolBeneficiaryData> beneficiaries =
        [
            new PoolBeneficiaryData { Beneficiary = walletAddress, Shares = BigInteger.Parse("950000000000000000") },
            new PoolBeneficiaryData { Beneficiary = curve.ProtocolOwner, Shares = BigInteger.Parse("50000000000000000") }
        ];
        beneficiaries = beneficiaries.OrderBy(item => item.Beneficiary, StringComparer.OrdinalIgnoreCase).ToList();
        byte[] poolInitializerData = encoder.GetABIParamsEncoded(new PoolDataWrapper
        {
            Data = new PoolData
            {
                Fee = curve.Fee,
                TickSpacing = curve.TickSpacing,
                FarTick = curve.FarTick,
                Curves =
                [
                    new CurveData { TickLower = curve.FirstLower, TickUpper = curve.FirstUpper, NumPositions = 1, Shares = BigInteger.Parse("991000000000000000") },
                    new CurveData { TickLower = curve.SecondLower, TickUpper = curve.SecondUpper, NumPositions = 1, Shares = BigInteger.Parse("9000000000000000") }
                ],
                Beneficiaries = beneficiaries,
                DopplerHook = DopplerHookAddress,
                InitializationHookData = hookData
            }
        });

        byte[] salt = new byte[32];
        RandomNumberGenerator.Fill(salt.AsSpan(16));
        return new CreateFunction
        {
            Data = new CreateData
            {
                InitialSupply = BigInteger.Pow(10, 27),
                NumTokensToSell = BigInteger.Pow(10, 27),
                Numeraire = numeraire,
                TokenFactory = TokenFactoryAddress,
                TokenFactoryData = tokenFactoryData,
                GovernanceFactory = GovernanceFactoryAddress,
                PoolInitializer = PoolInitializerAddress,
                PoolInitializerData = poolInitializerData,
                LiquidityMigrator = LiquidityMigratorAddress,
                Integrator = IntegratorAddress,
                Salt = salt
            }
        };
    }

    private async Task<LongCurve> GetCurrentCurveAsync(Web3 web3, string numeraire, CancellationToken cancellationToken)
    {
        HexBigInteger latest = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().WaitAsync(cancellationToken);
        string numeraireTopic = "0x" + new string('0', 24) + numeraire[2..].ToLowerInvariant();

        for (BigInteger to = latest.Value; to >= 0 && to > latest.Value - 100000; to -= 10000)
        {
            BigInteger from = BigInteger.Max(0, to - 9999);
            NewFilterInput filter = new NewFilterInput
            {
                Address = [LauncherAddress],
                FromBlock = new BlockParameter(new HexBigInteger(from)),
                ToBlock = new BlockParameter(new HexBigInteger(to)),
                Topics = [LaunchCreatedTopic, null!, null!, numeraireTopic]
            };
            FilterLog[] logs = await web3.Eth.Filters.GetLogs.SendRequestAsync(filter).WaitAsync(cancellationToken);
            if (logs.Length == 0)
            {
                continue;
            }

            Transaction transaction = await web3.Eth.Transactions.GetTransactionByHash
                .SendRequestAsync(logs[^1].TransactionHash).WaitAsync(cancellationToken);
            return ReadCurve(transaction.Input);
        }

        throw new InvalidOperationException("Long has no recent launch data for " + numeraire + ".");
    }

    private static LongCurve ReadCurve(string input)
    {
        string data = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? input[10..] : input[8..];
        List<string> words = new List<string>();
        for (int index = 0; index + 64 <= data.Length; index += 64)
        {
            words.Add(data.Substring(index, 64));
        }

        int poolOffset = ReadInt(words[9]);
        int poolLengthWord = 1 + poolOffset / 32;
        int tupleStart = poolLengthWord + 2;
        int curveArray = tupleStart + ReadInt(words[tupleStart + 3]) / 32;
        int beneficiaryArray = tupleStart + ReadInt(words[tupleStart + 4]) / 32;
        string firstBeneficiary = "0x" + words[beneficiaryArray + 1][^40..];
        BigInteger firstShares = ReadHexBigInteger(words[beneficiaryArray + 2]);
        string secondBeneficiary = "0x" + words[beneficiaryArray + 3][^40..];
        string protocolOwner = firstShares == BigInteger.Parse("50000000000000000")
            ? firstBeneficiary
            : secondBeneficiary;
        return new LongCurve(ReadInt(words[tupleStart]), ReadSigned24(words[tupleStart + 1]),
            ReadSigned24(words[tupleStart + 2]), ReadSigned24(words[curveArray + 1]),
            ReadSigned24(words[curveArray + 2]), ReadSigned24(words[curveArray + 5]),
            ReadSigned24(words[curveArray + 6]), protocolOwner);
    }

    private async Task<string> UploadMetadataAsync(string walletAddress, LongRobinhoodTokenRequest request, string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PinataJwt))
        {
            throw new InvalidOperationException("LongRobinhood:PinataJwt is required for a real token.");
        }

        string imageCid = await UploadImageAsync(request.Image, cancellationToken);
        object metadata = new
        {
            pinataContent = new
            {
                name = Clean(request.Name, 64),
                description = Clean(request.Description, 500),
                image_hash = "ipfs://" + imageCid,
                social_links = new[] { new { platform = "twitter", url = request.PostUrl } },
                vesting_recipients = new[] { new { address = ZeroAddress, amount = 0 } },
                fee_receiver = walletAddress,
                categories = Array.Empty<string>()
            },
            pinataMetadata = new { name = symbol + ".json" }
        };

        using HttpRequestMessage pinRequest = new HttpRequestMessage(HttpMethod.Post, "pinning/pinJSONToIPFS");
        pinRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.PinataJwt);
        pinRequest.Content = JsonContent.Create(metadata);
        using HttpResponseMessage response = await httpClient.SendAsync(pinRequest, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Pinata metadata upload failed: HTTP " + (int)response.StatusCode + " " + Shorten(json), null, response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string cid = document.RootElement.GetProperty("IpfsHash").GetString()
            ?? throw new JsonException("Pinata returned an empty metadata hash.");
        return "ipfs://" + cid;
    }

    private async Task<string> UploadImageAsync(byte[] image, CancellationToken cancellationToken)
    {
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
            throw new HttpRequestException("Pinata image upload failed: HTTP " + (int)response.StatusCode + " " + Shorten(json), null, response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("IpfsHash").GetString()
            ?? throw new JsonException("Pinata returned an empty image hash.");
    }

    private static int ReadInt(string word)
    {
        return Convert.ToInt32(word[^8..], 16);
    }

    private static BigInteger AddGasBuffer(BigInteger gasPrice)
    {
        return gasPrice * 120 / 100;
    }

    private static int ReadSigned24(string word)
    {
        int value = Convert.ToInt32(word[^6..], 16);
        return value >= 0x800000 ? value - 0x1000000 : value;
    }

    private static BigInteger ReadHexBigInteger(string word)
    {
        return BigInteger.Parse("0" + word, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string CleanSymbol(string value)
    {
        string symbol = value.Trim().ToUpperInvariant();
        if (symbol.Length == 0 || symbol.Length > 15 || symbol.Any(character => character < 'A' || character > 'Z'))
        {
            throw new InvalidOperationException("Long ticker must contain 1-15 English letters.");
        }

        return symbol;
    }

    private static string Clean(string value, int maximumLength)
    {
        string clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("Long token metadata cannot be empty.");
        }

        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    [Function("getPair", "address")]
    private sealed class GetPairFunction : FunctionMessage
    {
        [Parameter("address", "tokenA", 1)] public string TokenA { get; set; } = string.Empty;
        [Parameter("address", "tokenB", 2)] public string TokenB { get; set; } = string.Empty;
    }

    [Function("getReserves", typeof(GetReservesOutput))]
    private sealed class GetReservesFunction : FunctionMessage
    {
    }

    [FunctionOutput]
    private sealed class GetReservesOutput : IFunctionOutputDTO
    {
        [Parameter("uint112", "reserve0", 1)] public BigInteger Reserve0 { get; set; }
        [Parameter("uint112", "reserve1", 2)] public BigInteger Reserve1 { get; set; }
        [Parameter("uint32", "blockTimestampLast", 3)] public uint BlockTimestampLast { get; set; }
    }

    [Function("token0", "address")]
    private sealed class Token0Function : FunctionMessage
    {
    }

    [Function("execute")]
    private sealed class ExecuteFunction : FunctionMessage
    {
        [Parameter("bytes", "commands", 1)] public byte[] Commands { get; set; } = [];
        [Parameter("bytes[]", "inputs", 2)] public List<byte[]> Inputs { get; set; } = [];
    }

    [Struct("PoolKey")]
    private sealed class PoolKeyData
    {
        [Parameter("address", "currency0", 1)] public string Currency0 { get; set; } = string.Empty;
        [Parameter("address", "currency1", 2)] public string Currency1 { get; set; } = string.Empty;
        [Parameter("uint24", "fee", 3)] public int Fee { get; set; }
        [Parameter("int24", "tickSpacing", 4)] public int TickSpacing { get; set; }
        [Parameter("address", "hooks", 5)] public string Hooks { get; set; } = string.Empty;
    }

    private sealed class WrapEthData
    {
        [Parameter("address", "recipient", 1)] public string Recipient { get; set; } = string.Empty;
        [Parameter("uint256", "amount", 2)] public BigInteger Amount { get; set; }
    }

    private sealed class V2SwapExactInputData
    {
        [Parameter("address", "recipient", 1)] public string Recipient { get; set; } = string.Empty;
        [Parameter("uint256", "amountIn", 2)] public BigInteger AmountIn { get; set; }
        [Parameter("uint256", "amountOutMinimum", 3)] public BigInteger AmountOutMinimum { get; set; }
        [Parameter("address[]", "path", 4)] public List<string> Path { get; set; } = [];
        [Parameter("bool", "payerIsUser", 5)] public bool PayerIsUser { get; set; }
        [Parameter("uint256[]", "minHopPriceX36", 6)] public List<BigInteger> MinimumHopPrices { get; set; } = [];
    }

    private sealed class ExactInputSingleWrapper
    {
        [Parameter("tuple", "swapParams", 1)] public ExactInputSingleData Params { get; set; } = new ExactInputSingleData();
    }

    [Struct("ExactInputSingleParams")]
    private sealed class ExactInputSingleData
    {
        [Parameter("tuple", "poolKey", 1)] public PoolKeyData PoolKey { get; set; } = new PoolKeyData();
        [Parameter("bool", "zeroForOne", 2)] public bool ZeroForOne { get; set; }
        [Parameter("uint128", "amountIn", 3)] public BigInteger AmountIn { get; set; }
        [Parameter("uint128", "amountOutMinimum", 4)] public BigInteger AmountOutMinimum { get; set; }
        [Parameter("uint256", "minHopPriceX36", 5)] public BigInteger MinimumHopPrice { get; set; }
        [Parameter("bytes", "hookData", 6)] public byte[] HookData { get; set; } = [];
    }

    private sealed class SettleData
    {
        [Parameter("address", "currency", 1)] public string Currency { get; set; } = string.Empty;
        [Parameter("uint256", "amount", 2)] public BigInteger Amount { get; set; }
        [Parameter("bool", "payerIsUser", 3)] public bool PayerIsUser { get; set; }
    }

    private sealed class TakeAllData
    {
        [Parameter("address", "currency", 1)] public string Currency { get; set; } = string.Empty;
        [Parameter("uint256", "minimumAmount", 2)] public BigInteger MinimumAmount { get; set; }
    }

    private sealed class V4SwapData
    {
        [Parameter("bytes", "actions", 1)] public byte[] Actions { get; set; } = [];
        [Parameter("bytes[]", "params", 2)] public List<byte[]> Parameters { get; set; } = [];
    }

    [Function("paused", "bool")]
    private sealed class PausedFunction : FunctionMessage
    {
    }

    [Function("isTickerAvailable", "bool")]
    private sealed class IsTickerAvailableFunction : FunctionMessage
    {
        [Parameter("string", "ticker", 1)] public string Ticker { get; set; } = string.Empty;
    }

    [Function("create")]
    private sealed class CreateFunction : FunctionMessage
    {
        [Parameter("tuple", "data", 1)] public CreateData Data { get; set; } = new CreateData();
    }

    [Struct("CreateParams")]
    private sealed class CreateData
    {
        [Parameter("uint256", "initialSupply", 1)] public BigInteger InitialSupply { get; set; }
        [Parameter("uint256", "numTokensToSell", 2)] public BigInteger NumTokensToSell { get; set; }
        [Parameter("address", "numeraire", 3)] public string Numeraire { get; set; } = string.Empty;
        [Parameter("address", "tokenFactory", 4)] public string TokenFactory { get; set; } = string.Empty;
        [Parameter("bytes", "tokenFactoryData", 5)] public byte[] TokenFactoryData { get; set; } = [];
        [Parameter("address", "governanceFactory", 6)] public string GovernanceFactory { get; set; } = string.Empty;
        [Parameter("bytes", "governanceFactoryData", 7)] public byte[] GovernanceFactoryData { get; set; } = [];
        [Parameter("address", "poolInitializer", 8)] public string PoolInitializer { get; set; } = string.Empty;
        [Parameter("bytes", "poolInitializerData", 9)] public byte[] PoolInitializerData { get; set; } = [];
        [Parameter("address", "liquidityMigrator", 10)] public string LiquidityMigrator { get; set; } = string.Empty;
        [Parameter("bytes", "liquidityMigratorData", 11)] public byte[] LiquidityMigratorData { get; set; } = [];
        [Parameter("address", "integrator", 12)] public string Integrator { get; set; } = string.Empty;
        [Parameter("bytes32", "salt", 13)] public byte[] Salt { get; set; } = [];
    }

    private sealed class TokenFactoryData
    {
        [Parameter("string", "name", 1)] public string Name { get; set; } = string.Empty;
        [Parameter("string", "symbol", 2)] public string Symbol { get; set; } = string.Empty;
        [Parameter("tuple[]", "schedules", 3)] public List<VestingScheduleData> Schedules { get; set; } = [];
        [Parameter("address[]", "beneficiaries", 4)] public List<string> Beneficiaries { get; set; } = [];
        [Parameter("uint256[]", "scheduleIds", 5)] public List<BigInteger> ScheduleIds { get; set; } = [];
        [Parameter("uint256[]", "amounts", 6)] public List<BigInteger> Amounts { get; set; } = [];
        [Parameter("string", "tokenURI", 7)] public string TokenUri { get; set; } = string.Empty;
        [Parameter("uint256", "maxBalanceLimit", 8)] public BigInteger MaxBalanceLimit { get; set; }
        [Parameter("uint48", "balanceLimitEnd", 9)] public BigInteger BalanceLimitEnd { get; set; }
        [Parameter("address", "controller", 10)] public string Controller { get; set; } = ZeroAddress;
        [Parameter("address[]", "excludedFromBalanceLimit", 11)] public List<string> ExcludedFromBalanceLimit { get; set; } = [];
    }

    [Struct("VestingSchedule")]
    private sealed class VestingScheduleData
    {
        [Parameter("uint64", "cliff", 1)] public ulong Cliff { get; set; }
        [Parameter("uint64", "duration", 2)] public ulong Duration { get; set; }
    }

    private sealed class PoolDataWrapper
    {
        [Parameter("tuple", "data", 1)] public PoolData Data { get; set; } = new PoolData();
    }

    [Struct("PoolData")]
    private sealed class PoolData
    {
        [Parameter("uint24", "fee", 1)] public int Fee { get; set; }
        [Parameter("int24", "tickSpacing", 2)] public int TickSpacing { get; set; }
        [Parameter("int24", "farTick", 3)] public int FarTick { get; set; }
        [Parameter("tuple[]", "curves", 4)] public List<CurveData> Curves { get; set; } = [];
        [Parameter("tuple[]", "beneficiaries", 5)] public List<PoolBeneficiaryData> Beneficiaries { get; set; } = [];
        [Parameter("address", "dopplerHook", 6)] public string DopplerHook { get; set; } = string.Empty;
        [Parameter("bytes", "onInitializationDopplerHookCalldata", 7)] public byte[] InitializationHookData { get; set; } = [];
        [Parameter("bytes", "graduationDopplerHookCalldata", 8)] public byte[] GraduationHookData { get; set; } = [];
    }

    [Struct("Curve")]
    private sealed class CurveData
    {
        [Parameter("int24", "tickLower", 1)] public int TickLower { get; set; }
        [Parameter("int24", "tickUpper", 2)] public int TickUpper { get; set; }
        [Parameter("uint16", "numPositions", 3)] public int NumPositions { get; set; }
        [Parameter("uint256", "shares", 4)] public BigInteger Shares { get; set; }
    }

    [Struct("Beneficiary")]
    private sealed class PoolBeneficiaryData
    {
        [Parameter("address", "beneficiary", 1)] public string Beneficiary { get; set; } = string.Empty;
        [Parameter("uint96", "shares", 2)] public BigInteger Shares { get; set; }
    }

    private sealed class HookDataWrapper
    {
        [Parameter("tuple", "data", 1)] public HookData Data { get; set; } = new HookData();
    }

    [Struct("HookData")]
    private sealed class HookData
    {
        [Parameter("address", "numeraire", 1)] public string Numeraire { get; set; } = string.Empty;
        [Parameter("address", "buybackDst", 2)] public string BuybackDestination { get; set; } = string.Empty;
        [Parameter("uint24", "startFee", 3)] public int StartFee { get; set; }
        [Parameter("uint24", "endFee", 4)] public int EndFee { get; set; }
        [Parameter("uint32", "durationSeconds", 5)] public uint DurationSeconds { get; set; }
        [Parameter("uint32", "startingTime", 6)] public uint StartingTime { get; set; }
        [Parameter("uint8", "feeRoutingMode", 7)] public byte FeeRoutingMode { get; set; }
        [Parameter("tuple", "feeDistributionInfo", 8)] public FeeDistributionData FeeDistribution { get; set; } = new FeeDistributionData();
    }

    [Struct("FeeDistributionInfo")]
    private sealed class FeeDistributionData
    {
        [Parameter("uint256", "assetFeesToAssetBuybackWad", 1)] public BigInteger AssetFeesToAssetBuyback { get; set; }
        [Parameter("uint256", "assetFeesToNumeraireBuybackWad", 2)] public BigInteger AssetFeesToNumeraireBuyback { get; set; }
        [Parameter("uint256", "assetFeesToBeneficiaryWad", 3)] public BigInteger AssetFeesToBeneficiary { get; set; }
        [Parameter("uint256", "assetFeesToLpWad", 4)] public BigInteger AssetFeesToLp { get; set; }
        [Parameter("uint256", "numeraireFeesToAssetBuybackWad", 5)] public BigInteger NumeraireFeesToAssetBuyback { get; set; }
        [Parameter("uint256", "numeraireFeesToNumeraireBuybackWad", 6)] public BigInteger NumeraireFeesToNumeraireBuyback { get; set; }
        [Parameter("uint256", "numeraireFeesToBeneficiaryWad", 7)] public BigInteger NumeraireFeesToBeneficiary { get; set; }
        [Parameter("uint256", "numeraireFeesToLpWad", 8)] public BigInteger NumeraireFeesToLp { get; set; }
    }

    [Event("LaunchCreated")]
    private sealed class LaunchCreatedEventDto : IEventDTO
    {
        [Parameter("address", "poolOrHook", 1, true)] public string PoolOrHook { get; set; } = string.Empty;
        [Parameter("address", "asset", 2, true)] public string Asset { get; set; } = string.Empty;
        [Parameter("address", "numeraire", 3, true)] public string Numeraire { get; set; } = string.Empty;
        [Parameter("address", "poolInitializer", 4, false)] public string PoolInitializer { get; set; } = string.Empty;
        [Parameter("address", "launcher", 5, false)] public string Launcher { get; set; } = string.Empty;
        [Parameter("bytes32", "tickerKey", 6, false)] public byte[] TickerKey { get; set; } = [];
        [Parameter("uint48", "deployedAt", 7, false)] public BigInteger DeployedAt { get; set; }
        [Parameter("uint48", "reservedUntil", 8, false)] public BigInteger ReservedUntil { get; set; }
        [Parameter("string", "normalizedTicker", 9, false)] public string NormalizedTicker { get; set; } = string.Empty;
    }

    private sealed record LongCurve(int Fee, int TickSpacing, int FarTick, int FirstLower, int FirstUpper, int SecondLower, int SecondUpper, string ProtocolOwner);
}

public sealed record LongRobinhoodTokenRequest(string Name, string Symbol, string Description, byte[] Image,
    string PostUrl, string Anchor, decimal BuyAmount, decimal SlippagePercent);

public sealed record LongRobinhoodTokenResult(string? TransactionHash, string? TokenAddress, BigInteger? EstimatedGas, bool IsDryRun, bool HasEnoughBalance);

public sealed record LongRobinhoodConnectionResult(bool CorrectChain, bool LauncherFound, bool Paused, decimal? Balance, bool EnableRealTransactions);
