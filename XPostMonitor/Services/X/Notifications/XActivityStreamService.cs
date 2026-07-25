using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.X.Notifications;

// Đọc Activity Stream của X và gửi thông báo khi account đổi avatar.
public sealed class XActivityStreamService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly TelegramNotificationService telegramNotifications;
    private readonly ILogger<XActivityStreamService> logger;
    private readonly string channelLanguage;
    private readonly BotTextService text;

    public XActivityStreamService(IServiceScopeFactory scopeFactory, XApiClient xApiClient,
        TelegramNotificationService telegramNotifications, BotOptions options, BotTextService text,
        ILogger<XActivityStreamService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.telegramNotifications = telegramNotifications;
        this.logger = logger;
        channelLanguage = BotTextService.Normalize(options.ChannelLanguage);
        this.text = text;
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
            catch (HttpRequestException exception)
            {
                logger.LogWarning("[X] Activity Stream unavailable: {Message}. Retry in 30 seconds.",
                    exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
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
        XAccount? account = await db.XAccounts
            .FirstOrDefaultAsync(item => item.XUserId == activity.Filter.UserId, cancellationToken);

        if (account == null)
        {
            return;
        }

        string? originalAvatarUrl = GetOriginalAvatarUrl(activity.Payload?.After);
        string eventId = "avatar:" + account.XUserId;
        if (account.TelegramChannelId.HasValue)
        {
            await telegramNotifications.QueueAsync(account.TelegramChannelId.Value,
                CreateMessage(account.Username, originalAvatarUrl, channelLanguage), eventId, receivedAt, null,
                cancellationToken);
        }

        logger.LogInformation("[X] Nhận sự kiện đổi avatar của @{Username} thành công.", account.Username);
    }

    private string CreateMessage(string username, string? avatarUrl, string language)
    {
        string message = text.Get(language, "AvatarChanged", username);
        return string.IsNullOrWhiteSpace(avatarUrl)
            ? message
            : message + "\n\n" + text.Get(language, "NewAvatar", avatarUrl);
    }

    // X thường trả ảnh profile dạng *_normal; bỏ hậu tố để lấy đúng ảnh gốc.
    private static string? GetOriginalAvatarUrl(string? avatarUrl)
    {
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out Uri? uri)
            || !(uri.Host == "pbs.twimg.com" || uri.Host.EndsWith(".twimg.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        UriBuilder builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        string path = builder.Path;
        foreach (string size in new[] { "_normal.", "_bigger.", "_mini.", "_200x200.", "_400x400." })
        {
            path = path.Replace(size, ".", StringComparison.OrdinalIgnoreCase);
        }

        builder.Path = path;
        builder.Query = builder.Query
            .Replace("name=normal", "name=orig", StringComparison.OrdinalIgnoreCase)
            .Replace("name=small", "name=orig", StringComparison.OrdinalIgnoreCase)
            .Replace("name=medium", "name=orig", StringComparison.OrdinalIgnoreCase);
        return builder.Uri.AbsoluteUri;
    }

    private async Task<bool> HasAvatarSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.XSubscriptions.AnyAsync(item => item.EventType == XApiClient.AvatarEventType, cancellationToken);
    }
}
