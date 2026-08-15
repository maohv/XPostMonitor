using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using XPostMonitor.Configuration;

namespace XPostMonitor.Services.Gmgn;

// Chạy gmgn-cli để đọc dữ liệu thị trường và chuẩn bị cho chức năng trading sau này.
public sealed class GmgnClient
{
    private readonly GmgnOptions options;

    public GmgnClient(GmgnOptions options)
    {
        this.options = options;
    }

    // Kiểm tra API key chung của admin trong appsettings.Development.json.
    public async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "GMGN API key is missing from configuration.";
        }

        try
        {
            string output = await RunAsync(
                ["market", "trending", "--chain", "bsc", "--interval", "1h", "--limit", "1", "--raw"],
                options.ApiKey, null, false, cancellationToken);
            return ReadConnectionResult(output);
        }
        catch (Exception exception)
        {
            return "GMGN connection failed: " + exception.Message;
        }
    }

    // Kiểm tra API key của Premium user và trả về các network đã có ví.
    public async Task<GmgnConnectionResult> CheckUserConnectionAsync(string apiKey, string privateKey,
        CancellationToken cancellationToken)
    {
        try
        {
            List<GmgnWallet> wallets = await GetWalletsAsync(apiKey, cancellationToken);
            if (wallets.Count == 0)
            {
                return new GmgnConnectionResult(false, "No linked wallet");
            }

            GmgnWallet wallet = FindWalletForConnectionCheck(wallets);
            await ValidateCredentialsAsync(apiKey, privateKey, wallet, cancellationToken);
            string chains = string.Join(", ", wallets.Select(item => item.Chain).Distinct().OrderBy(item => item));
            return new GmgnConnectionResult(true, chains);
        }
        catch (Exception exception)
        {
            return new GmgnConnectionResult(false, exception.Message);
        }
    }

    // Tạo cặp khóa Ed25519 ngay trên server. Chỉ public key được gửi cho user.
    public async Task<GmgnSigningKeyPair> GenerateSigningKeyAsync(CancellationToken cancellationToken)
    {
        const string script = """
            const crypto = require('node:crypto');
            const keys = crypto.generateKeyPairSync('ed25519', {
              publicKeyEncoding: { type: 'spki', format: 'pem' },
              privateKeyEncoding: { type: 'pkcs8', format: 'pem' }
            });
            const test = Buffer.from('gmgn-key-check');
            const signature = crypto.sign(null, test, keys.privateKey);
            if (!crypto.verify(null, test, keys.publicKey, signature)) throw new Error('Key check failed');
            process.stdout.write(JSON.stringify(keys));
            """;

        ProcessStartInfo startInfo = CreateNodeStartInfo();
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        string output = await RunProcessAsync(startInfo, TimeSpan.FromSeconds(10), cancellationToken);

        GmgnSigningKeyPair? keys = JsonSerializer.Deserialize<GmgnSigningKeyPair>(output,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return keys is { PublicKey.Length: > 0, PrivateKey.Length: > 0 }
            ? keys
            : throw new JsonException("Node.js did not return an Ed25519 key pair.");
    }

    // Kiểm tra private signing key có sẵn và lấy lại public key tương ứng.
    // Bot chỉ chấp nhận Ed25519 PKCS#8 PEM vì đây là định dạng GMGN CLI sử dụng.
    public async Task<GmgnSigningKeyPair> ImportSigningKeyAsync(string privateKey,
        CancellationToken cancellationToken)
    {
        const string script = """
            const crypto = require('node:crypto');
            const input = process.argv[1];
            const key = crypto.createPrivateKey(input);
            if (key.asymmetricKeyType !== 'ed25519') throw new Error('Signing key must be Ed25519');
            const normalizedPrivateKey = key.export({ type: 'pkcs8', format: 'pem' });
            const publicKey = crypto.createPublicKey(key).export({ type: 'spki', format: 'pem' });
            const test = Buffer.from('gmgn-key-check');
            const signature = crypto.sign(null, test, key);
            if (!crypto.verify(null, test, publicKey, signature)) throw new Error('Key check failed');
            process.stdout.write(JSON.stringify({ publicKey, privateKey: normalizedPrivateKey }));
            """;

        ProcessStartInfo startInfo = CreateNodeStartInfo();
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--"); // Không để Node hiểu dòng -----BEGIN... là một command option.
        startInfo.ArgumentList.Add(privateKey.Replace("\\n", "\n").Trim());
        string output = await RunProcessAsync(startInfo, TimeSpan.FromSeconds(10), cancellationToken);
        GmgnSigningKeyPair? keys = JsonSerializer.Deserialize<GmgnSigningKeyPair>(output,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return keys is { PublicKey.Length: > 0, PrivateKey.Length: > 0 }
            ? keys
            : throw new JsonException("Node.js could not read the Ed25519 signing key.");
    }

    // Xác nhận API key đã được ghép đúng public key. Lệnh này chỉ đọc số dư token.
    public async Task ValidateCredentialsAsync(string apiKey, string privateKey,
        CancellationToken cancellationToken)
    {
        List<GmgnWallet> wallets = await GetWalletsAsync(apiKey, cancellationToken);
        if (wallets.Count == 0)
        {
            throw new InvalidOperationException("No wallet is linked to this GMGN API key.");
        }

        await ValidateCredentialsAsync(apiKey, privateKey, FindWalletForConnectionCheck(wallets), cancellationToken);
    }

    // Tìm ví đã được liên kết với API key trên một network.
    public async Task<string> GetWalletAddressAsync(string apiKey, string chain, CancellationToken cancellationToken)
    {
        List<GmgnWallet> wallets = await GetWalletsAsync(apiKey, cancellationToken);
        GmgnWallet? wallet = wallets.FirstOrDefault(item =>
            string.Equals(item.Chain, chain, StringComparison.OrdinalIgnoreCase));

        return wallet?.Address
            ?? throw new InvalidOperationException("No " + chain + " wallet is linked to this GMGN API key.");
    }

    // Đợi GMGN ghi nhận giao dịch mua ban đầu và pool của token vừa tạo.
    public async Task<GmgnTokenPosition> GetTokenPositionAsync(GmgnCredentials credentials, string chain,
        string walletAddress, string tokenAddress, CancellationToken cancellationToken)
    {
        string linkedWallet = await GetWalletAddressAsync(credentials.ApiKey, chain, cancellationToken);
        if (!string.Equals(linkedWallet, walletAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The GMGN wallet does not match the token launch wallet.");
        }

        for (int attempt = 1; attempt <= 6; attempt++)
        {
            string activityJson = await RunAsync(
                ["portfolio", "activity", "--chain", chain, "--wallet", walletAddress,
                 "--token", tokenAddress, "--type", "buy", "--limit", "20", "--raw"],
                credentials.ApiKey, null, false, cancellationToken);
            string poolJson = await RunAsync(
                ["token", "pool", "--chain", chain, "--address", tokenAddress, "--raw"],
                credentials.ApiKey, null, false, cancellationToken);

            decimal entryPrice = ReadLatestBuyPrice(activityJson, tokenAddress);
            string quoteToken = ReadQuoteToken(poolJson);
            if (entryPrice > 0 && !string.IsNullOrWhiteSpace(quoteToken))
            {
                return new GmgnTokenPosition(linkedWallet, quoteToken, entryPrice);
            }

            if (attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        throw new InvalidOperationException("GMGN has not indexed the initial token purchase yet.");
    }

    // Dùng cho launchpad đã biết sẵn token ghép cặp, ví dụ Pons luôn dùng WETH.
    // Cách này không bắt Pons phải chờ GMGN index thêm thông tin pool.
    public async Task<GmgnTokenPosition> GetTokenPositionWithKnownQuoteAsync(GmgnCredentials credentials,
        string chain, string walletAddress, string tokenAddress, string quoteTokenAddress,
        CancellationToken cancellationToken)
    {
        string linkedWallet = await GetWalletAddressAsync(credentials.ApiKey, chain, cancellationToken);
        if (!string.Equals(linkedWallet, walletAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The GMGN wallet does not match the token launch wallet.");
        }

        for (int attempt = 1; attempt <= 12; attempt++)
        {
            string activityJson = await RunAsync(
                ["portfolio", "activity", "--chain", chain, "--wallet", walletAddress,
                 "--token", tokenAddress, "--type", "buy", "--limit", "20", "--raw"],
                credentials.ApiKey, null, false, cancellationToken);
            decimal entryPrice = ReadLatestBuyPrice(activityJson, tokenAddress);
            await AutoTradingDiagnosticLog.WriteAsync("PONS PRICE CHECK | Attempt=" + attempt
                + " | Token=" + tokenAddress + " | Entry="
                + entryPrice.ToString(CultureInfo.InvariantCulture));
            if (entryPrice > 0)
            {
                return new GmgnTokenPosition(linkedWallet, quoteTokenAddress, entryPrice);
            }

            if (attempt < 12)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        throw new InvalidOperationException("GMGN has not indexed the Pons initial purchase price yet.");
    }

    public async Task<string> CreateTakeProfitAsync(GmgnCredentials credentials, string chain,
        string walletAddress, string tokenAddress, string quoteTokenAddress, decimal targetPrice,
        BigInteger amountIn, decimal slippagePercent, decimal? gasPriceGwei,
        CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "order", "strategy", "create",
            "--chain", chain,
            "--from", walletAddress,
            "--base-token", tokenAddress,
            "--quote-token", quoteTokenAddress,
            "--order-type", "limit_order",
            "--sub-order-type", "take_profit",
            "--check-price", targetPrice.ToString("G29", CultureInfo.InvariantCulture),
            "--amount-in", amountIn.ToString(CultureInfo.InvariantCulture),
            "--slippage", slippagePercent.ToString(CultureInfo.InvariantCulture),
            "--yes", "--raw"
        ];
        if (gasPriceGwei.HasValue)
        {
            arguments.Add("--gas-price");
            arguments.Add(gasPriceGwei.Value.ToString("G29", CultureInfo.InvariantCulture));
        }

        string output = await RunAsync(arguments, credentials.ApiKey, credentials.PrivateKey, true,
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        return ReadString(document.RootElement, "order_id", string.Empty) is { Length: > 0 } orderId
            ? orderId
            : throw new JsonException("GMGN did not return a strategy order ID.");
    }

    public async Task<decimal> GetAverageGasPriceGweiAsync(GmgnCredentials credentials, string chain,
        CancellationToken cancellationToken)
    {
        string output = await RunAsync(["gas-price", "--chain", chain, "--raw"], credentials.ApiKey,
            null, false, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        decimal gasPriceWei = ReadDecimal(document.RootElement, "average");
        if (gasPriceWei <= 0)
        {
            gasPriceWei = ReadDecimal(document.RootElement, "auto");
        }
        return gasPriceWei > 0
            ? gasPriceWei / 1_000_000_000m
            : throw new JsonException("GMGN did not return a BSC gas price.");
    }

    public async Task<List<GmgnStrategyOrder>> GetTakeProfitOrdersAsync(GmgnCredentials credentials,
        string chain, string walletAddress, string tokenAddress, CancellationToken cancellationToken)
    {
        List<GmgnStrategyOrder> result = [];
        foreach (string type in new[] { "open", "history" })
        {
            string output = await RunAsync(
                ["order", "strategy", "list", "--chain", chain, "--type", type,
                 "--from", walletAddress, "--group-tag", "LimitOrder", "--base-token", tokenAddress,
                 "--limit", "50", "--raw"], credentials.ApiKey, credentials.PrivateKey, false,
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("list", out JsonElement orders)
                || orders.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement order in orders.EnumerateArray())
            {
                JsonElement statistic = order.TryGetProperty("order_statistic", out JsonElement value)
                    ? value : default;
                int successfulSells = ReadInt32(statistic, "success_sell_num");
                string transactionHash = ReadString(order, "close_sign_hash", string.Empty);
                string status = ReadString(order, "status", type == "open" ? "open" : "closed");
                bool filled = status == "closed" && (successfulSells > 0 || transactionHash.Length > 0);
                result.Add(new GmgnStrategyOrder(
                    ReadString(order, "order_id", string.Empty),
                    status == "closed" ? filled ? "filled" : "failed" : "open",
                    transactionHash,
                    ReadDecimal(statistic, "usdt_profit")));
            }
        }

        return result.Where(item => item.OrderId.Length > 0)
            .GroupBy(item => item.OrderId).Select(group => group.Last()).ToList();
    }

    public async Task<string> GetQuoteTokenAsync(GmgnCredentials credentials, string chain,
        string tokenAddress, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 15; attempt++)
        {
            string output = await RunAsync(
                ["token", "pool", "--chain", chain, "--address", tokenAddress, "--raw"],
                credentials.ApiKey, null, false, cancellationToken);
            string quoteToken = ReadQuoteToken(output);
            if (!string.IsNullOrWhiteSpace(quoteToken))
            {
                return quoteToken;
            }
            if (attempt < 15)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException("GMGN has not indexed the token pool yet.");
    }

    public async Task<string> SellAllAsync(GmgnCredentials credentials, string chain,
        string walletAddress, string tokenAddress, string quoteTokenAddress, BigInteger? exactAmountIn,
        decimal slippagePercent, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "swap",
            "--chain", chain,
            "--from", walletAddress,
            "--input-token", tokenAddress,
            "--output-token", quoteTokenAddress,
            "--slippage", slippagePercent.ToString("G29", CultureInfo.InvariantCulture),
            "--yes", "--raw"
        ];
        if (exactAmountIn > 0)
        {
            arguments.Add("--amount");
            arguments.Add(exactAmountIn.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            arguments.Add("--percent");
            arguments.Add("100");
        }
        if (chain == "bsc")
        {
            decimal gasPrice = await GetAverageGasPriceGweiAsync(credentials, chain, cancellationToken);
            arguments.Add("--gas-price");
            arguments.Add(gasPrice.ToString("G29", CultureInfo.InvariantCulture));
        }

        string output = await RunAsync(arguments, credentials.ApiKey, credentials.PrivateKey, true,
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement result = document.RootElement.TryGetProperty("data", out JsonElement data)
            ? data : document.RootElement;
        string orderId = ReadString(result, "order_id", string.Empty);
        string transactionHash = ReadString(result, "hash", string.Empty);
        string status = ReadString(result, "status", string.Empty);
        if (status == "confirmed")
        {
            return transactionHash.Length > 0 ? transactionHash : orderId;
        }
        if (status is "failed" or "expired")
        {
            throw new InvalidOperationException("GMGN sell order " + status + ".");
        }
        if (orderId.Length == 0)
        {
            throw new JsonException("GMGN did not return a sell order ID.");
        }

        return await WaitForSellConfirmationAsync(credentials, chain, orderId, transactionHash,
            cancellationToken);
    }

    // Đọc giá USD hiện tại, dùng chung dù pool ghép với BNB, BTCB, stablecoin hay RWA.
    public async Task<decimal> GetTokenPoolPriceUsdAsync(string apiKey, string chain,
        string tokenAddress, CancellationToken cancellationToken)
    {
        string output = await RunAsync(
            ["token", "info", "--chain", chain, "--address", tokenAddress, "--raw"],
            apiKey, null, false, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        JsonElement priceData = root.TryGetProperty("price", out JsonElement value)
            && value.ValueKind == JsonValueKind.Object ? value : root;
        decimal price = ReadDecimal(priceData, "price");
        if (price <= 0)
        {
            price = ReadDecimal(priceData, "price_usd");
        }
        return price > 0
            ? price
            : throw new InvalidOperationException("GMGN did not return the current token price.");
    }

    private async Task<string> WaitForSellConfirmationAsync(GmgnCredentials credentials, string chain,
        string orderId, string transactionHash, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }

            string output = await RunAsync(
                ["order", "get", "--chain", chain, "--order-id", orderId, "--raw"],
                credentials.ApiKey, credentials.PrivateKey, false, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement result = document.RootElement.TryGetProperty("data", out JsonElement data)
                ? data : document.RootElement;
            string status = ReadString(result, "status", string.Empty);
            string confirmedHash = ReadString(result, "hash", transactionHash);
            if (status == "confirmed")
            {
                return confirmedHash.Length > 0 ? confirmedHash : orderId;
            }
            if (status is "failed" or "expired")
            {
                string error = ReadString(result, "error_status", "Unknown GMGN error");
                throw new InvalidOperationException("GMGN sell order " + status + ": " + error);
            }
        }

        throw new TimeoutException("GMGN sell order was not confirmed after 10 seconds. Order: " + orderId);
    }

    public async Task CancelTakeProfitAsync(GmgnCredentials credentials, string chain,
        string walletAddress, string orderId, CancellationToken cancellationToken)
    {
        await RunAsync(
            ["order", "strategy", "cancel", "--chain", chain, "--from", walletAddress,
             "--order-id", orderId, "--order-type", "limit_order", "--raw"],
            credentials.ApiKey, credentials.PrivateKey, true, cancellationToken);
    }

    private async Task<List<GmgnWallet>> GetWalletsAsync(string apiKey, CancellationToken cancellationToken)
    {
        string output = await RunAsync(["portfolio", "info", "--raw"], apiKey, null, false, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("wallets", out JsonElement wallets) || wallets.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("GMGN did not return linked wallets.");
        }

        List<GmgnWallet> result = [];
        foreach (JsonElement wallet in wallets.EnumerateArray())
        {
            string chain = wallet.TryGetProperty("chain", out JsonElement chainValue)
                ? chainValue.GetString() ?? string.Empty : string.Empty;
            string address = wallet.TryGetProperty("address", out JsonElement addressValue)
                ? addressValue.GetString() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(chain) && !string.IsNullOrWhiteSpace(address))
            {
                result.Add(new GmgnWallet(chain.ToLowerInvariant(), address));
            }
        }

        return result;
    }

    private async Task ValidateCredentialsAsync(string apiKey, string privateKey, GmgnWallet wallet,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            ["portfolio", "holdings", "--chain", wallet.Chain, "--wallet", wallet.Address, "--limit", "1", "--raw"],
            apiKey, privateKey, false, cancellationToken);
    }

    private static GmgnWallet FindWalletForConnectionCheck(List<GmgnWallet> wallets)
    {
        return wallets.FirstOrDefault(item => item.Chain is "sol" or "bsc" or "base" or "eth") ?? wallets[0];
    }

    // Chạy process bằng ArgumentList để dữ liệu bài viết không thể biến thành lệnh shell.
    private async Task<string> RunAsync(IReadOnlyList<string> arguments, string apiKey, string? privateKey,
        bool allowAutomatedTrade, CancellationToken cancellationToken)
    {
        string cliScriptPath = FindCliScriptPath();
        if (string.IsNullOrWhiteSpace(cliScriptPath))
        {
            throw new FileNotFoundException("GMGN CLI was not found. Run: npm install -g gmgn-cli");
        }

        ProcessStartInfo startInfo = CreateNodeStartInfo();
        startInfo.ArgumentList.Add(cliScriptPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GMGN_API_KEY"] = apiKey;
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            startInfo.Environment["GMGN_PRIVATE_KEY"] = privateKey;
        }
        if (allowAutomatedTrade)
        {
            startInfo.Environment["GMGN_ALLOW_AUTOMATED_TRADES"] = "1";
        }

        return await RunProcessAsync(startInfo, TimeSpan.FromSeconds(60), cancellationToken);
    }

    private ProcessStartInfo CreateNodeStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(options.NodePath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    // Đọc song song stdout và stderr để process không bị treo khi output dài.
    private static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan maximumTime,
        CancellationToken cancellationToken)
    {
        using Process process = new Process { StartInfo = startInfo };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumTime);

        try
        {
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(Shorten(string.IsNullOrWhiteSpace(error) ? output : error));
            }

            return output.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            throw new TimeoutException("The process timed out after " + maximumTime.TotalSeconds + " seconds.");
        }
    }

    // Ưu tiên đường dẫn cấu hình; nếu đổi máy thì tự tìm CLI trong npm của user hiện tại.
    private string FindCliScriptPath()
    {
        string configuredPath = Environment.ExpandEnvironmentVariables(options.CliScriptPath);
        if (File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string automaticPath = Path.Combine(appData, "npm", "node_modules", "gmgn-cli", "dist", "index.js");
        return File.Exists(automaticPath) ? automaticPath : string.Empty;
    }

    private static string ReadConnectionResult(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("code", out JsonElement code) && code.GetInt32() != 0)
        {
            return "GMGN connection failed: " + ReadString(root, "message", "Unknown error");
        }

        JsonElement rank = root.GetProperty("data").GetProperty("rank");
        return rank.GetArrayLength() == 0
            ? "GMGN connected successfully. No BSC trending token was returned."
            : "GMGN connected successfully.\nChain: BSC";
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            ? value.GetString() ?? fallback : fallback;
    }

    private static decimal ReadLatestBuyPrice(string json, string tokenAddress)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("activities", out JsonElement activities)
            || activities.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return activities.EnumerateArray()
            .Where(item => ReadString(item, "event_type",
                    ReadString(item, "type", string.Empty)) == "buy"
                && (!item.TryGetProperty("token", out JsonElement token)
                    || string.Equals(ReadString(token, "address", tokenAddress), tokenAddress,
                        StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => ReadInt64(item, "timestamp"))
            .Select(ReadActivityUsdPrice)
            .FirstOrDefault(price => price > 0);
    }

    private static decimal ReadActivityUsdPrice(JsonElement activity)
    {
        decimal priceUsd = ReadDecimal(activity, "price_usd");
        if (priceUsd > 0)
        {
            return priceUsd;
        }

        decimal tokenAmount = ReadDecimal(activity, "token_amount");
        decimal costUsd = ReadDecimal(activity, "cost_usd");
        return tokenAmount > 0 ? costUsd / tokenAmount : 0;
    }

    private static string ReadQuoteToken(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement pool = root.TryGetProperty("pool", out JsonElement value) ? value : root;
        return ReadString(pool, "quote_address", string.Empty);
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)
            ? number
            : decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out decimal parsed) ? parsed : 0;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long result)
            ? result : 0;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value)
            && value.TryGetInt32(out int result) ? result : 0;
    }

    private static string Shorten(string text)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "Unknown error" : text.Trim();
        return value.Length <= 2000 ? value : value[..2000];
    }

    private sealed record GmgnWallet(string Chain, string Address);
}

public sealed record GmgnConnectionResult(bool Success, string Message);

public sealed record GmgnSigningKeyPair(string PublicKey, string PrivateKey);

public sealed record GmgnTokenPosition(string WalletAddress, string QuoteTokenAddress, decimal EntryPrice);

public sealed record GmgnStrategyOrder(string OrderId, string Status, string TransactionHash,
    decimal RealizedProfitUsd);
