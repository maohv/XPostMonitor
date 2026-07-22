using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;

namespace XPostMonitor.Services.X.Channels;

// Quản lý danh sách tài khoản X được đăng lên Telegram Channel, tách khỏi watchlist cá nhân.
public sealed class ChannelWatchlistService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly ILogger<ChannelWatchlistService> logger;

    // Nhận database, X API và logger qua dependency injection.
    public ChannelWatchlistService(IServiceScopeFactory scopeFactory, XApiClient xApiClient,
        ILogger<ChannelWatchlistService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.logger = logger;
    }

    // Kiểm tra tài khoản X rồi gắn tài khoản đó với Channel.
    public async Task<string> AddAsync(long channelId, string? username,
        CancellationToken cancellationToken)
    {
        if (!IsValidXUsername(username))
        {
            return "Usage: /channeladd username";
        }

        try
        {
            XUser? xUser = await xApiClient.GetUserByUsernameAsync(username!, cancellationToken);
            if (xUser == null)
            {
                return "X account not found.";
            }

            if (xUser.Protected)
            {
                return "Protected X accounts cannot be monitored.";
            }

            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            XAccount? account = await db.XAccounts.FindAsync(
                new object[] { xUser.Id }, cancellationToken);

            if (account?.TelegramChannelId == channelId)
            {
                return "@" + xUser.Username + " is already monitored by the channel.";
            }

            DateTime now = DateTime.UtcNow;
            if (account == null)
            {
                account = new XAccount
                {
                    XUserId = xUser.Id,
                    CreatedAtUtc = now
                };
                db.XAccounts.Add(account);
            }

            account.Username = xUser.Username;
            account.DisplayName = xUser.Name;
            account.TelegramChannelId = channelId;
            account.UpdatedAtUtc = now;

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[DB] Added @{Username} to the channel watchlist.", xUser.Username);
            return "Added @" + xUser.Username + " to the channel.";
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Unable to add X account {Username} to the channel.", username);
            return "Could not check @" + username + ": " + exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[DB] Could not add @{Username} to the channel.", username);
            return "Could not add @" + username + " to the channel.";
        }
    }

    // Bỏ tài khoản X khỏi Channel nhưng không đụng vào watchlist cá nhân.
    public async Task<string> RemoveAsync(long channelId, string? username,
        CancellationToken cancellationToken)
    {
        if (!IsValidXUsername(username))
        {
            return "Usage: /channelremove username";
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string normalizedUsername = username!.ToLowerInvariant();

        XAccount? account = await db.XAccounts.FirstOrDefaultAsync(item =>
            item.TelegramChannelId == channelId
            && item.Username.ToLower() == normalizedUsername, cancellationToken);

        if (account == null)
        {
            return "@" + username + " is not monitored by the channel.";
        }

        account.TelegramChannelId = null;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("[DB] Removed @{Username} from the channel watchlist.", account.Username);
        return "Removed @" + account.Username + " from the channel.";
    }

    // Đọc và hiển thị danh sách tài khoản X đang được Channel theo dõi.
    public async Task<string> ListAsync(long channelId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> usernames = await db.XAccounts
            .Where(item => item.TelegramChannelId == channelId)
            .OrderBy(item => item.Username)
            .Select(item => item.Username)
            .ToListAsync(cancellationToken);

        return usernames.Count == 0
            ? "The channel watchlist is empty."
            : "Channel monitoring:\n- " + string.Join("\n- ", usernames);
    }

    // Username X chỉ gồm chữ, số, dấu gạch dưới và dài tối đa 15 ký tự.
    private static bool IsValidXUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 15)
        {
            return false;
        }

        return username.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}
