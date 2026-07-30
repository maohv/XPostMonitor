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
    private readonly FlapOptions flapOptions;
    private readonly FlapRobinhoodOptions flapRobinhoodOptions;
    private readonly DyorStableOptions dyorStableOptions;
    private readonly LongRobinhoodOptions longRobinhoodOptions;
    private readonly PonsRobinhoodOptions ponsRobinhoodOptions;
    private readonly BotTextService text;
    private readonly TokenPreviewService tokenPreviewService;
    private readonly TradingWorkersOptions workerOptions;
    private readonly bool enableManualTokenCreation;
    private readonly ConcurrentDictionary<long, PendingManualImage> pendingImages = new();
    private readonly ConcurrentDictionary<(long ChatId, string PostId), ManualTokenSelection> selections = new();

    public ManualTokenMenuService(TelegramApiClient telegramApi, XApiClient xApiClient,
        TokenSettingsService tokenSettings, TokenCreationService tokenCreation,
        EvmWalletService evmWalletService, FourMemeOptions fourMemeOptions,
        FlapOptions flapOptions, FlapRobinhoodOptions flapRobinhoodOptions,
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
        this.flapOptions = flapOptions;
        this.flapRobinhoodOptions = flapRobinhoodOptions;
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

        if (enableManualTokenCreation)
        {
            foreach ((long ChatId, string PostId) key in selections.Keys.Where(key => key.ChatId == chatId))
            {
                selections.TryRemove(key, out _);
            }

            ManualTokenSelection selection = new ManualTokenSelection();
            selections[(chatId, postId)] = selection;
            await ShowAllChoicesAsync(chatId, postId, selection, language, cancellationToken);
            return;
        }

        string action = "preview";
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
        if (data == "manual:cancel")
        {
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            pendingImages.TryRemove(chatId, out _);
            foreach ((long ChatId, string PostId) key in selections.Keys.Where(key => key.ChatId == chatId))
            {
                selections.TryRemove(key, out _);
            }
            return;
        }

        string[] parts = data.Split(':');
        if (parts.Length != 4 || !XPostLinkParser.IsPostId(parts[2]))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        string postId = parts[2];
        if (parts[1] == "panel")
        {
            await HandlePanelAsync(chatId, messageId, postId, parts[3], language, cancellationToken);
            return;
        }

        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);

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
        int creatorTaxPercent = route.Length >= 3 && LaunchpadCatalog.SupportsCreatorTax(route[1])
            && int.TryParse(route[2], out int tax) ? tax : 0;
        int workerCount = ReadWorkerCount(parts[1], route);
        if (parts[1] == "market" && route.Length >= 2 && LaunchpadCatalog.IsValid(route[0], route[1]))
        {
            await ShowLongAnchorsAsync(chatId, postId, route[0], route[1], language, cancellationToken);
            return;
        }

        if (parts[1] == "tax" && route.Length >= 2 && LaunchpadCatalog.SupportsCreatorTax(route[1]))
        {
            await ShowCreatorTaxAsync(chatId, postId, route[0], route[1], language, cancellationToken);
            return;
        }

        if (route.Length < 2 || !LaunchpadCatalog.IsValidRoute(route[0], route[1], anchor)
            || !LaunchpadCatalog.IsValidCreatorTax(route[1], creatorTaxPercent))
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
            IReadOnlyList<TradingWorkerWallet> selectedWorkers = await evmWalletService
                .GetReadyWorkersAsync(chatId, workerCount, cancellationToken);
            if (selectedWorkers.Count != workerCount)
            {
                int missingSlot = Enumerable.Range(1, workerOptions.MaxWorkers)
                    .First(slot => selectedWorkers.All(worker => worker.SlotNumber != slot));
                await telegramApi.SendMessageAsync(chatId,
                    text.Get(language, "ConfigureWorkerFirst", missingSlot), cancellationToken);
                return;
            }
            pendingImages[chatId] = new PendingManualImage(postId, route[0], route[1], anchor,
                creatorTaxPercent, selectedWorkers.Select(worker => worker.SlotNumber).ToArray());
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

    private async Task HandlePanelAsync(long chatId, long messageId, string postId, string action, string language,
        CancellationToken cancellationToken)
    {
        if (!selections.TryGetValue((chatId, postId), out ManualTokenSelection? selection))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        if (action == "go")
        {
            if (!IsComplete(selection))
            {
                await ShowAllChoicesAsync(chatId, postId, selection, language, cancellationToken, true,
                    messageId);
                return;
            }

            selections.TryRemove((chatId, postId), out _);
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            await CreateAsync(chatId, postId, selection.Chain!, selection.Dex!, selection.Anchor,
                selection.CreatorTaxPercent!.Value, selection.WorkerSlots, null, language, cancellationToken);
            return;
        }

        if (action == "i=u")
        {
            if (!IsReadyForCustomImage(selection))
            {
                await ShowAllChoicesAsync(chatId, postId, selection, language, cancellationToken, true,
                    messageId);
                return;
            }

            selections.TryRemove((chatId, postId), out _);
            pendingImages[chatId] = new PendingManualImage(postId, selection.Chain!, selection.Dex!,
                selection.Anchor, selection.CreatorTaxPercent!.Value,
                selection.WorkerSlots.OrderBy(slot => slot).ToArray());
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "SendCustomTokenImage"),
                [[new TelegramInlineButton("X " + text.Get(language, "Cancel"), "manual:cancel")]],
                cancellationToken);
            return;
        }

        string[] value = action.Split('=', 2);
        if (value.Length != 2)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        if (value[0] == "c")
        {
            LaunchpadNetwork? network = LaunchpadCatalog.Find(value[1]);
            if (network != null)
            {
                selection.Chain = network.Chain;
                selection.Dex = null;
                selection.Anchor = null;
                selection.CreatorTaxPercent = null;
            }
        }
        else if (value[0] == "d" && selection.Chain != null
            && LaunchpadCatalog.IsValid(selection.Chain, value[1]))
        {
            selection.Dex = value[1];
            selection.Anchor = null;
            selection.CreatorTaxPercent = LaunchpadCatalog.SupportsCreatorTax(selection.Dex) ? null : 0;
        }
        else if (value[0] == "t" && int.TryParse(value[1], out int tax)
            && LaunchpadCatalog.IsValidCreatorTax(selection.Dex, tax))
        {
            selection.CreatorTaxPercent = tax;
        }
        else if (value[0] == "w" && int.TryParse(value[1], out int workerSlot)
            && workerSlot >= 1 && workerSlot <= workerOptions.MaxWorkers)
        {
            if (!selection.WorkerSlots.Add(workerSlot))
            {
                selection.WorkerSlots.Remove(workerSlot);
            }
        }
        else if (value[0] == "i")
        {
            selection.UseCustomImage = false;
        }
        else if (value[0] == "a" && selection.Dex == "long"
            && LaunchpadCatalog.FindLongAnchor(value[1]) != null)
        {
            selection.Anchor = value[1];
        }
        else if (value[0] == "p" && LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Dex)
            && LaunchpadCatalog.FindFlapBscPaymentToken(value[1]) != null)
        {
            selection.Anchor = value[1] == "BNB" ? null : value[1];
        }

        await ShowAllChoicesAsync(chatId, postId, selection, language, cancellationToken, false, messageId);
    }

    private async Task ShowAllChoicesAsync(long chatId, string postId, ManualTokenSelection selection,
        string language, CancellationToken cancellationToken, bool showMissingWarning = false,
        long? messageId = null)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(selection.Chain);
        LaunchpadInfo? launchpad = network?.Launchpads.FirstOrDefault(item => item.Code == selection.Dex);
        TokenCreateSettings? settings = network == null ? null
            : await tokenSettings.GetChainSettingsAsync(chatId, network.Chain, cancellationToken);
        string buyCurrency = network?.Currency ?? string.Empty;
        string flapQuoteToken = LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Dex)
            ? LaunchpadCatalog.FindFlapBscPaymentToken(selection.Anchor)!.Code
            : string.Empty;
        string buyAmount = network == null || settings == null
            ? text.Get(language, "NotSet")
            : settings.BuyAmount.ToString(CultureInfo.InvariantCulture) + " " + buyCurrency;
        string image = selection.UseCustomImage.HasValue
            ? text.Get(language, selection.UseCustomImage.Value ? "UploadCustomImage" : "UseAutomaticImage")
            : text.Get(language, "NotSet");

        string message = text.Get(language, showMissingWarning
                ? "ManualSelectionMissing" : "ManualSelectAll") + "\n\n"
            + "Post: https://x.com/i/status/" + postId + "\n"
            + text.Get(language, "Network") + ": " + (network?.DisplayName ?? text.Get(language, "NotSet")) + "\n"
            + text.Get(language, "Launchpad") + ": " + (launchpad?.DisplayName ?? text.Get(language, "NotSet")) + "\n"
            + text.Get(language, "DefaultBuyAmounts") + ": " + buyAmount + "\n";
        if (selection.Dex != null && LaunchpadCatalog.SupportsCreatorTax(selection.Dex))
        {
            string creatorTax = selection.CreatorTaxPercent.HasValue
                ? selection.CreatorTaxPercent.Value + "%" : text.Get(language, "NotSet");
            message += text.Get(language, "CreatorTax") + ": " + creatorTax + "\n";
        }
        if (selection.Dex == "pons")
        {
            message += text.Get(language, "CreatorFee") + ": 70%\n";
        }
        if (selection.Dex == "long")
        {
            message += text.Get(language, "StockAnchor") + ": " + selection.Anchor + "\n";
        }
        if (LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Dex))
        {
            message += text.Get(language, "PaymentToken") + ": " + flapQuoteToken + "\n";
        }
        string selectedWorkers = selection.WorkerSlots.Count == 0
            ? text.Get(language, "NotSet")
            : string.Join(", ", selection.WorkerSlots.OrderBy(slot => slot)
                .Select(slot => text.Get(language, "Worker") + " " + slot));
        message += text.Get(language, "ParallelWallets") + ": " + selectedWorkers + "\n"
            + text.Get(language, "ChooseManualImage") + " " + image;

        List<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            LaunchpadCatalog.All.Select(item => new TelegramInlineButton(
                Mark(item.Chain == selection.Chain, item.DisplayName),
                "manual:panel:" + postId + ":c=" + item.Chain)).ToList()
        ];

        if (network != null)
        {
            buttons.Add(network.Launchpads.Select(item => new TelegramInlineButton(
                Mark(item.Code == selection.Dex, item.DisplayName),
                "manual:panel:" + postId + ":d=" + item.Code)).ToList());
        }

        if (LaunchpadCatalog.SupportsCreatorTax(selection.Dex))
        {
            int[] rates = selection.Dex == "flap" ? [1, 3, 5, 10] : [0, 1, 3, 5, 10];
            buttons.Add(rates.Select(rate => new TelegramInlineButton(
                Mark(rate == selection.CreatorTaxPercent, rate + "%"),
                "manual:panel:" + postId + ":t=" + rate)).ToList());
        }

        if (selection.Dex == "long")
        {
            foreach (LaunchpadAnchor[] anchors in LaunchpadCatalog.LongAnchors.Chunk(2))
            {
                buttons.Add(anchors.Select(anchor => new TelegramInlineButton(
                    Mark(anchor.Code == selection.Anchor, anchor.Code),
                    "manual:panel:" + postId + ":a=" + anchor.Code)).ToList());
            }
        }

        if (LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Dex))
        {
            foreach (FlapPaymentToken[] paymentTokens in LaunchpadCatalog.FlapBscPaymentTokens.Chunk(2))
            {
                buttons.Add(paymentTokens.Select(paymentToken => new TelegramInlineButton(
                    Mark(paymentToken.Code == flapQuoteToken, paymentToken.Code),
                    "manual:panel:" + postId + ":p=" + paymentToken.Code)).ToList());
            }
        }

        buttons.Add(Enumerable.Range(1, workerOptions.MaxWorkers)
            .Select(slot => new TelegramInlineButton(
                Mark(selection.WorkerSlots.Contains(slot), text.Get(language, "Worker") + " " + slot),
                "manual:panel:" + postId + ":w=" + slot)).ToList());
        buttons.Add(
        [
            new TelegramInlineButton(Mark(selection.UseCustomImage == false,
                text.Get(language, "UseAutomaticImage")), "manual:panel:" + postId + ":i=a"),
            new TelegramInlineButton(Mark(selection.UseCustomImage == true,
                text.Get(language, "UploadCustomImage")), "manual:panel:" + postId + ":i=u")
        ]);
        buttons.Add(
        [
            new TelegramInlineButton(text.Get(language, "CreateRealToken"),
                "manual:panel:" + postId + ":go"),
            new TelegramInlineButton("X " + text.Get(language, "Cancel"), "manual:cancel")
        ]);

        if (messageId.HasValue)
        {
            await telegramApi.EditButtonsAsync(chatId, messageId.Value, message, buttons, cancellationToken);
            return;
        }

        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private static string Mark(bool selected, string label)
    {
        return selected ? "✅ " + label : label;
    }

    private static bool IsComplete(ManualTokenSelection selection)
    {
        return IsReadyForCustomImage(selection)
            && selection.UseCustomImage == false;
    }

    private static bool IsReadyForCustomImage(ManualTokenSelection selection)
    {
        return selection.Chain != null
            && selection.Dex != null
            && LaunchpadCatalog.IsValidRoute(selection.Chain, selection.Dex, selection.Anchor)
            && selection.CreatorTaxPercent.HasValue
            && LaunchpadCatalog.IsValidCreatorTax(selection.Dex, selection.CreatorTaxPercent.Value)
            && selection.WorkerSlots.Count > 0;
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
                    : LaunchpadCatalog.SupportsCreatorTax(launchpad.Code)
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
        int[] rates = dex == "flap" ? [1, 3, 5, 10] : [0, 1, 3, 5, 10];
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
        decimal minimumBuyAmount = chain == "bsc" ? 0m : network.MinimumBuyAmount;
        if (settings.BuyAmount <= 0 || settings.BuyAmount < minimumBuyAmount)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "MinimumBuyAmount", network.DisplayName,
                    minimumBuyAmount, network.Currency), cancellationToken);
            return;
        }

        string postUrl = "https://x.com/i/status/" + postId;
        bool live = IsLive(chain, dex);
        string confirmationKey = live ? "TokenConfirmation" : "TokenTestConfirmation";
        string message = text.Get(language, confirmationKey, postUrl, network.DisplayName,
            launchpad.DisplayName, settings.BuyAmount.ToString(CultureInfo.InvariantCulture), network.Currency);
        if (dex == "long")
        {
            message += "\n" + text.Get(language, "StockAnchor") + ": " + anchor;
        }
        if (LaunchpadCatalog.SupportsCreatorTax(dex))
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
        IReadOnlyList<TradingWorkerWallet> workers = await evmWalletService.GetReadyWorkersAsync(chatId,
            workerCount, cancellationToken);
        if (workers.Count != workerCount)
        {
            int missingSlot = Enumerable.Range(1, workerOptions.MaxWorkers)
                .First(slot => workers.All(worker => worker.SlotNumber != slot));
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "ConfigureWorkerFirst", missingSlot), cancellationToken);
            return;
        }
        await CreateAsync(chatId, postId, chain, dex, anchor, creatorTaxPercent,
            workers.Select(worker => worker.SlotNumber).ToArray(), customImage, language, cancellationToken);
    }

    private async Task CreateAsync(long chatId, string postId, string chain, string dex, string? anchor,
        int creatorTaxPercent, IReadOnlyCollection<int> workerSlots, byte[]? customImage, string language,
        CancellationToken cancellationToken)
    {
        try
        {
            int[] selectedSlots = workerSlots.Distinct().OrderBy(slot => slot).ToArray();
            IReadOnlyList<TradingWorkerWallet> workers = await evmWalletService.GetReadyWorkersAsync(chatId,
                selectedSlots, cancellationToken);
            if (workers.Count != selectedSlots.Length)
            {
                int missingSlot = selectedSlots.First(slot => workers.All(worker => worker.SlotNumber != slot));
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
                selectedSlots, customImage, language, cancellationToken);
            if (!queued)
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualAlreadyRunning"),
                    cancellationToken);
                return;
            }

            bool live = IsLive(chain, dex);
            string queuedText = live ? "ManualQueued" : "TokenTestStarted";
            string message = text.Get(language, queuedText) + "\n"
                + text.Get(language, "ParallelWallets") + ": "
                + string.Join(", ", selectedSlots.Select(slot => text.Get(language, "Worker") + " " + slot));
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
                pending.CreatorTaxPercent, pending.WorkerSlots, image, language, cancellationToken);
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
            + (anchor != null ? "," + anchor
                : LaunchpadCatalog.SupportsCreatorTax(dex) ? "," + creatorTaxPercent : string.Empty);
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

    private bool IsLive(string chain, string launchpad)
    {
        if (launchpad == "fourmeme")
        {
            return fourMemeOptions.EnableRealTransactions;
        }

        if (launchpad == "flap")
        {
            return chain == "robinhood"
                ? flapRobinhoodOptions.EnableRealTransactions
                : flapOptions.EnableRealTransactions;
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
        int CreatorTaxPercent, IReadOnlyCollection<int> WorkerSlots);

    private sealed class ManualTokenSelection
    {
        public string? Chain { get; set; }
        public string? Dex { get; set; }
        public string? Anchor { get; set; }
        public int? CreatorTaxPercent { get; set; }
        public HashSet<int> WorkerSlots { get; } = [];
        public bool? UseCustomImage { get; set; }
    }
}
