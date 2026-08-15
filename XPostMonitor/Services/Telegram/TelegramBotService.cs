using XPostMonitor.Configuration;
using XPostMonitor.Dtos;
using XPostMonitor.Services;
using XPostMonitor.Services.Flux;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.OpenAi;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Channels;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Telegram;

// Nhận lệnh Telegram và chuyển đến đúng chức năng Channel hoặc Premium.
public sealed class TelegramBotService : BackgroundService
{
    private readonly TelegramApiClient telegramApi;
    private readonly WatchlistService watchlistService;
    private readonly WatchlistMenuService watchlistMenu;
    private readonly TokenSettingsMenuService tokenSettingsMenu;
    private readonly AutoTradingMenuService autoTradingMenu;
    private readonly ChannelWatchlistService channelWatchlistService;
    private readonly PremiumService premiumService;
    private readonly LanguageMenuService languageMenu;
    private readonly ArcBridgeMenuService arcBridgeMenu;
    private readonly ManualTokenMenuService manualTokenMenu;
    private readonly LinkTokenSettingsMenuService linkTokenSettingsMenu;
    private readonly NftMintMenuService nftMintMenu;
    private readonly BotTextService text;
    private readonly GmgnClient gmgnClient;
    private readonly OpenAiClient openAiClient;
    private readonly FluxClient fluxClient;
    private readonly TokenPreviewService tokenPreviewService;
    private readonly ILogger<TelegramBotService> logger;
    private readonly string telegramToken;
    private readonly long telegramChannelId;
    private readonly bool enablePersonalBot;
    private readonly bool enableManualTokenCreation;
    private long nextUpdateId;

    // Nhận các service cần dùng qua dependency injection.
    public TelegramBotService(TelegramApiClient telegramApi,
        WatchlistService watchlistService, WatchlistMenuService watchlistMenu,
        TokenSettingsMenuService tokenSettingsMenu, AutoTradingMenuService autoTradingMenu,
        ChannelWatchlistService channelWatchlistService,
        PremiumService premiumService, LanguageMenuService languageMenu, ManualTokenMenuService manualTokenMenu,
        ArcBridgeMenuService arcBridgeMenu, LinkTokenSettingsMenuService linkTokenSettingsMenu,
        NftMintMenuService nftMintMenu, BotTextService text,
        GmgnClient gmgnClient, OpenAiClient openAiClient, FluxClient fluxClient,
        TokenPreviewService tokenPreviewService, BotOptions options,
        ILogger<TelegramBotService> logger)
    {
        this.telegramApi = telegramApi;
        this.watchlistService = watchlistService;
        this.watchlistMenu = watchlistMenu;
        this.tokenSettingsMenu = tokenSettingsMenu;
        this.autoTradingMenu = autoTradingMenu;
        this.channelWatchlistService = channelWatchlistService;
        this.premiumService = premiumService;
        this.languageMenu = languageMenu;
        this.arcBridgeMenu = arcBridgeMenu;
        this.manualTokenMenu = manualTokenMenu;
        this.linkTokenSettingsMenu = linkTokenSettingsMenu;
        this.nftMintMenu = nftMintMenu;
        this.text = text;
        this.gmgnClient = gmgnClient;
        this.openAiClient = openAiClient;
        this.fluxClient = fluxClient;
        this.tokenPreviewService = tokenPreviewService;
        this.logger = logger;
        telegramToken = options.TelegramToken;
        telegramChannelId = options.TelegramChannelId;
        enablePersonalBot = options.EnablePersonalBot;
        enableManualTokenCreation = options.EnableManualTokenCreation;
    }

    // Chạy liên tục cùng ứng dụng để chờ tin nhắn mới từ Telegram.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(telegramToken))
        {
            throw new InvalidOperationException("Bot:TelegramToken is missing from configuration.");
        }

        await telegramApi.DeleteWebhookAsync(stoppingToken);
        logger.LogInformation("Kết nối Telegram thành công.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadNewMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Kết nối hoặc nhận dữ liệu Telegram gặp lỗi.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    // Lấy một nhóm tin nhắn mới rồi xử lý lần lượt từng tin.
    private async Task ReadNewMessagesAsync(CancellationToken cancellationToken)
    {
        List<TelegramUpdate> updates = await telegramApi.GetUpdatesAsync(nextUpdateId, cancellationToken);

        foreach (TelegramUpdate update in updates)
        {
            // Lần gọi sau bỏ qua các update đã xử lý.
            nextUpdateId = update.UpdateId + 1;

            if (update.Message?.Text != null)
            {
                await HandleCommandAsync(update.Message, cancellationToken);
            }
            else if (update.Message?.Photo?.Count > 0)
            {
                await HandlePhotoAsync(update.Message, cancellationToken);
            }
            else if (update.CallbackQuery?.Data != null)
            {
                await HandleCallbackAsync(update.CallbackQuery, cancellationToken);
            }
        }
    }

    private async Task HandlePhotoAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type != "private" || message.From == null)
        {
            return;
        }

