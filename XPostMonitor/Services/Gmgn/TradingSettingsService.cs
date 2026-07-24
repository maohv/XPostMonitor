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

// Đọc và lưu cài đặt GMGN của từng Premium user.
public sealed class TradingSettingsService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IDataProtector protector;
    private readonly GmgnClient gmgnClient;
    private readonly GmgnOptions options;
    private readonly BotTextService text;

    public TradingSettingsService(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider,
        GmgnClient gmgnClient, GmgnOptions options, BotTextService text)
    {
        this.scopeFactory = scopeFactory;
        protector = protectionProvider.CreateProtector("XPostMonitor.GmgnCredentials.v1");
        this.gmgnClient = gmgnClient;
        this.options = options;
        this.text = text;
    }

    // Tạo nội dung chính cho lệnh /settings.
    public async Task<string> GetSummaryAsync(long chatId, string language, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        List<UserChainTradingSettings> chains = await db.UserChainTradingSettings
            .Where(item => item.ChatId == chatId).ToListAsync(cancellationToken);

        List<string> lines =
        [
            text.Get(language, "SettingsTitle"),
            string.Empty,
            text.Get(language, "GmgnApi") + ": " + text.Get(language,
                TryGetCredentials(settings, out _) ? "Configured" : "NotConfigured"),
            text.Get(language, "AutoCreateLabel") + ": " + text.Get(language,
                settings?.EnableTokenCreation == true ? "Enabled" : "Disabled"),
            string.Empty
        ];

        foreach (TradingNetwork network in TradingNetworks.All)
        {
            UserChainTradingSettings? chain = chains.FirstOrDefault(item => item.Chain == network.Chain);
            string amount = chain?.BuyAmount > 0
                ? chain.BuyAmount.ToString(CultureInfo.InvariantCulture) + " " + network.Currency
                : text.Get(language, "NotSet");
            decimal slippage = chain?.SlippagePercent ?? 5m;
            lines.Add(network.DisplayName + ": " + amount + " · " + text.Get(language, "Slippage") + " "
                + slippage.ToString(CultureInfo.InvariantCulture) + "%");
        }

        return string.Join("\n", lines);
    }

    // Hiển thị riêng số tiền và slippage của một network.
    public async Task<string> GetNetworkSummaryAsync(long chatId, string chain, string language,
        CancellationToken cancellationToken)
    {
        TradingNetwork? network = TradingNetworks.Find(chain);
        if (network == null)
        {
            return text.Get(language, "UnsupportedNetworkShort");
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserChainTradingSettings? settings = await db.UserChainTradingSettings
            .FindAsync([chatId, network.Chain], cancellationToken);

        string amount = settings?.BuyAmount > 0
            ? settings.BuyAmount.ToString(CultureInfo.InvariantCulture) + " " + network.Currency
            : text.Get(language, "NotSet");
        decimal slippage = settings?.SlippagePercent ?? 5m;
        return text.Get(language, "NetworkSettings", network.DisplayName, amount,
            slippage.ToString(CultureInfo.InvariantCulture));
    }

    // Mã hóa API key trước khi lưu database.
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
        if (!HasUsablePrivateKey(settings))
        {
            return text.Get(language, "GenerateFirst");
        }

        settings.EncryptedGmgnApiKey = protector.Protect(value);
        settings.EnableTokenCreation = false;
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "ApiKeySaved");
    }

    // Bot tự tạo khóa ký, mã hóa private key và chỉ đưa public key cho user.
    public async Task<string> CreateGmgnConnectionAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(options.PublicServerIp, out IPAddress? ip)
            || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return text.Get(language, "ServerIpMissing");
        }

        GmgnSigningKeyPair keys;
        try
        {
            keys = await gmgnClient.GenerateSigningKeyAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return text.Get(language, "GenerateFailed", exception.Message);
        }
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings settings = await GetOrCreateAsync(db, chatId, cancellationToken);
        settings.EncryptedGmgnPrivateKey = protector.Protect(keys.PrivateKey);
        settings.EncryptedGmgnApiKey = string.Empty;
        settings.EnableTokenCreation = false;
        await db.SaveChangesAsync(cancellationToken);

        return text.Get(language, "ConnectionCreated", keys.PublicKey.Trim(), options.PublicServerIp);
    }

    public async Task<string> SaveBuyAmountAsync(long chatId, string chain, string value, string language,
        CancellationToken cancellationToken)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
            || amount <= 0)
        {
            return text.Get(language, "InvalidAmount");
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserChainTradingSettings? settings = await GetOrCreateChainAsync(db, chatId, chain, cancellationToken);
        if (settings == null)
        {
            return text.Get(language, "UnsupportedNetworkShort");
        }

        settings.BuyAmount = amount;
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "AmountSaved");
    }

    public async Task<string> SaveSlippageAsync(long chatId, string chain, string value, string language,
        CancellationToken cancellationToken)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal slippage)
            || slippage <= 0 || slippage > 100)
        {
            return text.Get(language, "InvalidSlippage");
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserChainTradingSettings? settings = await GetOrCreateChainAsync(db, chatId, chain, cancellationToken);
        if (settings == null)
        {
            return text.Get(language, "UnsupportedNetworkShort");
        }

        settings.SlippagePercent = slippage;
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "SlippageSaved");
    }

    // Bật Auto Create chỉ khi khóa, route, số tiền và ví GMGN đều đầy đủ.
    public async Task<string> ToggleAutoCreateAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings settings = await GetOrCreateAsync(db, chatId, cancellationToken);

        if (settings.EnableTokenCreation)
        {
            settings.EnableTokenCreation = false;
            await db.SaveChangesAsync(cancellationToken);
            return text.Get(language, "AutoDisabled");
        }

        if (!TryGetCredentials(settings, out GmgnCredentials credentials))
        {
            return text.Get(language, "ConnectFirst");
        }

        List<string> routedChains = await db.WatchlistEntries
            .Where(item => item.ChatId == chatId && item.TokenChain != null && item.TokenDex != null)
            .Select(item => item.TokenChain!)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (routedChains.Count == 0)
        {
            return text.Get(language, "AddRouteFirst");
        }

        List<UserChainTradingSettings> chainSettings = await db.UserChainTradingSettings
            .Where(item => item.ChatId == chatId && routedChains.Contains(item.Chain))
            .ToListAsync(cancellationToken);
        string? missingChain = routedChains.FirstOrDefault(chain =>
            !chainSettings.Any(item => item.Chain == chain && item.BuyAmount > 0
                && item.SlippagePercent > 0 && item.SlippagePercent <= 100));
        if (missingChain != null)
        {
            return text.Get(language, "SetChainFirst",
                TradingNetworks.Find(missingChain)?.DisplayName ?? missingChain);
        }

        try
        {
            await gmgnClient.ValidateCredentialsAsync(credentials.ApiKey, credentials.PrivateKey,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return text.Get(language, "CannotEnableAuto", exception.Message);
        }

        foreach (string chain in routedChains)
        {
            try
            {
                await gmgnClient.GetWalletAddressAsync(credentials.ApiKey, chain, cancellationToken);
            }
            catch (Exception exception)
            {
                return text.Get(language, "CannotEnableAuto", exception.Message);
            }
        }

        settings.EnableTokenCreation = true;
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "AutoEnabled");
    }

    public async Task<string> CheckConnectionAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        if (!TryGetCredentials(settings, out GmgnCredentials credentials))
        {
            return text.Get(language, "ConnectFirst");
        }

        GmgnConnectionResult result = await gmgnClient.CheckUserConnectionAsync(credentials.ApiKey,
            credentials.PrivateKey, cancellationToken);
        return result.Success
            ? text.Get(language, "GmgnConnected", result.Message)
            : text.Get(language, "GmgnFailed", result.Message);
    }

    // Lấy cấu hình ngay trước khi tạo token để việc tắt Auto Create có hiệu lực tức thì.
    public async Task<AutoCreateSettings?> GetAutoCreateSettingsAsync(long chatId, string chain,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        UserChainTradingSettings? chainSettings = await db.UserChainTradingSettings
            .FindAsync([chatId, chain], cancellationToken);

        if (settings?.EnableTokenCreation != true || !TryGetCredentials(settings, out GmgnCredentials credentials)
            || chainSettings == null || chainSettings.BuyAmount <= 0)
        {
            return null;
        }

        return new AutoCreateSettings(
            credentials,
            chainSettings.BuyAmount,
            chainSettings.SlippagePercent);
    }

    // Tạo thủ công đã có nút xác nhận riêng nên không yêu cầu Auto Create phải bật.
    public async Task<AutoCreateSettings?> GetManualCreateSettingsAsync(long chatId, string chain,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        UserChainTradingSettings? chainSettings = await db.UserChainTradingSettings
            .FindAsync([chatId, chain], cancellationToken);

        if (!TryGetCredentials(settings, out GmgnCredentials credentials)
            || chainSettings == null || chainSettings.BuyAmount <= 0)
        {
            return null;
        }

        return new AutoCreateSettings(credentials, chainSettings.BuyAmount, chainSettings.SlippagePercent);
    }

    private static async Task<UserChainTradingSettings?> GetOrCreateChainAsync(AppDbContext db, long chatId,
        string chain, CancellationToken cancellationToken)
    {
        TradingNetwork? network = TradingNetworks.Find(chain);
        if (network == null)
        {
            return null;
        }

        UserChainTradingSettings? settings = await db.UserChainTradingSettings
            .FindAsync([chatId, network.Chain], cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = new UserChainTradingSettings
        {
            ChatId = chatId,
            Chain = network.Chain,
            SlippagePercent = 5m
        };
        db.UserChainTradingSettings.Add(settings);
        return settings;
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

    private static bool HasCredentials(UserTradingSettings? settings)
    {
        return settings != null
            && !string.IsNullOrWhiteSpace(settings.EncryptedGmgnApiKey)
            && !string.IsNullOrWhiteSpace(settings.EncryptedGmgnPrivateKey);
    }

    private bool HasUsablePrivateKey(UserTradingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.EncryptedGmgnPrivateKey))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(protector.Unprotect(settings.EncryptedGmgnPrivateKey));
        }
        catch
        {
            return false;
        }
    }

    // Khóa mã hóa của máy cũ không dùng được trên máy mới; khi đó yêu cầu user nhập lại.
    private bool TryGetCredentials(UserTradingSettings? settings, out GmgnCredentials credentials)
    {
        credentials = null!;
        if (!HasCredentials(settings))
        {
            return false;
        }

        try
        {
            credentials = new GmgnCredentials(
                protector.Unprotect(settings!.EncryptedGmgnApiKey),
                protector.Unprotect(settings.EncryptedGmgnPrivateKey));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record AutoCreateSettings(GmgnCredentials Credentials, decimal BuyAmount, decimal SlippagePercent);
