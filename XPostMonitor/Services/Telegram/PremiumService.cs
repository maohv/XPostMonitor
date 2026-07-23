using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;

namespace XPostMonitor.Services.Telegram;

// Lưu người dùng Telegram và quản lý quyền sử dụng bot cá nhân.
public sealed class PremiumService
{
    private readonly IServiceScopeFactory scopeFactory;

    public PremiumService(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    // Người dùng phải gửi /start ít nhất một lần để bot lưu username.
    public async Task RegisterUserAsync(long chatId, TelegramFrom sender, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TelegramUser? user = await db.TelegramUsers.FindAsync(new object[] { chatId }, cancellationToken);

        DateTime now = DateTime.UtcNow;
        if (user == null)
        {
            user = new TelegramUser
            {
                ChatId = chatId,
                TelegramUserId = sender.Id,
                CreatedAtUtc = now
            };
            db.TelegramUsers.Add(user);
        }

        user.Username = sender.Username?.ToLowerInvariant();
        user.DisplayName = string.IsNullOrWhiteSpace(sender.LastName)
            ? sender.FirstName
            : sender.FirstName + " " + sender.LastName;
        user.LastSeenAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
    }

    // Chỉ người có IsPremium = true mới được dùng /add, /remove và /list.
    public async Task<bool> IsPremiumAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.TelegramUsers.AnyAsync(user => user.ChatId == chatId && user.IsPremium, cancellationToken);
    }

    // Admin bật Premium cho một username đã từng gửi /start.
    public Task<string> AddAsync(string? username, CancellationToken cancellationToken)
    {
        return SetPremiumAsync(username, true, cancellationToken);
    }

    // Admin tắt Premium nhưng vẫn giữ lại watchlist cũ của người dùng.
    public Task<string> RemoveAsync(string? username, CancellationToken cancellationToken)
    {
        return SetPremiumAsync(username, false, cancellationToken);
    }

    // Hiển thị toàn bộ username đang có Premium.
    public async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<string> usernames = await db.TelegramUsers
            .Where(user => user.IsPremium)
            .OrderBy(user => user.Username)
            .Select(user => user.Username == null ? user.TelegramUserId.ToString() : "@" + user.Username)
            .ToListAsync(cancellationToken);

        return usernames.Count == 0
            ? "No Premium users."
            : "Premium users:\n" + string.Join("\n", usernames);
    }

    private async Task<string> SetPremiumAsync(string? username, bool isPremium, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return isPremium
                ? "Usage: /premiumadd username"
                : "Usage: /premiumremove username";
        }

        string normalizedUsername = username.Trim().TrimStart('@').ToLowerInvariant();
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TelegramUser? user = await db.TelegramUsers
            .Where(user => user.Username != null && user.Username.ToLower() == normalizedUsername)
            .OrderByDescending(user => user.LastSeenAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null)
        {
            return "User not found. Ask them to send /start to this bot first.";
        }

        user.IsPremium = isPremium;
        await db.SaveChangesAsync(cancellationToken);

        return isPremium
            ? "Premium enabled for @" + user.Username + "."
            : "Premium disabled for @" + user.Username + ".";
    }
}
