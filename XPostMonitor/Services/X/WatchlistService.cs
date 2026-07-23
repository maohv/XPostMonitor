using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;

namespace XPostMonitor.Services.X;

// Thêm, xóa và hiển thị các tài khoản X mà người dùng muốn theo dõi.
public sealed class WatchlistService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly ILogger<WatchlistService> logger;

    // Nhận database scope, X API và logger qua dependency injection.
    public WatchlistService(IServiceScopeFactory scopeFactory, XApiClient xApiClient, ILogger<WatchlistService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.logger = logger;

        System.Diagnostics.Debug.Assert(IsValidXUsername("binancezh"));
        System.Diagnostics.Debug.Assert(!IsValidXUsername("invalid-name"));
    }

    // Kiểm tra username trên X rồi thêm tài khoản vào watchlist của Telegram user.
    public async Task<string> AddAsync(long chatId, string? username, CancellationToken cancellationToken)
    {
        if (!IsValidXUsername(username))
        {
            return "Usage: /add username";
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

            bool alreadyWatching = await db.WatchlistEntries
                .AnyAsync(item => item.ChatId == chatId && item.XUserId == xUser.Id, cancellationToken);
            if (alreadyWatching)
            {
                return "You are already monitoring @" + xUser.Username + ".";
            }

            DateTime now = DateTime.UtcNow;
            XAccount? account = await db.XAccounts.FindAsync(new object[] { xUser.Id }, cancellationToken);
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
            account.UpdatedAtUtc = now;

            db.WatchlistEntries.Add(new WatchlistEntry
            {
                ChatId = chatId,
                XUserId = xUser.Id,
                CreatedAtUtc = now
            });

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[DB] Thêm @{Username} vào watchlist thành công.", xUser.Username);
            return "Added @" + xUser.Username + " to your watchlist.";
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Unable to add X account {Username}", username);
            return "Could not check @" + username + ": " + exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[DB] Thêm @{Username} vào watchlist thất bại.", username);
            return "Could not save @" + username + " to the watchlist.";
        }
    }

    // Xóa một tài khoản X khỏi watchlist của Telegram user.
    public async Task<string> RemoveAsync(long chatId, string? username, CancellationToken cancellationToken)
    {
        if (!IsValidXUsername(username))
        {
            return "Usage: /remove username";
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string normalizedUsername = username!.ToLowerInvariant();

        WatchlistEntry? entry = await db.WatchlistEntries.Include(item => item.XAccount)
            .FirstOrDefaultAsync(item => item.ChatId == chatId
                && item.XAccount.Username.ToLower() == normalizedUsername, cancellationToken);

        if (entry == null)
        {
            return "You are not monitoring @" + username + ".";
        }

        try
        {
            db.WatchlistEntries.Remove(entry);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[DB] Xóa @{Username} khỏi watchlist thành công.", entry.XAccount.Username);
            return "Removed @" + entry.XAccount.Username + " from your watchlist.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[DB] Xóa @{Username} khỏi watchlist thất bại.", username);
            return "Could not remove @" + username + " from the watchlist.";
        }
    }

    // Đọc database và tạo nội dung trả về cho lệnh /list.
    public async Task<string> ListAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> usernames = await db.WatchlistEntries.Where(item => item.ChatId == chatId)
            .OrderBy(item => item.XAccount.Username)
            .Select(item => item.XAccount.Username)
            .ToListAsync(cancellationToken);

        return usernames.Count == 0
            ? "Your watchlist is empty."
            : "Monitoring:\n- " + string.Join("\n- ", usernames);
    }

    // Username X chỉ được chứa chữ, số, dấu gạch dưới và dài tối đa 15 ký tự.
    private static bool IsValidXUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 15)
        {
            return false;
        }

        return username.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}
