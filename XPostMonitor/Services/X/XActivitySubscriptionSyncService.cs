using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;

namespace XPostMonitor.Services.X;

// Đồng bộ avatar subscription trên X với các account đang được theo dõi trong database.
public sealed class XActivitySubscriptionSyncService : BackgroundService
{
    private const string TagPrefix = "xpostmonitor:avatar:";
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly ILogger<XActivitySubscriptionSyncService> logger;
    private readonly bool enablePersonalBot;

    public XActivitySubscriptionSyncService(IServiceScopeFactory scopeFactory, XApiClient xApiClient,
        BotOptions options, ILogger<XActivitySubscriptionSyncService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.logger = logger;
        enablePersonalBot = options.EnablePersonalBot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Đồng bộ avatar subscription với X gặp lỗi.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        HashSet<string> desiredUserIds = (await db.XAccounts
            .Where(account => account.TelegramChannelId != null
                || (enablePersonalBot && account.Watchers.Any(watcher => watcher.TelegramUser.IsPremium)))
            .Select(account => account.XUserId)
            .ToListAsync(cancellationToken)).ToHashSet();

        List<XSubscription> localSubscriptions = await db.XSubscriptions
            .Where(item => item.EventType == XApiClient.AvatarEventType)
            .ToListAsync(cancellationToken);
        List<XActivitySubscription> remoteSubscriptions = (await xApiClient
            .GetActivitySubscriptionsAsync(cancellationToken))
            .Where(item => item.EventType == XApiClient.AvatarEventType)
            .ToList();

        DateTime now = DateTime.UtcNow;
        foreach (XSubscription local in localSubscriptions.Where(item => !desiredUserIds.Contains(item.XUserId)))
        {
            XActivitySubscription? remote = remoteSubscriptions.FirstOrDefault(item =>
                (item.SubscriptionId == local.RemoteSubscriptionId || item.Filter.UserId == local.XUserId)
                && item.Tag != null && item.Tag.StartsWith(TagPrefix, StringComparison.Ordinal));
            if (remote != null)
            {
                await xApiClient.DeleteActivitySubscriptionAsync(remote.SubscriptionId, cancellationToken);
            }

            db.XSubscriptions.Remove(local);
            logger.LogInformation("[X] Xóa avatar subscription cho X user {XUserId} thành công.", local.XUserId);
        }

        foreach (string xUserId in desiredUserIds)
        {
            XSubscription? local = localSubscriptions.FirstOrDefault(item => item.XUserId == xUserId);
            XActivitySubscription? remote = remoteSubscriptions.FirstOrDefault(item => item.Filter.UserId == xUserId);
            if (remote != null)
            {
                if (local == null)
                {
                    db.XSubscriptions.Add(new XSubscription
                    {
                        XUserId = xUserId,
                        EventType = XApiClient.AvatarEventType,
                        RemoteSubscriptionId = remote.SubscriptionId,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }
                else if (local.RemoteSubscriptionId != remote.SubscriptionId)
                {
                    local.RemoteSubscriptionId = remote.SubscriptionId;
                    local.UpdatedAtUtc = now;
                }
                continue;
            }

            remote = await xApiClient.CreateAvatarSubscriptionAsync(xUserId, TagPrefix + xUserId,
                cancellationToken);
            if (local == null)
            {
                db.XSubscriptions.Add(new XSubscription
                {
                    XUserId = xUserId,
                    EventType = XApiClient.AvatarEventType,
                    RemoteSubscriptionId = remote.SubscriptionId,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            else
            {
                local.RemoteSubscriptionId = remote.SubscriptionId;
                local.UpdatedAtUtc = now;
            }

            logger.LogInformation("[X] Tạo avatar subscription cho X user {XUserId} thành công.", xUserId);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
