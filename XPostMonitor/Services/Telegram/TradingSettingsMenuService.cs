using System.Collections.Concurrent;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Telegram;

// Hiển thị và nhận dữ liệu cho /settings bằng nút bấm dễ hiểu.
public sealed class TradingSettingsMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly TradingSettingsService tradingSettings;
    private readonly PremiumService premiumService;
    private readonly BotTextService text;
    private readonly ConcurrentDictionary<long, PendingSetting> pendingInputs = new();

    public TradingSettingsMenuService(TelegramApiClient telegramApi, TradingSettingsService tradingSettings,
        PremiumService premiumService, BotTextService text)
    {
        this.telegramApi = telegramApi;
        this.tradingSettings = tradingSettings;
        this.premiumService = premiumService;
        this.text = text;
    }

    public async Task ShowAsync(long chatId, CancellationToken cancellationToken)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        string message = await tradingSettings.GetSummaryAsync(chatId, language, cancellationToken);
        List<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "EnableDisableAuto"), "settings:toggle")],
            [new TelegramInlineButton(text.Get(language, "ConnectGmgn"), "settings:connect")],
            [new TelegramInlineButton(text.Get(language, "EnterApiKey"), "settings:api")],
            [new TelegramInlineButton(text.Get(language, "CheckGmgn"), "settings:check")]
        ];

        for (int index = 0; index < TradingNetworks.All.Count; index += 2)
        {
            buttons.Add(TradingNetworks.All.Skip(index).Take(2)
                .Select(network => new TelegramInlineButton(network.DisplayName,
                    "settings:network:" + network.Chain)).ToList());
        }
        buttons.Add(CloseButtons(language)[0]);

        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    public async Task HandleCallbackAsync(long chatId, long messageId, string data,
        CancellationToken cancellationToken)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> closeButtons = CloseButtons(language);
        string[] parts = data.Split(':');
        if (parts.Length < 2)
        {
            return;
        }

        switch (parts[1])
        {
            case "close":
                pendingInputs.TryRemove(chatId, out _);
                await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
                return;

            case "toggle":
                await telegramApi.SendButtonsAsync(chatId,
                    await tradingSettings.ToggleAutoCreateAsync(chatId, language, cancellationToken), closeButtons,
                    cancellationToken);
                await ShowAsync(chatId, cancellationToken);
                return;

            case "api":
                pendingInputs[chatId] = new PendingSetting("api", null);
                await telegramApi.SendButtonsAsync(chatId,
                    text.Get(language, "SendApiKey"), closeButtons,
                    cancellationToken);
                return;

            case "connect":
                await telegramApi.SendButtonsAsync(chatId,
                    text.Get(language, "GenerateWarning"),
                    [[new TelegramInlineButton(text.Get(language, "GenerateKey"), "settings:generate")],
                     [new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "settings:close")]], cancellationToken);
                return;

            case "generate":
                string connectionText = await tradingSettings.CreateGmgnConnectionAsync(chatId, language,
                    cancellationToken);
                bool connectionCreated = connectionText.Contains("<pre>", StringComparison.Ordinal);
                if (connectionCreated)
                {
                    await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
                }
                await telegramApi.SendButtonsAsync(chatId, connectionText, closeButtons, cancellationToken,
                    connectionCreated);
                return;

            case "check":
                await telegramApi.SendButtonsAsync(chatId,
                    await tradingSettings.CheckConnectionAsync(chatId, language, cancellationToken), closeButtons,
                    cancellationToken);
                return;

            case "network" when parts.Length == 3:
                await ShowNetworkAsync(chatId, parts[2], language, cancellationToken);
                return;

            case "amount" when parts.Length == 3:
                pendingInputs[chatId] = new PendingSetting("amount", parts[2]);
                TradingNetwork? amountNetwork = TradingNetworks.Find(parts[2]);
                await telegramApi.SendButtonsAsync(chatId,
                    text.Get(language, "SendAmount", amountNetwork?.DisplayName, amountNetwork?.Currency),
                    closeButtons, cancellationToken);
                return;

            case "slippage" when parts.Length == 3:
                pendingInputs[chatId] = new PendingSetting("slippage", parts[2]);
                await telegramApi.SendButtonsAsync(chatId,
                    text.Get(language, "SendSlippage"), closeButtons, cancellationToken);
                return;

            case "back":
                await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
                await ShowAsync(chatId, cancellationToken);
                return;
        }
    }

    // Nếu bot đang chờ một giá trị settings thì dùng tin nhắn này thay vì coi nó là command.
    public async Task<bool> HandlePendingInputAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.Text == null || !pendingInputs.TryRemove(message.Chat.Id, out PendingSetting? pending))
        {
            return false;
        }

        string reply;
        string language = await premiumService.GetLanguageAsync(message.Chat.Id, cancellationToken);
        if (pending.Type == "api")
        {
            await telegramApi.DeleteMessageAsync(message.Chat.Id, message.MessageId, cancellationToken);
            reply = await tradingSettings.SaveApiKeyAsync(message.Chat.Id, message.Text, language, cancellationToken);
        }
        else if (pending.Type == "amount")
        {
            reply = await tradingSettings.SaveBuyAmountAsync(message.Chat.Id, pending.Chain!, message.Text, language,
                cancellationToken);
        }
        else
        {
            reply = await tradingSettings.SaveSlippageAsync(message.Chat.Id, pending.Chain!, message.Text, language,
                cancellationToken);
        }

        await telegramApi.SendButtonsAsync(message.Chat.Id, reply, CloseButtons(language), cancellationToken);
        await ShowAsync(message.Chat.Id, cancellationToken);
        return true;
    }

    private async Task ShowNetworkAsync(long chatId, string chain, string language,
        CancellationToken cancellationToken)
    {
        TradingNetwork? network = TradingNetworks.Find(chain);
        if (network == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "UnsupportedNetworkShort"), cancellationToken);
            return;
        }

        string message = await tradingSettings.GetNetworkSummaryAsync(chatId, chain, language, cancellationToken);
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "ChangeAmount"), "settings:amount:" + chain)],
            [new TelegramInlineButton(text.Get(language, "ChangeSlippage"), "settings:slippage:" + chain)],
            [new TelegramInlineButton(text.Get(language, "Back"), "settings:back")],
            CloseButtons(language)[0]
        ];
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private IReadOnlyList<IReadOnlyList<TelegramInlineButton>> CloseButtons(string language)
    {
        return [[new TelegramInlineButton("✖ " + text.Get(language, "Close"), "settings:close")]];
    }

    private sealed record PendingSetting(string Type, string? Chain);
}
