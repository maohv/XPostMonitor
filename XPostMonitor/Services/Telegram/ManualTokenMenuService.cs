using System.Globalization;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Notifications;

namespace XPostMonitor.Services.Telegram;

// Nhận link X, cho user chọn nơi tạo và chỉ tạo token sau nút xác nhận cuối.
public sealed class ManualTokenMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly XApiClient xApiClient;
    private readonly TradingSettingsService tradingSettings;
    private readonly TokenCreationService tokenCreation;
    private readonly BotTextService text;

    public ManualTokenMenuService(TelegramApiClient telegramApi, XApiClient xApiClient,
        TradingSettingsService tradingSettings, TokenCreationService tokenCreation, BotTextService text)
    {
        this.telegramApi = telegramApi;
        this.xApiClient = xApiClient;
        this.tradingSettings = tradingSettings;
        this.tokenCreation = tokenCreation;
        this.text = text;
    }

    public async Task StartAsync(long chatId, string link, string language, CancellationToken cancellationToken)
    {
        string? postId = XPostLinkParser.ParsePostId(link);
        if (postId == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "InvalidXLink"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = TradingNetworks.All
            .Select(network => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(network.DisplayName, "manual:chain:" + postId + ":" + network.Chain)])
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
            return;
        }

        string[] parts = data.Split(':');
        if (parts.Length != 4 || !XPostLinkParser.IsPostId(parts[2]))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        string postId = parts[2];
        if (parts[1] == "chain")
        {
            await ShowLaunchpadsAsync(chatId, postId, parts[3], language, cancellationToken);
            return;
        }

        string[] route = parts[3].Split(',', 2);
        if (route.Length != 2 || !TradingNetworks.IsValid(route[0], route[1]))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        if (parts[1] == "confirm")
        {
            await ShowConfirmationAsync(chatId, postId, route[0], route[1], language, cancellationToken);
        }
        else if (parts[1] == "create")
        {
            await CreateAsync(chatId, messageId, postId, route[0], route[1], language, cancellationToken);
        }
    }

    private async Task ShowLaunchpadsAsync(long chatId, string postId, string chain, string language,
        CancellationToken cancellationToken)
    {
        TradingNetwork? network = TradingNetworks.Find(chain);
        if (network == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = network.Launchpads
            .Select(launchpad => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(launchpad.DisplayName,
                    "manual:confirm:" + postId + ":" + chain + "," + launchpad.Dex)])
            .ToList();
        buttons.Add([new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]);

        await telegramApi.SendButtonsAsync(chatId,
            text.Get(language, "ChooseLaunchpad", network.DisplayName), buttons, cancellationToken);
    }

    private async Task ShowConfirmationAsync(long chatId, string postId, string chain, string dex, string language,
        CancellationToken cancellationToken)
    {
        AutoCreateSettings? settings = await tradingSettings.GetManualCreateSettingsAsync(chatId, chain,
            cancellationToken);
        if (settings == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualSettingsMissing"),
                cancellationToken);
            return;
        }

        TradingNetwork network = TradingNetworks.Find(chain)!;
        TradingLaunchpad launchpad = network.Launchpads.First(item => item.Dex == dex);
        string postUrl = "https://x.com/i/status/" + postId;
        string message = text.Get(language, "ManualConfirmation", postUrl, network.DisplayName,
            launchpad.DisplayName, settings.BuyAmount.ToString(CultureInfo.InvariantCulture), network.Currency,
            settings.SlippagePercent.ToString(CultureInfo.InvariantCulture));
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "CreateRealToken"),
                "manual:create:" + postId + ":" + chain + "," + dex)],
            [new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]
        ];
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private async Task CreateAsync(long chatId, long messageId, string postId, string chain, string dex,
        string language, CancellationToken cancellationToken)
    {
        try
        {
            XStreamPostResponse response = await xApiClient.GetPostAsync(postId, cancellationToken);
            XNotificationContent content = XNotificationMessage.Create("x", response, text, language);
            bool queued = await tokenCreation.QueueManualAsync(chatId, postId, response.Data!.Text,
                content.PhotoUrl, content.PostUrl, chain, dex, language, cancellationToken);
            if (!queued)
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualAlreadyRunning"),
                    cancellationToken);
                return;
            }

            await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualQueued"), cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "ManualPostFailed", exception.Message), cancellationToken);
        }
    }

}
