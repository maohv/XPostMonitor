using System.Globalization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Gmgn;

// Đặt TP sau khi launch và theo dõi kết quả bán từ GMGN.
public sealed class AutoTradingService : BackgroundService
{
    private static readonly HashSet<string> SupportedChains =
        new(StringComparer.OrdinalIgnoreCase) { "bsc", "base", "eth", "robinhood", "sol" };

    private readonly IServiceScopeFactory scopeFactory;
    private readonly AutoTradingSettingsService settings;
    private readonly GmgnClient gmgnClient;
    private readonly TelegramApiClient telegramApi;
    private readonly BotTextService text;
    private readonly ILogger<AutoTradingService> logger;
    private readonly Channel<AutoTradingRequest> queue = Channel.CreateUnbounded<AutoTradingRequest>();

    public AutoTradingService(IServiceScopeFactory scopeFactory, AutoTradingSettingsService settings,
        GmgnClient gmgnClient, TelegramApiClient telegramApi, BotTextService text,
        ILogger<AutoTradingService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.settings = settings;
        this.gmgnClient = gmgnClient;
        this.telegramApi = telegramApi;
        this.text = text;
        this.logger = logger;
    }

    public ValueTask QueueAsync(long chatId, string postId, string chain, string tokenAddress,
        string tokenName, string tokenSymbol, string walletAddress, decimal slippagePercent,
        string language, CancellationToken cancellationToken)
    {
        return queue.Writer.WriteAsync(new AutoTradingRequest(chatId, postId, chain, tokenAddress,
            tokenName, tokenSymbol, walletAddress, slippagePercent, BotTextService.Normalize(language)),
            cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MarkInterruptedPreparationsAsync(stoppingToken);
        Task prepareTask = PrepareLoopAsync(stoppingToken);
        Task monitorTask = MonitorLoopAsync(stoppingToken);
        await Task.WhenAll(prepareTask, monitorTask);
    }

    private async Task PrepareLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (AutoTradingRequest request in queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await PrepareAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[GMGN] Cannot prepare TP for token {TokenAddress}.",
                    request.TokenAddress);
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "AutoTradingFailed", exception.Message), cancellationToken);
            }
        }
    }

    private async Task PrepareAsync(AutoTradingRequest request, CancellationToken cancellationToken)
    {
        LaunchpadNetwork? network = LaunchpadCatalog.Find(request.Chain);
        if (network == null || !SupportedChains.Contains(network.GmgnChain))
        {
            throw new InvalidOperationException("GMGN does not support auto trading on this chain.");
        }

        GmgnCredentials? credentials = await settings.GetCredentialsAsync(request.ChatId, cancellationToken);
        List<TakeProfitSetting> levels = await settings.GetTakeProfitsAsync(request.ChatId, cancellationToken);
        if (credentials == null)
        {
            throw new InvalidOperationException("Connect GMGN in /settings first.");
        }
        if (levels.Count == 0)
        {
            throw new InvalidOperationException("Add at least one take-profit level in /settings first.");
        }

        GmgnTokenPosition position = await gmgnClient.GetTokenPositionAsync(credentials, network.GmgnChain,
            request.WalletAddress, request.TokenAddress, cancellationToken);

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AutoTrade trade = new AutoTrade
        {
            ChatId = request.ChatId,
            PostId = request.PostId,
            Chain = network.GmgnChain,
            TokenAddress = request.TokenAddress,
            TokenName = request.TokenName,
            TokenSymbol = request.TokenSymbol,
            WalletAddress = position.WalletAddress,
            QuoteTokenAddress = position.QuoteTokenAddress,
            EntryPrice = position.EntryPrice,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.AutoTrades.Add(trade);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            decimal? gasPriceGwei = network.GmgnChain == "bsc"
                ? await gmgnClient.GetAverageGasPriceGweiAsync(credentials, network.GmgnChain, cancellationToken)
                : null;
            for (int index = 0; index < levels.Count; index++)
            {
                TakeProfitSetting level = levels[index];
                decimal targetPrice = position.EntryPrice * (1m + level.ProfitPercent / 100m);
                string orderId = await gmgnClient.CreateTakeProfitAsync(credentials, network.GmgnChain,
                    position.WalletAddress, request.TokenAddress, position.QuoteTokenAddress, targetPrice,
                    level.SellPercent, request.SlippagePercent, gasPriceGwei, cancellationToken);
                db.AutoTradeOrders.Add(new AutoTradeOrder
                {
                    AutoTradeId = trade.Id,
                    GmgnOrderId = orderId,
                    ProfitPercent = level.ProfitPercent,
                    SellPercent = level.SellPercent,
                    TargetPrice = targetPrice,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);
                if (index < levels.Count - 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
            }

            trade.Status = "active";
            await db.SaveChangesAsync(cancellationToken);
            string levelText = string.Join("\n", levels.Select((level, index) => "TP" + (index + 1)
                + ": +" + level.ProfitPercent.ToString(CultureInfo.InvariantCulture) + "% -> "
                + level.SellPercent.ToString(CultureInfo.InvariantCulture) + "%"));
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "AutoTradingStarted", request.TokenSymbol, levelText), cancellationToken);
        }
        catch (Exception exception)
        {
            bool hasOrders = await db.AutoTradeOrders.AnyAsync(item => item.AutoTradeId == trade.Id,
                cancellationToken);
            trade.Status = hasOrders ? "active" : "failed";
            trade.ErrorMessage = Shorten(exception.Message);
            trade.CompletedAtUtc = hasOrders ? null : DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (hasOrders)
            {
                throw new InvalidOperationException("Some TP orders were created before GMGN returned an error: "
                    + exception.Message, exception);
            }
            throw;
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            try
            {
                await MonitorOnceAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[GMGN] Auto trading monitor failed.");
            }
        }
    }

    private async Task MonitorOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<long> tradeIds = await db.AutoTrades.Where(item => item.Status == "active")
            .Select(item => item.Id).ToListAsync(cancellationToken);

        foreach (long tradeId in tradeIds)
        {
            AutoTrade? trade = await db.AutoTrades.Include(item => item.Orders)
                .Include(item => item.TelegramUser).FirstOrDefaultAsync(item => item.Id == tradeId,
                    cancellationToken);
            if (trade == null || trade.Orders.Count == 0)
            {
                continue;
            }

            GmgnCredentials? credentials = await settings.GetCredentialsAsync(trade.ChatId, cancellationToken);
            if (credentials == null)
            {
                continue;
            }

            List<GmgnStrategyOrder> remoteOrders = await gmgnClient.GetTakeProfitOrdersAsync(credentials,
                trade.Chain, trade.WalletAddress, trade.TokenAddress, cancellationToken);
            foreach (AutoTradeOrder order in trade.Orders.Where(item => item.Status == "open"))
            {
                GmgnStrategyOrder? remote = remoteOrders.FirstOrDefault(item => item.OrderId == order.GmgnOrderId);
                if (remote == null || remote.Status == "open")
                {
                    continue;
                }

                order.Status = remote.Status;
                order.TransactionHash = remote.TransactionHash;
                order.RealizedProfitUsd = remote.RealizedProfitUsd;
                order.ClosedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                string language = BotTextService.Normalize(trade.TelegramUser.LanguageCode);
                string message = remote.Status == "filled"
                    ? text.Get(language, "TakeProfitFilled", trade.TokenSymbol, order.ProfitPercent,
                        order.SellPercent, remote.RealizedProfitUsd, remote.TransactionHash)
                    : text.Get(language, "TakeProfitFailed", trade.TokenSymbol, order.ProfitPercent);
                await telegramApi.SendMessageAsync(trade.ChatId, message, cancellationToken);
                order.NotifiedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            if (trade.Orders.All(item => item.Status != "open"))
            {
                trade.Status = "completed";
                trade.CompletedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                decimal totalProfit = trade.Orders.Where(item => item.Status == "filled")
                    .Sum(item => item.RealizedProfitUsd ?? 0m);
                decimal soldPercent = trade.Orders.Where(item => item.Status == "filled")
                    .Sum(item => item.SellPercent);
                string language = BotTextService.Normalize(trade.TelegramUser.LanguageCode);
                await telegramApi.SendMessageAsync(trade.ChatId,
                    text.Get(language, "AutoTradingCompleted", trade.TokenSymbol, soldPercent, totalProfit),
                    cancellationToken);
            }
        }
    }

    private async Task MarkInterruptedPreparationsAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<AutoTrade> interrupted = await db.AutoTrades.Include(item => item.Orders)
            .Where(item => item.Status == "preparing")
            .ToListAsync(cancellationToken);
        foreach (AutoTrade trade in interrupted)
        {
            trade.Status = trade.Orders.Count > 0 ? "active" : "failed";
            trade.ErrorMessage = "Bot stopped while TP orders were being prepared.";
            trade.CompletedAtUtc = trade.Orders.Count > 0 ? null : DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Shorten(string value)
    {
        return value.Length <= 500 ? value : value[..500];
    }

    private sealed record AutoTradingRequest(long ChatId, string PostId, string Chain, string TokenAddress,
        string TokenName, string TokenSymbol, string WalletAddress, decimal SlippagePercent, string Language);
}
