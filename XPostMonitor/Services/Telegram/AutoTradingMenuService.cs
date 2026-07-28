using System.Collections.Concurrent;
using XPostMonitor.Configuration;
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
    private readonly TradingWorkersOptions workerOptions;
    private readonly ConcurrentDictionary<long, string> pendingInputs = new();

    public AutoTradingMenuService(TelegramApiClient telegramApi, AutoTradingSettingsService settings,
        PremiumService premiumService, BotTextService text, TradingWorkersOptions workerOptions)
    {
        this.telegramApi = telegramApi;
        this.settings = settings;
        this.premiumService = premiumService;
        this.text = text;
        this.workerOptions = workerOptions;
    }

    public async Task ShowAsync(long chatId, CancellationToken cancellationToken, string? notice = null)
    {
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        string message = await settings.GetSummaryAsync(chatId, language, cancellationToken);
        if (!string.IsNullOrWhiteSpace(notice))
        {
            message = notice + "\n\n" + message;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = Enumerable.Range(1, workerOptions.MaxWorkers)
            .Select(slot => (IReadOnlyList<TelegramInlineButton>)[new TelegramInlineButton(
                text.Get(language, "Worker") + " " + slot + " GMGN", "trading:worker:" + slot)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "AddTakeProfit"), "trading:addtp")]);

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
        if (parts[1] == "worker" && parts.Length == 3 && TryReadSlot(parts[2], out int workerSlot))
        {
            await ShowWorkerAsync(chatId, workerSlot, language, cancellationToken);
            return;
        }
        if (parts[1] == "back")
        {
            return;
        }
        if (parts[1] == "connect")
        {
            int slotNumber = ReadSlot(parts);
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "GenerateWarning"),
                [[new TelegramInlineButton(text.Get(language, "GenerateKey"),
                    "trading:generate:" + slotNumber)],
                 [new TelegramInlineButton(text.Get(language, "Back"), "trading:worker:" + slotNumber)],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "trading:close")]], cancellationToken);
            return;
        }
        if (parts[1] == "generate")
        {
            int slotNumber = ReadSlot(parts);
            string result = await settings.CreateConnectionAsync(chatId, slotNumber, language, cancellationToken);
            await telegramApi.SendButtonsAsync(chatId, result,
                [[new TelegramInlineButton(text.Get(language, "Back"), "trading:worker:" + slotNumber)],
                 [new TelegramInlineButton(text.Get(language, "Close"), "trading:close")]],
                cancellationToken, true);
            return;
        }
        if (parts[1] == "api")
        {
            int slotNumber = ReadSlot(parts);
            pendingInputs[chatId] = "api:" + slotNumber;
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "SendApiKey"), cancellationToken);
            return;
        }
        if (parts[1] == "check")
        {
            int slotNumber = ReadSlot(parts);
            await ShowWorkerAsync(chatId, slotNumber, language, cancellationToken,
                await settings.CheckConnectionAsync(chatId, slotNumber, language, cancellationToken));
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
        if (type.StartsWith("api:", StringComparison.Ordinal)
            && int.TryParse(type[4..], out int slotNumber))
        {
            await telegramApi.DeleteMessageAsync(message.Chat.Id, message.MessageId, cancellationToken);
            result = await settings.SaveApiKeyAsync(message.Chat.Id, slotNumber, message.Text, language,
                cancellationToken);
            await ShowWorkerAsync(message.Chat.Id, slotNumber, language, cancellationToken, result);
            return true;
        }
        else
        {
            result = await settings.AddTakeProfitAsync(message.Chat.Id, message.Text, language, cancellationToken);
        }

        await ShowAsync(message.Chat.Id, cancellationToken, result);
        return true;
    }

    private async Task ShowWorkerAsync(long chatId, int slotNumber, string language,
        CancellationToken cancellationToken, string? notice = null)
    {
        IReadOnlyList<GmgnWorkerState> states = await settings.GetWorkerStatesAsync(chatId, cancellationToken);
        GmgnWorkerState state = states.First(item => item.SlotNumber == slotNumber);
        string message = text.Get(language, "Worker") + " " + slotNumber + " GMGN\n\n"
            + text.Get(language, state.HasCredentials ? "Configured" : "NotConfigured");
        if (!string.IsNullOrWhiteSpace(notice))
        {
            message = notice + "\n\n" + message;
        }

        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "ConnectGmgn"), "trading:connect:" + slotNumber)],
            [new TelegramInlineButton(text.Get(language, "EnterApiKey"), "trading:api:" + slotNumber)],
            [new TelegramInlineButton(text.Get(language, "CheckGmgn"), "trading:check:" + slotNumber)],
            [new TelegramInlineButton(text.Get(language, "Back"), "trading:show")],
            [new TelegramInlineButton(text.Get(language, "Close"), "trading:close")]
        ];
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private int ReadSlot(string[] parts)
    {
        return parts.Length >= 3 && TryReadSlot(parts[2], out int slotNumber) ? slotNumber : 1;
    }

    private bool TryReadSlot(string value, out int slotNumber)
    {
        return int.TryParse(value, out slotNumber) && slotNumber >= 1
            && slotNumber <= workerOptions.MaxWorkers;
    }
}
