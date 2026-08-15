using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XPostMonitor.Services.Launchpads.Flap;

// Đọc danh sách Crypto và RWA khi admin bấm nút Update Payment Token Flap.
// Service chỉ cập nhật danh sách lựa chọn, không thay đổi luồng mua, TP hoặc bán token.
public sealed class FlapRwaCatalogService : BackgroundService
{
    private static readonly Regex ScriptRegex = new(
        "<script[^>]+src=\"(?<src>[^\"]+\\.js[^\"]*)\"", RegexOptions.IgnoreCase);
    private static readonly Regex CatalogEntryRegex = new("\\{(?<body>[^{}]+)\\}", RegexOptions.Compiled);
    private static readonly Regex SymbolRegex = new("symbol:\"(?<symbol>(?:\\\\.|[^\"])*)\"",
        RegexOptions.Compiled);
    private static readonly Regex NameRegex = new("name:\"(?<name>(?:\\\\.|[^\"])*)\"",
        RegexOptions.Compiled);
    private static readonly Regex AddressRegex = new("address:\"(?<address>0x[a-fA-F0-9]{40})\"",
        RegexOptions.Compiled);
    private static readonly Regex DecimalsRegex = new("decimals:(?<decimals>\\d+)",
        RegexOptions.Compiled);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly FlapClient flapClient;
    private readonly ILogger<FlapRwaCatalogService> logger;
    private readonly string catalogPath;
    private readonly SemaphoreSlim updateLock = new(1, 1);

    public FlapRwaCatalogService(IHttpClientFactory httpClientFactory, FlapClient flapClient,
        ILogger<FlapRwaCatalogService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.flapClient = flapClient;
        this.logger = logger;
        catalogPath = Path.Combine(AppContext.BaseDirectory, "Assets", "flap-payment-tokens.json");
    }

