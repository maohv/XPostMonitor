using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.X;

// Thêm, xóa và hiển thị các tài khoản X mà người dùng muốn theo dõi.
public sealed class WatchlistService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly ILogger<WatchlistService> logger;
    private readonly BotTextService text;
    private readonly TradingWorkersOptions workerOptions;

    // Nhận database scope, X API và logger qua dependency injection.
    public WatchlistService(IServiceScopeFactory scopeFactory, XApiClient xApiClient,
        ILogger<WatchlistService> logger, BotTextService text, TradingWorkersOptions workerOptions)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.logger = logger;
        this.text = text;
        this.workerOptions = workerOptions;
    }

    // Kiểm tra username trên X rồi thêm tài khoản vào watchlist của Telegram user.
    public async Task<string> AddAsync(long chatId, string? username, string? chain, string? dex, string? anchor,
        int creatorTaxPercent, bool enableAutoTrading, int parallelTokenCount, string language,
        CancellationToken cancellationToken)
    {
        username = username?.Trim().TrimStart('@');
        if (!IsValidXUsername(username))
        {
            return text.Get(language, "AddUsage");
        }

        bool alertsOnly = string.IsNullOrWhiteSpace(chain) && string.IsNullOrWhiteSpace(dex);
        parallelTokenCount = alertsOnly ? 1 : Math.Clamp(parallelTokenCount, 1, workerOptions.MaxWorkers);
        if (!alertsOnly && !LaunchpadCatalog.IsValidRoute(chain, dex, anchor))
        {
            return text.Get(language, "UnsupportedRoute");
        }

        creatorTaxPercent = LaunchpadCatalog.SupportsCreatorTax(dex)
            ? creatorTaxPercent : 0;
        if (!LaunchpadCatalog.IsValidCreatorTax(dex, creatorTaxPercent))
        {
            return text.Get(language, "UnsupportedRoute");
        }

        chain = chain?.ToLowerInvariant();
        dex = dex?.ToLowerInvariant();
        anchor = LaunchpadCatalog.NormalizeRouteOption(chain, dex, anchor);

        try
        {
            XUser? xUser = await xApiClient.GetUserByUsernameAsync(username!, cancellationToken);

            if (xUser == null)
            {
                return text.Get(language, "XAccountNotFound");
            }

            if (xUser.Protected)
            {
                return text.Get(language, "ProtectedAccount");
            }

            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            WatchlistEntry? existingEntry = await db.WatchlistEntries
                .Include(item => item.XAccount)
                .FirstOrDefaultAsync(item => item.ChatId == chatId && item.XUserId == xUser.Id, cancellationToken);
            if (existingEntry != null)
            {
                existingEntry.TokenChain = alertsOnly ? null : chain;
                existingEntry.TokenDex = alertsOnly ? null : dex;
                existingEntry.TokenAnchor = alertsOnly ? null : anchor;
                existingEntry.CreatorTaxPercent = creatorTaxPercent;
                existingEntry.EnableAutoTrading = enableAutoTrading;
                existingEntry.ParallelTokenCount = parallelTokenCount;
                existingEntry.XAccount.Username = xUser.Username;
                existingEntry.XAccount.DisplayName = xUser.Name;
                existingEntry.XAccount.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return text.Get(language, "WatchUpdated", xUser.Username,
                    FormatMode(chain, dex, anchor, creatorTaxPercent, language)
                    + (alertsOnly ? string.Empty : " | " + text.Get(language,
                        enableAutoTrading ? "AutoTradingEnabled" : "AutoTradingDisabled")
                        + " | " + text.Get(language, "TokensPerPost") + ": " + parallelTokenCount));
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
                TokenChain = alertsOnly ? null : chain,
                TokenDex = alertsOnly ? null : dex,
                TokenAnchor = alertsOnly ? null : anchor,
                CreatorTaxPercent = creatorTaxPercent,
                EnableAutoTrading = enableAutoTrading,
                ParallelTokenCount = parallelTokenCount,
                CreatedAtUtc = now
            });

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[DB] Thêm @{Username} vào watchlist thành công.", xUser.Username);
            return text.Get(language, "WatchAdded", xUser.Username,
                FormatMode(chain, dex, anchor, creatorTaxPercent, language)
                + (alertsOnly ? string.Empty : " | " + text.Get(language,
                    enableAutoTrading ? "AutoTradingEnabled" : "AutoTradingDisabled")
                    + " | " + text.Get(language, "TokensPerPost") + ": " + parallelTokenCount));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Unable to add X account {Username}", username);
            return text.Get(language, "WatchCheckFailed", username, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[DB] Thêm @{Username} vào watchlist thất bại.", username);
            return text.Get(language, "WatchSaveFailed", username);
        }
    }

    // Xóa một tài khoản X khỏi watchlist của Telegram user.
    public async Task<string> RemoveAsync(long chatId, string? username, string language,
        CancellationToken cancellationToken)
    {
        if (!IsValidXUsername(username))
        {
            return text.Get(language, "RemoveUsage");
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string normalizedUsername = username!.ToLowerInvariant();

        WatchlistEntry? entry = await db.WatchlistEntries.Include(item => item.XAccount)
            .FirstOrDefaultAsync(item => item.ChatId == chatId
                && item.XAccount.Username.ToLower() == normalizedUsername, cancellationToken);

        if (entry == null)
        {
            return text.Get(language, "NotMonitoring", username);
        }

        try
        {
            db.WatchlistEntries.Remove(entry);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[DB] Xóa @{Username} khỏi watchlist thành công.", entry.XAccount.Username);
            return text.Get(language, "WatchRemoved", entry.XAccount.Username);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[DB] Xóa @{Username} khỏi watchlist thất bại.", username);
            return text.Get(language, "WatchRemoveFailed", username);
        }
    }

    // Đọc database và tạo nội dung trả về cho lệnh /list.
    public async Task<string> ListAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        bool autoCreateEnabled = await db.UserTradingSettings
            .Where(item => item.ChatId == chatId)
            .Select(item => item.EnableTokenCreation)
            .FirstOrDefaultAsync(cancellationToken);

        List<WatchlistEntry> entries = await db.WatchlistEntries.Where(item => item.ChatId == chatId)
            .Include(item => item.XAccount)
            .OrderBy(item => item.XAccount.Username)
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return text.Get(language, "WatchEmpty");
        }

        List<string> lines = entries.Select(entry =>
        {
            LaunchpadNetwork? network = LaunchpadCatalog.Find(entry.TokenChain);
            LaunchpadInfo? launchpad = network?.Launchpads.FirstOrDefault(item => item.Code == entry.TokenDex);
            string mode = network == null || launchpad == null
                ? text.Get(language, "AlertsOnly")
                : network.DisplayName + " · " + launchpad.DisplayName
                    + FormatRouteOption(entry.TokenChain, entry.TokenDex, entry.TokenAnchor, language)
                    + FormatCreatorTax(entry.TokenDex, entry.CreatorTaxPercent, language)
                    + " · " + text.Get(language, autoCreateEnabled ? "AutoCreate" : "AutoCreateOff");
            if (network != null && launchpad != null)
            {
                mode += " | " + text.Get(language,
                    entry.EnableAutoTrading ? "AutoTradingEnabled" : "AutoTradingDisabled")
                    + " | " + text.Get(language, "TokensPerPost") + ": " + entry.ParallelTokenCount;
            }
            return "@" + entry.XAccount.Username + "\n   " + mode;
        }).ToList();

        return text.Get(language, "WatchTitle", string.Join("\n\n", lines));
    }

    // Lấy username để tạo các nút chỉnh sửa trong màn hình /list.
    public async Task<List<string>> GetUsernamesAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WatchlistEntries
            .Where(item => item.ChatId == chatId)
            .Include(item => item.XAccount)
            .OrderBy(item => item.XAccount.Username)
            .Select(item => item.XAccount.Username)
            .ToListAsync(cancellationToken);
    }

    // Đọc cấu hình hiện tại để menu sửa hiển thị sẵn các lựa chọn đã lưu.
    public async Task<WatchlistEditSettings?> GetEditSettingsAsync(long chatId, string username,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string normalizedUsername = username.ToLowerInvariant();
        WatchlistEntry? entry = await db.WatchlistEntries
            .Include(item => item.XAccount)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ChatId == chatId
                && item.XAccount.Username.ToLower() == normalizedUsername, cancellationToken);
        return entry == null
            ? null
            : new WatchlistEditSettings(entry.XAccount.Username, entry.TokenChain, entry.TokenDex,
                entry.TokenAnchor, entry.CreatorTaxPercent, entry.EnableAutoTrading,
                entry.ParallelTokenCount);
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

    private string FormatMode(string? chain, string? dex, string? anchor, int creatorTaxPercent, string language)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        LaunchpadInfo? launchpad = network?.Launchpads.FirstOrDefault(item => item.Code == dex);
        return network == null || launchpad == null
            ? text.Get(language, "AlertsOnly")
            : network.DisplayName + " · " + launchpad.DisplayName
                + FormatRouteOption(chain, dex, anchor, language)
                + FormatCreatorTax(dex, creatorTaxPercent, language);
    }

    private string FormatRouteOption(string? chain, string? dex, string? option, string language)
    {
        if (dex == "long")
        {
            return option == null ? string.Empty : " · " + option;
        }
        if (LaunchpadCatalog.IsFlapBsc(chain, dex))
        {
            return " · " + text.Get(language, "PaymentToken") + ": "
                + LaunchpadCatalog.FindFlapBscPaymentToken(option)!.Code;
        }
        return string.Empty;
    }

    private string FormatCreatorTax(string? dex, int creatorTaxPercent, string language)
    {
        return LaunchpadCatalog.SupportsCreatorTax(dex)
            ? " · " + text.Get(language, "CreatorTax") + ": "
                + (creatorTaxPercent == 0 ? text.Get(language, "NoCreatorTax") : creatorTaxPercent + "%")
            : string.Empty;
    }
}

public sealed record WatchlistEditSettings(string Username, string? Chain, string? Dex, string? Anchor,
    int CreatorTaxPercent, bool EnableAutoTrading, int WorkerCount);
