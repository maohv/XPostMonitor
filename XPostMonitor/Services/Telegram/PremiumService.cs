using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Telegram;

// Lưu người dùng Telegram và quản lý quyền sử dụng bot cá nhân.
public sealed class PremiumService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly BotTextService text;

    public PremiumService(IServiceScopeFactory scopeFactory, BotTextService text)
    {
        this.scopeFactory = scopeFactory;
        this.text = text;
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
                CreatedAtUtc = now,
                LanguageCode = BotTextService.Normalize(sender.LanguageCode)
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

    public async Task<string> GetLanguageAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string? language = await db.TelegramUsers.Where(user => user.ChatId == chatId)
            .Select(user => user.LanguageCode).FirstOrDefaultAsync(cancellationToken);
        return BotTextService.Normalize(language);
    }

    public async Task SetLanguageAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TelegramUser? user = await db.TelegramUsers.FindAsync([chatId], cancellationToken);
        if (user != null)
        {
            user.LanguageCode = BotTextService.Normalize(language);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // Chỉ người có IsPremium = true mới được dùng /add, /remove và /list.
    public async Task<bool> IsPremiumAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.TelegramUsers.AnyAsync(user => user.ChatId == chatId && user.IsPremium, cancellationToken);
    }

    // Admin bật Premium cho một username đã từng gửi /start.
    public Task<string> AddAsync(string? username, string language, CancellationToken cancellationToken)
    {
        return SetPremiumAsync(username, true, language, cancellationToken);
    }

    // Admin tắt Premium nhưng vẫn giữ lại watchlist cũ của người dùng.
    public Task<string> RemoveAsync(string? username, string language, CancellationToken cancellationToken)
    {
        return SetPremiumAsync(username, false, language, cancellationToken);
    }

    // Hiển thị toàn bộ username đang có Premium.
    public async Task<string> ListAsync(string language, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<string> usernames = await db.TelegramUsers
            .Where(user => user.IsPremium)
            .OrderBy(user => user.Username)
            .Select(user => user.Username == null ? user.TelegramUserId.ToString() : "@" + user.Username)
            .ToListAsync(cancellationToken);

        return usernames.Count == 0
            ? text.Get(language, "PremiumNone")
            : text.Get(language, "PremiumList", string.Join("\n", usernames));
    }

    private async Task<string> SetPremiumAsync(string? username, bool isPremium, string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return text.Get(language, isPremium ? "PremiumAddUsage" : "PremiumRemoveUsage");
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
            return text.Get(language, "UserNotFound");
        }

        user.IsPremium = isPremium;
        await db.SaveChangesAsync(cancellationToken);

        return text.Get(language, isPremium ? "PremiumEnabled" : "PremiumDisabled", user.Username);
    }
}
