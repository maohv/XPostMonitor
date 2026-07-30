using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Launchpads;

// Lưu cài đặt tạo token của từng Premium user. Không phụ thuộc GMGN.
public sealed class TokenSettingsService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly BotTextService text;
    private readonly TradingWorkersOptions workerOptions;

    public TokenSettingsService(IServiceScopeFactory scopeFactory, BotTextService text,
        TradingWorkersOptions workerOptions)
    {
        this.scopeFactory = scopeFactory;
        this.text = text;
        this.workerOptions = workerOptions;
    }

    public async Task<string> GetSummaryAsync(long chatId, string language,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        int walletCount = await db.TradingWorkers.CountAsync(item => item.ChatId == chatId
            && item.IsEnabled && item.EvmWalletAddress != "" && item.EncryptedEvmPrivateKey != "",
            cancellationToken);
        List<UserChainTradingSettings> chains = await db.UserChainTradingSettings
            .Where(item => item.ChatId == chatId).ToListAsync(cancellationToken);

        List<string> lines =
        [
            text.Get(language, "SettingsTitle"),
            string.Empty,
            text.Get(language, "ParallelWallets") + ": " + walletCount + "/" + workerOptions.MaxWorkers,
            text.Get(language, "AutoCreateLabel") + ": " + text.Get(language,
                settings?.EnableTokenCreation == true ? "Enabled" : "Disabled"),
            string.Empty
        ];

        foreach (LaunchpadNetwork network in LaunchpadCatalog.All)
        {
            UserChainTradingSettings? chain = chains.FirstOrDefault(item => item.Chain == network.Chain);
            string amount = network.MinimumBuyAmount == 0
                ? text.Get(language, "GasOnly")
                : chain?.BuyAmount > 0
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
        if (network.MinimumBuyAmount == 0)
        {
            return text.Get(language, "NetworkSettingsGasOnly", network.DisplayName);
        }

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
        if (network.MinimumBuyAmount == 0)
        {
            return text.Get(language, "GasOnly");
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
            || amount <= 0)
        {
            return text.Get(language, "InvalidAmount");
        }

        // BSC dùng chung Settings cho Four.Meme và Flap nên bot chỉ yêu cầu số tiền lớn hơn 0.
        // Nếu contract launchpad có giới hạn riêng, lỗi thật sẽ được ghi vào log khi tạo.
        if (network.Chain != "bsc" && amount < network.MinimumBuyAmount)
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

        List<WatchlistEntry> routes = await db.WatchlistEntries
            .Where(item => item.ChatId == chatId && item.TokenChain != null && item.TokenDex != null)
            .ToListAsync(cancellationToken);
        List<string> routedChains = routes
            .Where(item => LaunchpadCatalog.IsValidRoute(item.TokenChain, item.TokenDex, item.TokenAnchor))
            .Select(item => item.TokenChain!)
            .Distinct()
            .ToList();
        if (routedChains.Count == 0)
        {
            return text.Get(language, "AddRouteFirst");
        }

        int requiredWorkers = routes.Max(item => Math.Clamp(item.ParallelTokenCount, 1,
            workerOptions.MaxWorkers));
        List<int> readySlots = await db.TradingWorkers
            .Where(item => item.ChatId == chatId && item.IsEnabled
                && item.EvmWalletAddress != "" && item.EncryptedEvmPrivateKey != "")
            .Select(item => item.SlotNumber).ToListAsync(cancellationToken);
        int? missingSlot = Enumerable.Range(1, requiredWorkers)
            .FirstOrDefault(slot => !readySlots.Contains(slot));
        if (missingSlot > 0)
        {
            return text.Get(language, "ConfigureWorkerFirst", missingSlot);
        }

        int requiredGmgnWorkers = routes.Where(item => item.EnableAutoTrading)
            .Select(item => Math.Clamp(item.ParallelTokenCount, 1, workerOptions.MaxWorkers))
            .DefaultIfEmpty(0).Max();
        if (requiredGmgnWorkers > 0)
        {
            List<int> gmgnSlots = await db.TradingWorkers
                .Where(item => item.ChatId == chatId && item.IsEnabled
                    && item.EncryptedGmgnApiKey != "" && item.EncryptedGmgnPrivateKey != "")
                .Select(item => item.SlotNumber).ToListAsync(cancellationToken);
            int? missingGmgnSlot = Enumerable.Range(1, requiredGmgnWorkers)
                .FirstOrDefault(slot => !gmgnSlots.Contains(slot));
            if (missingGmgnSlot > 0)
            {
                return text.Get(language, "ConfigureGmgnFirst", missingGmgnSlot);
            }
        }

        List<UserChainTradingSettings> chainSettings = await db.UserChainTradingSettings
            .Where(item => item.ChatId == chatId && routedChains.Contains(item.Chain))
            .ToListAsync(cancellationToken);
        WatchlistEntry? missingRoute = routes.FirstOrDefault(route =>
        {
            if (!LaunchpadCatalog.IsValidRoute(route.TokenChain, route.TokenDex, route.TokenAnchor))
            {
                return false;
            }
            // BSC dùng chung một số tiền cho Flap và Four.Meme, bot chỉ yêu cầu lớn hơn 0.
            decimal minimum = route.TokenChain == "bsc"
                ? 0m : LaunchpadCatalog.Find(route.TokenChain)?.MinimumBuyAmount ?? 0m;
            return !chainSettings.Any(item => item.Chain == route.TokenChain && item.BuyAmount > 0
                && (minimum == 0 || item.BuyAmount >= minimum));
        });
        if (missingRoute != null)
        {
            return text.Get(language, "SetChainFirst",
                LaunchpadCatalog.Find(missingRoute.TokenChain)?.DisplayName ?? missingRoute.TokenChain!);
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
        LaunchpadNetwork? network = LaunchpadCatalog.Find(chain);
        if (network == null)
        {
            return null;
        }
        if (network.MinimumBuyAmount == 0)
        {
            return new TokenCreateSettings(0m, 5m);
        }

        UserChainTradingSettings? settings = await db.UserChainTradingSettings
            .FindAsync([chatId, chain], cancellationToken);
        return settings == null || settings.BuyAmount <= 0
            ? null
            : new TokenCreateSettings(settings.BuyAmount,
                settings.SlippagePercent > 0 ? settings.SlippagePercent : 5m);
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

}

public sealed record TokenCreateSettings(decimal BuyAmount, decimal SlippagePercent);
