using System.Collections.Concurrent;
using System.Globalization;
using Nethereum.Web3;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Nft;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Telegram;

// Menu NFT độc lập; không đọc hoặc sửa cấu hình tạo token/GMGN.
public sealed class NftMintMenuService
{
    private readonly TelegramApiClient telegram;
    private readonly NftWalletService wallets;
    private readonly NftMintService mintService;
    private readonly NftPortfolioClient portfolio;
    private readonly BotTextService text;
    private readonly ConcurrentDictionary<long, string> pendingInputs = new();
    private readonly ConcurrentDictionary<long,
        (decimal Amount, NftWalletGroup Group, long[] WalletIds)> preparedFunding = new();
    private readonly ConcurrentDictionary<long, HashSet<long>> selectedFundingWallets = new();
    private readonly ConcurrentDictionary<long, NftWalletGroup> selectedGroups = new();
    private readonly ConcurrentDictionary<long, string> collectionLinks = new();
    private readonly ConcurrentDictionary<long, int[]> selectedMintWalletSlots = new();
    private readonly ConcurrentDictionary<long, (NftMintMode Mode, int Value)> mintChoices = new();
    private readonly ConcurrentDictionary<long, NftMintPlan> mintPlans = new();
    private readonly ConcurrentDictionary<long, long> pendingMessageIds = new();

    public NftMintMenuService(TelegramApiClient telegram, NftWalletService wallets,
        NftMintService mintService, NftPortfolioClient portfolio, BotTextService text)
    {
        this.telegram = telegram;
        this.wallets = wallets;
        this.mintService = mintService;
        this.portfolio = portfolio;
        this.text = text;
    }