        await premiumService.RegisterUserAsync(message.Chat.Id, message.From, cancellationToken);
        string language = await premiumService.GetLanguageAsync(message.Chat.Id, cancellationToken);
        bool hasPremium = enablePersonalBot
            && await premiumService.IsPremiumAsync(message.Chat.Id, cancellationToken);
        if (!hasPremium)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "PersonalClosed"),
                cancellationToken);
            return;
        }

        if (!await manualTokenMenu.HandlePhotoAsync(message, language, cancellationToken))
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "CustomImageNotExpected"), cancellationToken);
        }
    }

    // Xác định lệnh người dùng gửi rồi kiểm tra quyền trước khi xử lý.
    private async Task HandleCommandAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type != "private" || message.From == null)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(message.From?.LanguageCode, "PrivateOnly"), cancellationToken);
            return;
        }

        await premiumService.RegisterUserAsync(message.Chat.Id, message.From, cancellationToken);
        string language = await premiumService.GetLanguageAsync(message.Chat.Id, cancellationToken);

        if (!message.Text!.StartsWith('/')
            && (await nftMintMenu.HandlePendingInputAsync(message, language, cancellationToken)
                || await arcBridgeMenu.HandlePendingInputAsync(message, cancellationToken)
                || await autoTradingMenu.HandlePendingInputAsync(message, cancellationToken)
                || await linkTokenSettingsMenu.HandlePendingInputAsync(message, cancellationToken)
                || await tokenSettingsMenu.HandlePendingInputAsync(message, cancellationToken)))
        {
            return;
        }

        if (!message.Text.StartsWith('/'))
        {
            bool canUsePersonalBot = enablePersonalBot
                && await premiumService.IsPremiumAsync(message.Chat.Id, cancellationToken);
            if (!canUsePersonalBot && !enableManualTokenCreation)
            {
                // Preview có tốn phí AI/ảnh nên chỉ mở thêm cho admin Channel.
                canUsePersonalBot = await telegramApi.IsChannelAdminAsync(telegramChannelId,
                    message.From.Id, cancellationToken);
            }

            if (!canUsePersonalBot)
            {
                await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "PersonalClosed"),
                    cancellationToken);
                return;
            }

            await manualTokenMenu.StartAsync(message.Chat.Id, message.Text, language, cancellationToken);
            return;
        }

        BotCommand? command = BotCommand.Parse(message.Text);
        if (command == null)
        {
            return;
        }

        if (command.Name == "/language")
        {
            await languageMenu.ShowAsync(message.Chat.Id, language, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/channel", StringComparison.Ordinal))
        {
            await HandleChannelCommandAsync(message, command, language, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/premium", StringComparison.Ordinal))
        {
            await HandlePremiumCommandAsync(message, command, language, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/gmgn", StringComparison.Ordinal))
        {
            await HandleGmgnCommandAsync(message, command, language, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/ai", StringComparison.Ordinal))
        {
            await HandleAiCommandAsync(message, command, language, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/flux", StringComparison.Ordinal))
        {
            await HandleFluxCommandAsync(message, command, language, cancellationToken);
            return;
        }

        if (command.Name == "/tokenpreview")
        {
            await HandleTokenPreviewCommandAsync(message, command, language, cancellationToken);
            return;
        }

        bool hasPremium = enablePersonalBot && await premiumService.IsPremiumAsync(message.Chat.Id, cancellationToken);

        string reply;
        if (command.Name == "/admin")
        {
            bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From.Id, cancellationToken);
            reply = isAdmin ? text.Get(language, "AdminHelp") : text.Get(language, "AdminOnly");
        }
        else if (command.Name == "/start")
        {
            await arcBridgeMenu.ShowStartAsync(message.Chat.Id, hasPremium, language,
                cancellationToken);
            return;
        }
        else if (command.Name == "/help")
        {
            reply = hasPremium ? text.Get(language, "PersonalHelp") : text.Get(language, "PersonalClosed");
        }
        else if (!hasPremium)
        {
            reply = text.Get(language, "PersonalClosed");
        }
        else
        {
            if (command.Name == "/add")
            {
                await watchlistMenu.StartAddAsync(message.Chat.Id, command.Argument, language, cancellationToken);
                return;
            }

            if (command.Name == "/settings")
            {
                await tokenSettingsMenu.ShowAsync(message.Chat.Id, cancellationToken);
                return;
            }

            if (command.Name == "/list")
            {
                await watchlistMenu.ShowListAsync(message.Chat.Id, language, cancellationToken);
                return;
            }

            reply = command.Name switch
            {
                "/remove" => await watchlistService.RemoveAsync(message.Chat.Id, command.Argument, language,
                    cancellationToken),
                _ => text.Get(language, "UnknownCommand")
            };
        }

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Xử lý nút bấm của /add và /settings trong chat riêng của Premium user.
    private async Task HandleCallbackAsync(TelegramCallbackQuery callback, CancellationToken cancellationToken)
    {
        await telegramApi.AnswerCallbackAsync(callback.Id, cancellationToken);
        if (callback.Message == null || string.IsNullOrWhiteSpace(callback.Data))
        {
            return;
        }

        if (callback.Message.Chat.Type != "private")
        {
            return;
        }

        long chatId = callback.Message.Chat.Id;
        await premiumService.RegisterUserAsync(chatId, callback.From, cancellationToken);
        string language = await premiumService.GetLanguageAsync(chatId, cancellationToken);
        if (callback.Data.StartsWith("language:", StringComparison.Ordinal))
        {
            await languageMenu.HandleCallbackAsync(chatId, callback.Message.MessageId, callback.Data,
                cancellationToken);
            return;
        }

        if (callback.Data.StartsWith("arcbridge:", StringComparison.Ordinal))
        {
            await arcBridgeMenu.HandleCallbackAsync(chatId, callback.Message.MessageId,
                callback.Data, language, cancellationToken);
            return;
        }

        bool hasPremium = enablePersonalBot && await premiumService.IsPremiumAsync(chatId, cancellationToken);
        if (!hasPremium)
        {
            await telegramApi.SendMessageAsync(chatId, text.Get(language, "PersonalClosed"), cancellationToken);
            return;
        }

        if (callback.Data.StartsWith("nft:", StringComparison.Ordinal))
        {
            await nftMintMenu.HandleCallbackAsync(chatId, callback.Message.MessageId, callback.Data,
                language, cancellationToken);
            return;
        }

        if (callback.Data.StartsWith("watch:", StringComparison.Ordinal))
        {
            await watchlistMenu.HandleCallbackAsync(chatId, callback.Message.MessageId, callback.Data, language,
                cancellationToken);
        }
        else if (callback.Data == "linkauto:show")
        {
            await telegramApi.DeleteMessageAsync(chatId, callback.Message.MessageId, cancellationToken);
            await linkTokenSettingsMenu.ShowAsync(chatId, cancellationToken);
        }
        else if (callback.Data.StartsWith("linkauto:", StringComparison.Ordinal))
        {
            await linkTokenSettingsMenu.HandleCallbackAsync(chatId, callback.Message.MessageId, callback.Data,
                cancellationToken);
        }
        else if (callback.Data.StartsWith("settings:", StringComparison.Ordinal))
        {
            await tokenSettingsMenu.HandleCallbackAsync(chatId, callback.Message.MessageId, callback.Data,
                cancellationToken);
        }
        else if (callback.Data == "trading:back")
        {
            await telegramApi.DeleteMessageAsync(chatId, callback.Message.MessageId, cancellationToken);
            await tokenSettingsMenu.ShowAsync(chatId, cancellationToken);
        }
        else if (callback.Data.StartsWith("trading:", StringComparison.Ordinal))
        {
            await autoTradingMenu.HandleCallbackAsync(chatId, callback.Message.MessageId, callback.Data,
                cancellationToken);
        }
        else if (callback.Data.StartsWith("manual:", StringComparison.Ordinal))
        {
            await manualTokenMenu.HandleCallbackAsync(chatId, callback.Message.MessageId, callback.Data, language,
                cancellationToken);
        }
    }

    private async Task HandleTokenPreviewCommandAsync(TelegramMessage message, BotCommand command, string language,
        CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "AdminOnly"), cancellationToken);
            return;
        }

        try
        {
            TokenPreviewDto preview = await tokenPreviewService.CreateAsync(command.Argument, cancellationToken);

            if (preview.IsExpired)
            {
                await telegramApi.SendMessageAsync(message.Chat.Id,
                    text.Get(language, "PreviewExpired", tokenPreviewService.AutoTimeoutSeconds),
                    cancellationToken);
                return;
            }

            string caption = text.Get(language, "PreviewCaption", preview.Draft.Name, preview.Draft.Symbol,
                preview.Draft.Description, text.Get(language, preview.UsedSourceImage ? "PostImage" : "PostText"),
                preview.OpenAiSeconds.ToString("0.00"), preview.FluxSeconds.ToString("0.00"),
                preview.TotalSeconds.ToString("0.00"));

            await telegramApi.SendPhotoAsync(message.Chat.Id, preview.Image, caption, cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                text.Get(language, "TokenPreviewFailed", exception.Message), cancellationToken);
        }
    }

    private async Task HandleFluxCommandAsync(TelegramMessage message, BotCommand command, string language,
        CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "AdminOnly"), cancellationToken);
            return;
        }

        if (command.Name == "/fluximage")
        {
            try
            {
                DateTime startedAt = DateTime.UtcNow;
                byte[] image = await fluxClient.CreateTokenImageAsync(command.Argument, cancellationToken);
                double seconds = (DateTime.UtcNow - startedAt).TotalSeconds;
                await telegramApi.SendPhotoAsync(message.Chat.Id, image,
                    text.Get(language, "FluxImageCaption", seconds.ToString("0.0")), cancellationToken);
            }
            catch (Exception exception)
            {
                await telegramApi.SendMessageAsync(message.Chat.Id, exception.Message, cancellationToken);
            }

            return;
        }

        string reply = command.Name switch
        {
            "/fluxstatus" => await fluxClient.CheckConnectionAsync(cancellationToken),
            _ => text.Get(language, "UnknownCommand")
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    private async Task HandleAiCommandAsync(TelegramMessage message, BotCommand command, string language,
        CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "AdminOnly"), cancellationToken);
            return;
        }

        if (command.Name == "/aiimage")
        {
            try
            {
                byte[] image = await openAiClient.CreateImageAsync(command.Argument, cancellationToken);
                await telegramApi.SendPhotoAsync(message.Chat.Id, image, text.Get(language, "AiImageCaption"),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                await telegramApi.SendMessageAsync(message.Chat.Id, exception.Message, cancellationToken);
            }

            return;
        }

        string reply = command.Name switch
        {
            "/aistatus" => await openAiClient.CheckConnectionAsync(cancellationToken),
            "/aitest" => await openAiClient.CreateTokenDraftAsync(command.Argument, cancellationToken),
            _ => text.Get(language, "UnknownCommand")
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Chỉ admin Channel được kiểm tra kết nối GMGN.
    private async Task HandleGmgnCommandAsync(TelegramMessage message, BotCommand command, string language,
        CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "AdminOnly"), cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/gmgnstatus" => await gmgnClient.CheckConnectionAsync(cancellationToken),
            _ => text.Get(language, "UnknownCommand")
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Chỉ admin Channel được cấp hoặc thu hồi Premium.
    private async Task HandlePremiumCommandAsync(TelegramMessage message, BotCommand command, string language,
        CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "AdminOnly"), cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/premiumadd" => await premiumService.AddAsync(command.Argument, language, cancellationToken),
            "/premiumremove" => await premiumService.RemoveAsync(command.Argument, language, cancellationToken),
            "/premiumlist" => await premiumService.ListAsync(language, cancellationToken),
            _ => text.Get(language, "UnknownCommand")
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Chỉ admin Channel được thay đổi danh sách X account của Channel.
    private async Task HandleChannelCommandAsync(TelegramMessage message, BotCommand command, string language,
        CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, text.Get(language, "AdminOnly"), cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/channeladd" => await channelWatchlistService.AddAsync(telegramChannelId, command.Argument, language,
                cancellationToken),
            "/channelremove" => await channelWatchlistService.RemoveAsync(telegramChannelId, command.Argument,
                language, cancellationToken),
            "/channellist" => await channelWatchlistService.ListAsync(telegramChannelId, language,
                cancellationToken),
            _ => text.Get(language, "UnknownCommand")
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }
}
