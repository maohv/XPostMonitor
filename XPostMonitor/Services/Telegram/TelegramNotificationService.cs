using System.Threading.Channels;

namespace XPostMonitor.Services.Telegram;

// Xếp hàng và gửi nhiều thông báo Telegram cùng lúc mà không chặn X Stream.
public sealed class TelegramNotificationService : BackgroundService
{
    private const int WorkerCount = 10;
    private readonly TelegramApiClient telegramApi;
    private readonly ILogger<TelegramNotificationService> logger;

    // Queue RAM nhanh và đơn giản; khi restart, các tin chưa gửi trong queue sẽ mất.
    private readonly Channel<Notification> queue = Channel.CreateUnbounded<Notification>();

    // Nhận Telegram API và logger qua dependency injection.
    public TelegramNotificationService(TelegramApiClient telegramApi,
        ILogger<TelegramNotificationService> logger)
    {
        this.telegramApi = telegramApi;
        this.logger = logger;
    }

    // Đưa thông báo vào queue để worker gửi nền, không bắt luồng Post phải chờ.
    public ValueTask QueueAsync(long chatId, string text, string postId,
        DateTimeOffset receivedAt, DateTimeOffset? postCreatedAt,
        CancellationToken cancellationToken, string? photoUrl = null,
        string? buttonUrl = null, bool useHtml = false)
    {
        Notification notification = new Notification(
            chatId, text, postId, receivedAt, postCreatedAt, photoUrl, buttonUrl, useHtml);

        return queue.Writer.WriteAsync(notification, cancellationToken);
    }

    // Khởi chạy 10 worker để nhiều người dùng nhận thông báo gần như cùng lúc.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] workers = Enumerable.Range(1, WorkerCount)
            .Select(workerId => SendMessagesAsync(workerId, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    // Mỗi worker lấy tin tiếp theo trong queue rồi gọi Telegram API.
    private async Task SendMessagesAsync(int workerId, CancellationToken cancellationToken)
    {
        await foreach (Notification notification in queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (notification.UseHtml && notification.ButtonUrl != null)
                {
                    await telegramApi.SendRichMessageAsync(notification.ChatId, notification.Text,
                        notification.PhotoUrl, notification.ButtonUrl, cancellationToken);
                }
                else
                {
                    await telegramApi.SendMessageAsync(notification.ChatId, notification.Text, cancellationToken);
                }

                LogDeliveryTime(notification);
                logger.LogInformation("[TELEGRAM] Gửi Post {PostId} thành công.", notification.PostId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "[TELEGRAM] Worker {WorkerId} gửi Post {PostId} thất bại.",
                    workerId, notification.PostId);
            }
        }
    }

    // Ghi thời gian xử lý của app và tổng thời gian từ lúc Post được tạo.
    private void LogDeliveryTime(Notification notification)
    {
        DateTimeOffset sentAt = DateTimeOffset.UtcNow;
        double appDelay = (sentAt - notification.ReceivedAt).TotalSeconds;
        double? totalDelay = notification.PostCreatedAt == null
            ? null
            : (sentAt - notification.PostCreatedAt.Value).TotalSeconds;

        logger.LogDebug(
            "Post {PostId} sent to Telegram. App delay: {AppDelay:F3}s. Total delay: {TotalDelay:F3}s",
            notification.PostId, appDelay, totalDelay);
    }

    private sealed record Notification(
        long ChatId,
        string Text,
        string PostId,
        DateTimeOffset ReceivedAt,
        DateTimeOffset? PostCreatedAt,
        string? PhotoUrl,
        string? ButtonUrl,
        bool UseHtml);
}
