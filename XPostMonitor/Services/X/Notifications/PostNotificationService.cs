using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Tokens;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.X.Notifications;

// Nhận Post, chống gửi trùng, tìm người theo dõi và tạo thông báo Telegram.
public sealed class PostNotificationService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TelegramNotificationService telegramNotifications;
    private readonly TokenCreationService tokenCreationService;
    private readonly ILogger<PostNotificationService> logger;
    private readonly bool enablePersonalBot;
    private readonly string channelLanguage;
    private readonly BotTextService text;

    // X Stream ghi Post vào queue rồi tiếp tục đọc, không phải chờ database.
    private readonly Channel<XPostEvent> queue = Channel.CreateUnbounded<XPostEvent>();

    // Nhận database scope, service gửi Telegram và logger qua dependency injection.
    public PostNotificationService(IServiceScopeFactory scopeFactory, TelegramNotificationService telegramNotifications,
        TokenCreationService tokenCreationService, BotOptions options, BotTextService text,
        ILogger<PostNotificationService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.telegramNotifications = telegramNotifications;
        this.tokenCreationService = tokenCreationService;
        this.logger = logger;
        enablePersonalBot = options.EnablePersonalBot;
        channelLanguage = BotTextService.Normalize(options.ChannelLanguage);
        this.text = text;
    }

    // Đưa Post vào queue RAM; thao tác này rất nhanh và không chặn X Stream.
    public ValueTask QueueAsync(XStreamPostResponse response, IReadOnlyList<string> xUserIds, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        XPostEvent postEvent = new XPostEvent(response, xUserIds, receivedAt);
        return queue.Writer.WriteAsync(postEvent, cancellationToken);
    }

    // Đọc Post trong queue theo đúng thứ tự rồi xử lý từng account.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Một reader giữ đúng thứ tự để Post cũ không ghi đè LastPostId của Post mới.
        await foreach (XPostEvent postEvent in queue.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (string xUserId in postEvent.XUserIds)
            {
                try
                {
                    await ProcessPostAsync(xUserId, postEvent, stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "[DB] Xử lý Post {PostId} thất bại.", postEvent.Response.Data!.Id);
                }
            }
        }
    }

    // Kiểm tra Post mới, cập nhật LastPostId rồi xếp thông báo cho từng watcher.
    private async Task ProcessPostAsync(string xUserId, XPostEvent postEvent, CancellationToken cancellationToken)
    {
        XPost post = postEvent.Response.Data!;
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        XAccount? account = await db.XAccounts.Include(item => item.Watchers)
            .ThenInclude(watcher => watcher.TelegramUser)
            .ThenInclude(user => user.TradingSettings)
            .FirstOrDefaultAsync(item => item.XUserId == xUserId, cancellationToken);

        if (account == null || !IsNewerPost(post.Id, account.LastPostId))
        {
            return;
        }

        // Lưu trước để event trùng không gửi cùng một Post hai lần.
        account.LastPostId = post.Id;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[DB] Lưu Post {PostId} thành công.", post.Id);

        // Có ChannelId thì gửi một lần vào Channel; Telegram tự báo cho mọi thành viên.
        if (account.TelegramChannelId.HasValue)
        {
            XNotificationContent channelContent = XNotificationMessage.Create(account.Username, postEvent.Response,
                text, channelLanguage);
            await telegramNotifications.QueueAsync(account.TelegramChannelId.Value, channelContent.Text, post.Id,
                postEvent.ReceivedAt, post.CreatedAt, cancellationToken, channelContent.PhotoUrl,
                channelContent.PostUrl, true, text.Get(channelLanguage, "ViewOnX"));
        }

        // Watchlist cá nhân chạy riêng: gửi cho từng người đã tự thêm tài khoản này.
        List<WatchlistEntry> premiumWatchers = account.Watchers
            .Where(watcher => enablePersonalBot && watcher.TelegramUser.IsPremium)
            .ToList();

        foreach (WatchlistEntry watcher in premiumWatchers)
        {
            string language = BotTextService.Normalize(watcher.TelegramUser.LanguageCode);
            XNotificationContent content = XNotificationMessage.Create(account.Username, postEvent.Response,
                text, language);
            await telegramNotifications.QueueAsync(watcher.ChatId, content.Text, post.Id, postEvent.ReceivedAt,
                post.CreatedAt, cancellationToken, content.PhotoUrl, content.PostUrl, true,
                text.Get(language, "ViewOnX"));

            string? referenceType = post.ReferencedPosts?.FirstOrDefault()?.Type;
            bool isRepost = referenceType == "retweeted";
            bool canCreateToken = watcher.TelegramUser.TradingSettings?.EnableTokenCreation == true
                && LaunchpadCatalog.IsValidRoute(watcher.TokenChain, watcher.TokenDex, watcher.TokenAnchor)
                && !isRepost;
            if (canCreateToken)
            {
                string tokenText = referenceType == "replied_to"
                    ? "[POST_TYPE=reply]\n" + post.Text
                    : post.Text;
                await tokenCreationService.QueueAsync(watcher.ChatId, post.Id, tokenText, post.Language,
                    content.OwnPhotoUrl, content.PostUrl, watcher.TokenChain!, watcher.TokenDex!, watcher.TokenAnchor,
                    content.OwnPhotoUrl != null,
                    watcher.TelegramUser.LanguageCode, postEvent.ReceivedAt, cancellationToken);
            }
        }

    }

    // So sánh Post ID để chỉ xử lý Post mới hơn Post đã lưu trong database.
    private static bool IsNewerPost(string postId, string? lastPostId)
    {
        if (lastPostId == null)
        {
            return true;
        }

        if (ulong.TryParse(postId, out ulong current) && ulong.TryParse(lastPostId, out ulong last))
        {
            return current > last;
        }

        return postId != lastPostId;
    }

    private sealed record XPostEvent(XStreamPostResponse Response, IReadOnlyList<string> XUserIds, DateTimeOffset ReceivedAt);
}
