using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using XPostMonitor.Dtos;
using XPostMonitor.Configuration;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Launchpads.DyorStable;
using XPostMonitor.Services.Launchpads.FourMeme;
using XPostMonitor.Services.Launchpads.Flap;
using XPostMonitor.Services.Launchpads.FlapRobinhood;
using XPostMonitor.Services.Launchpads.LongRobinhood;
using XPostMonitor.Services.Launchpads.PonsRobinhood;
using XPostMonitor.Services.Gmgn;
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
    private readonly FlapClient flapClient;
    private readonly FlapRobinhoodClient flapRobinhoodClient;
    private readonly DyorStableClient dyorStableClient;
    private readonly LongRobinhoodClient longRobinhoodClient;
    private readonly PonsRobinhoodClient ponsRobinhoodClient;
    private readonly EvmWalletService evmWalletService;
    private readonly AutoTradingService autoTradingService;
    private readonly TelegramApiClient telegramApi;
    private readonly ILogger<TokenCreationService> logger;
    private readonly BotTextService text;
    private readonly TradingWorkersOptions workerOptions;
    private readonly Channel<TokenCreationRequest> queue = Channel.CreateUnbounded<TokenCreationRequest>();
    private readonly ConcurrentDictionary<string, int> manualJobs = new();

    public TokenCreationService(TokenSettingsService tokenSettings, TokenPreviewService tokenPreviewService,
        FourMemeClient fourMemeClient, DyorStableClient dyorStableClient, LongRobinhoodClient longRobinhoodClient,
        PonsRobinhoodClient ponsRobinhoodClient, FlapClient flapClient,
        FlapRobinhoodClient flapRobinhoodClient, EvmWalletService evmWalletService,
        AutoTradingService autoTradingService, TelegramApiClient telegramApi, BotTextService text,
        TradingWorkersOptions workerOptions, ILogger<TokenCreationService> logger)
    {
        this.tokenSettings = tokenSettings;
        this.tokenPreviewService = tokenPreviewService;
        this.fourMemeClient = fourMemeClient;
        this.flapClient = flapClient;
        this.flapRobinhoodClient = flapRobinhoodClient;
        this.dyorStableClient = dyorStableClient;
        this.longRobinhoodClient = longRobinhoodClient;
        this.ponsRobinhoodClient = ponsRobinhoodClient;
        this.evmWalletService = evmWalletService;
        this.autoTradingService = autoTradingService;
        this.telegramApi = telegramApi;
        this.text = text;
        this.workerOptions = workerOptions;
        this.logger = logger;
    }

    public async ValueTask QueueAsync(long chatId, string postId, string? username, string postText,
        string? sourceLanguage,
        string? photoUrl, string postUrl, string chain, string launchpad, string? anchor, bool useOriginalImage,
        int creatorTaxPercent, bool enableAutoTrading, int parallelTokenCount, string language,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        int workerCount = Math.Clamp(parallelTokenCount, 1, workerOptions.MaxWorkers);
        IReadOnlyList<TradingWorkerWallet> workers = await evmWalletService.GetReadyWorkersAsync(chatId,
            workerCount, cancellationToken);
        if (workers.Count != workerCount)
        {
            int missingSlot = Enumerable.Range(1, workerCount)
                .First(slot => workers.All(worker => worker.SlotNumber != slot));
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ConfigureWorkerFirst", missingSlot),
                cancellationToken);
            return;
        }

        SharedTokenPreview sharedPreview = new SharedTokenPreview();
        foreach (TradingWorkerWallet worker in workers)
        {
            await queue.Writer.WriteAsync(new TokenCreationRequest(chatId, postId, username, postText,
                sourceLanguage, photoUrl, null, postUrl, chain, launchpad, anchor, useOriginalImage,
                creatorTaxPercent,
                enableAutoTrading, null, BotTextService.Normalize(language), receivedAt, false, null, worker.WorkerId,
                worker.SlotNumber, workerCount, sharedPreview), cancellationToken);
        }
    }

    public async ValueTask<bool> QueueManualAsync(long chatId, string postId, string? username, string postText,
        string? sourceLanguage, string? photoUrl, string postUrl, string chain, string launchpad, string? anchor,
        int creatorTaxPercent, IReadOnlyCollection<int> workerSlots, byte[]? customImage,
        int? flapHolderPercentOverride, string language, CancellationToken cancellationToken)
    {
        return await QueueDirectLinkAsync(chatId, postId, username, postText, sourceLanguage, photoUrl,
            postUrl, chain, launchpad, anchor, creatorTaxPercent, workerSlots, customImage,
            true, null, null, null, flapHolderPercentOverride, language, cancellationToken);
    }

    // Auto của link dùng cùng hàng đợi nhanh, nhưng mang theo số tiền riêng từ LinkTokenSettings.
    public async ValueTask<bool> QueueLinkAutoAsync(long chatId, string postId, string? username, string postText,
        string? sourceLanguage, string? photoUrl, string postUrl, LinkTokenConfiguration settings,
        IReadOnlyCollection<int> workerSlots, byte[]? customImage, string? tokenNameOverride,
        string? tokenSymbolOverride, string language, CancellationToken cancellationToken)
    {
        TokenCreateSettings amountSettings = new(settings.BuyAmount, settings.SlippagePercent);
        return await QueueDirectLinkAsync(chatId, postId, username, postText, sourceLanguage, photoUrl,
            postUrl, settings.Chain, settings.Launchpad, settings.Anchor, settings.CreatorTaxPercent,
            workerSlots, customImage, settings.EnableAutoTrading, amountSettings, tokenNameOverride,
            tokenSymbolOverride, settings.FlapHolderPercent, language, cancellationToken);
    }

    private async ValueTask<bool> QueueDirectLinkAsync(long chatId, string postId, string? username,
        string postText, string? sourceLanguage, string? photoUrl, string postUrl, string chain,
        string launchpad, string? anchor, int creatorTaxPercent, IReadOnlyCollection<int> workerSlots,
        byte[]? customImage, bool enableAutoTrading, TokenCreateSettings? settingsOverride,
        string? tokenNameOverride, string? tokenSymbolOverride, int? flapHolderPercentOverride, string language,
        CancellationToken cancellationToken)
    {
        int[] selectedSlots = workerSlots.Distinct().OrderBy(slot => slot).ToArray();
        if (selectedSlots.Length == 0 || selectedSlots.Length > workerOptions.MaxWorkers)
        {
            return false;
        }
        int workerCount = selectedSlots.Length;
        string jobKey = chatId + ":" + postId + ":" + chain + ":" + launchpad + ":" + anchor;
        if (!manualJobs.TryAdd(jobKey, workerCount))
        {
            return false;
        }

        try
        {
            IReadOnlyList<TradingWorkerWallet> workers = await evmWalletService.GetReadyWorkersAsync(chatId,
                selectedSlots, cancellationToken);
            if (workers.Count != workerCount)
            {
                manualJobs.TryRemove(jobKey, out _);
                return false;
            }

            SharedTokenPreview sharedPreview = new SharedTokenPreview();
            foreach (TradingWorkerWallet worker in workers)
            {
                await queue.Writer.WriteAsync(new TokenCreationRequest(chatId, postId, username, postText,
                    sourceLanguage, photoUrl, customImage, postUrl, chain, launchpad, anchor,
                    customImage != null || !string.IsNullOrWhiteSpace(photoUrl), creatorTaxPercent, true,
                    settingsOverride, BotTextService.Normalize(language), DateTimeOffset.UtcNow, true, jobKey,
                    worker.WorkerId,
                    worker.SlotNumber, workerCount, sharedPreview, tokenNameOverride, tokenSymbolOverride,
                    flapHolderPercentOverride),
                    cancellationToken);
            }
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
        Task[] workers = Enumerable.Range(0, workerOptions.MaxWorkers)
            .Select(_ => WorkAsync(stoppingToken)).ToArray();
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
                    CompleteManualJob(request.ManualJobKey);
                }
            }
        }
    }

    private async Task CreateTokenAsync(TokenCreationRequest request, CancellationToken cancellationToken)
    {
        if (!LaunchpadCatalog.IsValidRoute(request.Chain, request.Launchpad, request.Anchor))
        {
            throw new InvalidOperationException("Launchpad is not integrated yet.");
        }

        TradingWorkerWallet? worker = request.TradingWorkerId.HasValue
            ? await evmWalletService.GetByIdAsync(request.ChatId, request.TradingWorkerId.Value,
                cancellationToken)
            : (await evmWalletService.GetReadyWorkersAsync(request.ChatId, 1, cancellationToken))
                .FirstOrDefault();
        TokenCreateSettings? settings = request.SettingsOverride ?? (request.IsManual
            ? await tokenSettings.GetChainSettingsAsync(request.ChatId, request.Chain, cancellationToken)
            : await tokenSettings.GetAutoCreateSettingsAsync(request.ChatId, request.Chain, cancellationToken));
        if (worker == null || settings == null)
        {
            if (request.IsManual)
            {
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "LaunchpadSettingsMissing"), cancellationToken);
            }
            return;
        }

        LaunchpadNetwork network = LaunchpadCatalog.Find(request.Chain)!;
        string buyCurrency = network.Currency;
        decimal minimumBuyAmount = request.Chain == "bsc" ? 0m : network.MinimumBuyAmount;
        if (settings.BuyAmount <= 0 || settings.BuyAmount < minimumBuyAmount)
        {
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "MinimumBuyAmount", network.DisplayName,
                    minimumBuyAmount, buyCurrency), cancellationToken);
            return;
        }

        TokenPreviewDto preview;
        try
        {
            preview = await request.SharedPreview.GetOrCreateAsync(
                () => CreatePreviewAsync(request, cancellationToken));
        }
        catch
        {
            // Preview dùng chung bị lỗi thì chỉ một worker gửi thông báo; các worker còn lại dừng im lặng.
            if (!request.SharedPreview.TryBeginFailureNotification())
            {
                return;
            }
            throw;
        }

        if (preview.IsExpired)
        {
            if (request.SharedPreview.TryBeginFailureNotification())
            {
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "TokenSkipped", request.PostId,
                        tokenPreviewService.AutoTimeoutSeconds), cancellationToken);
            }
            return;
        }

        FlapTaxAllocation flapTaxAllocation = FlapTaxAllocation.DevOnly;
        if (request.Launchpad == "flap" && request.Chain == "bsc")
        {
            flapTaxAllocation = request.FlapHolderPercentOverride.HasValue
                ? new FlapTaxAllocation(100 - request.FlapHolderPercentOverride.Value,
                    request.FlapHolderPercentOverride.Value)
                : await tokenSettings.GetFlapTaxAllocationAsync(request.ChatId, cancellationToken);
        }
        TokenResult result = await CreateOnLaunchpadAsync(request, worker.Wallet, preview, settings.BuyAmount,
            settings.SlippagePercent, flapTaxAllocation, cancellationToken);
        string gas = result.EstimatedGas?.ToString() ?? text.Get(request.Language, "DryRunGasSkipped");
        string balanceStatus = text.Get(request.Language,
            result.HasEnoughBalance ? "DryRunBalanceEnough" : "DryRunBalanceLow");
        string caption = result.IsDryRun
            ? text.Get(request.Language, "TokenDryRunPassed", request.Launchpad, preview.Draft.Name,
                preview.Draft.Symbol, settings.BuyAmount, buyCurrency, gas, balanceStatus)
            : text.Get(request.Language, "TokenSubmitted", preview.Draft.Name,
                preview.Draft.Symbol, request.Chain, request.Launchpad);
        if (request.Launchpad == "long")
        {
            caption += "\n" + text.Get(request.Language, "StockAnchor") + ": " + request.Anchor;
        }
        if (request.Launchpad == "pons")
        {
            caption += "\n" + text.Get(request.Language, "CreatorFee") + ": 70%";
        }
        if (request.Launchpad == "flap")
        {
            caption += "\n" + text.Get(request.Language, "CreatorTax") + ": "
                + request.CreatorTaxPercent + "%";
            if (request.Chain == "bsc")
            {
                caption += "\n" + text.Get(request.Language, "PaymentToken") + ": "
                    + LaunchpadCatalog.FindFlapBscPaymentToken(request.Anchor)!.Code;
                caption += "\n" + text.Get(request.Language, "FlapTaxAllocationSummary",
                    flapTaxAllocation.DevPercent, flapTaxAllocation.HolderPercent);
            }
        }
        if (!string.IsNullOrWhiteSpace(result.TransactionHash))
        {
            caption += "\n" + text.Get(request.Language, "Transaction") + ": " + result.TransactionHash;
        }
        string? gmgnUrl = LaunchpadCatalog.GetGmgnTokenUrl(request.Chain, result.TokenAddress);
        if (gmgnUrl != null)
        {
            caption += "\nGMGN: " + gmgnUrl;
        }
        if (request.VariantCount > 1)
        {
            caption += "\n" + text.Get(request.Language, "Worker") + ": " + request.WorkerSlot;
        }

        if (!result.IsDryRun && request.EnableAutoTrading && !string.IsNullOrWhiteSpace(result.TokenAddress))
        {
            await autoTradingService.QueueAsync(request.ChatId, worker.WorkerId, request.PostId, request.Chain,
                request.Launchpad, result.TokenAddress, preview.Draft.Name, preview.Draft.Symbol,
                worker.Wallet.Address, settings.SlippagePercent, result.TransactionHash, request.Language,
                request.VariantCount, cancellationToken);
        }
        else if (!result.IsDryRun)
        {
            await AutoTradingDiagnosticLog.WriteAsync("NOT QUEUED | Post=" + request.PostId
                + " | Symbol=" + preview.Draft.Symbol + " | EnableAutoTrading=" + request.EnableAutoTrading
                + " | Token=" + (result.TokenAddress ?? "empty"));
        }
        await telegramApi.SendPhotoAsync(request.ChatId, preview.Image, caption, cancellationToken);
        logger.LogInformation("[{Launchpad}] Post {PostId} finished. Dry run: {IsDryRun}. Transaction {TransactionHash}.",
            request.Launchpad, request.PostId, result.IsDryRun, result.TransactionHash);
    }

    // Tên, mã và ảnh chỉ được chuẩn bị một lần rồi dùng chung cho mọi ví trong cùng một Post.
    private async Task<TokenPreviewDto> CreatePreviewAsync(TokenCreationRequest request,
        CancellationToken cancellationToken)
    {
        string aiPostText = AddSourceLanguage(request.PostText, request.SourceLanguage);
        TokenPreviewDto preview = request.CustomImage != null
            ? await tokenPreviewService.CreateWithUploadedImageAsync(aiPostText, request.CustomImage,
                request.ReceivedAt, request.Chain, cancellationToken)
            : request.UseOriginalImage
            ? await tokenPreviewService.CreateWithOriginalImageAsync(aiPostText, request.PhotoUrl,
                request.ReceivedAt, request.Chain, !request.IsManual, cancellationToken)
            : await tokenPreviewService.CreateAsync(aiPostText, request.PhotoUrl,
                request.ReceivedAt, request.Chain, request.Username, !request.IsManual, cancellationToken);

        // Tên user gửi kèm ảnh được ưu tiên; AI vẫn viết mô tả dựa trên nội dung Post.
        if (!string.IsNullOrWhiteSpace(request.TokenNameOverride))
        {
            preview.Draft.Name = request.TokenNameOverride;
            preview.Draft.Symbol = request.TokenSymbolOverride ?? request.TokenNameOverride;
        }
        return preview;
    }

    private async Task<TokenResult> CreateOnLaunchpadAsync(TokenCreationRequest request,
        EvmWalletCredentials wallet, TokenPreviewDto preview, decimal buyAmount, decimal slippagePercent,
        FlapTaxAllocation flapTaxAllocation, CancellationToken cancellationToken)
    {
        if (request.Launchpad == "fourmeme")
        {
            FourMemeTokenRequest tokenRequest = new FourMemeTokenRequest(preview.Draft.Name,
                preview.Draft.Symbol, preview.Draft.Description, preview.Image, request.PostUrl, buyAmount,
                request.CreatorTaxPercent);
            FourMemeTokenResult result = await fourMemeClient.CreateTokenAsync(wallet, tokenRequest,
                cancellationToken);
            return new TokenResult(result.TransactionHash, result.EstimatedGas, result.IsDryRun,
                result.HasEnoughBalance, result.TokenAddress);
        }

        if (request.Launchpad == "dyorswap")
        {
            DyorStableTokenRequest dyorRequest = new DyorStableTokenRequest(preview.Draft.Name,
                preview.Draft.Symbol, preview.Draft.Description, preview.Image, request.PostUrl, buyAmount);
            DyorStableTokenResult dyorResult = await dyorStableClient.CreateTokenAsync(wallet, dyorRequest,
                cancellationToken);
            return new TokenResult(dyorResult.TransactionHash, dyorResult.EstimatedGas, dyorResult.IsDryRun,
                dyorResult.HasEnoughBalance, dyorResult.TokenAddress);
        }

        if (request.Launchpad == "flap")
        {
            if (request.Chain == "robinhood")
            {
                FlapRobinhoodTokenRequest robinhoodRequest = new FlapRobinhoodTokenRequest(
                    preview.Draft.Name, preview.Draft.Symbol, preview.Draft.Description,
                    preview.Image, request.PostUrl, buyAmount, request.CreatorTaxPercent);
                FlapRobinhoodTokenResult robinhoodResult =
                    await flapRobinhoodClient.CreateTokenAsync(wallet, robinhoodRequest,
                        cancellationToken);
                return new TokenResult(robinhoodResult.TransactionHash,
                    robinhoodResult.EstimatedGas, robinhoodResult.IsDryRun,
                    robinhoodResult.HasEnoughBalance, robinhoodResult.TokenAddress);
            }

            FlapTokenRequest flapRequest = new FlapTokenRequest(preview.Draft.Name,
                preview.Draft.Symbol, preview.Draft.Description, preview.Image, request.PostUrl, buyAmount,
                request.CreatorTaxPercent, request.Anchor, flapTaxAllocation.HolderPercent);
            FlapTokenResult flapResult = await flapClient.CreateTokenAsync(wallet, flapRequest,
                cancellationToken);
            return new TokenResult(flapResult.TransactionHash, flapResult.EstimatedGas, flapResult.IsDryRun,
                flapResult.HasEnoughBalance, flapResult.TokenAddress);
        }

        if (request.Launchpad == "pons")
        {
            PonsRobinhoodTokenRequest ponsRequest = new PonsRobinhoodTokenRequest(preview.Draft.Name,
                preview.Draft.Symbol, preview.Draft.Description, preview.Image, request.PostUrl, buyAmount);
            PonsRobinhoodTokenResult ponsResult = await ponsRobinhoodClient.CreateTokenAsync(wallet, ponsRequest,
                cancellationToken);
            return new TokenResult(ponsResult.TransactionHash, ponsResult.EstimatedGas, ponsResult.IsDryRun,
                ponsResult.HasEnoughBalance, ponsResult.TokenAddress);
        }

        LongRobinhoodTokenRequest longRequest = new LongRobinhoodTokenRequest(preview.Draft.Name,
            preview.Draft.Symbol, preview.Draft.Description, preview.Image, request.PostUrl, request.Anchor!,
            buyAmount, slippagePercent);
        LongRobinhoodTokenResult longResult = await longRobinhoodClient.CreateTokenAsync(wallet, longRequest,
            cancellationToken);
        return new TokenResult(longResult.TransactionHash, longResult.EstimatedGas, longResult.IsDryRun,
            longResult.HasEnoughBalance, longResult.TokenAddress);
    }

    private static string AddSourceLanguage(string postText, string? sourceLanguage)
    {
        return string.IsNullOrWhiteSpace(sourceLanguage)
            ? postText
            : "[SOURCE_LANGUAGE=" + sourceLanguage.ToLowerInvariant() + "]\n" + postText;
    }

    private void CompleteManualJob(string jobKey)
    {
        int remaining = manualJobs.AddOrUpdate(jobKey, 0, (_, current) => Math.Max(0, current - 1));
        if (remaining == 0)
        {
            manualJobs.TryRemove(jobKey, out _);
        }
    }

    private sealed record TokenCreationRequest(long ChatId, string PostId, string? Username, string PostText,
        string? SourceLanguage, string? PhotoUrl, byte[]? CustomImage, string PostUrl, string Chain, string Launchpad,
        string? Anchor, bool UseOriginalImage, int CreatorTaxPercent, bool EnableAutoTrading,
        TokenCreateSettings? SettingsOverride, string Language, DateTimeOffset ReceivedAt, bool IsManual,
        string? ManualJobKey,
        long? TradingWorkerId, int WorkerSlot,
        int VariantCount, SharedTokenPreview SharedPreview, string? TokenNameOverride = null,
        string? TokenSymbolOverride = null, int? FlapHolderPercentOverride = null);

    private sealed class SharedTokenPreview
    {
        private readonly object sync = new object();
        private Task<TokenPreviewDto>? previewTask;
        private int failureNotificationSent;

        public Task<TokenPreviewDto> GetOrCreateAsync(Func<Task<TokenPreviewDto>> createPreview)
        {
            lock (sync)
            {
                previewTask ??= createPreview();
                return previewTask;
            }
        }

        public bool TryBeginFailureNotification()
        {
            return Interlocked.Exchange(ref failureNotificationSent, 1) == 0;
        }
    }

    private sealed record TokenResult(string? TransactionHash, BigInteger? EstimatedGas, bool IsDryRun,
        bool HasEnoughBalance, string? TokenAddress);
}
