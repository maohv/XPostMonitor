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
    private readonly TradingWorkersOptions workerOptions;
    private readonly BotTextService text;

    public AutoTradingSettingsService(IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protectionProvider, GmgnClient gmgnClient,
        GmgnOptions options, TradingWorkersOptions workerOptions, BotTextService text)
    {
        this.scopeFactory = scopeFactory;
        protector = protectionProvider.CreateProtector("XPostMonitor.GmgnCredentials.v1");
        this.gmgnClient = gmgnClient;
        this.options = options;
        this.workerOptions = workerOptions;
        this.text = text;
    }

    public async Task<string> GetSummaryAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<TradingWorker> workers = await db.TradingWorkers.Where(item => item.ChatId == chatId)
            .OrderBy(item => item.SlotNumber).ToListAsync(cancellationToken);
        List<TakeProfitSetting> levels = await db.TakeProfitSettings.Where(item => item.ChatId == chatId)
            .OrderBy(item => item.ProfitPercent).ToListAsync(cancellationToken);

        List<string> lines =
        [
            text.Get(language, "TradingSettingsTitle"),
            string.Empty,
            .. Enumerable.Range(1, workerOptions.MaxWorkers).Select(slot =>
            {
                TradingWorker? worker = workers.FirstOrDefault(item => item.SlotNumber == slot);
                return text.Get(language, "Worker") + " " + slot + " GMGN: "
                    + text.Get(language, TryGetCredentials(worker, out _) ? "Configured" : "NotConfigured");
            }),
            string.Empty,
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
        return await CreateConnectionAsync(chatId, 1, language, cancellationToken);
    }

    public async Task<string> CreateConnectionAsync(long chatId, int slotNumber, string language,
        CancellationToken cancellationToken)
    {
        ValidateSlot(slotNumber);
        if (!IPAddress.TryParse(options.PublicServerIp, out IPAddress? ip)
            || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return text.Get(language, "ServerIpMissing");
        }

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            TradingWorker? worker = await db.TradingWorkers
                .FirstOrDefaultAsync(item => item.ChatId == chatId && item.SlotNumber == slotNumber,
                    cancellationToken);
            if (!HasEvmWallet(worker))
            {
                return text.Get(language, "ConfigureWalletBeforeGmgn", slotNumber);
            }

            GmgnSigningKeyPair keys = await gmgnClient.GenerateSigningKeyAsync(cancellationToken);
            worker!.EncryptedGmgnPrivateKey = protector.Protect(keys.PrivateKey);
            worker.EncryptedGmgnApiKey = string.Empty;
            worker.UpdatedAtUtc = DateTime.UtcNow;
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
        return await SaveApiKeyAsync(chatId, 1, value, language, cancellationToken);
    }

    public async Task<string> SaveApiKeyAsync(long chatId, int slotNumber, string value, string language,
        CancellationToken cancellationToken)
    {
        ValidateSlot(slotNumber);
        value = value.Trim();
        if (value.Length < 10)
        {
            return text.Get(language, "InvalidApiKey");
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TradingWorker? worker = await db.TradingWorkers
            .FirstOrDefaultAsync(item => item.ChatId == chatId && item.SlotNumber == slotNumber,
                cancellationToken);
        if (!HasEvmWallet(worker))
        {
            return text.Get(language, "ConfigureWalletBeforeGmgn", slotNumber);
        }
        if (!TryUnprotect(worker!.EncryptedGmgnPrivateKey, out _))
        {
            return text.Get(language, "GenerateFirst");
        }

        worker.EncryptedGmgnApiKey = protector.Protect(value);
        worker.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "ApiKeySaved");
    }

    public async Task<string> CheckConnectionAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        return await CheckConnectionAsync(chatId, 1, language, cancellationToken);
    }

    public async Task<string> CheckConnectionAsync(long chatId, int slotNumber, string language,
        CancellationToken cancellationToken)
    {
        GmgnCredentials? credentials = await GetCredentialsBySlotAsync(chatId, slotNumber, cancellationToken);
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
        return await GetCredentialsBySlotAsync(chatId, 1, cancellationToken);
    }

    public async Task<GmgnCredentials?> GetCredentialsBySlotAsync(long chatId, int slotNumber,
        CancellationToken cancellationToken)
    {
        ValidateSlot(slotNumber);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TradingWorker? worker = await db.TradingWorkers.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ChatId == chatId && item.SlotNumber == slotNumber,
                cancellationToken);
        return TryGetCredentials(worker, out GmgnCredentials credentials) ? credentials : null;
    }

    public async Task<GmgnCredentials?> GetCredentialsAsync(long chatId, long workerId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TradingWorker? worker = await db.TradingWorkers.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ChatId == chatId && item.Id == workerId, cancellationToken);
        return TryGetCredentials(worker, out GmgnCredentials credentials) ? credentials : null;
    }

    public async Task<IReadOnlyList<GmgnWorkerState>> GetWorkerStatesAsync(long chatId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<TradingWorker> workers = await db.TradingWorkers.AsNoTracking()
            .Where(item => item.ChatId == chatId).OrderBy(item => item.SlotNumber)
            .ToListAsync(cancellationToken);
        return Enumerable.Range(1, workerOptions.MaxWorkers).Select(slot =>
        {
            TradingWorker? worker = workers.FirstOrDefault(item => item.SlotNumber == slot);
            return new GmgnWorkerState(slot, HasEvmWallet(worker), TryGetCredentials(worker, out _));
        }).ToList();
    }

    private static bool HasEvmWallet(TradingWorker? worker)
    {
        return worker != null
            && !string.IsNullOrWhiteSpace(worker.EvmWalletAddress)
            && !string.IsNullOrWhiteSpace(worker.EncryptedEvmPrivateKey);
    }

    private bool TryGetCredentials(TradingWorker? worker, out GmgnCredentials credentials)
    {
        credentials = null!;
        if (worker == null
            || !TryUnprotect(worker.EncryptedGmgnApiKey, out string apiKey)
            || !TryUnprotect(worker.EncryptedGmgnPrivateKey, out string privateKey))
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

    private void ValidateSlot(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > workerOptions.MaxWorkers)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        }
    }
}

public sealed record GmgnCredentials(string ApiKey, string PrivateKey);

public sealed record GmgnWorkerState(int SlotNumber, bool HasWallet, bool HasCredentials);
