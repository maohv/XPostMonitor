using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Channels;

namespace XPostMonitor.Services.Telegram;

// Nhận và xử lý các lệnh Telegram như /add, /remove và /list.
public sealed class TelegramBotService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TelegramApiClient telegramApi;
    private readonly WatchlistService watchlistService;
    private readonly ChannelWatchlistService channelWatchlistService;
    private readonly ILogger<TelegramBotService> logger;
    private readonly string telegramToken;
    private readonly long telegramChannelId;
    private long nextUpdateId;

    // Nhận các service cần dùng qua dependency injection.
    public TelegramBotService(IServiceScopeFactory scopeFactory, TelegramApiClient telegramApi,
        WatchlistService watchlistService, ChannelWatchlistService channelWatchlistService,
        BotOptions options, ILogger<TelegramBotService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.telegramApi = telegramApi;
        this.watchlistService = watchlistService;
        this.channelWatchlistService = channelWatchlistService;
        this.logger = logger;
        telegramToken = options.TelegramToken;
        telegramChannelId = options.TelegramChannelId;
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

    // Xác định lệnh người dùng gửi và gọi đúng chức năng watchlist.
    private async Task HandleCommandAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type != "private" || message.From == null)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                "This bot currently supports private chats only.", cancellationToken);
            return;
        }

        BotCommand? command = BotCommand.Parse(message.Text);
        if (command == null)
        {
            return;
        }

        await SaveTelegramUserAsync(message, cancellationToken);

        if (command.Name.StartsWith("/channel", StringComparison.Ordinal))
        {
            await HandleChannelCommandAsync(message, command, cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/start" or "/help" => HelpText,
            "/add" => await watchlistService.AddAsync(message.Chat.Id, command.Argument, cancellationToken),
            "/remove" => await watchlistService.RemoveAsync(message.Chat.Id, command.Argument, cancellationToken),
            "/list" => await watchlistService.ListAsync(message.Chat.Id, cancellationToken),
            _ => "Unknown command. Use /help."
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Chỉ Admin của Channel mới được thêm, xóa hoặc xem danh sách tài khoản X của Channel.
    private async Task HandleChannelCommandAsync(TelegramMessage message, BotCommand command,
        CancellationToken cancellationToken)
    {
        bool isAdmin = await telegramApi.IsChannelAdminAsync(
            telegramChannelId, message.From!.Id, cancellationToken);

        if (!isAdmin)
        {
            await telegramApi.SendMessageAsync(message.Chat.Id,
                "Only channel administrators can use this command.", cancellationToken);
            return;
        }

        string reply = command.Name switch
        {
            "/channeladd" => await channelWatchlistService.AddAsync(
                telegramChannelId, command.Argument, cancellationToken),
            "/channelremove" => await channelWatchlistService.RemoveAsync(
                telegramChannelId, command.Argument, cancellationToken),
            "/channellist" => await channelWatchlistService.ListAsync(
                telegramChannelId, cancellationToken),
            _ => "Unknown channel command. Use /help."
        };

        await telegramApi.SendMessageAsync(message.Chat.Id, reply, cancellationToken);
    }

    // Tạo mới hoặc cập nhật thông tin người dùng Telegram trong database.
    private async Task SaveTelegramUserAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TelegramUser? user = await db.TelegramUsers.FindAsync(new object[] { message.Chat.Id }, cancellationToken);

        DateTime now = DateTime.UtcNow;
        TelegramFrom sender = message.From!;

        if (user == null)
        {
            user = new TelegramUser
            {
                ChatId = message.Chat.Id,
                TelegramUserId = sender.Id,
                CreatedAtUtc = now
            };
            db.TelegramUsers.Add(user);
        }

        user.Username = sender.Username;
        user.DisplayName = string.IsNullOrWhiteSpace(sender.LastName)
            ? sender.FirstName
            : sender.FirstName + " " + sender.LastName;
        user.LastSeenAtUtc = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[DB] Lưu người dùng Telegram thành công.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[DB] Lưu người dùng Telegram thất bại.");
            throw;
        }
    }

    private const string HelpText =
        "Commands:\n"
        + "/add username - add an X account\n"
        + "/remove username - remove an X account\n"
        + "/list - show your watchlist\n\n"
        + "Channel admin commands:\n"
        + "/channeladd username - add an X account to the channel\n"
        + "/channelremove username - remove an X account from the channel\n"
        + "/channellist - show the channel watchlist";
}
