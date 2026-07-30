using System.Collections.Concurrent;
using XPostMonitor.Dtos;
using XPostMonitor.Configuration;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.X;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;
using XPostMonitor.Services.Gmgn;

namespace XPostMonitor.Services.Telegram;

// Hướng dẫn người dùng chọn network và launchpad khi thêm tài khoản X.
public sealed class WatchlistMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly WatchlistService watchlistService;
    private readonly EvmWalletService evmWalletService;
    private readonly AutoTradingSettingsService autoTradingSettings;
    private readonly BotTextService text;
    private readonly TradingWorkersOptions workerOptions;
    private readonly ConcurrentDictionary<(long ChatId, string Username), WatchlistEditSelection>
        editSelections = new();

    public WatchlistMenuService(TelegramApiClient telegramApi, WatchlistService watchlistService,
        EvmWalletService evmWalletService, AutoTradingSettingsService autoTradingSettings, BotTextService text,
        TradingWorkersOptions workerOptions)
    {
        this.telegramApi = telegramApi;
        this.watchlistService = watchlistService;
        this.evmWalletService = evmWalletService;
        this.autoTradingSettings = autoTradingSettings;
        this.text = text;
        this.workerOptions = workerOptions;
    }

    // Bắt đầu lệnh /add bằng danh sách network dễ chọn.
    public async Task StartAddAsync(long chatId, string? username, string language,
        CancellationToken cancellationToken)
    {
        username = username?.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username) || username.Length > 15
            || !username.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "AddUsage"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.All
            .Select(network => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(network.DisplayName, "watch:chain:" + username + ":" + network.Chain)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "AlertsOnly"), "watch:save:" + username + ":none,none")]);
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);

        await telegramApi.SendButtonsAsync(chatId,
            text.Get(language, "ChooseDestination", username), buttons, cancellationToken);
    }

    // Hiển thị danh sách theo dõi kèm nút sửa cho từng tài khoản.
    public async Task ShowListAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        string message = await watchlistService.ListAsync(chatId, language, cancellationToken);
        List<string> usernames = await watchlistService.GetUsernamesAsync(chatId, cancellationToken);
        List<IReadOnlyList<TelegramInlineButton>> buttons = usernames
            .Select(username => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(text.Get(language, "EditAccount", username),
                    "watch:edit:" + username + ":open")])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Close"), "watch:cancel")]);
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    // Xử lý từng nút của quy trình /add mà không cần lưu trạng thái tạm vào database.
    public async Task HandleCallbackAsync(long chatId, long messageId, string data, string language,
        CancellationToken cancellationToken)
    {
        string[] parts = data.Split(':');
        if (parts.Length < 2)
        {
            return;
        }

        if (parts.Length == 4 && parts[1] == "panel")
        {
            await HandleEditPanelAsync(chatId, messageId, parts[2], parts[3], language, cancellationToken);
            return;
        }

        if (parts[1] == "cancel")
        {
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            foreach ((long ChatId, string Username) key in editSelections.Keys
                         .Where(key => key.ChatId == chatId))
            {
                editSelections.TryRemove(key, out _);
            }
            return;
        }

        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);

        if (parts.Length != 4)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "SelectionExpired"), cancellationToken);
            return;
        }

        string username = parts[2];
        string chain = parts[3];

        if (parts[1] == "edit")
        {
            await StartEditAsync(chatId, username, language, cancellationToken);
            return;
        }

        if (parts[1] == "chain")
        {
            await ShowLaunchpadsAsync(chatId, username, chain, language, cancellationToken);
            return;
        }

        if (parts[1] == "dex")
        {
            string[] values = chain.Split(',', 3);
            if (values.Length >= 2)
            {
                string? anchor = values.Length == 3 ? values[2] : null;
                if (LaunchpadCatalog.IsFlapBsc(values[0], values[1]))
                {
                    await ShowFlapPaymentTokensAsync(chatId, username, language, cancellationToken);
                }
                else if (LaunchpadCatalog.SupportsCreatorTax(values[1]))
                {
                    await ShowCreatorTaxAsync(chatId, username, values[0], values[1], null, language,
                        cancellationToken);
                }
                else
                {
                    await ShowAutoTradingAsync(chatId, username, values[0], values[1], anchor, 0,
                        language, cancellationToken);
                }
            }
            return;
        }

        if (parts[1] == "payment")
        {
            string[] values = chain.Split(',', 3);
            if (values.Length == 3 && LaunchpadCatalog.IsFlapBsc(values[0], values[1])
                && LaunchpadCatalog.FindFlapBscPaymentToken(values[2]) != null)
            {
                await ShowCreatorTaxAsync(chatId, username, values[0], values[1], values[2], language,
                    cancellationToken);
            }
            return;
        }

        if (parts[1] == "tax")
        {
            string[] values = chain.Split(',', 4);
            int taxIndex = values.Length == 4 ? 3 : 2;
            if (values.Length >= 3 && int.TryParse(values[taxIndex], out int creatorTaxPercent))
            {
                string? anchor = values.Length == 4 ? values[2] : null;
                await ShowAutoTradingAsync(chatId, username, values[0], values[1], anchor,
                    creatorTaxPercent, language, cancellationToken);
            }
            return;
        }

        if (parts[1] == "market")
        {
            string[] values = chain.Split(',', 2);
            if (values.Length == 2)
            {
                await ShowLongAnchorsAsync(chatId, username, values[0], values[1], language, cancellationToken);
            }
            return;
        }

        if (parts[1] == "trade")
        {
            string[] values = chain.Split(',', 5);
            if (values.Length == 5 && int.TryParse(values[3], out int creatorTaxPercent)
                && int.TryParse(values[4], out int autoTrading))
            {
                string? option = values[2] == "none" ? null : values[2];
                string? anchor = values[1] == "long" || LaunchpadCatalog.IsFlapBsc(values[0], values[1])
                    ? option : null;
                await ShowWorkerCountAsync(chatId, username, values[0], values[1], anchor,
                    creatorTaxPercent, autoTrading == 1, language, cancellationToken);
            }
            return;
        }

        if (parts[1] == "workers")
        {
            string[] values = chain.Split(',', 6);
            if (values.Length == 6 && int.TryParse(values[3], out int creatorTaxPercent)
                && int.TryParse(values[4], out int autoTrading)
                && int.TryParse(values[5], out int workerCount) && workerCount >= 1
                && workerCount <= workerOptions.MaxWorkers)
            {
                IReadOnlyList<TradingWorkerWallet> readyWorkers = await evmWalletService
                    .GetReadyWorkersAsync(chatId, workerCount, cancellationToken);
                if (readyWorkers.Count != workerCount)
                {
                    int missingSlot = Enumerable.Range(1, workerCount)
                        .First(slot => readyWorkers.All(worker => worker.SlotNumber != slot));
                    await telegramApi.SendButtonsAsync(chatId,
                        text.Get(language, "ConfigureWorkerFirst", missingSlot),
                        [[new TelegramInlineButton(text.Get(language, "ConfigureWallets"), "settings:wallets")],
                         [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]],
                        cancellationToken);
                    return;
                }

                if (autoTrading == 1)
                {
                    IReadOnlyList<GmgnWorkerState> gmgnStates = await autoTradingSettings
                        .GetWorkerStatesAsync(chatId, cancellationToken);
                    int? missingGmgnSlot = Enumerable.Range(1, workerCount)
                        .FirstOrDefault(slot => !gmgnStates.Any(state => state.SlotNumber == slot
                            && state.HasCredentials));
                    if (missingGmgnSlot > 0)
                    {
                        await telegramApi.SendButtonsAsync(chatId,
                            text.Get(language, "ConfigureGmgnFirst", missingGmgnSlot),
                            [[new TelegramInlineButton(text.Get(language, "ConfigureGmgn"),
                                "trading:worker:" + missingGmgnSlot)],
                             [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]],
                            cancellationToken);
                        return;
                    }
                }

                string? option = values[2] == "none" ? null : values[2];
                string? anchor = values[1] == "long" || LaunchpadCatalog.IsFlapBsc(values[0], values[1])
                    ? option : null;
                await ShowConfirmationAsync(chatId, username, values[0], values[1], anchor,
                    creatorTaxPercent, autoTrading == 1, workerCount, language, cancellationToken);
            }
            return;
        }

        if (parts[1] == "save")
        {
            string[] values = chain.Split(',', 6);
            string? selectedChain = values[0] == "none" ? null : values[0];
            string? selectedDex = values.Length < 2 || values[1] == "none" ? null : values[1];
            string? selectedOption = values.Length >= 3 && values[2] != "none" ? values[2] : null;
            string? selectedAnchor = selectedDex == "long" || LaunchpadCatalog.IsFlapBsc(selectedChain, selectedDex)
                ? selectedOption : null;
            int creatorTaxPercent = LaunchpadCatalog.SupportsCreatorTax(selectedDex) && values.Length >= 4
                && int.TryParse(values[3], out int tax) ? tax : 0;
            bool enableAutoTrading = values.Length >= 5 && values[4] == "1";
            int workerCount = values.Length == 6 && int.TryParse(values[5], out int selectedCount)
                ? Math.Clamp(selectedCount, 1, workerOptions.MaxWorkers) : 1;
            string reply = await watchlistService.AddAsync(chatId, username, selectedChain, selectedDex,
                selectedAnchor, creatorTaxPercent, enableAutoTrading, workerCount, language, cancellationToken);
            await telegramApi.SendMessageAsync(chatId, reply, cancellationToken);
        }
    }

    private async Task StartEditAsync(long chatId, string username, string language,
        CancellationToken cancellationToken)
    {
        WatchlistEditSettings? current = await watchlistService.GetEditSettingsAsync(chatId, username,
            cancellationToken);
        if (current == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "NotMonitoring", username),
                cancellationToken);
            return;
        }

        WatchlistEditSelection selection = new WatchlistEditSelection
        {
            Chain = current.Chain,
            Dex = current.Dex,
            Anchor = current.Anchor,
            CreatorTaxPercent = current.CreatorTaxPercent,
            EnableAutoTrading = current.EnableAutoTrading,
            WorkerCount = Math.Clamp(current.WorkerCount, 1, workerOptions.MaxWorkers)
        };
        editSelections[(chatId, current.Username.ToLowerInvariant())] = selection;
        await ShowEditPanelAsync(chatId, current.Username, selection, language, cancellationToken);
    }

    private async Task HandleEditPanelAsync(long chatId, long messageId, string username, string action,
        string language, CancellationToken cancellationToken)
    {
        string keyUsername = username.ToLowerInvariant();
        if (!editSelections.TryGetValue((chatId, keyUsername), out WatchlistEditSelection? selection))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "SelectionExpired"),
                cancellationToken);
            return;
        }

        if (action == "save")
        {
            if (!IsValidEditSelection(selection))
            {
                await ShowEditPanelAsync(chatId, username, selection, language, cancellationToken,
                    text.Get(language, "EditSelectionMissing"), messageId);
                return;
            }

            if (selection.Chain != null)
            {
                IReadOnlyList<TradingWorkerWallet> wallets = await evmWalletService
                    .GetReadyWorkersAsync(chatId, selection.WorkerCount, cancellationToken);
                if (wallets.Count != selection.WorkerCount)
                {
                    int missingSlot = Enumerable.Range(1, selection.WorkerCount)
                        .First(slot => wallets.All(wallet => wallet.SlotNumber != slot));
                    await ShowEditPanelAsync(chatId, username, selection, language, cancellationToken,
                        text.Get(language, "ConfigureWorkerFirst", missingSlot), messageId);
                    return;
                }

                if (selection.EnableAutoTrading)
                {
                    IReadOnlyList<GmgnWorkerState> gmgnStates = await autoTradingSettings
                        .GetWorkerStatesAsync(chatId, cancellationToken);
                    int? missingSlot = Enumerable.Range(1, selection.WorkerCount)
                        .FirstOrDefault(slot => !gmgnStates.Any(state =>
                            state.SlotNumber == slot && state.HasCredentials));
                    if (missingSlot > 0)
                    {
                        await ShowEditPanelAsync(chatId, username, selection, language, cancellationToken,
                            text.Get(language, "ConfigureGmgnFirst", missingSlot), messageId);
                        return;
                    }
                }
            }

            string reply = await watchlistService.AddAsync(chatId, username, selection.Chain, selection.Dex,
                selection.Anchor, selection.CreatorTaxPercent ?? 0, selection.EnableAutoTrading,
                selection.WorkerCount, language, cancellationToken);
            editSelections.TryRemove((chatId, keyUsername), out _);
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            await telegramApi.SendMessageAsync(chatId, reply, cancellationToken);
            return;
        }

        string[] value = action.Split('=', 2);
        if (value.Length != 2)
        {
            return;
        }

        if (value[0] == "c")
        {
            if (value[1] == "alerts")
            {
                selection.Chain = null;
                selection.Dex = null;
                selection.Anchor = null;
                selection.CreatorTaxPercent = 0;
                selection.EnableAutoTrading = false;
                selection.WorkerCount = 1;
            }
            else if (LaunchpadCatalog.Find(value[1]) is LaunchpadNetwork network)
            {
                selection.Chain = network.Chain;
                selection.Dex = null;
                selection.Anchor = null;
                selection.CreatorTaxPercent = null;
                selection.EnableAutoTrading = false;
            }
        }
        else if (value[0] == "d" && selection.Chain != null
            && LaunchpadCatalog.IsValid(selection.Chain, value[1]))
        {
            selection.Dex = value[1];
            selection.Anchor = null;
            selection.CreatorTaxPercent = LaunchpadCatalog.SupportsCreatorTax(value[1]) ? null : 0;
        }
        else if (value[0] == "t" && int.TryParse(value[1], out int tax)
            && LaunchpadCatalog.IsValidCreatorTax(selection.Dex, tax))
        {
            selection.CreatorTaxPercent = tax;
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
        else if (value[0] == "g" && value[1] is "0" or "1" && selection.Chain != "stable")
        {
            selection.EnableAutoTrading = value[1] == "1";
        }
        else if (value[0] == "w" && int.TryParse(value[1], out int workerCount)
            && workerCount >= 1 && workerCount <= workerOptions.MaxWorkers)
        {
            selection.WorkerCount = workerCount;
        }

        await ShowEditPanelAsync(chatId, username, selection, language, cancellationToken,
            messageId: messageId);
    }

    private async Task ShowEditPanelAsync(long chatId, string username, WatchlistEditSelection selection,
        string language, CancellationToken cancellationToken, string? notice = null, long? messageId = null)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(selection.Chain);
        LaunchpadInfo? launchpad = network?.Launchpads.FirstOrDefault(item => item.Code == selection.Dex);
        string message = (notice == null ? text.Get(language, "EditWatchlist") : notice) + "\n\n"
            + text.Get(language, "Account") + ": @" + username + "\n"
            + text.Get(language, "Network") + ": "
            + (selection.Chain == null ? text.Get(language, "AlertsOnly")
                : network?.DisplayName ?? text.Get(language, "NotSet")) + "\n"
            + text.Get(language, "Launchpad") + ": "
            + (selection.Chain == null ? "-" : launchpad?.DisplayName ?? text.Get(language, "NotSet")) + "\n";
        if (LaunchpadCatalog.SupportsCreatorTax(selection.Dex))
        {
            message += text.Get(language, "CreatorTax") + ": "
                + (selection.CreatorTaxPercent.HasValue
                    ? selection.CreatorTaxPercent + "%" : text.Get(language, "NotSet")) + "\n";
        }
        if (selection.Dex == "long")
        {
            message += text.Get(language, "StockAnchor") + ": "
                + (selection.Anchor ?? text.Get(language, "NotSet")) + "\n";
        }
        if (LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Dex))
        {
            message += text.Get(language, "PaymentToken") + ": "
                + LaunchpadCatalog.FindFlapBscPaymentToken(selection.Anchor)!.Code + "\n";
        }
        if (selection.Chain != null)
        {
            message += text.Get(language, "AutoTrading") + ": "
                + text.Get(language, selection.EnableAutoTrading ? "Enabled" : "Disabled") + "\n"
                + text.Get(language, "TokensPerPost") + ": " + selection.WorkerCount;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [
                new TelegramInlineButton(Mark(selection.Chain == null, text.Get(language, "AlertsOnly")),
                    "watch:panel:" + username + ":c=alerts"),
                .. LaunchpadCatalog.All.Select(item => new TelegramInlineButton(
                    Mark(item.Chain == selection.Chain, item.DisplayName),
                    "watch:panel:" + username + ":c=" + item.Chain))
            ]
        ];

        if (network != null)
        {
            buttons.Add(network.Launchpads.Select(item => new TelegramInlineButton(
                Mark(item.Code == selection.Dex, item.DisplayName),
                "watch:panel:" + username + ":d=" + item.Code)).ToList());
        }
        if (LaunchpadCatalog.SupportsCreatorTax(selection.Dex))
        {
            int[] rates = selection.Dex == "flap" ? [1, 3, 5, 10] : [0, 1, 3, 5, 10];
            buttons.Add(rates.Select(rate => new TelegramInlineButton(
                Mark(rate == selection.CreatorTaxPercent, rate + "%"),
                "watch:panel:" + username + ":t=" + rate)).ToList());
        }
        if (selection.Dex == "long")
        {
            foreach (LaunchpadAnchor[] anchors in LaunchpadCatalog.LongAnchors.Chunk(2))
            {
                buttons.Add(anchors.Select(anchor => new TelegramInlineButton(
                    Mark(anchor.Code == selection.Anchor, anchor.Code),
                    "watch:panel:" + username + ":a=" + anchor.Code)).ToList());
            }
        }
        if (LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Dex))
        {
            string selectedPayment = LaunchpadCatalog.FindFlapBscPaymentToken(selection.Anchor)!.Code;
            foreach (FlapPaymentToken[] paymentTokens in LaunchpadCatalog.FlapBscPaymentTokens.Chunk(2))
            {
                buttons.Add(paymentTokens.Select(paymentToken => new TelegramInlineButton(
                    Mark(paymentToken.Code == selectedPayment, paymentToken.Code),
                    "watch:panel:" + username + ":p=" + paymentToken.Code)).ToList());
            }
        }
        if (selection.Chain != null)
        {
            if (selection.Chain != "stable")
            {
                buttons.Add(
                [
                    new TelegramInlineButton(Mark(selection.EnableAutoTrading,
                        text.Get(language, "EnableAutoTrading")), "watch:panel:" + username + ":g=1"),
                    new TelegramInlineButton(Mark(!selection.EnableAutoTrading,
                        text.Get(language, "DisableAutoTrading")), "watch:panel:" + username + ":g=0")
                ]);
            }
            else
            {
                buttons.Add([new TelegramInlineButton("✅ " + text.Get(language, "DisableAutoTrading"),
                    "watch:panel:" + username + ":g=0")]);
            }
            buttons.Add(Enumerable.Range(1, workerOptions.MaxWorkers)
                .Select(count => new TelegramInlineButton(
                    Mark(count == selection.WorkerCount, text.Get(language, "Worker") + " x" + count),
                    "watch:panel:" + username + ":w=" + count)).ToList());
        }
        buttons.Add(
        [
            new TelegramInlineButton(text.Get(language, "SaveChanges"),
                "watch:panel:" + username + ":save"),
            new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")
        ]);

        if (messageId.HasValue)
        {
            await telegramApi.EditButtonsAsync(chatId, messageId.Value, message, buttons, cancellationToken);
            return;
        }
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private static bool IsValidEditSelection(WatchlistEditSelection selection)
    {
        if (selection.Chain == null)
        {
            return true;
        }
        return selection.Dex != null
            && LaunchpadCatalog.IsValidRoute(selection.Chain, selection.Dex, selection.Anchor)
            && selection.CreatorTaxPercent.HasValue
            && LaunchpadCatalog.IsValidCreatorTax(selection.Dex, selection.CreatorTaxPercent.Value)
            && selection.WorkerCount > 0;
    }

    private static string Mark(bool selected, string label)
    {
        return selected ? "✅ " + label : label;
    }

    private async Task ShowLaunchpadsAsync(long chatId, string username, string chain, string language,
        CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        if (network == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "UnsupportedNetwork"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = network.Launchpads
            .Select(launchpad => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(launchpad.DisplayName, launchpad.Code == "long"
                    ? "watch:market:" + username + ":" + network.Chain + "," + launchpad.Code
                    : "watch:dex:" + username + ":" + network.Chain + "," + launchpad.Code)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);

        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseLaunchpad", network.DisplayName),
            buttons, cancellationToken);
    }

    private async Task ShowLongAnchorsAsync(long chatId, string username, string chain, string dex, string language,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.LongAnchors
            .Select(anchor => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(anchor.DisplayName,
                    "watch:dex:" + username + ":" + chain + "," + dex + "," + anchor.Code)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);

        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseLongAnchor"), buttons,
            cancellationToken);
    }

    private async Task ShowCreatorTaxAsync(long chatId, string username, string chain, string dex, string? option,
        string language, CancellationToken cancellationToken)
    {
        int[] rates = dex == "flap" ? [1, 3, 5, 10] : [0, 1, 3, 5, 10];
        List<IReadOnlyList<TelegramInlineButton>> buttons = rates
            .Select(rate => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(rate == 0 ? text.Get(language, "NoCreatorTax") : rate + "%",
                    "watch:tax:" + username + ":" + chain + "," + dex
                    + (option == null ? string.Empty : "," + option) + "," + rate)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseCreatorTax"), buttons,
            cancellationToken);
    }

    private async Task ShowFlapPaymentTokensAsync(long chatId, string username, string language,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.FlapBscPaymentTokens
            .Chunk(2)
            .Select(row => (IReadOnlyList<TelegramInlineButton>)row
                .Select(token => new TelegramInlineButton(token.Code,
                    "watch:payment:" + username + ":bsc,flap," + token.Code))
                .ToList())
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseFlapPaymentToken"), buttons,
            cancellationToken);
    }

    private async Task ShowConfirmationAsync(long chatId, string username, string chain, string dex, string? anchor,
        int creatorTaxPercent, bool enableAutoTrading, int workerCount, string language,
        CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        LaunchpadInfo? launchpad = network?.Launchpads.FirstOrDefault(item => item.Code == dex);
        if (network == null || launchpad == null || !LaunchpadCatalog.IsValidRoute(chain, dex, anchor))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "UnsupportedSelection"), cancellationToken);
            return;
        }

        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "Confirm"), "watch:save:" + username + ":" + chain + "," + dex
                + "," + (anchor ?? "none") + "," + creatorTaxPercent
                + "," + (enableAutoTrading ? "1" : "0") + "," + workerCount)],
            [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]
        ];

        string message = text.Get(language, "PleaseConfirm") + "\n\n"
            + text.Get(language, "Account") + ": @" + username + "\n"
            + text.Get(language, "Network") + ": " + network.DisplayName + "\n"
            + text.Get(language, "Launchpad") + ": " + launchpad.DisplayName
            + (dex == "long" && anchor != null
                ? "\n" + text.Get(language, "StockAnchor") + ": " + anchor : string.Empty)
            + (LaunchpadCatalog.IsFlapBsc(chain, dex)
                ? "\n" + text.Get(language, "PaymentToken") + ": "
                    + LaunchpadCatalog.FindFlapBscPaymentToken(anchor)!.Code : string.Empty)
            + (LaunchpadCatalog.SupportsCreatorTax(dex) ? "\n" + text.Get(language, "CreatorTax") + ": "
                + (creatorTaxPercent == 0 ? text.Get(language, "NoCreatorTax") : creatorTaxPercent + "%")
                : string.Empty)
            + (dex == "pons" ? "\n" + text.Get(language, "CreatorFee") + ": 70%" : string.Empty)
            + "\n" + text.Get(language, "AutoTrading") + ": "
            + text.Get(language, enableAutoTrading ? "Enabled" : "Disabled")
            + "\n" + text.Get(language, "TokensPerPost") + ": " + workerCount;
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private async Task ShowWorkerCountAsync(long chatId, string username, string chain, string dex,
        string? anchor, int creatorTaxPercent, bool enableAutoTrading, string language,
        CancellationToken cancellationToken)
    {
        string route = chain + "," + dex + "," + (anchor ?? "none") + "," + creatorTaxPercent + ","
            + (enableAutoTrading ? "1" : "0") + ",";
        List<IReadOnlyList<TelegramInlineButton>> buttons = Enumerable.Range(1, workerOptions.MaxWorkers)
            .Select(count => (IReadOnlyList<TelegramInlineButton>)[new TelegramInlineButton(count.ToString(),
                "watch:workers:" + username + ":" + route + count)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseWorkerCount", username),
            buttons, cancellationToken);
    }

    private async Task ShowAutoTradingAsync(long chatId, string username, string chain, string dex, string? anchor,
        int creatorTaxPercent, string language, CancellationToken cancellationToken)
    {
        string route = chain + "," + dex + "," + (anchor ?? "none") + "," + creatorTaxPercent + ",";
        if (chain == "stable")
        {
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "AutoTradingUnsupported"),
                [[new TelegramInlineButton(text.Get(language, "DisableAutoTrading"),
                    "watch:trade:" + username + ":" + route + "0")],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]], cancellationToken);
            return;
        }

        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "EnableAutoTrading"),
                "watch:trade:" + username + ":" + route + "1")],
            [new TelegramInlineButton(text.Get(language, "DisableAutoTrading"),
                "watch:trade:" + username + ":" + route + "0")],
            [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]
        ];
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseAutoTrading", username),
            buttons, cancellationToken);
    }

    private sealed class WatchlistEditSelection
    {
        public string? Chain { get; set; }
        public string? Dex { get; set; }
        public string? Anchor { get; set; }
        public int? CreatorTaxPercent { get; set; }
        public bool EnableAutoTrading { get; set; }
        public int WorkerCount { get; set; } = 1;
    }
}
