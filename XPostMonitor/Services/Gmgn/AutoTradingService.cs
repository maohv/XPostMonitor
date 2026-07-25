using System.Globalization;
using System.Numerics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using XPostMonitor.Configuration;
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
        new(StringComparer.OrdinalIgnoreCase) { "bsc", "base", "eth", "robinhood", "sol", "stable" };
    private const string TransferTopic =
        "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly AutoTradingSettingsService settings;
    private readonly GmgnClient gmgnClient;
    private readonly EvmNetworksOptions networks;
    private readonly TelegramApiClient telegramApi;
    private readonly BotTextService text;
    private readonly ILogger<AutoTradingService> logger;
    private readonly Channel<AutoTradingRequest> queue = Channel.CreateUnbounded<AutoTradingRequest>();

    public AutoTradingService(IServiceScopeFactory scopeFactory, AutoTradingSettingsService settings,
        GmgnClient gmgnClient, EvmNetworksOptions networks, TelegramApiClient telegramApi, BotTextService text,
        ILogger<AutoTradingService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.settings = settings;
        this.gmgnClient = gmgnClient;
        this.networks = networks;
        this.telegramApi = telegramApi;
        this.text = text;
        this.logger = logger;
    }

    public async ValueTask QueueAsync(long chatId, string postId, string chain, string tokenAddress,
        string tokenName, string tokenSymbol, string walletAddress, decimal slippagePercent,
        string? launchTransactionHash, string language, CancellationToken cancellationToken)
    {
        await AutoTradingDiagnosticLog.WriteAsync("QUEUED | Post=" + postId + " | Symbol=" + tokenSymbol
            + " | Chain=" + chain + " | Token=" + tokenAddress);
        await queue.Writer.WriteAsync(new AutoTradingRequest(chatId, postId, chain, tokenAddress,
            tokenName, tokenSymbol, walletAddress, slippagePercent, launchTransactionHash,
            BotTextService.Normalize(language)),
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
                await ProtectAndPrepareAsync(request, cancellationToken);
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

    private async Task ProtectAndPrepareAsync(AutoTradingRequest request,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource preparationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        logger.LogInformation("[GMGN] Starting TP preparation for {Symbol} ({TokenAddress}).",
            request.TokenSymbol, request.TokenAddress);
        await AutoTradingDiagnosticLog.WriteAsync("PREPARE START | Symbol=" + request.TokenSymbol
            + " | Token=" + request.TokenAddress);
        Task preparationTask = PrepareAsync(request, preparationCancellation.Token);

        TimeSpan buyerTimeout = TimeSpan.FromSeconds(30);
        DateTimeOffset buyerDeadline = DateTimeOffset.UtcNow.Add(buyerTimeout);
        DateTimeOffset? firstExternalBuy = null;
        try
        {
            firstExternalBuy = await WaitForExternalBuyAsync(request, buyerTimeout,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[GMGN] Cannot monitor buyers for token {TokenAddress}.",
                request.TokenAddress);
            await AutoTradingDiagnosticLog.WriteAsync("BUY MONITOR ERROR | Symbol=" + request.TokenSymbol
                + " | " + exception);
            TimeSpan remainingTime = buyerDeadline - DateTimeOffset.UtcNow;
            if (remainingTime > TimeSpan.Zero)
            {
                await Task.Delay(remainingTime, cancellationToken);
            }
        }

        if (firstExternalBuy == null)
        {
            preparationCancellation.Cancel();
            await ObservePreparationAsync(preparationTask, request);
            await ExitPositionAsync(request, GetAutoExitReason(false, false)!, cancellationToken);
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
        bool firstTakeProfitFilled = await IsFirstTakeProfitFilledAsync(request, cancellationToken);
        string? exitReason = GetAutoExitReason(true, firstTakeProfitFilled);
        if (exitReason == null)
        {
            await preparationTask;
            return;
        }

        preparationCancellation.Cancel();
        await ObservePreparationAsync(preparationTask, request);
        await ExitPositionAsync(request, exitReason, cancellationToken);
    }

    private static string? GetAutoExitReason(bool hasExternalBuyer, bool firstTakeProfitFilled)
    {
        if (!hasExternalBuyer)
        {
            return "No external buyer within 30 seconds.";
        }

        return firstTakeProfitFilled
            ? null
            : "TP1 was not filled within 60 seconds after the first buyer.";
    }

    private async Task<DateTimeOffset?> WaitForExternalBuyAsync(AutoTradingRequest request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(GetRpcUrl(request.Chain));
        BigInteger nextBlock;
        if (!string.IsNullOrWhiteSpace(request.LaunchTransactionHash))
        {
            TransactionReceipt receipt = await web3.Eth.Transactions.GetTransactionReceipt
                .SendRequestAsync(request.LaunchTransactionHash).WaitAsync(cancellationToken);
            nextBlock = receipt.BlockNumber.Value;
        }
        else
        {
            nextBlock = (await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()
                .WaitAsync(cancellationToken)).Value;
        }
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            BigInteger latestBlock = (await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()
                .WaitAsync(cancellationToken)).Value;
            if (latestBlock >= nextBlock)
            {
                NewFilterInput filter = new NewFilterInput
                {
                    Address = [request.TokenAddress],
                    FromBlock = new BlockParameter(new HexBigInteger(nextBlock)),
                    ToBlock = new BlockParameter(new HexBigInteger(latestBlock)),
                    Topics = [TransferTopic]
                };
                FilterLog[] logs = await web3.Eth.Filters.GetLogs.SendRequestAsync(filter)
                    .WaitAsync(cancellationToken);
                foreach (FilterLog log in logs)
                {
                    if (IsExternalTransfer(log, request.WalletAddress, request.LaunchTransactionHash))
                    {
                        return DateTimeOffset.UtcNow;
                    }
                }
                nextBlock = latestBlock + 1;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return null;
    }

    private static bool IsExternalTransfer(FilterLog log, string walletAddress,
        string? launchTransactionHash)
    {
        if (!string.IsNullOrWhiteSpace(launchTransactionHash)
            && string.Equals(log.TransactionHash, launchTransactionHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (log.Topics == null || log.Topics.Length < 3)
        {
            return false;
        }

        string topic = log.Topics[2]?.ToString() ?? string.Empty;
        if (topic.Length < 40)
        {
            return false;
        }

        string receiver = "0x" + topic[^40..];
        return receiver != "0x0000000000000000000000000000000000000000"
            && !string.Equals(receiver, walletAddress, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsFirstTakeProfitFilledAsync(AutoTradingRequest request,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AutoTrade? trade = await db.AutoTrades.Include(item => item.Orders)
            .Where(item => item.ChatId == request.ChatId && item.TokenAddress == request.TokenAddress)
            .OrderByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        AutoTradeOrder? firstOrder = trade?.Orders.OrderBy(item => item.ProfitPercent).FirstOrDefault();
        if (trade == null || firstOrder == null)
        {
            return false;
        }
        if (firstOrder.Status == "filled")
        {
            return true;
        }

        GmgnCredentials? credentials = await settings.GetCredentialsAsync(request.ChatId, cancellationToken);
        if (credentials == null)
        {
            return false;
        }
        List<GmgnStrategyOrder> remoteOrders = await gmgnClient.GetTakeProfitOrdersAsync(credentials,
            trade.Chain, trade.WalletAddress, trade.TokenAddress, cancellationToken);
        return remoteOrders.Any(item => item.OrderId == firstOrder.GmgnOrderId && item.Status == "filled");
    }

    private async Task ExitPositionAsync(AutoTradingRequest request, string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            LaunchpadNetwork network = LaunchpadCatalog.Find(request.Chain)
                ?? throw new InvalidOperationException("Unknown token network.");
            GmgnCredentials credentials = await settings.GetCredentialsAsync(request.ChatId,
                cancellationToken) ?? throw new InvalidOperationException("GMGN is not connected.");
            string quoteToken = await gmgnClient.GetQuoteTokenAsync(credentials, network.GmgnChain,
                request.TokenAddress, cancellationToken);
            if (network.GmgnChain == "bsc")
            {
                quoteToken = "0x0000000000000000000000000000000000000000";
            }
            string sellReference = await gmgnClient.SellAllAsync(credentials, network.GmgnChain,
                request.WalletAddress, request.TokenAddress, quoteToken, request.SlippagePercent,
                cancellationToken);

            await CloseRemainingOrdersAsync(request, credentials, network.GmgnChain, cancellationToken);
            await MarkTradeExitedAsync(request, reason, cancellationToken);
            await AutoTradingDiagnosticLog.WriteAsync("AUTO EXIT SOLD | Symbol=" + request.TokenSymbol
                + " | Reason=" + reason + " | Reference=" + sellReference);
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "AutoExitSold", request.TokenSymbol, reason, sellReference),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[GMGN] Cannot auto-sell token {TokenAddress}.", request.TokenAddress);
            await AutoTradingDiagnosticLog.WriteAsync("AUTO EXIT ERROR | Symbol=" + request.TokenSymbol
                + " | Reason=" + reason + " | " + exception);
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "AutoExitFailed", request.TokenSymbol, reason, exception.Message),
                cancellationToken);
        }
    }

    private async Task CloseRemainingOrdersAsync(AutoTradingRequest request, GmgnCredentials credentials,
        string chain, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AutoTrade? trade = await db.AutoTrades.Include(item => item.Orders)
            .Where(item => item.ChatId == request.ChatId && item.TokenAddress == request.TokenAddress)
            .OrderByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        if (trade == null)
        {
            return;
        }

        foreach (AutoTradeOrder order in trade.Orders.Where(item => item.Status == "open"))
        {
            try
            {
                await gmgnClient.CancelTakeProfitAsync(credentials, chain, trade.WalletAddress,
                    order.GmgnOrderId, cancellationToken);
                order.Status = "cancelled";
                order.ClosedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "[GMGN] Cannot cancel TP order {OrderId}.", order.GmgnOrderId);
            }
        }
    }

    private async Task MarkTradeExitedAsync(AutoTradingRequest request, string reason,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AutoTrade? trade = await db.AutoTrades
            .Where(item => item.ChatId == request.ChatId && item.TokenAddress == request.TokenAddress)
            .OrderByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        if (trade == null)
        {
            return;
        }

        trade.Status = "exited";
        trade.ErrorMessage = reason;
        trade.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ObservePreparationAsync(Task preparationTask, AutoTradingRequest request)
    {
        try
        {
            await preparationTask;
        }
        catch (OperationCanceledException)
        {
            await AutoTradingDiagnosticLog.WriteAsync("PREPARE CANCELLED | Symbol=" + request.TokenSymbol
                + " | Token=" + request.TokenAddress);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[GMGN] TP preparation failed for {Symbol} ({TokenAddress}).",
                request.TokenSymbol, request.TokenAddress);
            await AutoTradingDiagnosticLog.WriteAsync("PREPARE ERROR | Symbol=" + request.TokenSymbol
                + " | " + exception);
        }
    }

    private string GetRpcUrl(string chain)
    {
        return chain.ToLowerInvariant() switch
        {
            "bsc" => networks.BscRpcUrl,
            "base" => networks.BaseRpcUrl,
            "robinhood" => networks.RobinhoodRpcUrl,
            "stable" => networks.StableRpcUrl,
            _ => throw new InvalidOperationException("No RPC URL is configured for " + chain + ".")
        };
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
        logger.LogInformation("[GMGN] Position found for {Symbol}. Entry price: {EntryPrice}.",
            request.TokenSymbol, position.EntryPrice);
        await AutoTradingDiagnosticLog.WriteAsync("POSITION FOUND | Symbol=" + request.TokenSymbol
            + " | Entry=" + position.EntryPrice.ToString(CultureInfo.InvariantCulture)
            + " | Quote=" + position.QuoteTokenAddress);

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
            BigInteger tokenBalance = await GetTokenBalanceAsync(request.Chain, position.WalletAddress,
                request.TokenAddress, cancellationToken);
            await AutoTradingDiagnosticLog.WriteAsync("TP BALANCE | Symbol=" + request.TokenSymbol
                + " | Raw=" + tokenBalance);
            decimal? gasPriceGwei = network.GmgnChain == "bsc"
                ? await gmgnClient.GetAverageGasPriceGweiAsync(credentials, network.GmgnChain, cancellationToken)
                : null;
            if (network.GmgnChain == "bsc" && gasPriceGwei < 0.05m)
            {
                gasPriceGwei = 0.05m;
            }
            await AutoTradingDiagnosticLog.WriteAsync("TP GAS | Symbol=" + request.TokenSymbol
                + " | GasGwei=" + (gasPriceGwei?.ToString(CultureInfo.InvariantCulture) ?? "auto"));
            for (int index = 0; index < levels.Count; index++)
            {
                TakeProfitSetting level = levels[index];
                decimal targetPrice = position.EntryPrice * (1m + level.ProfitPercent / 100m);
                logger.LogInformation("[GMGN] Creating TP{Level} for {Symbol}: +{ProfitPercent}%, sell {SellPercent}%.",
                    index + 1, request.TokenSymbol, level.ProfitPercent, level.SellPercent);
                await AutoTradingDiagnosticLog.WriteAsync("TP CREATE | Symbol=" + request.TokenSymbol
                    + " | Level=" + (index + 1) + " | Profit=" + level.ProfitPercent
                    + " | Sell=" + level.SellPercent + " | Target="
                    + targetPrice.ToString(CultureInfo.InvariantCulture));
                int sellBasisPoints = decimal.ToInt32(level.SellPercent * 100m);
                BigInteger amountIn = tokenBalance * sellBasisPoints / 10_000;
                if (amountIn <= 0)
                {
                    throw new InvalidOperationException("Token balance is too small for TP" + (index + 1) + ".");
                }
                string orderId = await gmgnClient.CreateTakeProfitAsync(credentials, network.GmgnChain,
                    position.WalletAddress, request.TokenAddress, position.QuoteTokenAddress, targetPrice, amountIn,
                    request.SlippagePercent, gasPriceGwei, cancellationToken);
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
                logger.LogInformation("[GMGN] TP{Level} created for {Symbol}. Order: {OrderId}.",
                    index + 1, request.TokenSymbol, orderId);
                await AutoTradingDiagnosticLog.WriteAsync("TP CREATED | Symbol=" + request.TokenSymbol
                    + " | Level=" + (index + 1) + " | Order=" + orderId);
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

    private async Task<BigInteger> GetTokenBalanceAsync(string chain, string walletAddress,
        string tokenAddress, CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(GetRpcUrl(chain));
        return await web3.Eth.ERC20.GetContractService(tokenAddress).BalanceOfQueryAsync(walletAddress)
            .WaitAsync(cancellationToken);
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
        string TokenName, string TokenSymbol, string WalletAddress, decimal SlippagePercent,
        string? LaunchTransactionHash, string Language);
}
