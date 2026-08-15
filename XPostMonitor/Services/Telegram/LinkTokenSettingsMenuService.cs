using System.Collections.Concurrent;
using System.Globalization;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Telegram;

// Menu này chỉ cấu hình cách xử lý khi user tự gửi một link X vào bot.
public sealed class LinkTokenSettingsMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly LinkTokenSettingsService settingsService;
    private readonly PremiumService premiumService;
    private readonly EvmWalletService walletService;
    private readonly BotTextService text;
    private readonly TradingWorkersOptions workerOptions;
    private readonly ConcurrentDictionary<long, LinkTokenSelection> selections = new();
    private readonly ConcurrentDictionary<long, bool> waitingForAmount = new();
    private readonly ConcurrentDictionary<long, bool> waitingForFlapAllocation = new();

    public LinkTokenSettingsMenuService(TelegramApiClient telegramApi,
        LinkTokenSettingsService settingsService, PremiumService premiumService,
        EvmWalletService walletService, BotTextService text, TradingWorkersOptions workerOptions)
    {
        this.telegramApi = telegramApi;
        this.settingsService = settingsService;
        this.premiumService = premiumService;
        this.walletService = walletService;
        this.text = text;
        this.workerOptions = workerOptions;
    }

    // Mở một bản nháp để user chỉnh. Chỉ nút Lưu mới ghi xuống DB.
    public async Task ShowAsync(long chatId, CancellationToken cancellationToken)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        LinkTokenConfiguration? saved = await settingsService.GetAsync(chatId, cancellationToken);
        LinkTokenSelection selection = saved == null
            ? new LinkTokenSelection()
            : new LinkTokenSelection(saved);
        selections[chatId] = selection;
        await ShowPanelAsync(chatId, null, selection, language, cancellationToken);
    }

    public async Task HandleCallbackAsync(long chatId, long messageId, string data,
        CancellationToken cancellationToken)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        if (data == "linkauto:close")
        {
            selections.TryRemove(chatId, out _);
            waitingForAmount.TryRemove(chatId, out _);
            waitingForFlapAllocation.TryRemove(chatId, out _);
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            return;
        }
        if (data == "linkauto:back")
        {
            waitingForAmount.TryRemove(chatId, out _);
            waitingForFlapAllocation.TryRemove(chatId, out _);
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            if (selections.TryGetValue(chatId, out LinkTokenSelection? current))
            {
                await ShowPanelAsync(chatId, null, current, language, cancellationToken);
            }
            else
            {
                await ShowAsync(chatId, cancellationToken);
            }
            return;
        }
        if (!selections.TryGetValue(chatId, out LinkTokenSelection? selection))
        {
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            await ShowAsync(chatId, cancellationToken);
            return;
        }

        string action = data["linkauto:".Length..];
        if (action == "amount")
        {
            waitingForFlapAllocation.TryRemove(chatId, out _);
            waitingForAmount[chatId] = true;
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            LaunchpadNetwork network = LaunchpadCatalog.Find(selection.Chain)!;
            await telegramApi.SendButtonsAsync(chatId,
                text.Get(language, "LinkAutoSendAmount", network.Currency),
                [[new TelegramInlineButton(text.Get(language, "Cancel"), "linkauto:back")]],
                cancellationToken);
            return;
        }
        if (action == "allocation")
        {
            waitingForAmount.TryRemove(chatId, out _);
            waitingForFlapAllocation[chatId] = true;
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "SendFlapTaxAllocation"),
                [[new TelegramInlineButton(text.Get(language, "Cancel"), "linkauto:back")]],
                cancellationToken);
            return;
        }
        if (action == "save")
        {
            string? error = await ValidateAsync(chatId, selection, language, cancellationToken);
            if (error != null)
            {
                await ShowPanelAsync(chatId, messageId, selection, language, cancellationToken, error);
                return;
            }
            await settingsService.SaveAsync(chatId, selection.ToConfiguration(), cancellationToken);
            selections.TryRemove(chatId, out _);
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "LinkAutoSaved"), cancellationToken);
            return;
        }

        ApplyAction(selection, action);
        await ShowPanelAsync(chatId, messageId, selection, language, cancellationToken);
    }

    // Nhận số tiền khi menu đang chờ. Các tin nhắn bình thường khác không bị giữ lại.
    public async Task<bool> HandlePendingInputAsync(TelegramMessage message,
        CancellationToken cancellationToken)
    {
        long chatId = message.Chat.Id;
        if (message.Text == null || !selections.TryGetValue(chatId, out LinkTokenSelection? selection))
        {
            return false;
        }

        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        if (waitingForFlapAllocation.TryRemove(chatId, out _))
        {
            string[] values = message.Text.Split([' ', '/', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 2
                || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int devPercent)
                || !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int holderPercent)
                || devPercent < 0 || holderPercent < 0 || devPercent + holderPercent != 100)
            {
                await telegramApi.SendMessageAsync(chatId,
                    text.Get(language, "InvalidFlapTaxAllocation"), cancellationToken);
                await ShowPanelAsync(chatId, null, selection, language, cancellationToken);
                return true;
            }

            selection.FlapHolderPercent = holderPercent;
            await ShowPanelAsync(chatId, null, selection, language, cancellationToken);
            return true;
        }

        if (!waitingForAmount.TryRemove(chatId, out _))
        {
            return false;
        }

        if (!decimal.TryParse(message.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture,
                out decimal amount) || amount <= 0)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "InvalidAmount"), cancellationToken);
            await ShowPanelAsync(chatId, null, selection, language, cancellationToken);
            return true;
        }
        selection.BuyAmount = amount;
        await ShowPanelAsync(chatId, null, selection, language, cancellationToken);
        return true;
    }

    private void ApplyAction(LinkTokenSelection selection, string action)
    {
        string[] value = action.Split('=', 2);
        if (value.Length != 2)
        {
            return;
        }
        if (value[0] == "mode" && value[1] is "manual" or "auto")
        {
            selection.EnableAutoCreate = value[1] == "auto";
        }
        else if (value[0] == "chain" && LaunchpadCatalog.Find(value[1]) is LaunchpadNetwork network)
        {
            selection.Chain = network.Chain;
            selection.Launchpad = network.Launchpads[0].Code;
            selection.Anchor = selection.Launchpad == "long" ? LaunchpadCatalog.LongAnchors[0].Code : null;
            selection.CreatorTaxPercent = selection.Launchpad == "flap" ? 1 : 0;
            selection.EnableAutoTrading = false;
            selection.BuyAmount = network.Chain == "bsc" ? 0.05m : network.MinimumBuyAmount;
        }
        else if (value[0] == "launchpad" && LaunchpadCatalog.IsValid(selection.Chain, value[1]))
        {
            selection.Launchpad = value[1];
            selection.Anchor = value[1] == "long" ? LaunchpadCatalog.LongAnchors[0].Code : null;
            selection.CreatorTaxPercent = value[1] == "flap" ? 1 : 0;
        }
        else if (value[0] == "tax" && int.TryParse(value[1], out int tax)
            && LaunchpadCatalog.IsValidCreatorTax(selection.Launchpad, tax))
        {
            selection.CreatorTaxPercent = tax;
        }
        else if (value[0] == "anchor" && LaunchpadCatalog.FindLongAnchor(value[1]) != null)
        {
            selection.Anchor = value[1];
        }
        else if (value[0] == "payment" && LaunchpadCatalog.FindFlapBscPaymentToken(value[1]) != null)
        {
            selection.Anchor = value[1] == "BNB" ? null : value[1];
        }
        else if (value[0] == "trading" && value[1] is "0" or "1" && selection.Chain != "stable")
        {
            selection.EnableAutoTrading = value[1] == "1";
        }
        else if (value[0] == "worker" && int.TryParse(value[1], out int workerSlot)
            && workerSlot >= 1 && workerSlot <= workerOptions.MaxWorkers)
        {
            if (!selection.WorkerSlots.Add(workerSlot))
            {
                selection.WorkerSlots.Remove(workerSlot);
            }
        }
    }

    private async Task<string?> ValidateAsync(long chatId, LinkTokenSelection selection, string language,
        CancellationToken cancellationToken)
    {
        if (!selection.EnableAutoCreate)
        {
            return null;
        }
        LaunchpadNetwork? network = LaunchpadCatalog.Find(selection.Chain);
        if (network == null || !LaunchpadCatalog.IsValidRoute(selection.Chain, selection.Launchpad,
                selection.Anchor)
            || !LaunchpadCatalog.IsValidCreatorTax(selection.Launchpad, selection.CreatorTaxPercent))
        {
            return text.Get(language, "UnsupportedSelection");
        }
        decimal minimum = selection.Chain == "bsc" ? 0m : network.MinimumBuyAmount;
        if (selection.BuyAmount <= 0 || selection.BuyAmount < minimum)
        {
            return text.Get(language, "MinimumBuyAmount", network.DisplayName, minimum, network.Currency);
        }
        int[] selectedSlots = selection.WorkerSlots
            .Where(slot => slot >= 1 && slot <= workerOptions.MaxWorkers)
            .Distinct().OrderBy(slot => slot).ToArray();
        if (selectedSlots.Length == 0)
        {
            return text.Get(language, "EditSelectionMissing");
        }
        IReadOnlyList<TradingWorkerWallet> wallets = await walletService.GetReadyWorkersAsync(chatId,
            selectedSlots, cancellationToken);
        if (wallets.Count != selectedSlots.Length)
        {
            int missing = selectedSlots.First(slot => wallets.All(wallet => wallet.SlotNumber != slot));
            return text.Get(language, "ConfigureWorkerFirst", missing);
        }
        return null;
    }

    private async Task ShowPanelAsync(long chatId, long? messageId, LinkTokenSelection selection,
        string language, CancellationToken cancellationToken, string? notice = null)
    {
        LaunchpadNetwork network = LaunchpadCatalog.Find(selection.Chain)!;
        LaunchpadInfo launchpad = network.Launchpads.First(item => item.Code == selection.Launchpad);
        string mode = text.Get(language, selection.EnableAutoCreate ? "LinkAutoModeAuto" : "LinkAutoModeManual");
        string message = (notice == null ? text.Get(language, "LinkAutoTitle") : notice) + "\n\n"
            + text.Get(language, "LinkAutoMode") + ": " + mode + "\n"
            + text.Get(language, "Network") + ": " + network.DisplayName + "\n"
            + text.Get(language, "Launchpad") + ": " + launchpad.DisplayName + "\n"
            + text.Get(language, "BuyAmount") + ": "
            + selection.BuyAmount.ToString(CultureInfo.InvariantCulture) + " " + network.Currency + "\n"
            + text.Get(language, "ParallelWallets") + ": "
            + (selection.WorkerSlots.Count == 0 ? text.Get(language, "NotSet")
                : string.Join(", ", selection.WorkerSlots.OrderBy(slot => slot)
                    .Select(slot => text.Get(language, "Worker") + " " + slot))) + "\n"
            + text.Get(language, "AutoTrading") + ": "
            + text.Get(language, selection.EnableAutoTrading ? "Enabled" : "Disabled");
        if (LaunchpadCatalog.SupportsCreatorTax(selection.Launchpad))
        {
            message += "\n" + text.Get(language, "CreatorTax") + ": " + selection.CreatorTaxPercent + "%";
        }
        if (selection.Launchpad == "long")
        {
            message += "\n" + text.Get(language, "StockAnchor") + ": " + selection.Anchor;
        }
        if (LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Launchpad))
        {
            message += "\n" + text.Get(language, "PaymentToken") + ": "
                + LaunchpadCatalog.FindFlapBscPaymentToken(selection.Anchor)!.Code;
            message += "\n" + text.Get(language, "FlapTaxAllocationSummary",
                100 - selection.FlapHolderPercent, selection.FlapHolderPercent);
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [
                Button(selection.EnableAutoCreate, text.Get(language, "LinkAutoModeAuto"), "linkauto:mode=auto"),
                Button(!selection.EnableAutoCreate, text.Get(language, "LinkAutoModeManual"), "linkauto:mode=manual")
            ],
            LaunchpadCatalog.All.Select(item => Button(item.Chain == selection.Chain, item.DisplayName,
                "linkauto:chain=" + item.Chain)).ToList(),
            network.Launchpads.Select(item => Button(item.Code == selection.Launchpad, item.DisplayName,
                "linkauto:launchpad=" + item.Code)).ToList(),
            [new TelegramInlineButton(text.Get(language, "ChangeAmount"), "linkauto:amount")]
        ];
        if (LaunchpadCatalog.SupportsCreatorTax(selection.Launchpad))
        {
            int[] rates = selection.Launchpad == "flap" ? [1, 3, 5, 10] : [0, 1, 3, 5, 10];
            buttons.Add(rates.Select(rate => Button(rate == selection.CreatorTaxPercent, rate + "%",
                "linkauto:tax=" + rate)).ToList());
        }
        if (selection.Launchpad == "long")
        {
            foreach (LaunchpadAnchor[] row in LaunchpadCatalog.LongAnchors.Chunk(2))
            {
                buttons.Add(row.Select(item => Button(item.Code == selection.Anchor, item.Code,
                    "linkauto:anchor=" + item.Code)).ToList());
            }
        }
        if (LaunchpadCatalog.IsFlapBsc(selection.Chain, selection.Launchpad))
        {
            string payment = LaunchpadCatalog.FindFlapBscPaymentToken(selection.Anchor)!.Code;
            foreach (FlapPaymentToken[] row in LaunchpadCatalog.FlapBscPaymentTokens.Chunk(2))
            {
                buttons.Add(row.Select(item => Button(item.Code == payment, item.Code,
                    "linkauto:payment=" + item.Code)).ToList());
            }
            buttons.Add([new TelegramInlineButton(text.Get(language, "FlapTaxAllocation"),
                "linkauto:allocation")]);
        }
        buttons.Add(Enumerable.Range(1, workerOptions.MaxWorkers).Select(slot => Button(
            selection.WorkerSlots.Contains(slot), text.Get(language, "Worker") + " " + slot,
            "linkauto:worker=" + slot)).ToList());
        if (selection.Chain != "stable")
        {
            buttons.Add([
                Button(selection.EnableAutoTrading, text.Get(language, "EnableAutoTrading"),
                    "linkauto:trading=1"),
                Button(!selection.EnableAutoTrading, text.Get(language, "DisableAutoTrading"),
                    "linkauto:trading=0")
            ]);
        }
        buttons.Add([
            new TelegramInlineButton(text.Get(language, "SaveChanges"), "linkauto:save"),
            new TelegramInlineButton(text.Get(language, "Cancel"), "linkauto:close")
        ]);

        if (messageId.HasValue)
        {
            await telegramApi.EditButtonsAsync(chatId, messageId.Value, message, buttons, cancellationToken);
        }
        else
        {
            await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
        }
    }

    private static TelegramInlineButton Button(bool selected, string label, string callback)
    {
        return new TelegramInlineButton((selected ? "✅ " : string.Empty) + label, callback);
    }

    private sealed class LinkTokenSelection
    {
        public LinkTokenSelection() { }

        public LinkTokenSelection(LinkTokenConfiguration settings)
        {
            EnableAutoCreate = settings.EnableAutoCreate;
            Chain = settings.Chain;
            Launchpad = settings.Launchpad;
            Anchor = settings.Anchor;
            CreatorTaxPercent = settings.CreatorTaxPercent;
            FlapHolderPercent = settings.FlapHolderPercent;
            EnableAutoTrading = settings.EnableAutoTrading;
            WorkerSlots.Clear();
            foreach (int slot in settings.WorkerSlots)
            {
                WorkerSlots.Add(slot);
            }
            BuyAmount = settings.BuyAmount;
            SlippagePercent = settings.SlippagePercent;
        }

        public bool EnableAutoCreate { get; set; }
        public string Chain { get; set; } = "bsc";
        public string Launchpad { get; set; } = "fourmeme";
        public string? Anchor { get; set; }
        public int CreatorTaxPercent { get; set; }
        public int FlapHolderPercent { get; set; }
        public bool EnableAutoTrading { get; set; }
        // Không tự tích Ví 1: user tích ví nào thì chỉ chạy đúng ví đó.
        public HashSet<int> WorkerSlots { get; } = [];
        public decimal BuyAmount { get; set; } = 0.05m;
        public decimal SlippagePercent { get; set; } = 5m;

        public LinkTokenConfiguration ToConfiguration() => new(EnableAutoCreate, Chain, Launchpad,
            LaunchpadCatalog.NormalizeRouteOption(Chain, Launchpad, Anchor), CreatorTaxPercent,
            FlapHolderPercent, EnableAutoTrading, WorkerSlots.OrderBy(slot => slot).ToArray(), BuyAmount,
            SlippagePercent);
    }
}
