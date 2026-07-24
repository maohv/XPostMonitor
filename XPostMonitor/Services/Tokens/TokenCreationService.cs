using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Launchpads.DyorStable;
using XPostMonitor.Services.Launchpads.FourMeme;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Tokens;

// Tạo token trong queue riêng để không làm chậm thông báo Telegram.
public sealed class TokenCreationService : BackgroundService
{
    private readonly TokenSettingsService tokenSettings;
    private readonly TokenPreviewService tokenPreviewService;
    private readonly FourMemeClient fourMemeClient;
    private readonly DyorStableClient dyorStableClient;
    private readonly EvmWalletService evmWalletService;
    private readonly TelegramApiClient telegramApi;
    private readonly ILogger<TokenCreationService> logger;
    private readonly BotTextService text;
    private readonly Channel<TokenCreationRequest> queue = Channel.CreateUnbounded<TokenCreationRequest>();
    private readonly ConcurrentDictionary<string, byte> manualJobs = new();

    public TokenCreationService(TokenSettingsService tokenSettings, TokenPreviewService tokenPreviewService,
        FourMemeClient fourMemeClient, DyorStableClient dyorStableClient, EvmWalletService evmWalletService,
        TelegramApiClient telegramApi, BotTextService text, ILogger<TokenCreationService> logger)
    {
        this.tokenSettings = tokenSettings;
        this.tokenPreviewService = tokenPreviewService;
        this.fourMemeClient = fourMemeClient;
        this.dyorStableClient = dyorStableClient;
        this.evmWalletService = evmWalletService;
        this.telegramApi = telegramApi;
        this.text = text;
        this.logger = logger;
    }

    public ValueTask QueueAsync(long chatId, string postId, string postText, string? sourceLanguage,
        string? photoUrl, string postUrl, string chain, string launchpad, bool useOriginalImage, string language,
        DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        return queue.Writer.WriteAsync(new TokenCreationRequest(chatId, postId, postText, sourceLanguage, photoUrl,
            postUrl, chain, launchpad, useOriginalImage, BotTextService.Normalize(language), receivedAt, false, null),
            cancellationToken);
    }