    public async Task HandleCallbackAsync(long chatId, long messageId, string data, string language,
        CancellationToken cancellationToken)
    {
        string action = data[4..];
        if (action == "open") { await ShowAsync(chatId, messageId, language, cancellationToken); return; }
        if (action == "close") { await telegram.DeleteMessageAsync(chatId, messageId, cancellationToken); return; }
        if (action is "groupfree" or "grouppaid")
        {
            selectedGroups[chatId] = action == "groupfree" ? NftWalletGroup.Free : NftWalletGroup.Paid;
            await ShowAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (action == "createmain")
        {
            try
            {
                var created = await wallets.CreateAsync(chatId, true, NftWalletGroup.Free,
                    cancellationToken);
                await telegram.SendMessageAsync(chatId, text.Get(language, "NftWalletCreated",
                    created.Address, created.PrivateKey), cancellationToken);
            }
            catch (Exception exception) { await SendErrorAsync(chatId, language, exception, cancellationToken); }
            await ShowAsync(chatId, messageId, language, cancellationToken); return;
        }
        if (action == "createsub")
        {
            pendingInputs[chatId] = action + ":" + SelectedGroup(chatId);
            pendingMessageIds[chatId] = messageId;
            await ShowPromptAsync(chatId, messageId, text.Get(language, "NftSendWalletCount"), language,
                cancellationToken);
            return;
        }
        if (action is "importmain" or "importsub")
        {
            pendingInputs[chatId] = action == "importmain"
                ? action : action + ":" + SelectedGroup(chatId);
            pendingMessageIds[chatId] = messageId;
            await ShowPromptAsync(chatId, messageId, text.Get(language, "NftSendPrivateKey"), language,
                cancellationToken);
            return;
        }
        if (action == "fund")
        {
            selectedFundingWallets[chatId] = [];
            await ShowFundingWalletsAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (action.StartsWith("fundtoggle", StringComparison.Ordinal)
            && long.TryParse(action[10..], out long toggleWalletId))
        {
            HashSet<long> selected = selectedFundingWallets.GetOrAdd(chatId, _ => []);
            if (!selected.Add(toggleWalletId)) selected.Remove(toggleWalletId);
            await ShowFundingWalletsAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (action is "fundall" or "fundnone")
        {
            HashSet<long> selected = selectedFundingWallets.GetOrAdd(chatId, _ => []);
            selected.Clear();
            if (action == "fundall")
            {
                NftWalletGroup group = SelectedGroup(chatId);
                IReadOnlyList<NftWalletInfo> list = await wallets.ListAsync(chatId, group,
                    cancellationToken);
                foreach (NftWalletInfo wallet in list.Where(x => x.SlotNumber > 0 && x.IsEnabled))
                    selected.Add(wallet.Id);
            }
            await ShowFundingWalletsAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (action == "fundnext")
        {
            if (!selectedFundingWallets.TryGetValue(chatId, out HashSet<long>? selected)
                || selected.Count == 0)
            {
                await ShowFundingWalletsAsync(chatId, messageId, language, cancellationToken,
                    text.Get(language, "NftSelectFundingWalletRequired"));
                return;
            }
            pendingInputs[chatId] = "fundamount:" + SelectedGroup(chatId);
            pendingMessageIds[chatId] = messageId;
            await ShowPromptAsync(chatId, messageId, text.Get(language, "NftSendFundAmount"),
                language, cancellationToken);
            return;
        }
        if (action == "fundconfirm")
        {
            if (!preparedFunding.TryRemove(chatId, out var funding)) return;
            try
            {
                IReadOnlyList<string> hashes = await mintService.FundAllAsync(chatId,
                    funding.Amount, funding.Group, funding.WalletIds, cancellationToken);
                selectedFundingWallets.TryRemove(chatId, out _);
                await telegram.EditButtonsAsync(chatId, messageId,
                    text.Get(language, "NftFundDone", hashes.Count),
                    [[new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]], cancellationToken);
            }
            catch (Exception exception) { await SendErrorAsync(chatId, language, exception, cancellationToken); }
            return;
        }
        if (action == "link")
        {
            selectedMintWalletSlots.TryRemove(chatId, out _);
            pendingInputs[chatId] = action;
            pendingMessageIds[chatId] = messageId;
            await ShowPromptAsync(chatId, messageId, text.Get(language, "NftSendOpenSeaLink"), language,
                cancellationToken);
            return;
        }
        if (action == "portfolio")
        {
            await ShowPortfolioAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (action.StartsWith("mode", StringComparison.Ordinal))
        {
            NftMintMode mode = action[4..] switch
            {
                "fixed" => NftMintMode.Fixed,
                "random" => NftMintMode.Random,
                "round" => NftMintMode.Round,
                _ => throw new ArgumentException("Chế độ mint không hợp lệ.")
            };
            pendingInputs[chatId] = "mintvalue:" + mode;
            pendingMessageIds[chatId] = messageId;
            await ShowPromptAsync(chatId, messageId, text.Get(language, mode switch
            {
                NftMintMode.Fixed => "NftSendFixedQuantity",
                NftMintMode.Random => "NftSendRandomMaximum",
                _ => "NftSendRoundCount"
            }), language, cancellationToken);
            return;
        }
        if (action == "reroll")
        {
            if (mintChoices.TryGetValue(chatId, out var choice))
            {
                await ShowMintPreparationAsync(chatId, messageId, language, cancellationToken);
                await PrepareMintAsync(chatId, messageId, language, choice.Mode, choice.Value, cancellationToken);
            }
            return;
        }
        if (action == "mintconfirm") { await MintAsync(chatId, messageId, language, cancellationToken); return; }
        if (action == "exportallask")
        {
            await telegram.EditButtonsAsync(chatId, messageId, text.Get(language, "NftExportConfirm"),
                [[new TelegramInlineButton(text.Get(language, "Confirm"), "nft:exportallconfirm")],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "nft:open")]], cancellationToken);
            return;
        }
        if (action == "exportallconfirm")
        {
            await ExportAllMintWalletsAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (action.StartsWith("export", StringComparison.Ordinal) && long.TryParse(action[6..], out long exportId))
        {
            EvmWalletCredentials? wallet = await wallets.GetByIdAsync(chatId, exportId, cancellationToken);
            if (wallet == null) return;
            await telegram.SendButtonsAsync(chatId,
                text.Get(language, "NftPrivateKey", wallet.Address, wallet.PrivateKey),
                [[new TelegramInlineButton(text.Get(language, "Close"), "nft:close")]],
                cancellationToken, useHtml: true);
            return;
        }
        if (action.StartsWith("move", StringComparison.Ordinal) && long.TryParse(action[4..], out long moveId))
        {
            await wallets.MoveToOtherGroupAsync(chatId, moveId, cancellationToken);
            await ShowAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (action.StartsWith("deleteask", StringComparison.Ordinal) && long.TryParse(action[9..], out long askId))
        {
            await telegram.EditButtonsAsync(chatId, messageId, text.Get(language, "NftDeleteConfirm"),
                [[new TelegramInlineButton(text.Get(language, "Confirm"), "nft:deleteconfirm" + askId)],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "nft:open")]], cancellationToken);
            return;
        }
        if (action.StartsWith("deleteconfirm", StringComparison.Ordinal) && long.TryParse(action[13..], out long id))
        {
            await wallets.DeleteAsync(chatId, id, cancellationToken);
            await ShowAsync(chatId, messageId, language, cancellationToken);
        }
    }

    public async Task<bool> HandlePendingInputAsync(TelegramMessage message, string language,
        CancellationToken cancellationToken)
    {
        if (!pendingInputs.TryRemove(message.Chat.Id, out string? action) || message.Text == null) return false;
        if (action.StartsWith("createsub:", StringComparison.Ordinal))
        {
            if (!int.TryParse(message.Text.Trim(), out int count) || count is < 1 or > 30)
            {
                pendingInputs[message.Chat.Id] = action;
                await telegram.SendMessageAsync(message.Chat.Id, text.Get(language, "NftInvalidWalletCount"), cancellationToken);
                return true;
            }
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var created = await wallets.CreateAsync(message.Chat.Id, false,
                        ReadGroup(action), cancellationToken);
                    await telegram.SendMessageAsync(message.Chat.Id, text.Get(language, "NftWalletCreated",
                        created.Address, created.PrivateKey), cancellationToken);
                }
            }
            catch (Exception exception) { await SendErrorAsync(message.Chat.Id, language, exception, cancellationToken); }
            await SafeDeleteAsync(message.Chat.Id, message.MessageId, cancellationToken);
            await ShowAsync(message.Chat.Id, TakePendingMessageId(message.Chat.Id), language, cancellationToken);
            return true;
        }
        if (action == "importmain" || action.StartsWith("importsub:", StringComparison.Ordinal))
        {
            await telegram.DeleteMessageAsync(message.Chat.Id, message.MessageId, cancellationToken);
            try
            {
                await wallets.ImportAsync(message.Chat.Id, action == "importmain", message.Text,
                    action == "importmain" ? NftWalletGroup.Free : ReadGroup(action), cancellationToken);
                await telegram.SendMessageAsync(message.Chat.Id, text.Get(language, "NftWalletImported"), cancellationToken);
            }
            catch (Exception exception) { await SendErrorAsync(message.Chat.Id, language, exception, cancellationToken); }
            await ShowAsync(message.Chat.Id, TakePendingMessageId(message.Chat.Id), language, cancellationToken);
            return true;
        }
        if (action.StartsWith("fundamount:", StringComparison.Ordinal))
        {
            string input = message.Text.Trim().Replace(',', '.');
            if (!decimal.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                    out decimal amount) || amount <= 0 || amount > 10)
            {
                pendingInputs[message.Chat.Id] = action;
                await telegram.SendMessageAsync(message.Chat.Id, text.Get(language, "NftInvalidAmount"), cancellationToken);
                return true;
            }
            NftWalletGroup group = ReadGroup(action);
            if (!selectedFundingWallets.TryGetValue(message.Chat.Id, out HashSet<long>? selected)
                || selected.Count == 0)
            {
                await telegram.SendMessageAsync(message.Chat.Id,
                    text.Get(language, "NftSelectFundingWalletRequired"), cancellationToken);
                return true;
            }
            long[] walletIds = selected.ToArray();
            int count = walletIds.Length;
            preparedFunding[message.Chat.Id] = (amount, group, walletIds);
            await SafeDeleteAsync(message.Chat.Id, message.MessageId, cancellationToken);
            await telegram.EditButtonsAsync(message.Chat.Id, TakePendingMessageId(message.Chat.Id)!.Value,
                text.Get(language, "NftFundConfirm", amount, count, amount * count,
                    GroupName(language, group)),
                [[new TelegramInlineButton(text.Get(language, "Confirm"), "nft:fundconfirm")],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "nft:open")]], cancellationToken);
            return true;
        }
        if (action == "link")
        {
            await NftDiagnosticLog.WriteAsync($"Nhận link từ ChatId={message.Chat.Id}: {message.Text.Trim()}");
            try { _ = OpenSeaNftClient.GetCollectionSlug(message.Text); }
            catch (Exception exception) { await SendErrorAsync(message.Chat.Id, language, exception, cancellationToken); return true; }
            collectionLinks[message.Chat.Id] = message.Text.Trim();
            await SafeDeleteAsync(message.Chat.Id, message.MessageId, cancellationToken);
            long menuMessageId = TakePendingMessageId(message.Chat.Id)!.Value;
            pendingInputs[message.Chat.Id] = "mintwallets";
            pendingMessageIds[message.Chat.Id] = menuMessageId;
            await ShowPromptAsync(message.Chat.Id, menuMessageId,
                text.Get(language, "NftSendMintWalletSelection"), language, cancellationToken);
            return true;
        }
        if (action == "mintwallets")
        {
            int[] slots;
            try { slots = ParseWalletSlots(message.Text); }
            catch (ArgumentException)
            {
                pendingInputs[message.Chat.Id] = action;
                await telegram.SendMessageAsync(message.Chat.Id,
                    text.Get(language, "NftInvalidMintWalletSelection"), cancellationToken);
                return true;
            }
            selectedMintWalletSlots[message.Chat.Id] = slots;
            await SafeDeleteAsync(message.Chat.Id, message.MessageId, cancellationToken);
            await telegram.EditButtonsAsync(message.Chat.Id, TakePendingMessageId(message.Chat.Id)!.Value,
                text.Get(language, "NftChooseModeForWallets", FormatWalletSlots(slots), slots.Length),
                [[new TelegramInlineButton(text.Get(language, "NftModeFixed"), "nft:modefixed")],
                 [new TelegramInlineButton(text.Get(language, "NftModeRandom"), "nft:moderandom")],
                 [new TelegramInlineButton(text.Get(language, "NftModeRound"), "nft:moderound")],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "nft:open")]], cancellationToken);
            return true;
        }
        if (action.StartsWith("mintvalue:", StringComparison.Ordinal))
        {
            if (!Enum.TryParse(action[10..], out NftMintMode mode)
                || !int.TryParse(message.Text.Trim(), out int value) || value is < 1 or > 100)
            {
                pendingInputs[message.Chat.Id] = action;
                await telegram.SendMessageAsync(message.Chat.Id, text.Get(language, "NftInvalidMintValue"), cancellationToken);
                return true;
            }
            mintChoices[message.Chat.Id] = (mode, value);
            await SafeDeleteAsync(message.Chat.Id, message.MessageId, cancellationToken);
            long? menuMessageId = TakePendingMessageId(message.Chat.Id);
            if (menuMessageId.HasValue)
                await ShowMintPreparationAsync(message.Chat.Id, menuMessageId.Value, language,
                    cancellationToken);
            await PrepareMintAsync(message.Chat.Id, menuMessageId, language, mode, value,
                cancellationToken);
            return true;
        }
        return true;
    }

    private async Task ShowAsync(long chatId, long? messageId, string language,
        CancellationToken cancellationToken)
    {
        NftWalletGroup selectedGroup = SelectedGroup(chatId);
        IReadOnlyList<NftWalletInfo> list = await wallets.ListAsync(chatId, selectedGroup,
            cancellationToken);
        NftWalletInfo? mainWallet = list.FirstOrDefault(x => x.SlotNumber == 0);
        List<NftWalletInfo> mintWallets = list.Where(x => x.SlotNumber > 0).ToList();
        string mainLine = mainWallet == null ? text.Get(language, "NftNoWallet")
            : WalletLine(language, mainWallet);
        string mintLines = mintWallets.Count == 0 ? text.Get(language, "NftGroupEmpty")
            : string.Join('\n', mintWallets.Select(x => WalletLine(language, x)));
        string lines = text.Get(language, "NftSharedMain") + "\n" + mainLine + "\n\n"
            + text.Get(language, "NftSelectedGroup", GroupName(language, selectedGroup)) + "\n"
            + mintLines;
        List<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(
                 (selectedGroup == NftWalletGroup.Free ? "✅ " : "") + text.Get(language, "NftFreeWallets"),
                 "nft:groupfree"),
             new TelegramInlineButton(
                 (selectedGroup == NftWalletGroup.Paid ? "✅ " : "") + text.Get(language, "NftPaidWallets"),
                 "nft:grouppaid")],
            [new TelegramInlineButton(text.Get(language, "NftCreateSub"), "nft:createsub"),
             new TelegramInlineButton(text.Get(language, "NftImportSub"), "nft:importsub")],
            [new TelegramInlineButton(text.Get(language, "NftFundWallets"), "nft:fund"),
             new TelegramInlineButton(text.Get(language, "NftMintButton"), "nft:link")],
            [new TelegramInlineButton(text.Get(language, "NftPortfolioButton"), "nft:portfolio")],
            [new TelegramInlineButton(text.Get(language, "NftExportExcel"), "nft:exportallask")]
        ];
        if (mainWallet == null)
            buttons.Insert(1,
            [
                new TelegramInlineButton(text.Get(language, "NftCreateMain"), "nft:createmain"),
                new TelegramInlineButton(text.Get(language, "NftImportMain"), "nft:importmain")
            ]);
        foreach (NftWalletInfo wallet in list)
        {
            string walletName = wallet.SlotNumber == 0 ? text.Get(language, "NftMainWallet")
                : text.Get(language, "NftMintWallet", wallet.SlotNumber);
            List<TelegramInlineButton> walletButtons =
            [
                new TelegramInlineButton("🔑 " + walletName, "nft:export" + wallet.Id),
                new TelegramInlineButton("🗑 " + walletName, "nft:deleteask" + wallet.Id)
            ];
            if (wallet.SlotNumber > 0)
                walletButtons.Add(new TelegramInlineButton("↔️ " + text.Get(language, "NftMoveWallet"),
                    "nft:move" + wallet.Id));
            buttons.Add(walletButtons);
        }
        buttons.Add([new TelegramInlineButton(text.Get(language, "Close"), "nft:close")]);
        string title = text.Get(language, "NftMenu", lines);
        if (messageId.HasValue) await telegram.EditButtonsAsync(chatId, messageId.Value, title, buttons, cancellationToken);
        else await telegram.SendButtonsAsync(chatId, title, buttons, cancellationToken);
    }

    private async Task PrepareMintAsync(long chatId, long? messageId, string language,
        NftMintMode mode, int value, CancellationToken cancellationToken)
    {
        if (!collectionLinks.TryGetValue(chatId, out string? link)
            || !selectedMintWalletSlots.TryGetValue(chatId, out int[]? selectedSlots)) return;
        try
        {
            await NftDiagnosticLog.WriteAsync(
                $"Chuẩn bị mint ChatId={chatId}, Ví={FormatWalletSlots(selectedSlots)}, "
                + $"Mode={mode}, Value={value}, Link={link}");
            NftMintPlan plan = await mintService.PrepareAsync(chatId, link, mode, value,
                selectedSlots, cancellationToken);
            mintPlans[chatId] = plan;
            string perWallet = string.Join(", ", plan.Items.GroupBy(x => x.Wallet.SlotNumber)
                .Select(x => $"V{x.Key}:{x.Sum(i => i.Quantity)}"));
            List<IReadOnlyList<TelegramInlineButton>> buttons =
            [
                [new TelegramInlineButton(text.Get(language, "Confirm"), "nft:mintconfirm")],
                [new TelegramInlineButton(text.Get(language, "Cancel"), "nft:open")]
            ];
            if (mode == NftMintMode.Random)
                buttons.Insert(1, [new TelegramInlineButton(text.Get(language, "NftReroll"), "nft:reroll")]);
            string message = text.Get(language, "NftMintPlanConfirm",
                plan.Drop.Collection.Name, ModeName(language, mode), perWallet, plan.TotalQuantity,
                Web3.Convert.FromWei(plan.Drop.MintPriceWei), Web3.Convert.FromWei(plan.TotalValueWei),
                GroupName(language, plan.MintGroup), FormatWalletSlots(selectedSlots),
                plan.Items.Select(x => x.Wallet.Id).Distinct().Count());
            if (messageId.HasValue)
                await telegram.EditButtonsAsync(chatId, messageId.Value, message, buttons, cancellationToken);
            else
                await telegram.SendButtonsAsync(chatId, message, buttons, cancellationToken);
        }
        catch (Exception exception)
        {
            await NftDiagnosticLog.WriteAsync($"LỖI ChatId={chatId}: {exception}");
            if (messageId.HasValue)
                await telegram.EditButtonsAsync(chatId, messageId.Value,
                    text.Get(language, "NftError", exception.Message),
                    [[new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]],
                    cancellationToken);
            else
                await SendErrorAsync(chatId, language, exception, cancellationToken);
        }
    }

    private Task ShowMintPreparationAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken) => telegram.EditButtonsAsync(chatId, messageId,
            text.Get(language, "NftMintPreparing"),
            Array.Empty<IReadOnlyList<TelegramInlineButton>>(), cancellationToken);

    private async Task ShowFundingWalletsAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken, string? notice = null)
    {
        NftWalletGroup group = SelectedGroup(chatId);
        IReadOnlyList<NftWalletInfo> walletList = await wallets.ListAsync(chatId, group,
            cancellationToken);
        List<NftWalletInfo> mintWallets = walletList
            .Where(x => x.SlotNumber > 0 && x.IsEnabled).ToList();
        HashSet<long> selected = selectedFundingWallets.GetOrAdd(chatId, _ => []);
        HashSet<long> validIds = mintWallets.Select(x => x.Id).ToHashSet();
        selected.RemoveWhere(id => !validIds.Contains(id));

        List<IReadOnlyList<TelegramInlineButton>> buttons = [];
        foreach (NftWalletInfo wallet in mintWallets)
        {
            string balance = wallet.BalanceEth?.ToString("0.########", CultureInfo.InvariantCulture) ?? "?";
            string mark = selected.Contains(wallet.Id) ? "✅ " : "☐ ";
            buttons.Add([new TelegramInlineButton(
                mark + text.Get(language, "NftFundingWallet", wallet.SlotNumber, balance),
                "nft:fundtoggle" + wallet.Id)]);
        }
        buttons.Add(
        [
            new TelegramInlineButton(text.Get(language, "NftSelectAll"), "nft:fundall"),
            new TelegramInlineButton(text.Get(language, "NftClearSelection"), "nft:fundnone")
        ]);
        buttons.Add([new TelegramInlineButton(text.Get(language, "NftContinue"), "nft:fundnext")]);
        buttons.Add([new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]);

        string message = text.Get(language, "NftChooseFundingWallets", GroupName(language, group),
            selected.Count);
        if (mintWallets.Count == 0) message += "\n\n" + text.Get(language, "NftGroupEmpty");
        if (!string.IsNullOrWhiteSpace(notice)) message += "\n\n" + notice;
        await telegram.EditButtonsAsync(chatId, messageId, message, buttons, cancellationToken);
    }

    private async Task MintAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken)
    {
        if (!mintPlans.TryRemove(chatId, out NftMintPlan? plan)) return;
        try
        {
            // Xóa nút xác nhận ngay để user biết bot đã nhận lệnh và không bấm trùng.
            await telegram.EditButtonsAsync(chatId, messageId,
                text.Get(language, "NftMintProcessing", plan.TotalQuantity),
                Array.Empty<IReadOnlyList<TelegramInlineButton>>(), cancellationToken);

            IReadOnlyList<NftMintOutcome> results = await mintService.ExecuteAsync(plan, cancellationToken);
            if (plan.Mode == NftMintMode.Round)
            {
                List<string> walletLines = [];
                foreach (NftMintWallet wallet in plan.Items.Select(x => x.Wallet)
                    .DistinctBy(x => x.Id).OrderBy(x => x.SlotNumber))
                {
                    List<NftMintOutcome> walletResults = results
                        .Where(x => x.SlotNumber == wallet.SlotNumber).ToList();
                    int succeeded = walletResults.Count(x => x.Error == null);
                    NftMintOutcome? failed = walletResults.FirstOrDefault(x => x.Error != null);
                    walletLines.Add(failed == null
                        ? text.Get(language, "NftRoundWalletSuccess", wallet.SlotNumber,
                            succeeded)
                        : text.Get(language, "NftRoundWalletFailed", wallet.SlotNumber,
                            succeeded, failed.Round,
                            failed.Error!, failed.TransactionHash ?? "-"));
                }

                await telegram.EditButtonsAsync(chatId, messageId,
                    text.Get(language, "NftRoundMintReport", string.Join('\n', walletLines)),
                    [[new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]],
                    cancellationToken);
                return;
            }

            int success = results.Count(x => x.Error == null);
            string details = string.Join('\n', results.Select(x => x.Error == null
                ? $"V{x.SlotNumber}/R{x.Round}: " + (x.TransactionHash ?? "DRY-RUN")
                : $"V{x.SlotNumber}/R{x.Round}: {x.Error} | Tx: {x.TransactionHash ?? "-"}"));
            await telegram.EditButtonsAsync(chatId, messageId,
                text.Get(language, "NftMintDone", success, plan.Items.Count, details),
                [[new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]], cancellationToken);
        }
        catch (Exception exception) { await SendErrorAsync(chatId, language, exception, cancellationToken); }
    }

    private async Task ShowPortfolioAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken)
    {
        try
        {
            NftWalletGroup selectedGroup = SelectedGroup(chatId);
            IReadOnlyList<NftWalletInfo> walletList = await wallets.ListAsync(chatId, selectedGroup,
                cancellationToken);
            List<string> blocks = [];
            foreach (NftWalletInfo wallet in walletList.Where(x => x.SlotNumber > 0))
            {
                string walletName = wallet.SlotNumber == 0 ? text.Get(language, "NftMainWallet")
                    : text.Get(language, "NftMintWallet", wallet.SlotNumber);
                try
                {
                    IReadOnlyList<NftCollectionBalance> collections = await portfolio
                        .GetCollectionsAsync(wallet.Address, cancellationToken);
                    string nftLines = collections.Count == 0 ? text.Get(language, "NftPortfolioEmpty")
                        : string.Join('\n', collections.Select(x => $"• {x.Name}: {x.Quantity}"));
                    blocks.Add($"{walletName}: {wallet.Address}\n{nftLines}");
                }
                catch (Exception exception)
                {
                    await NftDiagnosticLog.WriteAsync(
                        $"LỖI ĐỌC NFT V{wallet.SlotNumber}, Wallet={wallet.Address}: {exception}");
                    blocks.Add($"{walletName}: {wallet.Address}\n"
                        + text.Get(language, "NftPortfolioUnavailable"));
                }
            }
            string summary = text.Get(language, "NftPortfolioTitle") + "\n"
                + text.Get(language, "NftSelectedGroup", GroupName(language, selectedGroup)) + "\n\n"
                + (blocks.Count == 0 ? text.Get(language, "NftNoWallet") : string.Join("\n\n", blocks));
            if (summary.Length > 3900)
                throw new InvalidOperationException(text.Get(language, "NftPortfolioTooLong"));
            await telegram.EditButtonsAsync(chatId, messageId, summary,
                [[new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]], cancellationToken);
        }
        catch (Exception exception)
        {
            await NftDiagnosticLog.WriteAsync($"LỖI TỔNG HỢP NFT ChatId={chatId}: {exception}");
            await SendErrorAsync(chatId, language, exception, cancellationToken);
        }
    }

    private async Task ExportAllMintWalletsAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken)
    {
        try
        {
            List<NftWalletExportRow> rows = [];
            foreach (NftWalletGroup group in Enum.GetValues<NftWalletGroup>())
            {
                IReadOnlyList<NftMintWallet> groupWallets = await wallets.GetMintWalletsAsync(chatId, group,
                    cancellationToken);
                rows.AddRange(groupWallets.Select(wallet => new NftWalletExportRow(group.ToString(),
                    wallet.SlotNumber, wallet.Credentials.Address, wallet.Credentials.PrivateKey)));
            }
            rows = rows.OrderBy(x => x.WalletNumber).ToList();
            if (rows.Count == 0) throw new InvalidOperationException(text.Get(language, "NftNoWallet"));

            await telegram.SendDocumentAsync(chatId, NftWalletExcelExporter.Create(rows),
                $"nft-mint-wallets-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx",
                text.Get(language, "NftExportWarning"), cancellationToken);
            await telegram.EditButtonsAsync(chatId, messageId,
                text.Get(language, "NftExportDone", rows.Count),
                [[new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]], cancellationToken);
        }
        catch (Exception exception) { await SendErrorAsync(chatId, language, exception, cancellationToken); }
    }

    private string ModeName(string language, NftMintMode mode) => text.Get(language, mode switch
    {
        NftMintMode.Fixed => "NftModeFixed",
        NftMintMode.Random => "NftModeRandom",
        _ => "NftModeRound"
    });

    private NftWalletGroup SelectedGroup(long chatId) => selectedGroups.GetOrAdd(chatId,
        NftWalletGroup.Free);

    private static NftWalletGroup ReadGroup(string action) =>
        Enum.TryParse(action[(action.IndexOf(':') + 1)..], out NftWalletGroup group)
            ? group : NftWalletGroup.Free;

    internal static int[] ParseWalletSlots(string input)
    {
        HashSet<int> slots = [];
        foreach (string part in input.Split(',', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            string[] range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out int start)
                || start is < 1 or > 1000)
                throw new ArgumentException("Invalid wallet selection.");
            int end = start;
            if (range.Length == 2 && (!int.TryParse(range[1], out end)
                || end < start || end > 1000))
                throw new ArgumentException("Invalid wallet selection.");
            for (int slot = start; slot <= end; slot++) slots.Add(slot);
        }
        if (slots.Count == 0) throw new ArgumentException("Invalid wallet selection.");
        return slots.OrderBy(x => x).ToArray();
    }

    internal static string FormatWalletSlots(IReadOnlyCollection<int> slots)
    {
        int[] ordered = slots.Distinct().OrderBy(x => x).ToArray();
        List<string> ranges = [];
        for (int index = 0; index < ordered.Length;)
        {
            int start = ordered[index], end = start;
            while (++index < ordered.Length && ordered[index] == end + 1) end = ordered[index];
            ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
        }
        return string.Join(",", ranges);
    }

    private string GroupName(string language, NftWalletGroup group) => text.Get(language,
        group == NftWalletGroup.Free ? "NftFreeWallets" : "NftPaidWallets");

    private string WalletLine(string language, NftWalletInfo wallet) =>
        (wallet.SlotNumber == 0 ? text.Get(language, "NftMainWallet")
            : text.Get(language, "NftMintWallet", wallet.SlotNumber))
        + ": " + Short(wallet.Address) + " | "
        + (wallet.BalanceEth.HasValue
            ? wallet.BalanceEth.Value.ToString("0.########", CultureInfo.InvariantCulture)
            : "?") + " ETH";

    private async Task SendErrorAsync(long chatId, string language, Exception exception,
        CancellationToken cancellationToken)
    {
        await NftDiagnosticLog.WriteAsync($"LỖI ChatId={chatId}: {exception}");
        await telegram.SendMessageAsync(chatId,
            text.Get(language, "NftError", exception.Message), cancellationToken);
    }

    private Task ShowPromptAsync(long chatId, long messageId, string message, string language,
        CancellationToken cancellationToken) => telegram.EditButtonsAsync(chatId, messageId, message,
            [[new TelegramInlineButton(text.Get(language, "Back"), "nft:open")]], cancellationToken);

    private long? TakePendingMessageId(long chatId)
    {
        pendingMessageIds.TryRemove(chatId, out long messageId);
        return messageId == 0 ? null : messageId;
    }

    private async Task SafeDeleteAsync(long chatId, long messageId,
        CancellationToken cancellationToken)
    {
        try { await telegram.DeleteMessageAsync(chatId, messageId, cancellationToken); }
        catch (Exception exception)
        {
            await NftDiagnosticLog.WriteAsync(
                $"Không thể tự xoá tin nhắn NFT ChatId={chatId}, MessageId={messageId}: {exception.Message}");
        }
    }

    private static string Short(string address) => address.Length < 12 ? address : address[..6] + "..." + address[^4..];
}
