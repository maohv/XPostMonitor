using System.Globalization;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Gmgn.Launchpads;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Gmgn;

// Đặt TP sau khi launch và theo dõi kết quả bán từ GMGN.
public sealed class AutoTradingService : BackgroundService
{
    private const string TransferTopic =
        "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly AutoTradingSettingsService settings;
    private readonly GmgnClient gmgnClient;
    private readonly IReadOnlyList<IAutoTradingLaunchpadHandler> launchpadHandlers;
    private readonly TelegramApiClient telegramApi;
    private readonly BotTextService text;
    private readonly ILogger<AutoTradingService> logger;
    private readonly TradingWorkersOptions workerOptions;
    private readonly AutoTradingOptions autoTradingOptions;
    private readonly Channel<AutoTradingRequest> queue = Channel.CreateUnbounded<AutoTradingRequest>();
    private readonly ConcurrentDictionary<string, GroupedNotification> groupedNotifications = new();

    public AutoTradingService(IServiceScopeFactory scopeFactory, AutoTradingSettingsService settings,
        GmgnClient gmgnClient, IEnumerable<IAutoTradingLaunchpadHandler> launchpadHandlers,
        TelegramApiClient telegramApi, BotTextService text, TradingWorkersOptions workerOptions,
        AutoTradingOptions autoTradingOptions, ILogger<AutoTradingService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.settings = settings;
        this.gmgnClient = gmgnClient;
        this.launchpadHandlers = launchpadHandlers.ToList();
        this.telegramApi = telegramApi;
        this.text = text;
        this.workerOptions = workerOptions;
        this.autoTradingOptions = autoTradingOptions;
        this.logger = logger;
    }

    public async ValueTask QueueAsync(long chatId, long tradingWorkerId, string postId, string chain,
        string launchpad,
        string tokenAddress, string tokenName, string tokenSymbol, string walletAddress, decimal slippagePercent,
        string? launchTransactionHash, string language, int groupSize, CancellationToken cancellationToken)
    {
        await AutoTradingDiagnosticLog.WriteAsync("QUEUED | Post=" + postId + " | Symbol=" + tokenSymbol
            + " | Chain=" + chain + " | Launchpad=" + launchpad + " | Token=" + tokenAddress);
        await queue.Writer.WriteAsync(new AutoTradingRequest(chatId, tradingWorkerId, postId, chain, launchpad,
            tokenAddress, tokenName, tokenSymbol, walletAddress, slippagePercent, launchTransactionHash,
            BotTextService.Normalize(language), groupSize),
            cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MarkInterruptedPreparationsAsync(stoppingToken);
        Task[] prepareTasks = Enumerable.Range(0, workerOptions.MaxWorkers)
            .Select(_ => PrepareLoopAsync(stoppingToken)).ToArray();
        Task monitorTask = MonitorLoopAsync(stoppingToken);
        await Task.WhenAll(prepareTasks.Append(monitorTask));
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

        TimeSpan buyerTimeout = TimeSpan.FromSeconds(autoTradingOptions.NoBuyerTimeoutSeconds);
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
            await ExitPositionAsync(request, GetAutoExitReason(request.Language, false, false)!,
                cancellationToken);
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(autoTradingOptions.FirstTakeProfitTimeoutSeconds),
            cancellationToken);
        bool firstTakeProfitFilled = await IsFirstTakeProfitFilledAsync(request, cancellationToken);
        string? exitReason = GetAutoExitReason(request.Language, true, firstTakeProfitFilled);
        if (exitReason == null)
        {
            await preparationTask;
            return;
        }

        preparationCancellation.Cancel();
        await ObservePreparationAsync(preparationTask, request);
        await ExitPositionAsync(request, exitReason, cancellationToken);
    }

    private string? GetAutoExitReason(string language, bool hasExternalBuyer,
        bool firstTakeProfitFilled)
    {
        if (!hasExternalBuyer)
        {
            return text.Get(language, "AutoExitNoBuyer", autoTradingOptions.NoBuyerTimeoutSeconds);
        }

        return firstTakeProfitFilled
            ? null
            : text.Get(language, "AutoExitTp1Timeout", autoTradingOptions.FirstTakeProfitTimeoutSeconds);
    }

