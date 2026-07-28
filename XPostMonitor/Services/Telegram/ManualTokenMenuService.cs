using System.Collections.Concurrent;
using System.Globalization;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Tokens;
using XPostMonitor.Services.Wallets;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Notifications;

namespace XPostMonitor.Services.Telegram;

// Nhận link X, cho user chọn nơi tạo và chỉ tạo token sau nút xác nhận cuối.
public sealed class ManualTokenMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly XApiClient xApiClient;
    private readonly TokenSettingsService tokenSettings;
    private readonly TokenCreationService tokenCreation;
    private readonly EvmWalletService evmWalletService;
    private readonly FourMemeOptions fourMemeOptions;
    private readonly DyorStableOptions dyorStableOptions;
    private readonly LongRobinhoodOptions longRobinhoodOptions;
    private readonly PonsRobinhoodOptions ponsRobinhoodOptions;
    private readonly BotTextService text;
    private readonly TokenPreviewService tokenPreviewService;
    private readonly TradingWorkersOptions workerOptions;
    private readonly bool enableManualTokenCreation;
    private readonly ConcurrentDictionary<long, PendingManualImage> pendingImages = new();

    public ManualTokenMenuService(TelegramApiClient telegramApi, XApiClient xApiClient,
        TokenSettingsService tokenSettings, TokenCreationService tokenCreation,
        EvmWalletService evmWalletService, FourMemeOptions fourMemeOptions,
        DyorStableOptions dyorStableOptions, LongRobinhoodOptions longRobinhoodOptions,
        PonsRobinhoodOptions ponsRobinhoodOptions, BotTextService text,
        TokenPreviewService tokenPreviewService, BotOptions botOptions, TradingWorkersOptions workerOptions)
    {
        this.telegramApi = telegramApi;
        this.xApiClient = xApiClient;
        this.tokenSettings = tokenSettings;
        this.tokenCreation = tokenCreation;
        this.evmWalletService = evmWalletService;
        this.fourMemeOptions = fourMemeOptions;
        this.dyorStableOptions = dyorStableOptions;
        this.longRobinhoodOptions = longRobinhoodOptions;
        this.ponsRobinhoodOptions = ponsRobinhoodOptions;
        this.text = text;
        this.tokenPreviewService = tokenPreviewService;
        this.workerOptions = workerOptions;
        enableManualTokenCreation = botOptions.EnableManualTokenCreation;
    }

    public async Task StartAsync(long chatId, string link, string language, CancellationToken cancellationToken)
    {
        // Link mới hủy yêu cầu ảnh cũ để không dùng nhầm ảnh cho Post trước.
        pendingImages.TryRemove(chatId, out _);

        string? postId = XPostLinkParser.ParsePostId(link);
        if (postId == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "InvalidXLink"), cancellationToken);
            return;
        }

        string action = enableManualTokenCreation ? "chain" : "preview";
        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.All
            .Select(network => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(network.DisplayName, "manual:" + action + ":" + postId + ":" + network.Chain)])
            .ToList();
        buttons.Add([new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]);

        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ManualChooseNetwork"), buttons,
            cancellationToken);
    }

    public async Task HandleCallbackAsync(long chatId, long messageId, string data, string language,
        CancellationToken cancellationToken)
    {
        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);

        if (data == "manual:cancel")
        {
            pendingImages.TryRemove(chatId, out _);
            return;
        }

        string[] parts = data.Split(':');
        if (parts.Length != 4 || !XPostLinkParser.IsPostId(parts[2]))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        string postId = parts[2];
        if (!enableManualTokenCreation && parts[1] == "preview"
            && LaunchpadCatalog.Find(parts[3]) != null)
        {
            await PreviewAsync(chatId, postId, parts[3], language, cancellationToken);
            return;
        }

        // Chặn cả nút cũ còn sót lại để chắc chắn không thể tạo token thủ công.
        if (!enableManualTokenCreation)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualCreationDisabled"),
                cancellationToken);
            return;
        }

        if (parts[1] == "chain")
        {
            await ShowLaunchpadsAsync(chatId, postId, parts[3], language, cancellationToken);
            return;
        }

        string[] route = parts[3].Split(',');
        string? anchor = route.Length >= 3 && route[1] == "long" ? route[2] : null;
        int creatorTaxPercent = route.Length >= 3 && route[1] == "fourmeme"
            && int.TryParse(route[2], out int tax) ? tax : 0;
        int workerCount = ReadWorkerCount(parts[1], route);
        if (parts[1] == "market" && route.Length >= 2 && LaunchpadCatalog.IsValid(route[0], route[1]))
        {
            await ShowLongAnchorsAsync(chatId, postId, route[0], route[1], language, cancellationToken);
            return;
        }

        if (parts[1] == "tax" && route.Length >= 2 && route[1] == "fourmeme")
        {
            await ShowCreatorTaxAsync(chatId, postId, route[0], route[1], language, cancellationToken);
            return;
        }

        if (route.Length < 2 || !LaunchpadCatalog.IsValidRoute(route[0], route[1], anchor))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        if (parts[1] == "confirm")
        {
            await ShowConfirmationAsync(chatId, postId, route[0], route[1], anchor, creatorTaxPercent,
                language, cancellationToken);
        }
        else if (parts[1] == "workers" && workerCount > 0)
        {
            await ShowImageChoiceAsync(chatId, postId, route[0], route[1], anchor, creatorTaxPercent,
                workerCount, language, cancellationToken);
        }
        else if (parts[1] == "upload" && workerCount > 0)
        {
            pendingImages[chatId] = new PendingManualImage(postId, route[0], route[1], anchor,
                creatorTaxPercent, workerCount);
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "SendCustomTokenImage"),
                [[new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]],
                cancellationToken);
        }
        else if (parts[1] == "create" && workerCount > 0)
        {
            await CreateAsync(chatId, postId, route[0], route[1], anchor, creatorTaxPercent,
                workerCount, null, language, cancellationToken);
        }
        else
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
        }
    }

    private async Task ShowLaunchpadsAsync(long chatId, string postId, string chain, string language,
        CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        if (network == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = network.Launchpads
            .Select(launchpad => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(launchpad.DisplayName, launchpad.Code == "long"
                    ? "manual:market:" + postId + ":" + chain + "," + launchpad.Code
                    : launchpad.Code == "fourmeme"
                        ? "manual:tax:" + postId + ":" + chain + "," + launchpad.Code
                        : "manual:confirm:" + postId + ":" + chain + "," + launchpad.Code)])
            .ToList();
        buttons.Add([new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]);

        await telegramApi.SendButtonsAsync(chatId,
            text.Get(language, "ChooseLaunchpad", network.DisplayName), buttons, cancellationToken);
    }

    private async Task ShowLongAnchorsAsync(long chatId, string postId, string chain, string dex, string language,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.LongAnchors
            .Select(anchor => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(anchor.DisplayName,
                    "manual:confirm:" + postId + ":" + chain + "," + dex + "," + anchor.Code)])
            .ToList();
        buttons.Add([new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]);

        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseLongAnchor"), buttons,
            cancellationToken);
    }

    private async Task ShowCreatorTaxAsync(long chatId, string postId, string chain, string dex, string language,
        CancellationToken cancellationToken)
    {
        int[] rates = [0, 1, 3, 5, 10];
        List<IReadOnlyList<TelegramInlineButton>> buttons = rates
            .Select(rate => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(rate == 0 ? text.Get(language, "NoCreatorTax") : rate + "%",
                    "manual:confirm:" + postId + ":" + chain + "," + dex + "," + rate)])
            .ToList();
        buttons.Add([new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]);
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseCreatorTax"), buttons,
            cancellationToken);
    }

    private async Task ShowConfirmationAsync(long chatId, string postId, string chain, string dex, string? anchor,
        int creatorTaxPercent, string language, CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await evmWalletService.GetAsync(chatId, cancellationToken);
        TokenCreateSettings? settings = await tokenSettings.GetChainSettingsAsync(chatId, chain,
            cancellationToken);
        if (wallet == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "EvmWalletMissing"),
                cancellationToken);
            return;
        }
        if (settings == null)
        {
            string networkName = LaunchpadCatalog.Find(chain)?.DisplayName ?? chain;
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "SetChainFirst", networkName),
                cancellationToken);
            return;
        }

        LaunchpadNetwork network = LaunchpadCatalog.Find(chain)!;
        LaunchpadInfo launchpad = network.Launchpads.First(item => item.Code == dex);
        if (settings.BuyAmount < network.MinimumBuyAmount)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "MinimumBuyAmount", network.DisplayName,
                    network.MinimumBuyAmount, network.Currency), cancellationToken);
            return;
        }

        string postUrl = "https://x.com/i/status/" + postId;
        bool live = IsLive(dex);
        string confirmationKey = live ? "TokenConfirmation" : "TokenTestConfirmation";
        string message = text.Get(language, confirmationKey, postUrl, network.DisplayName,
            launchpad.DisplayName, settings.BuyAmount.ToString(CultureInfo.InvariantCulture), network.Currency);
        if (dex == "long")
        {
            message += "\n" + text.Get(language, "StockAnchor") + ": " + anchor;
        }
        if (dex == "fourmeme")
        {
            message += "\n" + text.Get(language, "CreatorTax") + ": "
                + (creatorTaxPercent == 0 ? text.Get(language, "NoCreatorTax") : creatorTaxPercent + "%");
        }
        if (dex == "pons")
        {
            message += "\n" + text.Get(language, "CreatorFee") + ": 70%";
        }
        message += "\n\n" + text.Get(language, "ChooseManualWorkerCount");
        string route = BuildRoute(chain, dex, anchor, creatorTaxPercent);
        List<IReadOnlyList<TelegramInlineButton>> buttons = Enumerable.Range(1, workerOptions.MaxWorkers)
            .Select(count => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(text.Get(language, "Worker") + " x" + count,
                    "manual:workers:" + postId + ":" + route + "," + count)])
            .ToList();
        buttons.Add([new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]);
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private async Task ShowImageChoiceAsync(long chatId, string postId, string chain, string dex, string? anchor,
        int creatorTaxPercent, int workerCount, string language, CancellationToken cancellationToken)
    {
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

        string route = BuildRoute(chain, dex, anchor, creatorTaxPercent) + "," + workerCount;
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "UseAutomaticImage"),
                "manual:create:" + postId + ":" + route)],
            [new TelegramInlineButton(text.Get(language, "UploadCustomImage"),
                "manual:upload:" + postId + ":" + route)],
            [new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]
        ];
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseManualImage"), buttons,
            cancellationToken);
    }

    private async Task CreateAsync(long chatId, string postId, string chain, string dex, string? anchor,
        int creatorTaxPercent, int workerCount, byte[]? customImage, string language,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TradingWorkerWallet> workers = await evmWalletService.GetReadyWorkersAsync(chatId,
                workerCount, cancellationToken);
            if (workers.Count != workerCount)
            {
                int missingSlot = Enumerable.Range(1, workerCount)
                    .First(slot => workers.All(worker => worker.SlotNumber != slot));
                await telegramApi.SendMessageAsync(chatId,
                    text.Get(language, "ConfigureWorkerFirst", missingSlot), cancellationToken);
                return;
            }

            XStreamPostResponse response = await xApiClient.GetPostAsync(postId, cancellationToken);
            XPost post = response.Data!;
            string? referenceType = post.ReferencedPosts?.FirstOrDefault()?.Type;
            if (referenceType == "retweeted")
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "RepostTokenSkipped"),
                    cancellationToken);
                return;
            }

            XNotificationContent content = XNotificationMessage.Create("x", response, text, language);
            if (!TokenPostContext.IsMeaningfulReply(response, content.OwnPhotoUrl))
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "SimpleReplyTokenSkipped"),
                    cancellationToken);
                return;
            }

            string tokenText = TokenPostContext.BuildAiInput(response);
            string? username = TokenPostContext.GetAuthorUsername(response);
            bool queued = await tokenCreation.QueueManualAsync(chatId, postId, username, tokenText,
                post.Language, content.OwnPhotoUrl, content.PostUrl, chain, dex, anchor, creatorTaxPercent,
                workerCount, customImage, language, cancellationToken);
            if (!queued)
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualAlreadyRunning"),
                    cancellationToken);
                return;
            }

            bool live = IsLive(dex);
            string queuedText = live ? "ManualQueued" : "TokenTestStarted";
            string message = text.Get(language, queuedText) + "\n"
                + text.Get(language, "ParallelWallets") + ": " + workerCount;
            await telegramApi.SendMessageAsync(chatId, message, cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "ManualPostFailed", exception.Message), cancellationToken);
        }
    }

    public async Task<bool> HandlePhotoAsync(TelegramMessage message, string language,
        CancellationToken cancellationToken)
    {
        if (!pendingImages.TryRemove(message.Chat.Id, out PendingManualImage? pending))
        {
            return false;
        }

        TelegramPhotoSize? photo = message.Photo?
            .OrderByDescending(item => (long)item.Width * item.Height)
            .FirstOrDefault();
        if (photo == null)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "CustomImageInvalid"), cancellationToken);
            return true;
        }

        try
        {
            byte[] image = await telegramApi.DownloadPhotoAsync(photo.FileId, cancellationToken);
            await CreateAsync(message.Chat.Id, pending.PostId, pending.Chain, pending.Dex, pending.Anchor,
                pending.CreatorTaxPercent, pending.WorkerCount, image, language, cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "CustomImageFailed", exception.Message), cancellationToken);
        }
        return true;
    }

    private int ReadWorkerCount(string action, string[] route)
    {
        if (action is not ("workers" or "upload" or "create"))
        {
            return 1;
        }

        return int.TryParse(route[^1], out int count) && count >= 1 && count <= workerOptions.MaxWorkers
            ? count
            : 0;
    }

    private static string BuildRoute(string chain, string dex, string? anchor, int creatorTaxPercent)
    {
        return chain + "," + dex
            + (anchor != null ? "," + anchor : dex == "fourmeme" ? "," + creatorTaxPercent : string.Empty);
    }

    // Lấy dữ liệu thật từ link X rồi chạy phần tạo ảnh và metadata, không gọi launchpad.
    private async Task PreviewAsync(long chatId, string postId, string chain, string language,
        CancellationToken cancellationToken)
    {
        try
        {
            XStreamPostResponse response = await xApiClient.GetPostAsync(postId, cancellationToken);
            XPost post = response.Data!;
            string? referenceType = post.ReferencedPosts?.FirstOrDefault()?.Type;
            if (referenceType == "retweeted")
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "RepostTokenSkipped"),
                    cancellationToken);
                return;
            }

            XNotificationContent content = XNotificationMessage.Create("x", response, text, language);
            if (!TokenPostContext.IsMeaningfulReply(response, content.OwnPhotoUrl))
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "SimpleReplyTokenSkipped"),
                    cancellationToken);
                return;
            }

            string tokenText = TokenPostContext.BuildAiInput(response);
            string aiText = AddSourceLanguage(tokenText, post.Language);
            string? username = TokenPostContext.GetAuthorUsername(response);
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;

            // Post có ảnh riêng dùng nguyên ảnh; Post chỉ có chữ thì tạo ảnh mới bằng Flux.
            TokenPreviewDto preview = content.OwnPhotoUrl != null
                ? await tokenPreviewService.CreateWithOriginalImageAsync(aiText, content.OwnPhotoUrl,
                    startedAt, chain, false, cancellationToken)
                : await tokenPreviewService.CreateAsync(aiText, null, startedAt, chain, username, false,
                    cancellationToken);

            string source = text.Get(language, preview.UsedSourceImage ? "PostImage" : "PostText");
            string caption = text.Get(language, "PreviewCaption", preview.Draft.Name, preview.Draft.Symbol,
                preview.Draft.Description, source, preview.OpenAiSeconds.ToString("0.00"),
                preview.FluxSeconds.ToString("0.00"), preview.TotalSeconds.ToString("0.00"));

            await telegramApi.SendPhotoAsync(chatId, preview.Image, caption, cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "TokenPreviewFailed", exception.Message), cancellationToken);
        }
    }

    // Gửi ngôn ngữ do X nhận diện để AI không tự dịch tên token.
    private static string AddSourceLanguage(string postText, string? sourceLanguage)
    {
        return string.IsNullOrWhiteSpace(sourceLanguage)
            ? postText
            : "[SOURCE_LANGUAGE=" + sourceLanguage.ToLowerInvariant() + "]\n" + postText;
    }

    private bool IsLive(string launchpad)
    {
        if (launchpad == "fourmeme")
        {
            return fourMemeOptions.EnableRealTransactions;
        }

        if (launchpad == "dyorswap")
        {
            return dyorStableOptions.EnableRealTransactions;
        }

        return launchpad == "pons"
            ? ponsRobinhoodOptions.EnableRealTransactions
            : longRobinhoodOptions.EnableRealTransactions;
    }

    private sealed record PendingManualImage(string PostId, string Chain, string Dex, string? Anchor,
        int CreatorTaxPercent, int WorkerCount);
}
