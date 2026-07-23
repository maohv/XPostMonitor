using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;

namespace XPostMonitor.Services.X;

// Đồng bộ Filtered Stream rule trên X với danh sách tài khoản trong database.
public sealed class XRuleSyncService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly ILogger<XRuleSyncService> logger;
    private readonly bool enablePersonalBot;
    private bool isConnected;

    // Nhận database scope, X API và logger qua dependency injection.
    public XRuleSyncService(IServiceScopeFactory scopeFactory, XApiClient xApiClient,
        BotOptions options, ILogger<XRuleSyncService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.logger = logger;
        enablePersonalBot = options.EnablePersonalBot;
    }

    // Chạy đồng bộ rule ngay khi khởi động, sau đó lặp lại mỗi 30 giây.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncRulesAsync(stoppingToken);

                if (!isConnected)
                {
                    logger.LogInformation("Kết nối X API thành công.");
                    isConnected = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                isConnected = false;
                logger.LogError(exception, "Kết nối hoặc đồng bộ X API gặp lỗi.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    // Thêm rule còn thiếu và xóa rule của account không còn ai theo dõi.
    private async Task SyncRulesAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<XAccount> accounts = await db.XAccounts
            .Where(account => account.TelegramChannelId != null
                || (enablePersonalBot && account.Watchers.Any(watcher => watcher.TelegramUser.IsPremium)))
            .ToListAsync(cancellationToken);

        Dictionary<string, string> desiredRules = accounts.ToDictionary(
            account => XRuleTag.Create(account.XUserId),
            account => "from:" + account.Username);

        List<XStreamRule> remoteRules = await xApiClient.GetStreamRulesAsync(cancellationToken);
        List<XStreamRule> managedRules = remoteRules
            .Where(rule => XRuleTag.TryGetXUserId(rule.Tag, out _))
            .ToList();

        List<string> deleteIds = managedRules
            .Where(rule => !desiredRules.TryGetValue(rule.Tag!, out string? value) || value != rule.Value)
            .Select(rule => rule.Id)
            .ToList();

        await xApiClient.DeleteStreamRulesAsync(deleteIds, cancellationToken);

        HashSet<string> existingTags = managedRules
            .Where(rule => !deleteIds.Contains(rule.Id))
            .Select(rule => rule.Tag!)
            .ToHashSet();

        List<XStreamRuleDefinition> addRules = desiredRules
            .Where(rule => !existingTags.Contains(rule.Key))
            .Select(rule => new XStreamRuleDefinition(rule.Value, rule.Key))
            .ToList();

        await xApiClient.AddStreamRulesAsync(addRules, cancellationToken);

        if (addRules.Count > 0 || deleteIds.Count > 0)
        {
            logger.LogDebug("X stream rules synchronized: {Added} added, {Deleted} deleted",
                addRules.Count, deleteIds.Count);
        }
    }
}