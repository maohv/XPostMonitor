using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Gmgn;

// Lưu kết nối GMGN và danh sách TP dùng chung của từng Premium user.
public sealed class AutoTradingSettingsService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IDataProtector protector;
    private readonly GmgnClient gmgnClient;
    private readonly GmgnOptions options;
    private readonly BotTextService text;

    public AutoTradingSettingsService(IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protectionProvider, GmgnClient gmgnClient,
        GmgnOptions options, BotTextService text)
    {
        this.scopeFactory = scopeFactory;
        protector = protectionProvider.CreateProtector("XPostMonitor.GmgnCredentials.v1");
        this.gmgnClient = gmgnClient;
        this.options = options;
        this.text = text;
    }

    public async Task<string> GetSummaryAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        List<TakeProfitSetting> levels = await db.TakeProfitSettings.Where(item => item.ChatId == chatId)
            .OrderBy(item => item.ProfitPercent).ToListAsync(cancellationToken);

        List<string> lines =
        [
            text.Get(language, "TradingSettingsTitle"),
            string.Empty,
            "GMGN: " + text.Get(language, TryGetCredentials(settings, out _) ? "Configured" : "NotConfigured"),
            text.Get(language, "TakeProfitLevels") + ":"
        ];

        lines.AddRange(levels.Count == 0
            ? [text.Get(language, "NoTakeProfitLevels")]
            : levels.Select((level, index) => "TP" + (index + 1) + ": +"
                + level.ProfitPercent.ToString(CultureInfo.InvariantCulture) + "% -> "
                + level.SellPercent.ToString(CultureInfo.InvariantCulture) + "%"));
        return string.Join("\n", lines);
    }

    public async Task<string> CreateConnectionAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(options.PublicServerIp, out IPAddress? ip)
            || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return text.Get(language, "ServerIpMissing");
        }

        try
        {
            GmgnSigningKeyPair keys = await gmgnClient.GenerateSigningKeyAsync(cancellationToken);
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            UserTradingSettings settings = await GetOrCreateAsync(db, chatId, cancellationToken);
            settings.EncryptedGmgnPrivateKey = protector.Protect(keys.PrivateKey);
            settings.EncryptedGmgnApiKey = string.Empty;
            await db.SaveChangesAsync(cancellationToken);
            return text.Get(language, "ConnectionCreated", keys.PublicKey.Trim(), options.PublicServerIp);
        }
        catch (Exception exception)
        {
            return text.Get(language, "GenerateFailed", exception.Message);
        }
    }

    public async Task<string> SaveApiKeyAsync(long chatId, string value, string language,
        CancellationToken cancellationToken)
    {
        value = value.Trim();
        if (value.Length < 10)
        {
            return text.Get(language, "InvalidApiKey");
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings settings = await GetOrCreateAsync(db, chatId, cancellationToken);
        if (!TryUnprotect(settings.EncryptedGmgnPrivateKey, out _))
        {
            return text.Get(language, "GenerateFirst");
        }

        settings.EncryptedGmgnApiKey = protector.Protect(value);
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "ApiKeySaved");
    }

    public async Task<string> CheckConnectionAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        GmgnCredentials? credentials = await GetCredentialsAsync(chatId, cancellationToken);
        if (credentials == null)
        {
            return text.Get(language, "ConnectFirst");
        }

        GmgnConnectionResult result = await gmgnClient.CheckUserConnectionAsync(credentials.ApiKey,
            credentials.PrivateKey, cancellationToken);
        return result.Success
            ? text.Get(language, "GmgnConnected", result.Message)
            : text.Get(language, "GmgnFailed", result.Message);
    }

    public async Task<string> AddTakeProfitAsync(long chatId, string value, string language,
        CancellationToken cancellationToken)
    {
        string[] values = value.Replace("%", string.Empty).Split([' ', '/', ',', '\r', '\n', '\t', ';'],
            StringSplitOptions.RemoveEmptyEntries);
        List<(decimal ProfitPercent, decimal SellPercent)> newLevels = [];
        if (values.Length == 0 || values.Length % 2 != 0)
        {
            return text.Get(language, "InvalidTakeProfit");
        }

        for (int index = 0; index < values.Length; index += 2)
        {
            if (!decimal.TryParse(values[index], NumberStyles.Number, CultureInfo.InvariantCulture,
                    out decimal profitPercent)
                || !decimal.TryParse(values[index + 1], NumberStyles.Number, CultureInfo.InvariantCulture,
                    out decimal sellPercent)
                || profitPercent <= 0 || sellPercent <= 0 || sellPercent > 100)
            {
                return text.Get(language, "InvalidTakeProfit");
            }
            newLevels.Add((profitPercent, sellPercent));
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<TakeProfitSetting> levels = await db.TakeProfitSettings
            .Where(item => item.ChatId == chatId).ToListAsync(cancellationToken);
        if (newLevels.Select(item => item.ProfitPercent).Distinct().Count() != newLevels.Count
            || newLevels.Any(newLevel => levels.Any(item => item.ProfitPercent == newLevel.ProfitPercent)))
        {
            return text.Get(language, "DuplicateTakeProfit");
        }
        if (levels.Count + newLevels.Count > 4)
        {
            return text.Get(language, "TakeProfitLimit");
        }
        if (levels.Sum(item => item.SellPercent) + newLevels.Sum(item => item.SellPercent) > 100)
        {
            return text.Get(language, "TakeProfitTotalTooHigh");
        }

        db.TakeProfitSettings.AddRange(newLevels.Select(level => new TakeProfitSetting
        {
            ChatId = chatId,
            ProfitPercent = level.ProfitPercent,
            SellPercent = level.SellPercent
        }));
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "TakeProfitSaved");
    }

    public async Task RemoveTakeProfitAsync(long chatId, int id, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TakeProfitSetting? level = await db.TakeProfitSettings
            .FirstOrDefaultAsync(item => item.Id == id && item.ChatId == chatId, cancellationToken);
        if (level == null)
        {
            return;
        }

        db.TakeProfitSettings.Remove(level);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<TakeProfitSetting>> GetTakeProfitsAsync(long chatId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.TakeProfitSettings.Where(item => item.ChatId == chatId)
            .OrderBy(item => item.ProfitPercent).AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<GmgnCredentials?> GetCredentialsAsync(long chatId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        return TryGetCredentials(settings, out GmgnCredentials credentials) ? credentials : null;
    }

    private static async Task<UserTradingSettings> GetOrCreateAsync(AppDbContext db, long chatId,
        CancellationToken cancellationToken)
    {
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = new UserTradingSettings { ChatId = chatId };
        db.UserTradingSettings.Add(settings);
        return settings;
    }

    private bool TryGetCredentials(UserTradingSettings? settings, out GmgnCredentials credentials)
    {
        credentials = null!;
        if (settings == null
            || !TryUnprotect(settings.EncryptedGmgnApiKey, out string apiKey)
            || !TryUnprotect(settings.EncryptedGmgnPrivateKey, out string privateKey))
        {
            return false;
        }

        credentials = new GmgnCredentials(apiKey, privateKey);
        return true;
    }

    private bool TryUnprotect(string encryptedValue, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(encryptedValue))
        {
            return false;
        }

        try
        {
            value = protector.Unprotect(encryptedValue);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record GmgnCredentials(string ApiKey, string PrivateKey);