    public async ValueTask<bool> QueueManualAsync(long chatId, string postId, string postText,
        string? sourceLanguage, string? photoUrl, string postUrl, string chain, string launchpad, string language,
        CancellationToken cancellationToken)
    {
        string jobKey = chatId + ":" + postId + ":" + chain + ":" + launchpad;
        if (!manualJobs.TryAdd(jobKey, 0))
        {
            return false;
        }

        try
        {
            await queue.Writer.WriteAsync(new TokenCreationRequest(chatId, postId, postText, sourceLanguage,
                photoUrl, postUrl, chain, launchpad, !string.IsNullOrWhiteSpace(photoUrl),
                BotTextService.Normalize(language), DateTimeOffset.UtcNow, true, jobKey), cancellationToken);
            return true;
        }
        catch
        {
            manualJobs.TryRemove(jobKey, out _);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] workers = Enumerable.Range(0, 4).Select(_ => WorkAsync(stoppingToken)).ToArray();
        await Task.WhenAll(workers);
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        await foreach (TokenCreationRequest request in queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await CreateTokenAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[Token] Tạo token từ Post {PostId} thất bại.", request.PostId);
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "TokenFailed", request.PostId, exception.Message), cancellationToken);
            }
            finally
            {
                if (request.ManualJobKey != null)
                {
                    manualJobs.TryRemove(request.ManualJobKey, out _);
                }
            }
        }
    }

    private async Task CreateTokenAsync(TokenCreationRequest request, CancellationToken cancellationToken)
    {
        if (!LaunchpadCatalog.IsValid(request.Chain, request.Launchpad))
        {
            throw new InvalidOperationException("Launchpad is not integrated yet.");
        }

        EvmWalletCredentials? wallet = await evmWalletService.GetAsync(request.ChatId, cancellationToken);
        TokenCreateSettings? settings = request.IsManual
            ? await tokenSettings.GetChainSettingsAsync(request.ChatId, request.Chain, cancellationToken)
            : await tokenSettings.GetAutoCreateSettingsAsync(request.ChatId, request.Chain, cancellationToken);
        if (wallet == null || settings == null)
        {
            if (request.IsManual)
            {
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "LaunchpadSettingsMissing"), cancellationToken);
            }
            return;
        }

        LaunchpadNetwork network = LaunchpadCatalog.Find(request.Chain)!;
        if (settings.BuyAmount < network.MinimumBuyAmount)
        {
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "MinimumBuyAmount", network.DisplayName,
                    network.MinimumBuyAmount, network.Currency), cancellationToken);
            return;
        }

        string aiPostText = AddSourceLanguage(request.PostText, request.SourceLanguage);
        TokenPreviewDto preview = request.UseOriginalImage
            ? await tokenPreviewService.CreateWithOriginalImageAsync(aiPostText, request.PhotoUrl,
                request.ReceivedAt, request.Chain, cancellationToken)
            : await tokenPreviewService.CreateAsync(aiPostText, request.PhotoUrl,
                request.ReceivedAt, request.Chain, cancellationToken);
        if (preview.IsExpired)
        {
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "TokenSkipped", request.PostId), cancellationToken);
            return;
        }

        TokenResult result = await CreateOnLaunchpadAsync(request, wallet, preview, settings.BuyAmount,
            cancellationToken);
        string gas = result.EstimatedGas?.ToString() ?? text.Get(request.Language, "DryRunGasSkipped");
        string balanceStatus = text.Get(request.Language,
            result.HasEnoughBalance ? "DryRunBalanceEnough" : "DryRunBalanceLow");
        string caption = result.IsDryRun
            ? text.Get(request.Language, "TokenDryRunPassed", request.Launchpad, preview.Draft.Name,
                preview.Draft.Symbol, settings.BuyAmount, network.Currency, gas, balanceStatus)
            : text.Get(request.Language, "TokenSubmitted", preview.Draft.Name,
                preview.Draft.Symbol, request.Chain, request.Launchpad);
        if (!string.IsNullOrWhiteSpace(result.TransactionHash))
        {
            caption += "\n" + text.Get(request.Language, "Transaction") + ": " + result.TransactionHash;
        }
        string? gmgnUrl = LaunchpadCatalog.GetGmgnTokenUrl(request.Chain, result.TokenAddress);
        if (gmgnUrl != null)
        {
            caption += "\nGMGN: " + gmgnUrl;
        }

        await telegramApi.SendPhotoAsync(request.ChatId, preview.Image, caption, cancellationToken);
        logger.LogInformation("[{Launchpad}] Post {PostId} finished. Dry run: {IsDryRun}. Transaction {TransactionHash}.",
            request.Launchpad, request.PostId, result.IsDryRun, result.TransactionHash);
    }

    private async Task<TokenResult> CreateOnLaunchpadAsync(TokenCreationRequest request,
        EvmWalletCredentials wallet, TokenPreviewDto preview, decimal buyAmount,
        CancellationToken cancellationToken)
    {
        if (request.Launchpad == "fourmeme")
        {
            FourMemeTokenRequest tokenRequest = new FourMemeTokenRequest(preview.Draft.Name,
                preview.Draft.Symbol, preview.Draft.Description, preview.Image, request.PostUrl, buyAmount);
            FourMemeTokenResult result = await fourMemeClient.CreateTokenAsync(wallet, tokenRequest,
                cancellationToken);
            return new TokenResult(result.TransactionHash, result.EstimatedGas, result.IsDryRun,
                result.HasEnoughBalance, result.TokenAddress);
        }

        DyorStableTokenRequest dyorRequest = new DyorStableTokenRequest(preview.Draft.Name,
            preview.Draft.Symbol, preview.Draft.Description, preview.Image, request.PostUrl, buyAmount);
        DyorStableTokenResult dyorResult = await dyorStableClient.CreateTokenAsync(wallet, dyorRequest,
            cancellationToken);
        return new TokenResult(dyorResult.TransactionHash, dyorResult.EstimatedGas, dyorResult.IsDryRun,
            dyorResult.HasEnoughBalance, dyorResult.TokenAddress);
    }

    private static string AddSourceLanguage(string postText, string? sourceLanguage)
    {
        return string.IsNullOrWhiteSpace(sourceLanguage)
            ? postText
            : "[SOURCE_LANGUAGE=" + sourceLanguage.ToLowerInvariant() + "]\n" + postText;
    }

    private sealed record TokenCreationRequest(long ChatId, string PostId, string PostText,
        string? SourceLanguage, string? PhotoUrl, string PostUrl, string Chain, string Launchpad,
        bool UseOriginalImage, string Language, DateTimeOffset ReceivedAt, bool IsManual, string? ManualJobKey);

    private sealed record TokenResult(string? TransactionHash, BigInteger? EstimatedGas, bool IsDryRun,
        bool HasEnoughBalance, string? TokenAddress);
}
