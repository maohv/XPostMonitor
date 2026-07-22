using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram;

namespace XPostMonitor.Services.X.Notifications;

// Nhận Post, chống gửi trùng, tìm người theo dõi và tạo thông báo Telegram.
public sealed class PostNotificationService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TelegramNotificationService telegramNotifications;
    private readonly ILogger<PostNotificationService> logger;

    // X Stream ghi Post vào queue rồi tiếp tục đọc, không phải chờ database.
    private readonly Channel<XPostEvent> queue = Channel.CreateUnbounded<XPostEvent>();

    // Nhận database scope, service gửi Telegram và logger qua dependency injection.
    public PostNotificationService(IServiceScopeFactory scopeFactory,
        TelegramNotificationService telegramNotifications,
        ILogger<PostNotificationService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.telegramNotifications = telegramNotifications;
        this.logger = logger;

        System.Diagnostics.Debug.Assert(IsNewerPost("101", "100"));
        System.Diagnostics.Debug.Assert(!IsNewerPost("100", "100"));
        System.Diagnostics.Debug.Assert(!IsNewerPost("99", "100"));
    }

    // Đưa Post vào queue RAM; thao tác này rất nhanh và không chặn X Stream.
    public ValueTask QueueAsync(XPost post, IReadOnlyList<string> xUserIds,
        DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        XPostEvent postEvent = new XPostEvent(post, xUserIds, receivedAt);
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
                    logger.LogError(exception,
                        "[DB] Xử lý Post {PostId} thất bại.", postEvent.Post.Id);
                }
            }
        }
    }

    // Kiểm tra Post mới, cập nhật LastPostId rồi xếp thông báo cho từng watcher.
    private async Task ProcessPostAsync(string xUserId, XPostEvent postEvent,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        XAccount? account = await db.XAccounts.Include(item => item.Watchers)
            .FirstOrDefaultAsync(item => item.XUserId == xUserId, cancellationToken);

        if (account == null || !IsNewerPost(postEvent.Post.Id, account.LastPostId))
        {
            return;
        }

        // Lưu trước để event trùng không gửi cùng một Post hai lần.
        account.LastPostId = postEvent.Post.Id;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[DB] Lưu Post {PostId} thành công.", postEvent.Post.Id);

        string message = XNotificationMessage.Create(account.Username, postEvent.Post);

        // Có ChannelId thì gửi một lần vào Channel; Telegram tự báo cho mọi thành viên.
        if (account.TelegramChannelId.HasValue)
        {
            await telegramNotifications.QueueAsync(
                account.TelegramChannelId.Value,
                message,
                postEvent.Post.Id,
                postEvent.ReceivedAt,
                postEvent.Post.CreatedAt,
                cancellationToken);

        }

        // Watchlist cá nhân chạy riêng: gửi cho từng người đã tự thêm tài khoản này.
        foreach (long chatId in account.Watchers.Select(watcher => watcher.ChatId))
        {
            await telegramNotifications.QueueAsync(
                chatId, message, postEvent.Post.Id, postEvent.ReceivedAt,
                postEvent.Post.CreatedAt, cancellationToken);
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

    private sealed record XPostEvent(
        XPost Post,
        IReadOnlyList<string> XUserIds,
        DateTimeOffset ReceivedAt);
}
