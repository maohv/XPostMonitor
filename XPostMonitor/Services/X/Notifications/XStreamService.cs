using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Services.X;

namespace XPostMonitor.Services.X.Notifications;

// Đọc X Filtered Stream và chuyển Post mới sang PostNotificationService.
public sealed class XStreamService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly PostNotificationService postNotifications;
    private readonly ILogger<XStreamService> logger;
    private readonly bool enablePersonalBot;

    // Nhận database scope, X API, service xử lý Post và logger.
    public XStreamService(IServiceScopeFactory scopeFactory, XApiClient xApiClient,
        PostNotificationService postNotifications, BotOptions options,
        ILogger<XStreamService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.postNotifications = postNotifications;
        this.logger = logger;
        enablePersonalBot = options.EnablePersonalBot;
    }

    // Giữ kết nối X Stream luôn chạy; tự kết nối lại khi bị mất kết nối.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await HasWatchedAccountsAsync(stoppingToken))
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
                logger.LogError(exception, "X Filtered Stream bị ngắt kết nối.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    // Đọc từng dòng JSON từ X; dòng rỗng chỉ là tín hiệu giữ kết nối.
    private async Task ReadStreamAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await xApiClient.OpenFilteredStreamAsync(cancellationToken);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new StreamReader(stream);

        logger.LogDebug("Connected to X filtered stream");

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (TryReadPost(line, out XStreamPostResponse? message, out List<string> xUserIds))
            {
                await postNotifications.QueueAsync(message!, xUserIds, DateTimeOffset.UtcNow, cancellationToken);
                logger.LogInformation("[X] Nhận Post {PostId} thành công.", message!.Data!.Id);
            }
        }
    }

    // Chuyển JSON thành Post và lấy danh sách account ID từ matching rules.
    private static bool TryReadPost(string? json, out XStreamPostResponse? message, out List<string> xUserIds)
    {
        message = null;
        xUserIds = new List<string>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return false; // X gửi dòng rỗng mỗi 20 giây để giữ kết nối.
        }

        message = JsonSerializer.Deserialize<XStreamPostResponse>(json);
        if (message?.Data == null || message.MatchingRules == null)
        {
            return false;
        }

        foreach (XStreamMatch match in message.MatchingRules)
        {
            if (XRuleTag.TryGetXUserId(match.Tag, out string xUserId) && !xUserIds.Contains(xUserId))
            {
                xUserIds.Add(xUserId);
            }
        }

        if (xUserIds.Count == 0)
        {
            return false;
        }

        return true;
    }

    // Chỉ mở stream khi database có ít nhất một tài khoản đang được theo dõi.
    private async Task<bool> HasWatchedAccountsAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.XAccounts.AnyAsync(account =>
            account.TelegramChannelId != null
            || (enablePersonalBot && account.Watchers.Any(watcher => watcher.TelegramUser.IsPremium)),
            cancellationToken);
    }
}