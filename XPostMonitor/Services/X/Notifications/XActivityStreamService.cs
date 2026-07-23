using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram;

namespace XPostMonitor.Services.X.Notifications;

// Đọc Activity Stream của X và gửi thông báo khi account đổi avatar.
public sealed class XActivityStreamService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly TelegramNotificationService telegramNotifications;
    private readonly ILogger<XActivityStreamService> logger;
    private readonly bool enablePersonalBot;

    public XActivityStreamService(IServiceScopeFactory scopeFactory, XApiClient xApiClient,
        TelegramNotificationService telegramNotifications, BotOptions options,
        ILogger<XActivityStreamService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.telegramNotifications = telegramNotifications;
        this.logger = logger;
        enablePersonalBot = options.EnablePersonalBot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await HasAvatarSubscriptionsAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                await ReadStreamAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "X Activity Stream bị ngắt kết nối.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task ReadStreamAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await xApiClient.OpenActivityStreamAsync(cancellationToken);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new StreamReader(stream);

        logger.LogInformation("Kết nối X Activity Stream thành công.");

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            XActivityEvent? activity = ReadAvatarEvent(line);
            if (activity != null)
            {
                await NotifyAsync(activity, DateTimeOffset.UtcNow, cancellationToken);
            }
        }
    }

    private static XActivityEvent? ReadAvatarEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        XActivityStreamResponse? message = JsonSerializer.Deserialize<XActivityStreamResponse>(json);
        if (message?.Data?.EventType != XApiClient.AvatarEventType || string.IsNullOrWhiteSpace(message.Data.Filter.UserId))
        {
            return null;
        }

        return message.Data;
    }

    private async Task NotifyAsync(XActivityEvent activity, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        XAccount? account = await db.XAccounts.Include(item => item.Watchers)
            .ThenInclude(watcher => watcher.TelegramUser)
            .FirstOrDefaultAsync(item => item.XUserId == activity.Filter.UserId, cancellationToken);

        if (account == null)
        {
            return;
        }

        string message = CreateMessage(account.Username, activity.Payload?.After);
        string eventId = "avatar:" + account.XUserId;

        if (account.TelegramChannelId.HasValue)
        {
            await telegramNotifications.QueueAsync(account.TelegramChannelId.Value, message, eventId, receivedAt, null, cancellationToken);
        }

        List<long> premiumChatIds = account.Watchers
            .Where(watcher => enablePersonalBot && watcher.TelegramUser.IsPremium)
            .Select(watcher => watcher.ChatId)
            .ToList();

        foreach (long chatId in premiumChatIds)
        {
            await telegramNotifications.QueueAsync(chatId, message, eventId, receivedAt, null, cancellationToken);
        }

        logger.LogInformation("[X] Nhận sự kiện đổi avatar của @{Username} thành công.", account.Username);
    }

    private static string CreateMessage(string username, string? avatarUrl)
    {
        string message = "Avatar changed for @" + username + ".\n\nhttps://x.com/" + username;
        return string.IsNullOrWhiteSpace(avatarUrl) ? message : message + "\n\nNew avatar: " + avatarUrl;
    }

    private async Task<bool> HasAvatarSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.XSubscriptions.AnyAsync(item => item.EventType == XApiClient.AvatarEventType, cancellationToken);
    }
}