    // Khi bot khởi động, chỉ nạp lại danh sách admin đã cập nhật trước đó từ JSON.
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return LoadSavedCatalogAsync(stoppingToken);
    }

    // Tải cả quote token crypto và RWA, rồi chỉ lưu token được Portal hỗ trợ mua bằng BNB.
    public async Task<IReadOnlyList<string>> CheckAndUpdateAsync(CancellationToken cancellationToken)
    {
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            List<FlapPaymentToken> discovered = await DownloadActiveTokensAsync(cancellationToken);
            Dictionary<string, FlapPaymentToken> currentTokens = LaunchpadCatalog.FlapBscPaymentTokens
                .ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
            List<FlapPaymentToken> changedTokens = [];

            foreach (IGrouping<string, FlapPaymentToken> group in discovered
                         .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase))
            {
                FlapPaymentToken? supportedToken = null;
                foreach (FlapPaymentToken candidate in group)
                {
                    try
                    {
                        if (await flapClient.SupportsBnbPurchaseAsync(candidate.TokenAddress, cancellationToken))
                        {
                            supportedToken = candidate;
                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception,
                            "[FLAP PAYMENT] Cannot verify {Code} at {Address}.",
                            candidate.Code, candidate.TokenAddress);
                    }
                }

                if (supportedToken != null
                    && (!currentTokens.TryGetValue(supportedToken.Code, out FlapPaymentToken? current)
                        || current != supportedToken))
                {
                    changedTokens.Add(supportedToken);
                }
            }

            if (changedTokens.Count == 0)
            {
                return Array.Empty<string>();
            }

            List<FlapPaymentToken> updated = LaunchpadCatalog.FlapBscPaymentTokens
                .Concat(changedTokens)
                .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            await SaveCatalogAsync(updated, cancellationToken);
            LaunchpadCatalog.ReplaceFlapBscPaymentTokens(updated);

            string[] codes = changedTokens.Select(item => item.Code).ToArray();
            logger.LogInformation("[FLAP PAYMENT] Updated catalog: {Codes}", string.Join(", ", codes));
            return codes;
        }
        finally
        {
            updateLock.Release();
        }
    }

    private async Task LoadSavedCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(catalogPath))
        {
            return;
        }

        await using FileStream stream = File.OpenRead(catalogPath);
        List<FlapPaymentToken>? saved = await JsonSerializer.DeserializeAsync<List<FlapPaymentToken>>(stream,
            cancellationToken: cancellationToken);
        if (saved == null || saved.Count == 0 || saved.Any(item => !IsValidToken(item)))
        {
            logger.LogWarning("[FLAP PAYMENT] Saved catalog is invalid. Using the built-in catalog.");
            return;
        }

        // File cũ có thể chỉ có RWA. Gộp với danh sách mặc định để không làm mất nhóm Crypto.
        List<FlapPaymentToken> combined = LaunchpadCatalog.FlapBscPaymentTokens
            .Concat(saved)
            .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        LaunchpadCatalog.ReplaceFlapBscPaymentTokens(combined);
        logger.LogInformation("[FLAP PAYMENT] Loaded {Count} Crypto/RWA payment tokens.", combined.Count);
    }

    // Trang Flap chứa danh sách trong JavaScript. BTCB thuộc nhóm crypto, không phải nhóm RWA.
    private async Task<List<FlapPaymentToken>> DownloadActiveTokensAsync(CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient("FlapCatalog");
        string html = await client.GetStringAsync("create?lang=en", cancellationToken);
        string[] scripts = ScriptRegex.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups["src"].Value))
            .OrderByDescending(path => path.Contains("main-app", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (string script in scripts)
        {
            string javascript = await client.GetStringAsync(script, cancellationToken);
            int start = javascript.IndexOf("launchPaymentTokenCatalog:[", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            int end = javascript.IndexOf(']', start);
            if (end < 0)
            {
                continue;
            }

            string catalog = javascript[start..end];
            HashSet<string> activePaymentTokenSymbols = CatalogEntryRegex.Matches(catalog)
                .Select(match => match.Groups["body"].Value)
                .Where(body => (body.Contains("category:\"rwa\"", StringComparison.Ordinal)
                        || body.Contains("category:\"crypto\"", StringComparison.Ordinal))
                    && !body.Contains("status:\"coming-soon\"", StringComparison.Ordinal))
                .Select(body => SymbolRegex.Match(body))
                .Where(match => match.Success)
                .Select(match => DecodeJavascriptString(match.Groups["symbol"].Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return ReadTokenDetails(javascript)
                .Where(item => activePaymentTokenSymbols.Contains(item.Code) && IsValidToken(item))
                .GroupBy(item => item.Code + "|" + item.TokenAddress, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        throw new InvalidOperationException("Flap active Crypto/RWA payment-token catalog was not found.");
    }

    private async Task SaveCatalogAsync(List<FlapPaymentToken> tokens, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        string temporaryPath = catalogPath + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, tokens, new JsonSerializerOptions { WriteIndented = true },
                cancellationToken);
        }
        File.Move(temporaryPath, catalogPath, true);
    }

    // Mỗi payment token trong JavaScript có thể chèn thêm các trường ở giữa.
    // Vì vậy ta đọc từng trường trong đoạn của token, không phụ thuộc thứ tự cố định.
    private static IEnumerable<FlapPaymentToken> ReadTokenDetails(string javascript)
    {
        Match[] symbols = SymbolRegex.Matches(javascript).Cast<Match>().ToArray();
        for (int index = 0; index < symbols.Length; index++)
        {
            Match symbol = symbols[index];
            int nextSymbolIndex = index + 1 < symbols.Length ? symbols[index + 1].Index : javascript.Length;
            int length = Math.Min(nextSymbolIndex - symbol.Index, 4_000);
            string tokenBlock = javascript.Substring(symbol.Index, length);
            Match name = NameRegex.Match(tokenBlock);
            Match address = AddressRegex.Match(tokenBlock);
            Match decimals = DecimalsRegex.Match(tokenBlock);
            if (!name.Success || !address.Success || !decimals.Success)
            {
                continue;
            }

            string code = DecodeJavascriptString(symbol.Groups["symbol"].Value);
            string displayName = DecodeJavascriptString(name.Groups["name"].Value);
            string menuName = string.Equals(code, displayName, StringComparison.OrdinalIgnoreCase)
                ? code
                : code + " - " + displayName;
            yield return new FlapPaymentToken(code, menuName,
                address.Groups["address"].Value, int.Parse(decimals.Groups["decimals"].Value));
        }
    }

    private static string DecodeJavascriptString(string value)
    {
        return JsonSerializer.Deserialize<string>("\"" + value + "\"") ?? value;
    }

    private static bool IsValidToken(FlapPaymentToken token)
    {
        return !string.IsNullOrWhiteSpace(token.Code)
            && Regex.IsMatch(token.TokenAddress, "^0x[a-fA-F0-9]{40}$")
            && token.Decimals is >= 0 and <= 36;
    }
}
