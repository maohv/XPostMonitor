using XPostMonitor.Dtos;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.X;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Telegram;

// Hướng dẫn người dùng chọn network và launchpad khi thêm tài khoản X.
public sealed class WatchlistMenuService
{
    private readonly TelegramApiClient telegramApi;
    private readonly WatchlistService watchlistService;
    private readonly BotTextService text;

    public WatchlistMenuService(TelegramApiClient telegramApi, WatchlistService watchlistService, BotTextService text)
    {
        this.telegramApi = telegramApi;
        this.watchlistService = watchlistService;
        this.text = text;
    }

    // Bắt đầu lệnh /add bằng danh sách network dễ chọn.
    public async Task StartAddAsync(long chatId, string? username, string language,
        CancellationToken cancellationToken)
    {
        username = username?.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username) || username.Length > 15
            || !username.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "AddUsage"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.All
            .Select(network => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(network.DisplayName, "watch:chain:" + username + ":" + network.Chain)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "AlertsOnly"), "watch:save:" + username + ":none,none")]);
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);

        await telegramApi.SendButtonsAsync(chatId,
            text.Get(language, "ChooseDestination", username), buttons, cancellationToken);
    }

    // Xử lý từng nút của quy trình /add mà không cần lưu trạng thái tạm vào database.
    public async Task HandleCallbackAsync(long chatId, long messageId, string data, string language,
        CancellationToken cancellationToken)
    {
        string[] parts = data.Split(':');
        if (parts.Length < 2)
        {
            return;
        }

        await telegramApi.DeleteMessageAsync(chatId, messageId, cancellationToken);

        if (parts[1] == "cancel")
        {
            return;
        }

        if (parts.Length != 4)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "SelectionExpired"), cancellationToken);
            return;
        }

        string username = parts[2];
        string chain = parts[3];

        if (parts[1] == "chain")
        {
            await ShowLaunchpadsAsync(chatId, username, chain, language, cancellationToken);
            return;
        }

        if (parts[1] == "dex")
        {
            string[] values = chain.Split(',', 3);
            if (values.Length >= 2)
            {
                string? anchor = values.Length == 3 ? values[2] : null;
                if (values[1] == "fourmeme")
                {
                    await ShowCreatorTaxAsync(chatId, username, values[0], values[1], language,
                        cancellationToken);
                }
                else
                {
                    await ShowAutoTradingAsync(chatId, username, values[0], values[1], anchor, 0,
                        language, cancellationToken);
                }
            }
            return;
        }

        if (parts[1] == "tax")
        {
            string[] values = chain.Split(',', 3);
            if (values.Length == 3 && int.TryParse(values[2], out int creatorTaxPercent))
            {
                await ShowAutoTradingAsync(chatId, username, values[0], values[1], null,
                    creatorTaxPercent, language, cancellationToken);
            }
            return;
        }

        if (parts[1] == "market")
        {
            string[] values = chain.Split(',', 2);
            if (values.Length == 2)
            {
                await ShowLongAnchorsAsync(chatId, username, values[0], values[1], language, cancellationToken);
            }
            return;
        }

        if (parts[1] == "trade")
        {
            string[] values = chain.Split(',', 4);
            if (values.Length == 4 && int.TryParse(values[3], out int autoTrading))
            {
                string? option = values[2] == "none" ? null : values[2];
                string? anchor = values[1] == "long" ? option : null;
                int creatorTaxPercent = values[1] == "fourmeme" && int.TryParse(option, out int tax) ? tax : 0;
                await ShowConfirmationAsync(chatId, username, values[0], values[1], anchor,
                    creatorTaxPercent, autoTrading == 1, language, cancellationToken);
            }
            return;
        }

        if (parts[1] == "save")
        {
            string[] values = chain.Split(',', 4);
            string? selectedChain = values[0] == "none" ? null : values[0];
            string? selectedDex = values.Length < 2 || values[1] == "none" ? null : values[1];
            string? selectedAnchor = selectedDex == "long" && values.Length >= 3 ? values[2] : null;
            int creatorTaxPercent = selectedDex == "fourmeme" && values.Length >= 3
                && int.TryParse(values[2], out int tax) ? tax : 0;
            bool enableAutoTrading = values.Length == 4 && values[3] == "1";
            string reply = await watchlistService.AddAsync(chatId, username, selectedChain, selectedDex,
                selectedAnchor, creatorTaxPercent, enableAutoTrading, language, cancellationToken);
            await telegramApi.SendMessageAsync(chatId, reply, cancellationToken);
        }
    }

    private async Task ShowLaunchpadsAsync(long chatId, string username, string chain, string language,
        CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        if (network == null)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "UnsupportedNetwork"), cancellationToken);
            return;
        }

        List<IReadOnlyList<TelegramInlineButton>> buttons = network.Launchpads
            .Select(launchpad => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(launchpad.DisplayName, launchpad.Code == "long"
                    ? "watch:market:" + username + ":" + network.Chain + "," + launchpad.Code
                    : "watch:dex:" + username + ":" + network.Chain + "," + launchpad.Code)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);

        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseLaunchpad", network.DisplayName),
            buttons, cancellationToken);
    }

    private async Task ShowLongAnchorsAsync(long chatId, string username, string chain, string dex, string language,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<TelegramInlineButton>> buttons = LaunchpadCatalog.LongAnchors
            .Select(anchor => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(anchor.DisplayName,
                    "watch:dex:" + username + ":" + chain + "," + dex + "," + anchor.Code)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);

        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseLongAnchor"), buttons,
            cancellationToken);
    }

    private async Task ShowCreatorTaxAsync(long chatId, string username, string chain, string dex, string language,
        CancellationToken cancellationToken)
    {
        int[] rates = [0, 1, 3, 5, 10];
        List<IReadOnlyList<TelegramInlineButton>> buttons = rates
            .Select(rate => (IReadOnlyList<TelegramInlineButton>)
                [new TelegramInlineButton(rate == 0 ? text.Get(language, "NoCreatorTax") : rate + "%",
                    "watch:tax:" + username + ":" + chain + "," + dex + "," + rate)])
            .ToList();
        buttons.Add([new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]);
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseCreatorTax"), buttons,
            cancellationToken);
    }

    private async Task ShowConfirmationAsync(long chatId, string username, string chain, string dex, string? anchor,
        int creatorTaxPercent, bool enableAutoTrading, string language, CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        LaunchpadInfo? launchpad = network?.Launchpads.FirstOrDefault(item => item.Code == dex);
        if (network == null || launchpad == null || !LaunchpadCatalog.IsValidRoute(chain, dex, anchor))
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "UnsupportedSelection"), cancellationToken);
            return;
        }

        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "Confirm"), "watch:save:" + username + ":" + chain + "," + dex
                + "," + (anchor ?? (dex == "fourmeme" ? creatorTaxPercent.ToString() : "none"))
                + "," + (enableAutoTrading ? "1" : "0"))],
            [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]
        ];

        string message = text.Get(language, "PleaseConfirm") + "\n\n"
            + text.Get(language, "Account") + ": @" + username + "\n"
            + text.Get(language, "Network") + ": " + network.DisplayName + "\n"
            + text.Get(language, "Launchpad") + ": " + launchpad.DisplayName
            + (anchor == null ? string.Empty : "\n" + text.Get(language, "StockAnchor") + ": " + anchor)
            + (dex == "fourmeme" ? "\n" + text.Get(language, "CreatorTax") + ": "
                + (creatorTaxPercent == 0 ? text.Get(language, "NoCreatorTax") : creatorTaxPercent + "%")
                : string.Empty)
            + (dex == "pons" ? "\n" + text.Get(language, "CreatorFee") + ": 70%" : string.Empty)
            + "\n" + text.Get(language, "AutoTrading") + ": "
            + text.Get(language, enableAutoTrading ? "Enabled" : "Disabled");
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }

    private async Task ShowAutoTradingAsync(long chatId, string username, string chain, string dex, string? anchor,
        int creatorTaxPercent, string language, CancellationToken cancellationToken)
    {
        string option = anchor ?? (dex == "fourmeme" ? creatorTaxPercent.ToString() : "none");
        string route = chain + "," + dex + "," + option + ",";
        if (chain == "stable")
        {
            await telegramApi.SendButtonsAsync(chatId, text.Get(language, "AutoTradingUnsupported"),
                [[new TelegramInlineButton(text.Get(language, "DisableAutoTrading"),
                    "watch:trade:" + username + ":" + route + "0")],
                 [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]], cancellationToken);
            return;
        }

        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons =
        [
            [new TelegramInlineButton(text.Get(language, "EnableAutoTrading"),
                "watch:trade:" + username + ":" + route + "1")],
            [new TelegramInlineButton(text.Get(language, "DisableAutoTrading"),
                "watch:trade:" + username + ":" + route + "0")],
            [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]
        ];
        await telegramApi.SendButtonsAsync(chatId, text.Get(language, "ChooseAutoTrading", username),
            buttons, cancellationToken);
    }
}