    private async Task<DateTimeOffset?> WaitForExternalBuyAsync(AutoTradingRequest request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        Web3 web3 = new Web3(GetLaunchpadHandler(request).RpcUrl);
        BigInteger nextBlock = await GetMonitoringStartBlockAsync(web3, request.LaunchTransactionHash,
            cancellationToken);
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

    private static async Task<BigInteger> GetMonitoringStartBlockAsync(Web3 web3,
        string? launchTransactionHash, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(launchTransactionHash))
        {
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                TransactionReceipt? receipt = await web3.Eth.Transactions.GetTransactionReceipt
                    .SendRequestAsync(launchTransactionHash).WaitAsync(cancellationToken);
                if (receipt?.BlockNumber != null)
                {
                    return receipt.BlockNumber.Value;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        BigInteger latestBlock = (await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()
            .WaitAsync(cancellationToken)).Value;
        return BigInteger.Max(BigInteger.Zero, latestBlock - 100);
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
        if (firstOrder.GmgnOrderId.StartsWith("local:", StringComparison.Ordinal))
        {
            // TP của Pons được bot theo dõi trong database, không tồn tại trong danh sách order GMGN.
            return false;
        }

        GmgnCredentials? credentials = await settings.GetCredentialsAsync(request.ChatId,
            request.TradingWorkerId, cancellationToken);
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
            IAutoTradingLaunchpadHandler handler = GetLaunchpadHandler(request);
            GmgnCredentials credentials = await settings.GetCredentialsAsync(request.ChatId,
                request.TradingWorkerId,
                cancellationToken) ?? throw new InvalidOperationException("GMGN is not connected.");
            string quoteToken = await handler.GetSellQuoteTokenAsync(credentials, request.TokenAddress,
                cancellationToken);
            BigInteger? exactAmountIn = await handler.GetSellAmountAsync(request.WalletAddress,
                request.TokenAddress, cancellationToken);
            if (exactAmountIn == 0)
            {
                throw new InvalidOperationException("The wallet no longer has this token to sell.");
            }
            await AutoTradingDiagnosticLog.WriteAsync("AUTO EXIT SWAP | Symbol=" + request.TokenSymbol
                + " | Chain=" + handler.GmgnChain + " | Quote=" + quoteToken + " | Amount="
                + (exactAmountIn?.ToString(CultureInfo.InvariantCulture) ?? "100%"));
            string sellReference = await gmgnClient.SellAllAsync(credentials, handler.GmgnChain,
                request.WalletAddress, request.TokenAddress, quoteToken, exactAmountIn,
                request.SlippagePercent, cancellationToken);

            await CloseRemainingOrdersAsync(request, credentials, handler.GmgnChain, cancellationToken);
            await MarkTradeExitedAsync(request, reason, cancellationToken);
            await AutoTradingDiagnosticLog.WriteAsync("AUTO EXIT SOLD | Symbol=" + request.TokenSymbol
                + " | Reason=" + reason + " | Reference=" + sellReference);
            if (request.GroupSize == 1)
            {
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "AutoExitSold", request.TokenSymbol, reason, sellReference),
                    cancellationToken);
            }
            else
            {
                AddGroupedNotification(request, "exit", null, reason, sellReference);
            }
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
            if (order.GmgnOrderId.StartsWith("local:", StringComparison.Ordinal))
            {
                order.Status = "cancelled";
                order.ClosedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

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

    private IAutoTradingLaunchpadHandler GetLaunchpadHandler(AutoTradingRequest request)
    {
        return launchpadHandlers.FirstOrDefault(item => item.Supports(request.Chain, request.Launchpad))
            ?? throw new InvalidOperationException("Auto trading is not supported for " + request.Chain
                + "/" + request.Launchpad + ".");
    }

    private async Task PrepareAsync(AutoTradingRequest request, CancellationToken cancellationToken)
    {
        IAutoTradingLaunchpadHandler handler = GetLaunchpadHandler(request);

        GmgnCredentials? credentials = await settings.GetCredentialsAsync(request.ChatId,
            request.TradingWorkerId, cancellationToken);
        List<TakeProfitSetting> levels = await settings.GetTakeProfitsAsync(request.ChatId, cancellationToken);
        if (credentials == null)
        {
            throw new InvalidOperationException("Connect GMGN in /settings first.");
        }
        if (levels.Count == 0)
        {
            throw new InvalidOperationException("Add at least one take-profit level in /settings first.");
        }

        GmgnTokenPosition position = await handler.GetPositionAsync(credentials, request.WalletAddress,
            request.TokenAddress, cancellationToken);
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
            TradingWorkerId = request.TradingWorkerId,
            PostId = request.PostId,
            Chain = handler.GmgnChain,
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
            BigInteger tokenBalance = await handler.GetTokenBalanceAsync(position.WalletAddress,
                request.TokenAddress, cancellationToken);
            await AutoTradingDiagnosticLog.WriteAsync("TP BALANCE | Symbol=" + request.TokenSymbol
                + " | Raw=" + tokenBalance);
            if (handler is ILocalTakeProfitHandler localHandler)
            {
                await CreateLocalTakeProfitsAsync(request, trade, levels, tokenBalance, localHandler, db,
                    cancellationToken);
                return;
            }

            decimal? gasPriceGwei = await handler.GetTakeProfitGasPriceGweiAsync(credentials,
                cancellationToken);
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
                string orderId = await gmgnClient.CreateTakeProfitAsync(credentials, handler.GmgnChain,
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
            if (request.GroupSize == 1)
            {
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "AutoTradingStarted", request.TokenSymbol, levelText),
                    cancellationToken);
            }
            else
            {
                AddGroupedNotification(request, "tp", levelText, null, null);
            }
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

    // Pons không tạo order chờ trên GMGN. Bot lưu các mức TP và tự gọi GMGN swap khi đạt giá.
    private async Task CreateLocalTakeProfitsAsync(AutoTradingRequest request, AutoTrade trade,
        List<TakeProfitSetting> levels, BigInteger initialBalance, ILocalTakeProfitHandler handler,
        AppDbContext db, CancellationToken cancellationToken)
    {
        if (initialBalance <= 0)
        {
            throw new InvalidOperationException("The Pons launch wallet has no token balance.");
        }

        for (int index = 0; index < levels.Count; index++)
        {
            TakeProfitSetting level = levels[index];
            int sellBasisPoints = decimal.ToInt32(level.SellPercent * 100m);
            BigInteger amountIn = initialBalance * sellBasisPoints / 10_000;
            if (amountIn <= 0)
            {
                throw new InvalidOperationException("Token balance is too small for TP" + (index + 1) + ".");
            }

            decimal targetPrice = trade.EntryPrice * (1m + level.ProfitPercent / 100m);
            db.AutoTradeOrders.Add(new AutoTradeOrder
            {
                AutoTradeId = trade.Id,
                GmgnOrderId = handler.LocalOrderPrefix + trade.Id + ":" + index + ":" + amountIn,
                ProfitPercent = level.ProfitPercent,
                SellPercent = level.SellPercent,
                TargetPrice = targetPrice,
                CreatedAtUtc = DateTime.UtcNow
            });
            await AutoTradingDiagnosticLog.WriteAsync("PONS TP WATCH | Symbol=" + request.TokenSymbol
                + " | Level=" + (index + 1) + " | Profit=" + level.ProfitPercent
                + " | Sell=" + level.SellPercent + " | TargetWETH="
                + targetPrice.ToString(CultureInfo.InvariantCulture));
        }

        trade.Status = "active";
        await db.SaveChangesAsync(cancellationToken);
        string levelText = string.Join("\n", levels.Select((level, index) => "TP" + (index + 1)
            + ": +" + level.ProfitPercent.ToString(CultureInfo.InvariantCulture) + "% -> "
            + level.SellPercent.ToString(CultureInfo.InvariantCulture) + "%"));
        if (request.GroupSize == 1)
        {
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "LocalAutoTradingStarted", request.TokenSymbol, levelText),
                cancellationToken);
        }
        else
        {
            AddGroupedNotification(request, "local-tp", levelText, null, null);
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

            GmgnCredentials? credentials = await settings.GetCredentialsAsync(trade.ChatId,
                trade.TradingWorkerId, cancellationToken);
            if (credentials == null)
            {
                continue;
            }

            ILocalTakeProfitHandler? localHandler = FindLocalHandler(trade);
            if (localHandler != null)
            {
                await MonitorLocalTakeProfitsAsync(trade, credentials, localHandler, db, cancellationToken);
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

    // Kiểm tra giá Pons. Chỉ khi đạt TP thì mới gọi GMGN swap để bán đúng số token đã định.
    private async Task MonitorLocalTakeProfitsAsync(AutoTrade trade, GmgnCredentials credentials,
        ILocalTakeProfitHandler handler, AppDbContext db, CancellationToken cancellationToken)
    {
        decimal currentPrice = await handler.GetCurrentPriceAsync(trade.TokenAddress, cancellationToken);
        foreach (AutoTradeOrder order in trade.Orders.Where(item => item.Status == "open")
                     .OrderBy(item => item.ProfitPercent))
        {
            if (currentPrice < order.TargetPrice)
            {
                continue;
            }

            try
            {
                BigInteger plannedAmount = ReadLocalSellAmount(order.GmgnOrderId, handler.LocalOrderPrefix);
                BigInteger currentBalance = await handler.GetTokenBalanceAsync(trade.WalletAddress,
                    trade.TokenAddress, cancellationToken);
                BigInteger amountToSell = BigInteger.Min(plannedAmount, currentBalance);
                if (amountToSell <= 0)
                {
                    throw new InvalidOperationException("The wallet no longer has tokens for this TP.");
                }

                await AutoTradingDiagnosticLog.WriteAsync("PONS TP SELL | Symbol=" + trade.TokenSymbol
                    + " | Profit=" + order.ProfitPercent + " | CurrentWETH="
                    + currentPrice.ToString(CultureInfo.InvariantCulture) + " | TargetWETH="
                    + order.TargetPrice.ToString(CultureInfo.InvariantCulture) + " | Amount=" + amountToSell);
                string sellQuoteToken = await handler.GetSellQuoteTokenAsync(credentials, trade.TokenAddress,
                    cancellationToken);
                string sellReference = await gmgnClient.SellAllAsync(credentials, handler.GmgnChain,
                    trade.WalletAddress, trade.TokenAddress, sellQuoteToken, amountToSell,
                    await GetTradeSlippageAsync(trade.ChatId, cancellationToken), cancellationToken);

                order.Status = "filled";
                order.TransactionHash = sellReference;
                order.ClosedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                string language = BotTextService.Normalize(trade.TelegramUser.LanguageCode);
                await telegramApi.SendMessageAsync(trade.ChatId,
                    text.Get(language, "LocalTakeProfitFilled", trade.TokenSymbol, order.ProfitPercent,
                        order.SellPercent, sellReference), cancellationToken);
                order.NotifiedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                // Không tự retry lệnh bán không rõ trạng thái để tránh bán hai lần.
                order.Status = "failed";
                order.ClosedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogError(exception, "[Pons] TP sell failed for {TokenAddress}.", trade.TokenAddress);
                await AutoTradingDiagnosticLog.WriteAsync("PONS TP SELL ERROR | Symbol=" + trade.TokenSymbol
                    + " | Profit=" + order.ProfitPercent + " | " + exception);
                string language = BotTextService.Normalize(trade.TelegramUser.LanguageCode);
                await telegramApi.SendMessageAsync(trade.ChatId,
                    text.Get(language, "TakeProfitFailed", trade.TokenSymbol, order.ProfitPercent),
                    cancellationToken);
                break;
            }
        }

        if (trade.Orders.All(item => item.Status != "open"))
        {
            bool hasFailure = trade.Orders.Any(item => item.Status == "failed");
            trade.Status = hasFailure ? "failed" : "completed";
            trade.CompletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (!hasFailure)
            {
                decimal soldPercent = trade.Orders.Sum(item => item.SellPercent);
                string language = BotTextService.Normalize(trade.TelegramUser.LanguageCode);
                await telegramApi.SendMessageAsync(trade.ChatId,
                    text.Get(language, "LocalAutoTradingCompleted", trade.TokenSymbol, soldPercent),
                    cancellationToken);
            }
        }
    }

    private ILocalTakeProfitHandler? FindLocalHandler(AutoTrade trade)
    {
        return launchpadHandlers.OfType<ILocalTakeProfitHandler>()
            .FirstOrDefault(handler => trade.Orders.Any(order =>
                order.GmgnOrderId.StartsWith(handler.LocalOrderPrefix, StringComparison.Ordinal)));
    }

    private static BigInteger ReadLocalSellAmount(string orderId, string prefix)
    {
        if (!orderId.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid local TP order.");
        }

        string amountText = orderId[(orderId.LastIndexOf(':') + 1)..];
        return BigInteger.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture,
            out BigInteger amount) && amount > 0
            ? amount
            : throw new InvalidOperationException("Invalid local TP sell amount.");
    }

    private async Task<decimal> GetTradeSlippageAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserChainTradingSettings? chainSettings = await db.UserChainTradingSettings
            .AsNoTracking().FirstOrDefaultAsync(item => item.ChatId == chatId && item.Chain == "robinhood",
                cancellationToken);
        return chainSettings?.SlippagePercent ?? 5m;
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

    private void AddGroupedNotification(AutoTradingRequest request, string eventName, string? levels,
        string? reason, string? reference)
    {
        string key = request.ChatId + ":" + request.PostId + ":" + request.Chain + ":"
            + request.Launchpad + ":" + eventName;
        GroupedNotification notification = groupedNotifications.GetOrAdd(key,
            _ => new GroupedNotification(request.ChatId, request.Language, eventName, request.GroupSize));
        bool startFlush;
        lock (notification)
        {
            notification.Items.Add(new GroupedNotificationItem(request.TokenName, request.TokenSymbol,
                levels, reason, reference));
            startFlush = !notification.FlushStarted;
            notification.FlushStarted = true;
            if (notification.Items.Count >= notification.ExpectedCount)
            {
                notification.Ready.TrySetResult();
            }
        }
        if (startFlush)
        {
            _ = FlushGroupedNotificationAsync(key, notification);
        }
    }

    private async Task FlushGroupedNotificationAsync(string key, GroupedNotification notification)
    {
        try
        {
            await Task.WhenAny(notification.Ready.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            groupedNotifications.TryRemove(key, out _);
            List<GroupedNotificationItem> items;
            lock (notification)
            {
                items = notification.Items.ToList();
            }
            if (items.Count == 0)
            {
                return;
            }

            string names = string.Join(", ", items.Select(item => item.TokenName).Distinct());
            string message;
            if (notification.EventName == "exit")
            {
                string references = string.Join("\n", items.Select((item, index) =>
                    (index + 1) + ". " + item.TokenSymbol + ": " + item.Reference));
                string reasons = string.Join("; ", items.Select(item => item.Reason).Distinct());
                message = text.Get(notification.Language, "GroupedAutoExitSold", items.Count, names,
                    references, reasons);
            }
            else
            {
                string levels = items.First().Levels!;
                string messageKey = notification.EventName == "local-tp"
                    ? "GroupedLocalAutoTradingStarted"
                    : "GroupedAutoTradingStarted";
                message = text.Get(notification.Language, messageKey, items.Count, names, levels);
            }
            await telegramApi.SendMessageAsync(notification.ChatId, message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cannot send grouped Auto Trading notification.");
        }
    }

    private sealed record AutoTradingRequest(long ChatId, long TradingWorkerId, string PostId, string Chain,
        string Launchpad,
        string TokenAddress, string TokenName, string TokenSymbol, string WalletAddress, decimal SlippagePercent,
        string? LaunchTransactionHash, string Language, int GroupSize);

    private sealed class GroupedNotification(long chatId, string language, string eventName, int expectedCount)
    {
        public long ChatId { get; } = chatId;
        public string Language { get; } = language;
        public string EventName { get; } = eventName;
        public int ExpectedCount { get; } = expectedCount;
        public bool FlushStarted { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<GroupedNotificationItem> Items { get; } = [];
    }

    private sealed record GroupedNotificationItem(string TokenName, string TokenSymbol, string? Levels,
        string? Reason, string? Reference);
}
