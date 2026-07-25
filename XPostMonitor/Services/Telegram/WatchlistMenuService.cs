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
                await ShowConfirmationAsync(chatId, username, values[0], values[1], anchor, language,
                    cancellationToken);
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

        if (parts[1] == "save")
        {
            string[] values = chain.Split(',', 3);
            string? selectedChain = values[0] == "none" ? null : values[0];
            string? selectedDex = values.Length < 2 || values[1] == "none" ? null : values[1];
            string? selectedAnchor = values.Length < 3 ? null : values[2];
            string reply = await watchlistService.AddAsync(chatId, username, selectedChain, selectedDex,
                selectedAnchor, language, cancellationToken);
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

    private async Task ShowConfirmationAsync(long chatId, string username, string chain, string dex, string? anchor,
        string language, CancellationToken cancellationToken)
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
                + (anchor == null ? string.Empty : "," + anchor))],
            [new TelegramInlineButton(text.Get(language, "Cancel"), "watch:cancel")]
        ];

        string message = text.Get(language, "PleaseConfirm") + "\n\n"
            + text.Get(language, "Account") + ": @" + username + "\n"
            + text.Get(language, "Network") + ": " + network.DisplayName + "\n"
            + text.Get(language, "Launchpad") + ": " + launchpad.DisplayName
            + (anchor == null ? string.Empty : "\n" + text.Get(language, "StockAnchor") + ": " + anchor);
        await telegramApi.SendButtonsAsync(chatId, message, buttons, cancellationToken);
    }
}
