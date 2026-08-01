using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Telegram;

namespace XPostMonitor.Services.BinanceAlpha;

// Theo dõi Binance Alpha độc lập, không chặn luồng X hoặc Auto Trading.
public sealed class BinanceAlphaMonitorService : BackgroundService
{
    private const string StreamUrl = "wss://nbstream.binance.com/w3w/wsa/stream";
    private const string StreamName = "came@allTokens@ticker24";
    private const string BinanceAlphaUrl = "https://www.binance.com/en/alpha";

    private readonly BinanceAlphaClient binanceClient;
    private readonly TelegramApiClient telegramApi;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly BinanceAlphaOptions options;
    private readonly ILogger<BinanceAlphaMonitorService> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly HashSet<string> knownContracts = new(StringComparer.Ordinal);

    public BinanceAlphaMonitorService(BinanceAlphaClient binanceClient,
        TelegramApiClient telegramApi,
        IServiceScopeFactory scopeFactory,
        BinanceAlphaOptions options,
        ILogger<BinanceAlphaMonitorService> logger)
    {
        this.binanceClient = binanceClient;
        this.telegramApi = telegramApi;
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("[BINANCE ALPHA] Monitoring is disabled.");
            return;
        }

        if (options.TelegramChannelId == 0)
        {
            logger.LogError("[BINANCE ALPHA] TelegramChannelId is missing. Monitoring was not started.");
            return;
        }

        // Nạp dữ liệu cũ trước khi mở stream để lần chạy đầu không gửi hàng loạt token cũ.
        await InitializeWithRetryAsync(stoppingToken);

