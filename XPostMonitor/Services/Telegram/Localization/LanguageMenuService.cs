using XPostMonitor.Dtos;

namespace XPostMonitor.Services.Telegram.Localization;

// Hiển thị và lưu ngôn ngữ người dùng chọn bằng nút Telegram.
public sealed class LanguageMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly PremiumService premiumService;
    private readonly BotTextService text;

    public LanguageMenuService(TelegramApiClient telegramApi, PremiumService premiumService, BotTextService text)
    {
        this.telegramApi = telegramApi;
        this.premiumService = premiumService;
        this.text = text;
    }

    public async Task ShowAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton("English", "language:set:en")],
            [new TelegramInlineButton("Tiếng Việt", "language:set:vi")],
            [new TelegramInlineButton("简体中文", "language:set:zh")],
            [new TelegramInlineButton("✖ " + text.Get(language, "Close"), "language:close")]
        ];
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseLanguage"), buttons,
            cancellationToken);
    }

    public async Task HandleCallbackAsync(long chatId, long messageId, string data,
        CancellationToken cancellationToken)
    {
        if (data == "language:close")
        {
            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            return;
        }

        string[] parts = data.Split(':');
        if (parts.Length != 3 || parts[1] != "set")
        {
            return;
        }

        string language = BotTextService.Normalize(parts[2]);
        await premiumService.SetLanguageAsync(chatId, language, cancellationToken);
        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
        await telegramApi.SendMessageAsync(chatId, text.Get(language, "LanguageSaved"), cancellationToken);
    }
}
