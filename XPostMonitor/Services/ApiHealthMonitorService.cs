using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Services.Flux;
using XPostMonitor.Services.Gemini;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.Launchpads.DyorStable;
using XPostMonitor.Services.Launchpads.FourMeme;
using XPostMonitor.Services.Launchpads.Flap;
using XPostMonitor.Services.Launchpads.FlapRobinhood;
using XPostMonitor.Services.Launchpads.LongRobinhood;
using XPostMonitor.Services.Launchpads.PonsRobinhood;
using XPostMonitor.Services.OpenAi;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.X;

namespace XPostMonitor.Services;

// Kiểm tra các API khi khởi động và lặp lại sau mỗi 30 phút.
public sealed class ApiHealthMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly XApiClient xApiClient;
    private readonly TelegramApiClient telegramApi;
    private readonly OpenAiClient openAiClient;
    private readonly FluxClient fluxClient;
    private readonly GeminiImageClient geminiImageClient;
    private readonly GmgnClient gmgnClient;
    private readonly AutoTradingSettingsService tradingSettings;
    private readonly FourMemeClient fourMemeClient;
    private readonly FlapClient flapClient;
    private readonly FlapRobinhoodClient flapRobinhoodClient;
    private readonly DyorStableClient dyorStableClient;
    private readonly LongRobinhoodClient longRobinhoodClient;
    private readonly PonsRobinhoodClient ponsRobinhoodClient;
    private readonly GmgnOptions gmgnOptions;
    private readonly ImageGenerationOptions imageGenerationOptions;
    private readonly DyorStableOptions dyorOptions;
    private readonly LongRobinhoodOptions longOptions;
    private readonly PonsRobinhoodOptions ponsOptions;
    private readonly ILogger<ApiHealthMonitorService> logger;

    public ApiHealthMonitorService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory,
        XApiClient xApiClient, TelegramApiClient telegramApi, OpenAiClient openAiClient,
        FluxClient fluxClient, GeminiImageClient geminiImageClient, GmgnClient gmgnClient,
        AutoTradingSettingsService tradingSettings,
        FourMemeClient fourMemeClient, DyorStableClient dyorStableClient,
        LongRobinhoodClient longRobinhoodClient, PonsRobinhoodClient ponsRobinhoodClient,
        FlapClient flapClient, FlapRobinhoodClient flapRobinhoodClient,
        GmgnOptions gmgnOptions, ImageGenerationOptions imageGenerationOptions, DyorStableOptions dyorOptions,
        LongRobinhoodOptions longOptions,
        PonsRobinhoodOptions ponsOptions, ILogger<ApiHealthMonitorService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.httpClientFactory = httpClientFactory;
        this.xApiClient = xApiClient;
        this.telegramApi = telegramApi;
        this.openAiClient = openAiClient;
        this.fluxClient = fluxClient;
        this.geminiImageClient = geminiImageClient;
        this.gmgnClient = gmgnClient;
        this.tradingSettings = tradingSettings;
        this.fourMemeClient = fourMemeClient;
        this.flapClient = flapClient;
        this.flapRobinhoodClient = flapRobinhoodClient;
        this.dyorStableClient = dyorStableClient;
        this.longRobinhoodClient = longRobinhoodClient;
        this.ponsRobinhoodClient = ponsRobinhoodClient;
        this.gmgnOptions = gmgnOptions;
        this.imageGenerationOptions = imageGenerationOptions;
        this.dyorOptions = dyorOptions;
        this.longOptions = longOptions;
        this.ponsOptions = ponsOptions;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAllAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[HEALTH] Checking API connections...");
        logger.LogInformation("[HEALTH] Image provider: {Provider}", imageGenerationOptions.Provider);
        await CheckAsync("SQL Server", CheckDatabaseAsync, cancellationToken);
        await CheckAsync("X API", async token =>
        {
            await xApiClient.GetStreamRulesAsync(token);
            return "stream rules available";
        }, cancellationToken);
        await CheckAsync("Telegram API", async token =>
        {
            await telegramApi.CheckConnectionAsync(token);
            return "bot token valid";
        }, cancellationToken);
        await CheckAsync("OpenAI API", token => EnsureSuccessAsync(openAiClient.CheckConnectionAsync(token)),
            cancellationToken);
        await CheckAsync("FLUX API", token => EnsureSuccessAsync(fluxClient.CheckConnectionAsync(token)),
            cancellationToken);
        if (imageGenerationOptions.Provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            await CheckAsync("Gemini 3.1 Image",
                token => EnsureSuccessAsync(geminiImageClient.CheckConnectionAsync(token)), cancellationToken);
        }
        await CheckAsync("GMGN API", CheckGmgnAsync, cancellationToken);
        await CheckAsync("FourMeme API + BSC RPC", async token =>
        {
            FourMemeConnectionResult result = await fourMemeClient.CheckAsync(null, token);
            return "launch fee " + result.LaunchFee + " BNB";
        }, cancellationToken);
        await CheckAsync("Flap Portal + BSC RPC", async token =>
        {
            FlapConnectionResult result = await flapClient.CheckAsync(token);
            if (!result.CorrectChain || !result.PortalFound)
            {
                throw new InvalidOperationException("wrong chain or Portal contract missing");
            }
            return "tax token Portal ready";
        }, cancellationToken);
        await CheckAsync("Flap Portal + Robinhood RPC", async token =>
        {
            FlapRobinhoodConnectionResult result = await flapRobinhoodClient.CheckAsync(token);
            if (!result.CorrectChain || !result.PortalFound)
            {
                throw new InvalidOperationException("wrong chain or Portal contract missing");
            }
            return "Tax Token V3 Portal ready";
        }, cancellationToken);
        await CheckAsync("Stable RPC + DYOR", async token =>
        {
            DyorStableConnectionResult result = await dyorStableClient.CheckAsync(null, token);
            if (!result.CorrectChain || !result.FactoryFound)
            {
                throw new InvalidOperationException("wrong chain or factory contract missing");
            }
            return "chain and factory ready";
        }, cancellationToken);
        await CheckAsync("Robinhood RPC + Long", async token =>
        {
            LongRobinhoodConnectionResult result = await longRobinhoodClient.CheckAsync(null, token);
            if (!result.CorrectChain || !result.LauncherFound || result.Paused)
            {
                throw new InvalidOperationException("wrong chain, launcher missing, or launch paused");
            }
            return "launcher ready";
        }, cancellationToken);
        await CheckAsync("Robinhood RPC + pons", async token =>
        {
            PonsRobinhoodConnectionResult result = await ponsRobinhoodClient.CheckAsync(token);
            if (!result.CorrectChain || !result.FactoryFound || !result.LaunchEnabled)
            {
                throw new InvalidOperationException("wrong chain, factory missing, or launch disabled");
            }
            return "factory ready";
        }, cancellationToken);
        await CheckAsync("Pinata API", CheckPinataAsync, cancellationToken);
        logger.LogInformation("[HEALTH] API connection check finished. Next check in 30 minutes.");
    }

    private async Task<string> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? "database reachable"
            : throw new InvalidOperationException("database unreachable");
    }

    private async Task<string> CheckGmgnAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var worker = await db.TradingWorkers
            .Where(item => item.EncryptedGmgnApiKey != "" && item.EncryptedGmgnPrivateKey != "")
            .OrderBy(item => item.ChatId).ThenBy(item => item.SlotNumber)
            .Select(item => new { item.ChatId, item.Id })
            .FirstOrDefaultAsync(cancellationToken);
        if (worker == null)
        {
            if (!string.IsNullOrWhiteSpace(gmgnOptions.ApiKey))
            {
                return await EnsureSuccessAsync(gmgnClient.CheckConnectionAsync(cancellationToken));
            }
            throw new InvalidOperationException("GMGN is not configured");
        }

        GmgnCredentials? credentials = await tradingSettings.GetCredentialsAsync(worker.ChatId, worker.Id,
            cancellationToken);
        if (credentials == null)
        {
            throw new InvalidOperationException("saved GMGN credentials cannot be decrypted");
        }
        GmgnConnectionResult result = await gmgnClient.CheckUserConnectionAsync(credentials.ApiKey,
            credentials.PrivateKey, cancellationToken);
        return result.Success
            ? "saved user connection valid - " + result.Message
            : throw new InvalidOperationException(result.Message);
    }

    private async Task<string> CheckPinataAsync(CancellationToken cancellationToken)
    {
        string jwt = new[] { dyorOptions.PinataJwt, longOptions.PinataJwt, ponsOptions.PinataJwt }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        if (jwt.Length == 0)
        {
            throw new InvalidOperationException("Pinata JWT is not configured");
        }

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.pinata.cloud/data/testAuthentication");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using HttpClient client = httpClientFactory.CreateClient();
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("HTTP " + (int)response.StatusCode);
        }
        return "authentication valid";
    }

    private async Task CheckAsync(string name, Func<CancellationToken, Task<string>> check,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            string message = await check(cancellationToken);
            logger.LogInformation("[HEALTH] {Api}: OK ({Elapsed} ms) - {Message}",
                name, stopwatch.ElapsedMilliseconds, message.Replace('\n', ' '));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("[HEALTH] {Api}: FAILED ({Elapsed} ms) - {Message}",
                name, stopwatch.ElapsedMilliseconds, exception.Message);
        }
    }

    private static async Task<string> EnsureSuccessAsync(Task<string> checkTask)
    {
        string result = await checkTask;
        if (result.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || result.Contains("missing", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result);
        }
        return result;
    }
}
