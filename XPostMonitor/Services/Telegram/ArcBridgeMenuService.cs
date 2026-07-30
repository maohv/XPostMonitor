using System.Collections.Concurrent;
using System.Globalization;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.ArcBridge;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Telegram;

// Menu Bridge doc lap. Chuc nang nay khong kiem tra Premium.
public sealed class ArcBridgeMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly ArcBridgeWalletService walletService;
    private readonly ArcBridgeClient bridgeClient;
    private readonly ArcBridgeTransferService transferService;
    private readonly BotTextService text;
    private readonly PremiumService premiumService;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly ILogger<ArcBridgeMenuService> logger;
    private readonly ConcurrentDictionary<long, bool> pendingWalletImports = new();
    private readonly ConcurrentDictionary<long, bool> pendingAmountInputs = new();
    private readonly ConcurrentDictionary<long, decimal> preparedAmounts = new();
    private readonly ConcurrentDictionary<long, bool> runningBridges = new();

    public ArcBridgeMenuService(TelegramApiClient telegramApi, ArcBridgeWalletService walletService,
        ArcBridgeClient bridgeClient, ArcBridgeTransferService transferService,
        BotTextService text, PremiumService premiumService,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ArcBridgeMenuService> logger)
    {
        this.telegramApi = telegramApi;
        this.walletService = walletService;
        this.bridgeClient = bridgeClient;
        this.transferService = transferService;
        this.text = text;
        this.premiumService = premiumService;
        this.applicationLifetime = applicationLifetime;
        this.logger = logger;
    }

    // Hien nut Bridge ngay khi user gui /start, ke ca user chua co Premium.
    public async Task ShowStartAsync(long chatId, bool hasPremium, string language,
        CancellationToken cancellationToken)
    {
        string message = hasPremium
            ? text.Get(language, "PersonalHelp") + "\n\n" + text.Get(language, "ArcBridgeFree")
            : text.Get(language, "ArcBridgePublicStart");
        await telegramApi.SendButtonsAsync(chatId, message,
            [[new TelegramInlineButton(text.Get(language, "ArcBridgeButton"), "arcbridge:open")]],
            cancellationToken);
    }

    // Xu ly cac nut rieng cua Bridge truoc khi TelegramBotService kiem tra Premium.
    public async Task HandleCallbackAsync(long chatId, long messageId, string data,
        string language, CancellationToken cancellationToken)
    {
        string action = data["arcbridge:".Length..];
        switch (action)
        {
            case "open":
                preparedAmounts.TryRemove(chatId, out _);
                await ShowMenuAsync(chatId, messageId, language, cancellationToken);
                return;

            case "refresh":
                await ShowMenuAsync(chatId, messageId, language, cancellationToken);
                return;

            case "create":
                await CreateWalletAsync(chatId, messageId, language, cancellationToken);
                return;

            case "import":
                await StartImportWalletAsync(chatId, language, cancellationToken);
                return;

            case "prepare":
            case "amount":
                await StartAmountInputAsync(chatId, language, cancellationToken);
                return;

            case "confirm":
                await StartBridgeAsync(chatId, messageId, language, cancellationToken);
                return;

            case "resume":
                await StartBridgeAsync(chatId, messageId, language, cancellationToken);
                return;

            case "claim":
                await ShowClaimConfirmationAsync(chatId, messageId, language, cancellationToken);
                return;

            case "claimconfirm":
                await StartClaimAsync(chatId, messageId, language, cancellationToken);
                return;

            case "close":
                await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
                return;
        }
    }

    // Nhan private key chi khi user vua bam Import trong menu Bridge.
    public async Task<bool> HandlePendingInputAsync(TelegramMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Text == null)
        {
            return false;
        }

        string language = await premiumService.GetLanguageAsync(message.Chat.Id, cancellationToken);
        if (pendingWalletImports.TryRemove(message.Chat.Id, out _))
        {
            await HandleWalletImportAsync(message, language, cancellationToken);
            return true;
        }
        if (pendingAmountInputs.TryRemove(message.Chat.Id, out _))
        {
            await HandleAmountInputAsync(message, language, cancellationToken);
            return true;
        }
        return false;
    }

    private async Task HandleWalletImportAsync(TelegramMessage message, string language,
        CancellationToken cancellationToken)
    {
        await telegramApi.DeleteMessageAsync(message.Chat.Id, message.MessageId, cancellationToken);
        try
        {
            EvmWalletCredentials? existing = await walletService.GetAsync(message.Chat.Id,
                cancellationToken);
            if (existing != null)
            {
                await telegramApi.SendMessageAsync(message.Chat.Id,
                    text.Get(language, "ArcBridgeWalletAlreadyExists"), cancellationToken);
                await ShowMenuAsync(message.Chat.Id, null, language, cancellationToken);
                return;
            }

            EvmWalletCredentials wallet = await walletService.ImportAsync(message.Chat.Id,
                message.Text!, cancellationToken);
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "ArcBridgeWalletImported", wallet.Address), cancellationToken);
            await ShowMenuAsync(message.Chat.Id, null, language, cancellationToken);
        }
        catch (ArgumentException)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "InvalidEvmPrivateKey"), cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "EvmWalletImportFailed", exception.Message), cancellationToken);
        }
    }

    private async Task HandleAmountInputAsync(TelegramMessage message, string language,
        CancellationToken cancellationToken)
    {
        string value = message.Text!.Trim().Replace(',', '.');
        bool isValid = decimal.TryParse(value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out decimal amount)
            && amount > 0
            && amount <= 1_000_000m
            && decimal.Round(amount, 6, MidpointRounding.ToZero) == amount;
        if (!isValid)
        {
            pendingAmountInputs[message.Chat.Id] = true;
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "ArcBridgeInvalidAmount"), cancellationToken);
            return;
        }

        await telegramApi.DeleteMessageAsync(message.Chat.Id, message.MessageId, cancellationToken);
        preparedAmounts[message.Chat.Id] = amount;
        await ShowConfirmationAsync(message.Chat.Id, null, language, cancellationToken);
    }

    private async Task ShowMenuAsync(long chatId, long? messageId, string language,
        CancellationToken cancellationToken, string? notice = null)
    {
        EvmWalletCredentials? wallet = await walletService.GetAsync(chatId, cancellationToken);
        if (wallet == null)
        {
            string missing = AddNotice(text.Get(language, "ArcBridgeWalletMissing"), notice);
            IReadOnlyList<IReadOnlyList<TelegramInlineButton>> missingButtons =
            [
                [new TelegramInlineButton(text.Get(language, "CreateEvmWallet"), "arcbridge:create"),
                 new TelegramInlineButton(text.Get(language, "ImportEvmPrivateKey"), "arcbridge:import")],
                [new TelegramInlineButton(text.Get(language, "Close"), "arcbridge:close")]
            ];
            await SendOrEditAsync(chatId, messageId, missing, missingButtons, cancellationToken);
            return;
        }

        try
        {
            ArcBridgeWalletStatus status = await bridgeClient.GetWalletStatusAsync(wallet,
                cancellationToken);
            ArcBridgeTransfer? pending =
                await transferService.GetPendingAsync(chatId, cancellationToken);
            string message = AddNotice(text.Get(language, "ArcBridgeMenu",
                status.WalletAddress,
                Format(status.BaseUsdc),
                Format(status.BaseEth),
                Format(status.GatewayUsdc)), notice);
            if (pending != null)
            {
                message += "\n\n" + text.Get(language, "ArcBridgePendingTransfer");
            }

            List<IReadOnlyList<TelegramInlineButton>> buttons = [];
            if (pending == null)
            {
                buttons.Add([new TelegramInlineButton(text.Get(language, "ArcBridgeEnterAmount"),
                    "arcbridge:amount")]);
            }
            else
            {
                buttons.Add([new TelegramInlineButton(text.Get(language, "ArcBridgeResume"),
                    "arcbridge:resume")]);
            }
            if (pending == null && status.GatewayUsdc > 0.011m)
            {
                buttons.Add([new TelegramInlineButton(text.Get(language, "ArcBridgeClaimPending"),
                    "arcbridge:claim")]);
            }
            buttons.Add(
                [new TelegramInlineButton(text.Get(language, "Refresh"), "arcbridge:refresh"),
                 new TelegramInlineButton(text.Get(language, "Close"), "arcbridge:close")]);
            await SendOrEditAsync(chatId, messageId, message, buttons, cancellationToken);
        }
        catch (Exception exception)
        {
            string message = AddNotice(text.Get(language, "ArcBridgeStatusFailed",
                exception.Message), notice);
            await SendOrEditAsync(chatId, messageId, message,
                [[new TelegramInlineButton(text.Get(language, "Refresh"), "arcbridge:refresh"),
                  new TelegramInlineButton(text.Get(language, "Close"), "arcbridge:close")]],
                cancellationToken);
        }
    }

    private async Task ShowConfirmationAsync(long chatId, long? messageId, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await walletService.GetAsync(chatId, cancellationToken);
        if (wallet == null)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (await transferService.GetPendingAsync(chatId, cancellationToken) != null)
        {
            if (messageId.HasValue)
            {
                await StartBridgeAsync(chatId, messageId.Value, language, cancellationToken);
            }
            return;
        }
        if (!preparedAmounts.TryGetValue(chatId, out decimal amount))
        {
            await StartAmountInputAsync(chatId, language, cancellationToken);
            return;
        }

        try
        {
            ArcBridgeStatus status = await bridgeClient.GetStatusAsync(wallet, amount,
                cancellationToken);
            if (status.BaseUsdc < status.RequiredBaseUsdc)
            {
                await ShowMenuAsync(chatId, messageId, language, cancellationToken,
                    text.Get(language, "ArcBridgeInsufficientUsdc", Format(status.RequiredBaseUsdc)));
                return;
            }
            if (status.BaseEth <= 0)
            {
                await ShowMenuAsync(chatId, messageId, language, cancellationToken,
                    text.Get(language, "ArcBridgeInsufficientEth"));
                return;
            }

            string message = text.Get(language, "ArcBridgeConfirm",
                status.WalletAddress,
                Format(status.GrossAmount),
                Format(status.PlatformFee),
                Format(status.CircleMaxFee),
                Format(status.ExpectedReceive),
                Format(status.RequiredBaseUsdc));
            await SendOrEditAsync(chatId, messageId, message,
                [[new TelegramInlineButton(text.Get(language, "Confirm"), "arcbridge:confirm")],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "arcbridge:open")]],
                cancellationToken);
        }
        catch (Exception exception)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken,
                text.Get(language, "ArcBridgeStatusFailed", exception.Message));
        }
    }

    private async Task ShowClaimConfirmationAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await walletService.GetAsync(chatId, cancellationToken);
        if (wallet == null)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (await transferService.GetPendingAsync(chatId, cancellationToken) != null)
        {
            await StartBridgeAsync(chatId, messageId, language, cancellationToken);
            return;
        }

        ArcBridgeClaimStatus status = await bridgeClient.GetClaimStatusAsync(wallet,
            cancellationToken);
        if (status.GatewayUsdc <= status.CircleMaxFee)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken,
                text.Get(language, "ArcBridgeNothingToClaim"));
            return;
        }

        await telegramApi.EditButtonsAsync(chatId, messageId,
            text.Get(language, "ArcBridgeClaimConfirm", Format(status.GatewayUsdc),
                Format(status.CircleMaxFee)),
            [[new TelegramInlineButton(text.Get(language, "Confirm"),
                "arcbridge:claimconfirm")],
             [new TelegramInlineButton(text.Get(language, "Cancel"), "arcbridge:open")]],
            cancellationToken);
    }

    private async Task CreateWalletAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? existing = await walletService.GetAsync(chatId, cancellationToken);
        if (existing != null)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken,
                text.Get(language, "ArcBridgeWalletAlreadyExists"));
            return;
        }

        try
        {
            EvmWalletCreated wallet = await walletService.CreateAsync(chatId, cancellationToken);
            if (wallet.IsNew && wallet.PrivateKey != null)
            {
                await telegramApi.SendButtonsAsync(chatId,
                    text.Get(language, "ArcBridgeWalletCreated", wallet.Address, wallet.PrivateKey),
                    [[new TelegramInlineButton(text.Get(language, "Close"), "arcbridge:close")]],
                    cancellationToken, true);
            }
            await ShowMenuAsync(chatId, messageId, language, cancellationToken);
        }
        catch (Exception exception)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken,
                text.Get(language, "EvmWalletFailed", exception.Message));
        }
    }

    private async Task StartImportWalletAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? existing = await walletService.GetAsync(chatId, cancellationToken);
        if (existing != null)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "ArcBridgeWalletAlreadyExists"), cancellationToken);
            return;
        }

        pendingWalletImports[chatId] = true;
        await telegramApi.SendMessageAsync(chatId,
            text.Get(language, "ArcBridgeSendPrivateKey"), cancellationToken);
    }

    private async Task StartAmountInputAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        if (await transferService.GetPendingAsync(chatId, cancellationToken) != null)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "ArcBridgePendingTransfer"), cancellationToken);
            return;
        }

        pendingAmountInputs[chatId] = true;
        await telegramApi.SendMessageAsync(chatId,
            text.Get(language, "ArcBridgeSendAmount"), cancellationToken);
    }

    private async Task StartBridgeAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await walletService.GetAsync(chatId, cancellationToken);
        if (wallet == null)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        ArcBridgeTransfer? pending = await transferService.GetPendingAsync(chatId,
            cancellationToken);
        decimal amount = 0;
        if (pending == null && !preparedAmounts.TryGetValue(chatId, out amount))
        {
            await StartAmountInputAsync(chatId, language, cancellationToken);
            return;
        }
        if (!runningBridges.TryAdd(chatId, true))
        {
            await telegramApi.EditButtonsAsync(chatId, messageId,
                text.Get(language, "ArcBridgeAlreadyRunning"),
                [[new TelegramInlineButton(text.Get(language, "Refresh"), "arcbridge:refresh")]],
                cancellationToken);
            return;
        }

        await telegramApi.EditButtonsAsync(chatId, messageId,
            text.Get(language, "ArcBridgeStarted"),
            Array.Empty<IReadOnlyList<TelegramInlineButton>>(), cancellationToken);
        preparedAmounts.TryRemove(chatId, out _);
        _ = RunBridgeAsync(chatId, messageId, language, wallet, amount);
    }

    private async Task StartClaimAsync(long chatId, long messageId, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await walletService.GetAsync(chatId, cancellationToken);
        if (wallet == null)
        {
            await ShowMenuAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (await transferService.GetPendingAsync(chatId, cancellationToken) != null)
        {
            await StartBridgeAsync(chatId, messageId, language, cancellationToken);
            return;
        }
        if (!runningBridges.TryAdd(chatId, true))
        {
            await telegramApi.EditButtonsAsync(chatId, messageId,
                text.Get(language, "ArcBridgeAlreadyRunning"),
                [[new TelegramInlineButton(text.Get(language, "Refresh"), "arcbridge:refresh")]],
                cancellationToken);
            return;
        }

        await telegramApi.EditButtonsAsync(chatId, messageId,
            text.Get(language, "ArcBridgeClaimStarted"),
            Array.Empty<IReadOnlyList<TelegramInlineButton>>(), cancellationToken);
        _ = RunClaimAsync(chatId, messageId, language, wallet);
    }

    // Chay nen de Circle co the cho finality ma khong chan bot Telegram cua user khac.
    private async Task RunBridgeAsync(long chatId, long messageId, string language,
        EvmWalletCredentials wallet, decimal amount)
    {
        CancellationToken cancellationToken = applicationLifetime.ApplicationStopping;
        try
        {
            ArcBridgeResult result = await bridgeClient.BridgeAsync(chatId, wallet, amount,
                stage => TryReportStageAsync(chatId, messageId, language, stage, cancellationToken),
                cancellationToken);
            await telegramApi.EditButtonsAsync(chatId, messageId,
                text.Get(language, "ArcBridgeSuccess", Format(result.ReceivedUsdc),
                    result.BaseExplorerUrl, result.ArcExplorerUrl),
                [[new TelegramInlineButton(text.Get(language, "ArcBridgeButton"), "arcbridge:open")]],
                cancellationToken);
            logger.LogInformation("Arc Bridge thành công. ChatId: {ChatId}, BaseTx: {BaseTx}, ArcTx: {ArcTx}",
                chatId, result.BaseTransactionHash, result.ArcTransactionHash);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Arc Bridge thất bại. ChatId: {ChatId}", chatId);
            await TryShowFailureAsync(chatId, messageId, language, exception.Message,
                cancellationToken);
        }
        finally
        {
            runningBridges.TryRemove(chatId, out _);
        }
    }

    private async Task RunClaimAsync(long chatId, long messageId, string language,
        EvmWalletCredentials wallet)
    {
        CancellationToken cancellationToken = applicationLifetime.ApplicationStopping;
        try
        {
            ArcBridgeResult result = await bridgeClient.ClaimAvailableAsync(wallet,
                stage => TryReportStageAsync(chatId, messageId, language, stage, cancellationToken),
                cancellationToken);
            await telegramApi.EditButtonsAsync(chatId, messageId,
                text.Get(language, "ArcBridgeClaimSuccess", Format(result.ReceivedUsdc),
                    result.ArcExplorerUrl),
                [[new TelegramInlineButton(text.Get(language, "ArcBridgeButton"), "arcbridge:open")]],
                cancellationToken);
            logger.LogInformation("Arc Bridge claim thành công. ChatId: {ChatId}, ArcTx: {ArcTx}",
                chatId, result.ArcTransactionHash);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Arc Bridge claim thất bại. ChatId: {ChatId}", chatId);
            await TryShowFailureAsync(chatId, messageId, language, exception.Message,
                cancellationToken);
        }
        finally
        {
            runningBridges.TryRemove(chatId, out _);
        }
    }

    private async Task TryReportStageAsync(long chatId, long messageId, string language,
        ArcBridgeStage stage, CancellationToken cancellationToken)
    {
        try
        {
            await telegramApi.EditButtonsAsync(chatId, messageId,
                text.Get(language, "ArcBridgeStage" + stage),
                Array.Empty<IReadOnlyList<TelegramInlineButton>>(), cancellationToken);
        }
        catch
        {
            // Loi cap nhat Telegram khong duoc phep lam dung giao dich dang chay.
        }
    }

    private async Task TryShowFailureAsync(long chatId, long messageId, string language,
        string error, CancellationToken cancellationToken)
    {
        try
        {
            await telegramApi.EditButtonsAsync(chatId, messageId,
                text.Get(language, "ArcBridgeFailed", error),
                [[new TelegramInlineButton(text.Get(language, "ArcBridgeButton"), "arcbridge:open")]],
                cancellationToken);
        }
        catch
        {
            // Giao dich co the da gui; user mo lai /start de kiem tra Gateway balance.
        }
    }

    private async Task SendOrEditAsync(long chatId, long? messageId, string message,
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons,
        CancellationToken cancellationToken)
    {
        if (messageId.HasValue)
        {
            await telegramApi.EditButtonsAsync(chatId, messageId.Value, message, buttons,
                cancellationToken);
        }
        else
        {
            await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
        }
    }

    private static string AddNotice(string message, string? notice)
    {
        return string.IsNullOrWhiteSpace(notice) ? message : notice + "\n\n" + message;
    }

    private static string Format(decimal value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
