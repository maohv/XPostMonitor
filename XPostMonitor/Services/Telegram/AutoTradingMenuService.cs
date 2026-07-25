using System.Collections.Concurrent;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Telegram;

// Màn hình GMGN và nhiều mức TP trong /settings.
public sealed class AutoTradingMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly AutoTradingSettingsService settings;
    private readonly PremiumService premiumService;
    private readonly BotTextService text;
    private readonly ConcurrentDictionary<long, string> pendingInputs = new();

    public AutoTradingMenuService(TelegramApiClient telegramApi, AutoTradingSettingsService settings,
        PremiumService premiumService, BotTextService text)
    {
        this.telegramApi = telegramApi;
        this.settings = settings;
        this.premiumService = premiumService;
        this.text = text;
    }

    public async Task ShowAsync(long chatId, CancellationToken cancellationToken, string? notice = null)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        string message = await settings.GetSummaryAsync(chatId, language, cancellationToken);
        if (!string.IsNullOrWhiteSpace(notice))
        {
            message = notice + "\n\n" + message;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "ConnectGmgn"), "trading:connect")],
            [new TelegramInlineButton(text.Get(language, "EnterApiKey"), "trading:api")],
            [new TelegramInlineButton(text.Get(language, "CheckGmgn"), "trading:check")],
            [new TelegramInlineButton(text.Get(language, "AddTakeProfit"), "trading:addtp")]
        ];

        List<TakeProfitSetting> levels = await settings.GetTakeProfitsAsync(chatId, cancellationToken);
        buttons.AddRange(levels.Select(level => (IReadOnlyList<TelegramInlineButton>)
            [new TelegramInlineButton(text.Get(language, "DeleteTakeProfit",
                level.ProfitPercent, level.SellPercent), "trading:deletetp:" + level.Id)]));
        buttons.Add([new TelegramInlineButton(text.Get(language, "Back"), "trading:back")]);
        buttons.Add([new TelegramInlineButton(text.Get(language, "Close"), "trading:close")]);
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    public async Task HandleCallbackAsync(long chatId, long messageId, string data,
        CancellationToken cancellationToken)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        string[] parts = data.Split(':');
        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);

        if (parts.Length < 2 || parts[1] == "close")
        {
            pendingInputs.TryRemove(chatId, out _);
            return;
        }
        if (parts[1] == "show")
        {
            await ShowAsync(chatId, cancellationToken);
            return;
        }
        if (parts[1] == "back")
        {
            return;
        }
        if (parts[1] == "connect")
        {
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "GenerateWarning"),
                [[new TelegramInlineButton(text.Get(language, "GenerateKey"), "trading:generate")],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "trading:close")]], cancellationToken);
            return;
        }
        if (parts[1] == "generate")
        {
            string result = await settings.CreateConnectionAsync(chatId, language, cancellationToken);
            await telegramApi.SendButtonsAsync(chatId, result,
                [[new TelegramInlineButton(text.Get(language, "Close"), "trading:close")]],
                cancellationToken, true);
            return;
        }
        if (parts[1] == "api")
        {
            pendingInputs[chatId] = "api";
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "SendApiKey"), cancellationToken);
            return;
        }
        if (parts[1] == "check")
        {
            await ShowAsync(chatId, cancellationToken,
                await settings.CheckConnectionAsync(chatId, language, cancellationToken));
            return;
        }
        if (parts[1] == "addtp")
        {
            pendingInputs[chatId] = "tp";
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "SendTakeProfit"), cancellationToken);
            return;
        }
        if (parts[1] == "deletetp" && parts.Length == 3 && int.TryParse(parts[2], out int id))
        {
            await settings.RemoveTakeProfitAsync(chatId, id, cancellationToken);
            await ShowAsync(chatId, cancellationToken, text.Get(language, "TakeProfitDeleted"));
        }
    }

    public async Task<bool> HandlePendingInputAsync(TelegramMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Text == null || !pendingInputs.TryRemove(message.Chat.Id, out string? type))
        {
            return false;
        }

        string language = await premiumService.GetLanguageAsync(message.Chat.Id, cancellationToken);
        string result;
        if (type == "api")
        {
            await telegramApi.DeleteMessageAsync(message.Chat.Id, message.MessageId, cancellationToken);
            result = await settings.SaveApiKeyAsync(message.Chat.Id, message.Text, language, cancellationToken);
        }
        else
        {
            result = await settings.AddTakeProfitAsync(message.Chat.Id, message.Text, language, cancellationToken);
        }

        await ShowAsync(message.Chat.Id, cancellationToken, result);
        return true;
    }
}
