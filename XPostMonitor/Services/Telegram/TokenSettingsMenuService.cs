using System.Collections.Concurrent;
using System.Globalization;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Launchpads.DyorStable;
using XPostMonitor.Services.Launchpads.FourMeme;
using XPostMonitor.Services.Launchpads.LongRobinhood;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Telegram;

// Hiển thị cài đặt ví, số tiền và Auto Create bằng nút bấm.
public sealed class TokenSettingsMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly TokenSettingsService tokenSettings;
    private readonly PremiumService premiumService;
    private readonly EvmWalletService evmWalletService;
    private readonly FourMemeClient fourMemeClient;
    private readonly DyorStableClient dyorStableClient;
    private readonly LongRobinhoodClient longRobinhoodClient;
    private readonly BotTextService text;
    private readonly ConcurrentDictionary<long, string> pendingAmounts = new();

    public TokenSettingsMenuService(TelegramApiClient telegramApi, TokenSettingsService tokenSettings,
        PremiumService premiumService, EvmWalletService evmWalletService, FourMemeClient fourMemeClient,
        DyorStableClient dyorStableClient, LongRobinhoodClient longRobinhoodClient, BotTextService text)
    {
        this.telegramApi = telegramApi;
        this.tokenSettings = tokenSettings;
        this.premiumService = premiumService;
        this.evmWalletService = evmWalletService;
        this.fourMemeClient = fourMemeClient;
        this.dyorStableClient = dyorStableClient;
        this.longRobinhoodClient = longRobinhoodClient;
        this.text = text;
    }

    public async Task ShowAsync(long chatId, CancellationToken cancellationToken, string? notice = null)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        string message = await tokenSettings.GetSummaryAsync(chatId, language, cancellationToken);
        if (!string.IsNullOrWhiteSpace(notice))
        {
            message = notice + "\n\n" + message;
        }
        List<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "EnableDisableAuto"), "settings:toggle")],
            [new TelegramInlineButton(text.Get(language, "EvmWallet"), "settings:evm")],
            [new TelegramInlineButton(text.Get(language, "GmgnAndAutoTrading"), "trading:show")],
            [new TelegramInlineButton(text.Get(language, "DefaultBuyAmounts"), "settings:amounts")]
        ];
        buttons.Add(CloseButtons(language)[0]);
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    public async Task HandleCallbackAsync(long chatId, long messageId, string data,
        CancellationToken cancellationToken)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        string[] parts = data.Split(':');
        if (parts.Length < 2)
        {
            return;
        }

        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
        switch (parts[1])
        {
            case "close":
                pendingAmounts.TryRemove(chatId, out _);
                return;

            case "toggle":
                string result = await tokenSettings.ToggleAutoCreateAsync(chatId, language, cancellationToken);
                await ShowAsync(chatId, cancellationToken, result);
                return;

            case "network" when parts.Length == 3:
                await ShowNetworkAsync(chatId, parts[2], language, cancellationToken);
                return;

            case "amounts":
                await ShowBuyAmountsAsync(chatId, language, cancellationToken);
                return;

            case "amount" when parts.Length == 3:
                pendingAmounts[chatId] = parts[2];
                LaunchpadNetwork? network = LaunchpadCatalog.Find(parts[2]);
                string amountMessage = text.Get(language, "SendAmount", network?.DisplayName, network?.Currency);
                if (network != null)
                {
                    amountMessage += "\n" + text.Get(language, "MinimumBuyAmountInfo",
                        network.MinimumBuyAmount, network.Currency);
                }
                await telegramApi.SendButtonsAsync(chatId, amountMessage, CloseButtons(language), cancellationToken);
                return;

            case "evm":
                await ShowEvmWalletAsync(chatId, language, cancellationToken);
                return;

            case "evmcreate":
                await CreateEvmWalletAsync(chatId, language, cancellationToken);
                return;

            case "evmexport":
                await ShowEvmPrivateKeyAsync(chatId, language, cancellationToken);
                return;

            case "fourmeme":
                await CheckFourMemeAsync(chatId, language, cancellationToken);
                return;

            case "dyorstable":
                await CheckDyorStableAsync(chatId, language, cancellationToken);
                return;

            case "longrobinhood":
                await CheckLongRobinhoodAsync(chatId, language, cancellationToken);
                return;

            case "back":
                await ShowAsync(chatId, cancellationToken);
                return;
        }
    }

    public async Task<bool> HandlePendingInputAsync(TelegramMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Text == null || !pendingAmounts.TryRemove(message.Chat.Id, out string? chain))
        {
            return false;
        }

        string language = await premiumService.GetLanguageAsync(message.Chat.Id, cancellationToken);
        string reply = await tokenSettings.SaveBuyAmountAsync(message.Chat.Id, chain, message.Text, language,
            cancellationToken);
        await telegramApi.SendButtonsAsync(message.Chat.Id, reply, CloseButtons(language), cancellationToken);
        await ShowAsync(message.Chat.Id, cancellationToken);
        return true;
    }

    private async Task ShowNetworkAsync(long chatId, string chain, string language,
        CancellationToken cancellationToken)
    {
        if (LaunchpadCatalog.Find(chain) == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "UnsupportedNetworkShort"),
                cancellationToken);
            return;
        }

        LaunchpadNetwork network = LaunchpadCatalog.Find(chain)!;
        string message = await tokenSettings.GetNetworkSummaryAsync(chatId, chain, language, cancellationToken);
        List<IReadOnlyList<TelegramInlineButton>> buttons = [];
        if (network.MinimumBuyAmount > 0)
        {
            buttons.Add([new TelegramInlineButton(text.Get(language, "ChangeAmount"), "settings:amount:" + chain)]);
        }
        buttons.Add([new TelegramInlineButton(text.Get(language, "Back"), "settings:amounts")]);
        buttons.Add(CloseButtons(language)[0]);
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private async Task ShowBuyAmountsAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<TelegramInlineButton>> buttons = [];
        foreach (LaunchpadNetwork network in LaunchpadCatalog.All)
        {
            buttons.Add([new TelegramInlineButton(network.DisplayName,
                "settings:network:" + network.Chain)]);
        }
        buttons.Add([new TelegramInlineButton(text.Get(language, "Back"), "settings:back")]);
        buttons.Add(CloseButtons(language)[0]);
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseBuyAmountNetwork"),
            buttons, cancellationToken);
    }

    private async Task ShowEvmWalletAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await evmWalletService.GetAsync(chatId, cancellationToken);
        if (wallet == null)
        {
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "EvmWalletMissing"),
                [[new TelegramInlineButton(text.Get(language, "CreateEvmWallet"), "settings:evmcreate")],
                 CloseButtons(language)[0]], cancellationToken);
            return;
        }

        string balances = await GetWalletBalancesAsync(wallet.Address, cancellationToken);
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "EvmWalletReady", wallet.Address, balances),
            [[new TelegramInlineButton(text.Get(language, "ShowPrivateKey"), "settings:evmexport")],
             CloseButtons(language)[0]], cancellationToken, true);
    }

    private async Task CreateEvmWalletAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        try
        {
            EvmWalletCreated wallet = await evmWalletService.CreateAsync(chatId, cancellationToken);
            string balances = await GetWalletBalancesAsync(wallet.Address, cancellationToken);
            string message = wallet.IsNew
                ? text.Get(language, "EvmWalletCreated", wallet.Address, wallet.PrivateKey, balances)
                : text.Get(language, "EvmWalletReady", wallet.Address, balances);
            await telegramApi.SendButtonsAsync(chatId, message, CloseButtons(language), cancellationToken, true);
        }
        catch (Exception exception)
        {
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "EvmWalletFailed", exception.Message),
                CloseButtons(language), cancellationToken);
        }
    }

    private async Task ShowEvmPrivateKeyAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await evmWalletService.GetAsync(chatId, cancellationToken);
        if (wallet == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "EvmWalletMissing"), cancellationToken);
            return;
        }

        await telegramApi.SendButtonsAsync(chatId,
            text.Get(language, "EvmPrivateKey", wallet.Address, wallet.PrivateKey), CloseButtons(language),
            cancellationToken, true);
    }

    private async Task CheckFourMemeAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        try
        {
            EvmWalletCredentials? wallet = await evmWalletService.GetAsync(chatId, cancellationToken);
            FourMemeConnectionResult result = await fourMemeClient.CheckAsync(wallet?.Address, cancellationToken);
            string balance = result.Balance?.ToString("0.########") ?? text.Get(language, "NotConfigured");
            await telegramApi.SendButtonsAsync(chatId,
                text.Get(language, "FourMemeConnected", result.RaisedToken, result.LaunchFee, balance,
                    text.Get(language, result.EnableRealTransactions ? "LiveMode" : "DryRunMode")),
                CloseButtons(language), cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendButtonsAsync(chatId,
                text.Get(language, "FourMemeFailed", exception.Message), CloseButtons(language), cancellationToken);
        }
    }

    private async Task CheckDyorStableAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        try
        {
            EvmWalletCredentials? wallet = await evmWalletService.GetAsync(chatId, cancellationToken);
            DyorStableConnectionResult result = await dyorStableClient.CheckAsync(wallet?.Address,
                cancellationToken);
            string balance = result.Balance?.ToString("0.########", CultureInfo.InvariantCulture)
                ?? text.Get(language, "NotConfigured");
            await telegramApi.SendButtonsAsync(chatId,
                text.Get(language, "DyorStableConnected", result.CorrectChain ? "OK" : "ERROR",
                    result.FactoryFound ? "OK" : "ERROR", balance,
                    text.Get(language, result.PinataConfigured ? "Configured" : "NotConfigured"),
                    text.Get(language,
                        result.EnableRealTransactions ? "LiveMode" : "DryRunMode")),
                CloseButtons(language), cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendButtonsAsync(chatId,
                text.Get(language, "DyorStableFailed", exception.Message), CloseButtons(language),
                cancellationToken);
        }
    }

    private async Task CheckLongRobinhoodAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        try
        {
            EvmWalletCredentials? wallet = await evmWalletService.GetAsync(chatId, cancellationToken);
            LongRobinhoodConnectionResult result = await longRobinhoodClient.CheckAsync(wallet?.Address,
                cancellationToken);
            string balance = result.Balance?.ToString("0.########", CultureInfo.InvariantCulture)
                ?? text.Get(language, "NotConfigured");
            await telegramApi.SendButtonsAsync(chatId,
                text.Get(language, "LongRobinhoodConnected", result.CorrectChain ? "OK" : "ERROR",
                    result.LauncherFound ? "OK" : "ERROR", result.Paused ? "PAUSED" : "OK", balance,
                    text.Get(language, result.EnableRealTransactions ? "LiveMode" : "DryRunMode")),
                CloseButtons(language), cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendButtonsAsync(chatId,
                text.Get(language, "LongRobinhoodFailed", exception.Message), CloseButtons(language),
                cancellationToken);
        }
    }

    private async Task<string> GetWalletBalancesAsync(string address, CancellationToken cancellationToken)
    {
        IReadOnlyList<EvmNativeBalance> balances = await evmWalletService.GetBalancesAsync(address,
            cancellationToken);
        return string.Join("\n", balances.Select(balance => balance.Network + ": "
            + (balance.Amount?.ToString("0.########", CultureInfo.InvariantCulture) ?? "-")
            + " " + balance.Currency));
    }

    private IReadOnlyList<IReadOnlyList<TelegramInlineButton>> CloseButtons(string language)
    {
        return [[new TelegramInlineButton("✖ " + text.Get(language, "Close"), "settings:close")]];
    }
}
