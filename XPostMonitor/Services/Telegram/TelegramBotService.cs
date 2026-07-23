using XPostMonitor.Configuration;
using XPostMonitor.Dtos;
using XPostMonitor.Services;
using XPostMonitor.Services.Flux;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.OpenAi;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Channels;

namespace XPostMonitor.Services.Telegram;

// Nhận lệnh Telegram và chuyển đến đúng chức năng Channel hoặc Premium.
public sealed class TelegramBotService : BackgroundService
{
    private readonly TelegramApiClient telegramApi;
    private readonly WatchlistService watchlistService;
    private readonly ChannelWatchlistService channelWatchlistService;
    private readonly PremiumService premiumService;
    private readonly GmgnClient gmgnClient;
    private readonly OpenAiClient openAiClient;
    private readonly FluxClient fluxClient;
    private readonly TokenPreviewService tokenPreviewService;
    private readonly ILogger<TelegramBotService> logger;
    private readonly string telegramToken;
    private readonly long telegramChannelId;
    private readonly bool enablePersonalBot;
    private long nextUpdateId;

    // Nhận các service cần dùng qua dependency injection.
    public TelegramBotService(TelegramApiClient telegramApi,
        WatchlistService watchlistService, ChannelWatchlistService channelWatchlistService,
        PremiumService premiumService, GmgnClient gmgnClient, OpenAiClient openAiClient, FluxClient fluxClient,
        TokenPreviewService tokenPreviewService, BotOptions options,
        ILogger<TelegramBotService> logger)
    {
        this.telegramApi = telegramApi;
        this.watchlistService = watchlistService;
        this.channelWatchlistService = channelWatchlistService;
        this.premiumService = premiumService;
        this.gmgnClient = gmgnClient;
        this.openAiClient = openAiClient;
        this.fluxClient = fluxClient;
        this.tokenPreviewService = tokenPreviewService;
        this.logger = logger;
        telegramToken = options.TelegramToken;
        telegramChannelId = options.TelegramChannelId;
        enablePersonalBot = options.EnablePersonalBot;
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
        }
    }

    // Xác định lệnh người dùng gửi rồi kiểm tra quyền trước khi xử lý.
    private async Task HandleCommandAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type != "private" || message.From == null)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "This bot currently supports private chats only.", cancellationToken);
            return;
        }

        BotCommand? command = BotCommand.Parse(message.Text);
        if (command == null)
        {
            return;
        }

        await premiumService.RegisterUserAsync(message.Chat.Id, message.From, cancellationToken);

        if (command.Name.StartsWith("/channel", StringComparison.Ordinal))
        {
            await HandleChannelCommandAsync(message, command, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/premium", StringComparison.Ordinal))
        {
            await HandlePremiumCommandAsync(message, command, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/gmgn", StringComparison.Ordinal))
        {
            await HandleGmgnCommandAsync(message, command, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/ai", StringComparison.Ordinal))
        {
            await HandleAiCommandAsync(message, command, cancellationToken);
            return;
        }

        if (command.Name.StartsWith("/flux", StringComparison.Ordinal))
        {
            await HandleFluxCommandAsync(message, command, cancellationToken);
            return;
        }

        if (command.Name == "/tokenpreview")
        {
            await HandleTokenPreviewCommandAsync(message, command, cancellationToken);
            return;
        }

        bool hasPremium = enablePersonalBot && await premiumService.IsPremiumAsync(message.Chat.Id, cancellationToken);

        string reply;
        if (command.Name is "/start" or "/help")
        {
            bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From.Id, cancellationToken);

            reply = isAdmin
                ? AdminHelpText + (hasPremium ? "\n\n" + PersonalHelpText : string.Empty)
                : hasPremium ? PersonalHelpText : PersonalBotClosedText;
        }
        else if (!hasPremium)
        {
            reply = PersonalBotClosedText;
        }
        else
        {
            reply = command.Name switch
            {
                "/add" => await watchlistService.AddAsync(message.Chat.Id, command.Argument, cancellationToken),
                "/remove" => await watchlistService.RemoveAsync(message.Chat.Id, command.Argument, cancellationToken),
                "/list" => await watchlistService.ListAsync(message.Chat.Id, cancellationToken),
                _ => "Unknown command. Use /help."
            };
        }

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    private async Task HandleTokenPreviewCommandAsync(TelegramMessage message, BotCommand command, CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "Only channel administrators can use this command.", cancellationToken);
            return;
        }

        try
        {
            TokenPreviewDto preview = await tokenPreviewService.CreateAsync(command.Argument, cancellationToken);

            if (preview.IsExpired)
            {
                await telegramApi.SendMessageAsync(message.Chat.Id,
                    "Token skipped. Metadata and image preparation reached 10 seconds. It will not be retried.", cancellationToken);
                return;
            }

            string caption = "Token preview\n\n"
                + "Name: " + preview.Draft.Name + "\n"
                + "Symbol: " + preview.Draft.Symbol + "\n"
                + "Description: " + preview.Draft.Description + "\n\n"
                + "Source: " + (preview.UsedSourceImage ? "post image" : "post text") + "\n"
                + "OpenAI: " + preview.OpenAiSeconds.ToString("0.00") + " seconds\n"
                + "FLUX: " + preview.FluxSeconds.ToString("0.00") + " seconds\n"
                + "Total: " + preview.TotalSeconds.ToString("0.00") + " seconds\n\n"
                + "Preview only. No token was created.";

            await telegramApi.SendPhotoAsync(message.Chat.Id, preview.Image, caption, cancellationToken);
        }
        catch (Exception exception)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "Token preview failed: " + exception.Message, cancellationToken);
        }
    }

    private async Task HandleFluxCommandAsync(TelegramMessage message, BotCommand command, CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "Only channel administrators can use this command.", cancellationToken);
            return;
        }

        if (command.Name == "/fluximage")
        {
            try
            {
                DateTime startedAt = DateTime.UtcNow;
                byte[] image = await fluxClient.CreateTokenImageAsync(command.Argument, cancellationToken);
                double seconds = (DateTime.UtcNow - startedAt).TotalSeconds;
                await telegramApi.SendPhotoAsync(message.Chat.Id, image, "FLUX token image\nGenerated in " + seconds.ToString("0.0") + " seconds", cancellationToken);
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
            _ => "Unknown FLUX command. Use /help."
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    private async Task HandleAiCommandAsync(TelegramMessage message, BotCommand command, CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "Only channel administrators can use this command.", cancellationToken);
            return;
        }

        if (command.Name == "/aiimage")
        {
            try
            {
                byte[] image = await openAiClient.CreateImageAsync(command.Argument, cancellationToken);
                await telegramApi.SendPhotoAsync(message.Chat.Id, image, "AI token image", cancellationToken);
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
            _ => "Unknown AI command. Use /help."
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Chỉ admin Channel được kiểm tra kết nối GMGN.
    private async Task HandleGmgnCommandAsync(TelegramMessage message, BotCommand command, CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "Only channel administrators can use this command.", cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/gmgnstatus" => await gmgnClient.CheckConnectionAsync(cancellationToken),
            _ => "Unknown GMGN command. Use /help."
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Chỉ admin Channel được cấp hoặc thu hồi Premium.
    private async Task HandlePremiumCommandAsync(TelegramMessage message, BotCommand command, CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "Only channel administrators can use this command.", cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/premiumadd" => await premiumService.AddAsync(command.Argument, cancellationToken),
            "/premiumremove" => await premiumService.RemoveAsync(command.Argument, cancellationToken),
            "/premiumlist" => await premiumService.ListAsync(cancellationToken),
            _ => "Unknown Premium command. Use /help."
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Chỉ admin Channel được thay đổi danh sách X account của Channel.
    private async Task HandleChannelCommandAsync(TelegramMessage message, BotCommand command, CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id, "Only channel administrators can use this command.", cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/channeladd" => await channelWatchlistService.AddAsync(telegramChannelId, command.Argument, cancellationToken),
            "/channelremove" => await channelWatchlistService.RemoveAsync(telegramChannelId, command.Argument, cancellationToken),
            "/channellist" => await channelWatchlistService.ListAsync(telegramChannelId, cancellationToken),
            _ => "Unknown channel command. Use /help."
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    private const string PersonalBotClosedText =
        "Bot currently only sends notifications to the channel. "
        + "Personal monitoring is not available yet.";

    private const string AdminHelpText =
        "Channel admin commands:\n"
        + "/channeladd username - add an X account to the channel\n"
        + "/channelremove username - remove an X account from the channel\n"
        + "/channellist - show the channel watchlist\n\n"
        + "Premium admin commands:\n"
        + "/premiumadd username - enable Premium for a user\n"
        + "/premiumremove username - disable Premium for a user\n"
        + "/premiumlist - show Premium users\n\n"
        + "GMGN admin commands:\n"
        + "/gmgnstatus - check the read-only GMGN connection\n\n"
        + "AI admin commands:\n"
        + "/aistatus - check the OpenAI connection\n"
        + "/aitest post text - create a token draft from sample text\n"
        + "/aiimage image prompt - create a token image\n\n"
        + "FLUX admin commands:\n"
        + "/fluxstatus - check the FLUX connection\n"
        + "/fluximage post text - create a fast token image\n\n"
        + "Token preview:\n"
        + "/tokenpreview post text - preview from text\n"
        + "/tokenpreview post text | image URL - prioritize the post image";

    private const string PersonalHelpText =
        "Personal commands:\n"
        + "/add username - add an X account\n"
        + "/remove username - remove an X account\n"
        + "/list - show your watchlist";
}