        Task streamTask = RunWebSocketLoopAsync(stoppingToken);
        Task restTask = RunRestFallbackLoopAsync(stoppingToken);
        await Task.WhenAll(streamTask, restTask);
    }

    // Binance hoặc DB lỗi lúc khởi động không được phép làm dừng các chức năng khác của bot.
    private async Task InitializeWithRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await InitializeAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "[BINANCE ALPHA] Startup failed. Retrying in 10 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }

    // Lần đầu sẽ tạo mốc dữ liệu hiện tại. Các lần sau sẽ gửi token còn chưa thông báo.
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        List<BinanceAlphaTokenDto> currentTokens = await binanceClient
            .GetActiveTokensAsync(cancellationToken);

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<BinanceAlphaToken> savedTokens = await db.BinanceAlphaTokens
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (BinanceAlphaToken token in savedTokens)
        {
            knownContracts.Add(CreateContractKey(token.ChainId, token.ContractAddress));
        }

        if (savedTokens.Count == 0)
        {
            DateTime now = DateTime.UtcNow;
            foreach (BinanceAlphaTokenDto token in currentTokens)
            {
                db.BinanceAlphaTokens.Add(CreateEntity(token, now, now));
                knownContracts.Add(CreateContractKey(token.ChainId, token.ContractAddress));
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[BINANCE ALPHA] Connected. Saved {Count} existing tokens as the starting point.",
                currentTokens.Count);
            return;
        }

        logger.LogInformation("[BINANCE ALPHA] Connected. Monitoring {Count} known tokens.",
            savedTokens.Count);
        await RefreshTokensAsync(cancellationToken);
    }

    // REST chạy dự phòng để không bỏ sót token nếu WebSocket bị gián đoạn.
    private async Task RunRestFallbackLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(options.RestCheckIntervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RefreshTokensAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[BINANCE ALPHA] REST check failed.");
            }
        }
    }

    // WebSocket là đường phát hiện nhanh; nếu rớt kết nối sẽ tự nối lại sau 5 giây.
    private async Task RunWebSocketLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using ClientWebSocket socket = new();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(new Uri(StreamUrl), cancellationToken);
                await SubscribeAsync(socket, cancellationToken);
                logger.LogInformation("[BINANCE ALPHA] WebSocket connected.");
                await ReceiveMessagesAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "[BINANCE ALPHA] WebSocket disconnected. Reconnecting in 5 seconds.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private static async Task SubscribeAsync(ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            method = "SUBSCRIBE",
            @params = new[] { StreamName },
            id = 1
        }));

        await socket.SendAsync(request, WebSocketMessageType.Text, true, cancellationToken);
    }

    // Ghép đủ các mảnh WebSocket rồi chỉ refresh REST khi thấy contract chưa biết.
    private async Task ReceiveMessagesAsync(ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        using MemoryStream message = new();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            if (ContainsUnknownContract(message.GetBuffer().AsMemory(0, (int)message.Length)))
            {
                await RefreshTokensAsync(cancellationToken);
            }
        }
    }

    private bool ContainsUnknownContract(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("d", out JsonElement tokens)
            || tokens.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement token in tokens.EnumerateArray())
        {
            if (!token.TryGetProperty("ca", out JsonElement contractElement))
            {
                continue;
            }

            string? contractWithChain = contractElement.GetString();
            int separatorIndex = contractWithChain?.LastIndexOf('@') ?? -1;
            if (separatorIndex <= 0 || separatorIndex == contractWithChain!.Length - 1)
            {
                continue;
            }

            string contract = contractWithChain[..separatorIndex];
            string chainId = contractWithChain[(separatorIndex + 1)..];
            string key = CreateContractKey(chainId, contract);
            lock (knownContracts)
            {
                if (!knownContracts.Contains(key))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Thêm token mới vào DB trước, gửi Telegram sau. Nếu gửi lỗi, lần REST tiếp theo sẽ thử lại.
    private async Task RefreshTokensAsync(CancellationToken cancellationToken)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            List<BinanceAlphaTokenDto> currentTokens = await binanceClient
                .GetActiveTokensAsync(cancellationToken);

            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DateTime now = DateTime.UtcNow;

            foreach (BinanceAlphaTokenDto token in currentTokens)
            {
                string key = CreateContractKey(token.ChainId, token.ContractAddress);
                bool isKnown;
                lock (knownContracts)
                {
                    isKnown = knownContracts.Contains(key);
                }

                if (isKnown)
                {
                    continue;
                }

                bool tokenIdExists = await db.BinanceAlphaTokens
                    .AnyAsync(item => item.TokenId == token.TokenId, cancellationToken);
                if (!tokenIdExists)
                {
                    db.BinanceAlphaTokens.Add(CreateEntity(token, now, null));
                }

                lock (knownContracts)
                {
                    knownContracts.Add(key);
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            List<BinanceAlphaToken> pendingTokens = await db.BinanceAlphaTokens
                .Where(token => token.NotifiedAtUtc == null)
                .OrderBy(token => token.FirstSeenAtUtc)
                .ToListAsync(cancellationToken);

            foreach (BinanceAlphaToken token in pendingTokens)
            {
                await SendNotificationAsync(token, cancellationToken);
                token.NotifiedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "[BINANCE ALPHA] Sent new listing {Symbol} ({Contract}) to Telegram.",
                    token.Symbol, token.ContractAddress);
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task SendNotificationAsync(BinanceAlphaToken token,
        CancellationToken cancellationToken)
    {
        string listingTime = token.ListingTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Unknown";
        string text = "🟡 <b>NEW BINANCE ALPHA LISTING</b>\n\n"
            + "<b>Name:</b> " + HtmlEncoder.Default.Encode(token.Name) + "\n"
            + "<b>Symbol:</b> " + HtmlEncoder.Default.Encode(token.Symbol) + "\n"
            + "<b>Chain:</b> " + HtmlEncoder.Default.Encode(
                string.IsNullOrWhiteSpace(token.ChainName) ? token.ChainId : token.ChainName) + "\n"
            + "<b>Contract:</b> <code>" + HtmlEncoder.Default.Encode(token.ContractAddress) + "</code>\n"
            + "<b>Listed at:</b> " + listingTime;

        try
        {
            await telegramApi.SendRichMessageAsync(options.TelegramChannelId, text,
                token.IconUrl, BinanceAlphaUrl, "View on Binance Alpha", cancellationToken);
        }
        catch (Exception exception) when (!string.IsNullOrWhiteSpace(token.IconUrl))
        {
            // Ảnh lỗi không được phép làm mất thông báo; gửi lại ngay dưới dạng văn bản.
            logger.LogWarning(exception,
                "[BINANCE ALPHA] Token image failed. Sending text notification instead.");
            await telegramApi.SendRichMessageAsync(options.TelegramChannelId, text,
                null, BinanceAlphaUrl, "View on Binance Alpha", cancellationToken);
        }
    }

    private static BinanceAlphaToken CreateEntity(BinanceAlphaTokenDto token,
        DateTime firstSeenAtUtc, DateTime? notifiedAtUtc)
    {
        return new BinanceAlphaToken
        {
            TokenId = token.TokenId,
            ChainId = token.ChainId,
            ChainName = token.ChainName,
            ContractAddress = token.ContractAddress,
            Name = token.Name,
            Symbol = token.Symbol,
            AlphaId = token.AlphaId,
            IconUrl = token.IconUrl,
            ListingTimeUtc = token.ListingTime is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(token.ListingTime.Value).UtcDateTime
                : null,
            FirstSeenAtUtc = firstSeenAtUtc,
            NotifiedAtUtc = notifiedAtUtc
        };
    }

    private static string CreateContractKey(string chainId, string contractAddress)
    {
        string normalizedContract = contractAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? contractAddress.ToLowerInvariant()
            : contractAddress;
        return chainId + "|" + normalizedContract;
    }
}
