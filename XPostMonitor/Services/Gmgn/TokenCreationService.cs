using System.Collections.Concurrent;
using System.Threading.Channels;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.Gmgn;

// Tạo token ở queue riêng để OpenAI, FLUX và GMGN không làm chậm thông báo Telegram.
public sealed class TokenCreationService : BackgroundService
{
    private readonly TradingSettingsService tradingSettings;
    private readonly TokenPreviewService tokenPreviewService;
    private readonly GmgnClient gmgnClient;
    private readonly TelegramApiClient telegramApi;
    private readonly ILogger<TokenCreationService> logger;
    private readonly BotTextService text;
    private readonly Channel<TokenCreationRequest> queue = Channel.CreateUnbounded<TokenCreationRequest>();
    private readonly ConcurrentDictionary<string, byte> manualJobs = new();

    public TokenCreationService(TradingSettingsService tradingSettings, TokenPreviewService tokenPreviewService,
        GmgnClient gmgnClient, TelegramApiClient telegramApi, BotTextService text,
        ILogger<TokenCreationService> logger)
    {
        this.tradingSettings = tradingSettings;
        this.tokenPreviewService = tokenPreviewService;
        this.gmgnClient = gmgnClient;
        this.telegramApi = telegramApi;
        this.logger = logger;
        this.text = text;
    }

    // Chỉ đưa dữ liệu cần thiết vào RAM rồi trả về ngay cho luồng nhận Post.
    public ValueTask QueueAsync(long chatId, string postId, string postText, string? photoUrl, string postUrl,
        string chain, string dex, bool useOriginalImage, string language, DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        return queue.Writer.WriteAsync(new TokenCreationRequest(chatId, postId, postText, photoUrl,
            postUrl, chain, dex, useOriginalImage, BotTextService.Normalize(language), receivedAt, false, null),
            cancellationToken);
    }

    // Chống hai lần bấm xác nhận cùng tạo trùng một token trong lúc job đầu còn chạy.
    public async ValueTask<bool> QueueManualAsync(long chatId, string postId, string postText, string? photoUrl,
        string postUrl, string chain, string dex, string language, CancellationToken cancellationToken)
    {
        string jobKey = chatId + ":" + postId + ":" + chain + ":" + dex;
        if (!manualJobs.TryAdd(jobKey, 0))
        {
            return false;
        }

        try
        {
            await queue.Writer.WriteAsync(new TokenCreationRequest(chatId, postId, postText, photoUrl, postUrl,
                chain, dex, false, BotTextService.Normalize(language), DateTimeOffset.UtcNow, true, jobKey),
                cancellationToken);
            return true;
        }
        catch
        {
            manualJobs.TryRemove(jobKey, out _);
            throw;
        }
    }

    // Bốn worker xử lý độc lập để một user chậm không chặn các user còn lại.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] workers = Enumerable.Range(0, 4).Select(_ => WorkAsync(stoppingToken)).ToArray();
        await Task.WhenAll(workers);
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        await foreach (TokenCreationRequest request in queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await CreateTokenAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[GMGN] Tạo token từ Post {PostId} thất bại.", request.PostId);
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "TokenFailed", request.PostId, exception.Message),
                    cancellationToken);
            }
            finally
            {
                if (request.ManualJobKey != null)
                {
                    manualJobs.TryRemove(request.ManualJobKey, out _);
                }
            }
        }
    }

    // Kiểm tra lại Auto Create ngay trước khi dùng tiền rồi mới chuẩn bị và gửi token.
    private async Task CreateTokenAsync(TokenCreationRequest request, CancellationToken cancellationToken)
    {
        AutoCreateSettings? settings = request.IsManual
            ? await tradingSettings.GetManualCreateSettingsAsync(request.ChatId, request.Chain, cancellationToken)
            : await tradingSettings.GetAutoCreateSettingsAsync(request.ChatId, request.Chain, cancellationToken);
        if (settings == null)
        {
            if (request.IsManual)
            {
                await telegramApi.SendMessageAsync(request.ChatId,
                    text.Get(request.Language, "ManualSettingsMissing"), cancellationToken);
            }
            return;
        }

        TokenPreviewDto preview = request.UseOriginalImage
            ? await tokenPreviewService.CreateWithOriginalImageAsync(request.PostText, request.PhotoUrl,
                request.ReceivedAt, cancellationToken)
            : await tokenPreviewService.CreateAsync(request.PostText, request.PhotoUrl,
                request.ReceivedAt, cancellationToken);
        if (preview.IsExpired)
        {
            await telegramApi.SendMessageAsync(request.ChatId,
                text.Get(request.Language, "TokenSkipped", request.PostId), cancellationToken);
            return;
        }

        GmgnTokenRequest gmgnRequest = new GmgnTokenRequest(
            request.Chain,
            request.Dex,
            preview.Draft.Name,
            preview.Draft.Symbol,
            preview.Draft.Description,
            preview.ImageUrl,
            request.PostUrl,
            settings.BuyAmount,
            settings.SlippagePercent);
        GmgnTokenResult result = await gmgnClient.CreateTokenAsync(settings.Credentials, gmgnRequest, cancellationToken);

        string caption = text.Get(request.Language, "TokenSubmitted", preview.Draft.Name,
            preview.Draft.Symbol, request.Chain, request.Dex, result.Status);
        if (!string.IsNullOrWhiteSpace(result.TransactionHash))
        {
            caption += "\n" + text.Get(request.Language, "Transaction") + ": " + result.TransactionHash;
        }
        if (!string.IsNullOrWhiteSpace(result.OrderId))
        {
            caption += "\n" + text.Get(request.Language, "OrderId") + ": " + result.OrderId;
        }

        if (preview.Image.Length > 0)
        {
            await telegramApi.SendPhotoAsync(request.ChatId, preview.Image, caption, cancellationToken);
        }
        else
        {
            await telegramApi.SendPhotoAsync(request.ChatId, preview.ImageUrl, caption, cancellationToken);
        }
        logger.LogInformation("[GMGN] Token created from Post {PostId}.", request.PostId);
    }

    private sealed record TokenCreationRequest(long ChatId, string PostId, string PostText, string? PhotoUrl,
        string PostUrl, string Chain, string Dex, bool UseOriginalImage, string Language, DateTimeOffset ReceivedAt,
        bool IsManual, string? ManualJobKey);
}
