using System.Globalization;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Tokens;
using XPostMonitor.Services.Wallets;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Notifications;

namespace XPostMonitor.Services.Telegram;

// Nhận link X, cho user chọn nơi tạo và chỉ tạo token sau nút xác nhận cuối.
public sealed class ManualTokenMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly XApiClient xApiClient;
    private readonly TokenSettingsService tokenSettings;
    private readonly TokenCreationService tokenCreation;
    private readonly EvmWalletService evmWalletService;
    private readonly FourMemeOptions fourMemeOptions;
    private readonly DyorStableOptions dyorStableOptions;
    private readonly BotTextService text;

    public ManualTokenMenuService(TelegramApiClient telegramApi, XApiClient xApiClient,
        TokenSettingsService tokenSettings, TokenCreationService tokenCreation,
        EvmWalletService evmWalletService, FourMemeOptions fourMemeOptions,
        DyorStableOptions dyorStableOptions, BotTextService text)
    {
        this.telegramApi = telegramApi;
        this.xApiClient = xApiClient;
        this.tokenSettings = tokenSettings;
        this.tokenCreation = tokenCreation;
        this.evmWalletService = evmWalletService;
        this.fourMemeOptions = fourMemeOptions;
        this.dyorStableOptions = dyorStableOptions;
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

        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.All
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
        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);

        if (data == "manual:cancel")
        {
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
        if (route.Length != 2 || !LaunchpadCatalog.IsValid(route[0], route[1]))
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
            await CreateAsync(chatId, postId, route[0], route[1], language, cancellationToken);
        }
    }

    private async Task ShowLaunchpadsAsync(long chatId, string postId, string chain, string language,
        CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        if (network == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualExpired"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = network.Launchpads
            .Select(launchpad => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(launchpad.DisplayName,
                    "manual:confirm:" + postId + ":" + chain + "," + launchpad.Code)])
            .ToList();
        buttons.Add([new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]);

        await telegramApi.SendButtonsAsync(chatId,
            text.Get(language, "ChooseLaunchpad", network.DisplayName), buttons, cancellationToken);
    }

    private async Task ShowConfirmationAsync(long chatId, string postId, string chain, string dex, string language,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials? wallet = await evmWalletService.GetAsync(chatId, cancellationToken);
        TokenCreateSettings? settings = await tokenSettings.GetChainSettingsAsync(chatId, chain,
            cancellationToken);
        if (wallet == null || settings == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "LaunchpadSettingsMissing"),
                cancellationToken);
            return;
        }

        LaunchpadNetwork network = LaunchpadCatalog.Find(chain)!;
        LaunchpadInfo launchpad = network.Launchpads.First(item => item.Code == dex);
        if (settings.BuyAmount < network.MinimumBuyAmount)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "MinimumBuyAmount", network.DisplayName,
                    network.MinimumBuyAmount, network.Currency), cancellationToken);
            return;
        }

        string postUrl = "https://x.com/i/status/" + postId;
        bool live = dex == "fourmeme"
            ? fourMemeOptions.EnableRealTransactions
            : dyorStableOptions.EnableRealTransactions;
        string message = text.Get(language, live ? "TokenConfirmation" : "TokenTestConfirmation",
            postUrl, network.DisplayName,
            launchpad.DisplayName, settings.BuyAmount.ToString(CultureInfo.InvariantCulture), network.Currency);
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, live ? "CreateRealToken" : "RunTokenTest"),
                "manual:create:" + postId + ":" + chain + "," + dex)],
            [new TelegramInlineButton("✖ " + text.Get(language, "Cancel"), "manual:cancel")]
        ];
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private async Task CreateAsync(long chatId, string postId, string chain, string dex,
        string language, CancellationToken cancellationToken)
    {
        try
        {
            XStreamPostResponse response = await xApiClient.GetPostAsync(postId, cancellationToken);
            XPost post = response.Data!;
            string? referenceType = post.ReferencedPosts?.FirstOrDefault()?.Type;
            if (referenceType == "retweeted")
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "RepostTokenSkipped"),
                    cancellationToken);
                return;
            }

            XNotificationContent content = XNotificationMessage.Create("x", response, text, language);
            string tokenText = referenceType == "replied_to"
                ? "[POST_TYPE=reply]\n" + post.Text
                : post.Text;
            bool queued = await tokenCreation.QueueManualAsync(chatId, postId, tokenText,
                post.Language, content.OwnPhotoUrl, content.PostUrl, chain, dex, language,
                cancellationToken);
            if (!queued)
            {
                await telegramApi.SendMessageAsync(chatId, text.Get(language, "ManualAlreadyRunning"),
                    cancellationToken);
                return;
            }

            bool live = dex == "fourmeme"
                ? fourMemeOptions.EnableRealTransactions
                : dyorStableOptions.EnableRealTransactions;
            string queuedText = live ? "ManualQueued" : "TokenTestStarted";
            await telegramApi.SendMessageAsync(chatId, text.Get(language, queuedText), cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(chatId,
                text.Get(language, "ManualPostFailed", exception.Message), cancellationToken);
        }
    }
}
