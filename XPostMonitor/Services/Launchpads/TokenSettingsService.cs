using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Launchpads;

// Lưu cài đặt tạo token của từng Premium user. Không phụ thuộc GMGN.
public sealed class TokenSettingsService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly BotTextService text;

    public TokenSettingsService(IServiceScopeFactory scopeFactory, BotTextService text)
    {
        this.scopeFactory = scopeFactory;
        this.text = text;
    }

    public async Task<string> GetSummaryAsync(long chatId, string language,
        CancellationToken cancellationToken)
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
            text.Get(language, "EvmWallet") + ": " + text.Get(language,
                HasEvmWallet(settings) ? "Configured" : "NotConfigured"),
            text.Get(language, "AutoCreateLabel") + ": " + text.Get(language,
                settings?.EnableTokenCreation == true ? "Enabled" : "Disabled"),
            string.Empty
        ];

        foreach (LaunchpadNetwork network in LaunchpadCatalog.All)
        {
            UserChainTradingSettings? chain = chains.FirstOrDefault(item => item.Chain == network.Chain);
            string amount = chain?.BuyAmount > 0
                ? chain.BuyAmount.ToString(CultureInfo.InvariantCulture) + " " + network.Currency
                : text.Get(language, "NotSet");
            lines.Add(network.DisplayName + ": " + amount);
        }

        return string.Join("\n", lines);
    }

    public async Task<string> GetNetworkSummaryAsync(long chatId, string chain, string language,
        CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
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
        return text.Get(language, "NetworkSettings", network.DisplayName, amount);
    }

    public async Task<string> SaveBuyAmountAsync(long chatId, string chain, string value, string language,
        CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        if (network == null)
        {
            return text.Get(language, "UnsupportedNetworkShort");
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
            || amount <= 0)
        {
            return text.Get(language, "InvalidAmount");
        }

        if (amount < network.MinimumBuyAmount)
        {
            return text.Get(language, "MinimumBuyAmount", network.DisplayName,
                network.MinimumBuyAmount, network.Currency);
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserChainTradingSettings settings = await GetOrCreateChainAsync(db, chatId, network.Chain,
            cancellationToken);
        settings.BuyAmount = amount;
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "AmountSaved");
    }

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

        if (!HasEvmWallet(settings))
        {
            return text.Get(language, "LaunchpadSettingsMissing");
        }

        List<WatchlistEntry> routes = await db.WatchlistEntries
            .Where(item => item.ChatId == chatId && item.TokenChain != null && item.TokenDex != null)
            .ToListAsync(cancellationToken);
        List<string> routedChains = routes
            .Where(item => LaunchpadCatalog.IsValid(item.TokenChain, item.TokenDex))
            .Select(item => item.TokenChain!)
            .Distinct()
            .ToList();
        if (routedChains.Count == 0)
        {
            return text.Get(language, "AddRouteFirst");
        }

        List<UserChainTradingSettings> chainSettings = await db.UserChainTradingSettings
            .Where(item => item.ChatId == chatId && routedChains.Contains(item.Chain))
            .ToListAsync(cancellationToken);
        string? missingChain = routedChains.FirstOrDefault(chain =>
            !chainSettings.Any(item => item.Chain == chain && item.BuyAmount >=
                (LaunchpadCatalog.Find(chain)?.MinimumBuyAmount ?? 0m)));
        if (missingChain != null)
        {
            return text.Get(language, "SetChainFirst",
                LaunchpadCatalog.Find(missingChain)?.DisplayName ?? missingChain);
        }

        settings.EnableTokenCreation = true;
        await db.SaveChangesAsync(cancellationToken);
        return text.Get(language, "AutoEnabled");
    }

    public async Task<TokenCreateSettings?> GetAutoCreateSettingsAsync(long chatId, string chain,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        if (settings?.EnableTokenCreation != true)
        {
            return null;
        }

        return await GetChainSettingsAsync(db, chatId, chain, cancellationToken);
    }

    public async Task<TokenCreateSettings?> GetChainSettingsAsync(long chatId, string chain,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await GetChainSettingsAsync(db, chatId, chain, cancellationToken);
    }

    private static async Task<TokenCreateSettings?> GetChainSettingsAsync(AppDbContext db, long chatId,
        string chain, CancellationToken cancellationToken)
    {
        UserChainTradingSettings? settings = await db.UserChainTradingSettings
            .FindAsync([chatId, chain], cancellationToken);
        return settings == null || settings.BuyAmount <= 0 ? null : new TokenCreateSettings(settings.BuyAmount);
    }

    private static async Task<UserChainTradingSettings> GetOrCreateChainAsync(AppDbContext db, long chatId,
        string chain, CancellationToken cancellationToken)
    {
        UserChainTradingSettings? settings = await db.UserChainTradingSettings
            .FindAsync([chatId, chain], cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = new UserChainTradingSettings { ChatId = chatId, Chain = chain };
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

    private static bool HasEvmWallet(UserTradingSettings? settings)
    {
        return settings != null
            && !string.IsNullOrWhiteSpace(settings.EvmWalletAddress)
            && !string.IsNullOrWhiteSpace(settings.EncryptedEvmPrivateKey);
    }
}

public sealed record TokenCreateSettings(decimal BuyAmount);
