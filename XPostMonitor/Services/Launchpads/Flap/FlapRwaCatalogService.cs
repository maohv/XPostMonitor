using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XPostMonitor.Services.Launchpads.Flap;

// Đọc danh sách RWA của Flap khi admin bấm nút Update Flap RWA.
// Service chỉ cập nhật danh sách lựa chọn, không thay đổi luồng mua, TP hoặc bán token.
public sealed class FlapRwaCatalogService : BackgroundService
{
    private static readonly Regex ScriptRegex = new(
        "<script[^>]+src=\"(?<src>[^\"]+\\.js[^\"]*)\"", RegexOptions.IgnoreCase);
    private static readonly Regex TokenRegex = new(
        "symbol:\"(?<symbol>(?:\\\\.|[^\"])*)\",name:\"(?<name>(?:\\\\.|[^\"])*)\",address:\"(?<address>0x[a-fA-F0-9]{40})\",logoUrl:\"(?:\\\\.|[^\"])*\",decimals:(?<decimals>\\d+)",
        RegexOptions.Compiled);
    private static readonly Regex CatalogEntryRegex = new("\\{(?<body>[^{}]+)\\}", RegexOptions.Compiled);
    private static readonly Regex SymbolRegex = new("symbol:\"(?<symbol>(?:\\\\.|[^\"])*)\"",
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

    // Tải danh sách mới, bỏ mã coming-soon, kiểm tra Portal rồi lưu ngay nếu có thay đổi.
    public async Task<IReadOnlyList<string>> CheckAndUpdateAsync(CancellationToken cancellationToken)
    {
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            List<FlapPaymentToken> discovered = await DownloadActiveTokensAsync(cancellationToken);
            HashSet<string> currentCodes = LaunchpadCatalog.FlapBscPaymentTokens
                .Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<FlapPaymentToken> newTokens = [];

            foreach (FlapPaymentToken token in discovered.Where(item => !currentCodes.Contains(item.Code)))
            {
                if (await flapClient.SupportsBnbPurchaseAsync(token.TokenAddress, cancellationToken))
                {
                    newTokens.Add(token);
                }
            }

            if (newTokens.Count == 0)
            {
                return Array.Empty<string>();
            }

            List<FlapPaymentToken> updated = LaunchpadCatalog.FlapBscPaymentTokens
                .Concat(newTokens)
                .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            await SaveCatalogAsync(updated, cancellationToken);
            LaunchpadCatalog.ReplaceFlapBscPaymentTokens(updated);

            string[] codes = newTokens.Select(item => item.Code).ToArray();
            logger.LogInformation("[FLAP RWA] Updated catalog: {Codes}", string.Join(", ", codes));
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
            logger.LogWarning("[FLAP RWA] Saved catalog is invalid. Using the built-in catalog.");
            return;
        }

        LaunchpadCatalog.ReplaceFlapBscPaymentTokens(saved);
        logger.LogInformation("[FLAP RWA] Loaded {Count} payment tokens.", saved.Count);
    }

    // Trang Flap chứa danh sách trong JavaScript. Chỉ nhận mục RWA không có trạng thái coming-soon.
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
            HashSet<string> activeRwaSymbols = CatalogEntryRegex.Matches(catalog)
                .Select(match => match.Groups["body"].Value)
                .Where(body => body.Contains("category:\"rwa\"", StringComparison.Ordinal)
                    && !body.Contains("status:\"coming-soon\"", StringComparison.Ordinal))
                .Select(body => SymbolRegex.Match(body))
                .Where(match => match.Success)
                .Select(match => DecodeJavascriptString(match.Groups["symbol"].Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return TokenRegex.Matches(javascript)
                .Select(ToPaymentToken)
                .Where(item => activeRwaSymbols.Contains(item.Code) && IsValidToken(item))
                .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        throw new InvalidOperationException("Flap active RWA catalog was not found in the create page.");
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

    private static FlapPaymentToken ToPaymentToken(Match match)
    {
        string code = DecodeJavascriptString(match.Groups["symbol"].Value);
        string name = DecodeJavascriptString(match.Groups["name"].Value);
        return new FlapPaymentToken(code, code + " - " + name, match.Groups["address"].Value,
            int.Parse(match.Groups["decimals"].Value));
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
