using System.Diagnostics;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Flux;
using XPostMonitor.Services.OpenAi;

namespace XPostMonitor.Services;

public sealed class TokenPreviewService
{
    private static readonly TimeSpan MaxPreparationTime = TimeSpan.FromSeconds(10);

    private readonly OpenAiClient openAiClient;
    private readonly FluxClient fluxClient;

    public TokenPreviewService(OpenAiClient openAiClient, FluxClient fluxClient)
    {
        this.openAiClient = openAiClient;
        this.fluxClient = fluxClient;
    }

    public async Task<TokenPreviewDto> CreateAsync(string? postText, CancellationToken cancellationToken)
    {
        (string? text, string? imageUrl) = ParseInput(postText);
        return await CreateAsync(text, imageUrl, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<TokenPreviewDto> CreateAsync(string? postText, string? imageUrl, CancellationToken cancellationToken)
    {
        return await CreateAsync(postText, imageUrl, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<TokenPreviewDto> CreateAsync(string? postText, string? imageUrl, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        return await CreateAsync(postText, imageUrl, receivedAt, null, cancellationToken);
    }

    public async Task<TokenPreviewDto> CreateAsync(string? postText, string? imageUrl, DateTimeOffset receivedAt,
        string? chain, CancellationToken cancellationToken)
    {
        TimeSpan age = DateTimeOffset.UtcNow - receivedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        TimeSpan remainingTime = MaxPreparationTime - age;
        if (remainingTime <= TimeSpan.Zero)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remainingTime);

        string? imageStyle = GetChainImageStyle(chain);
        Task<(TokenDraftDto Draft, double Seconds)> metadataTask = CreateMetadataAsync(postText, imageUrl,
            imageStyle, deadline.Token);

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return await CreateFromTextAsync(metadataTask, receivedAt, imageStyle, deadline, cancellationToken);
        }

        Task<(FluxImageDto Image, double Seconds)> imageTask = CreateImageAsync(postText, imageUrl, imageStyle,
            deadline.Token);

        try
        {
            await Task.WhenAll(metadataTask, imageTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }
        catch
        {
            deadline.Cancel();
            throw;
        }

        if (DateTimeOffset.UtcNow - receivedAt >= MaxPreparationTime)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }

        (TokenDraftDto draft, double openAiSeconds) = await metadataTask;
        (FluxImageDto image, double fluxSeconds) = await imageTask;

        return new TokenPreviewDto
        {
            Draft = draft,
            Image = image.Data,
            ImageUrl = image.Url,
            OpenAiSeconds = openAiSeconds,
            FluxSeconds = fluxSeconds,
            TotalSeconds = Math.Max(0, (DateTimeOffset.UtcNow - receivedAt).TotalSeconds),
            UsedSourceImage = !string.IsNullOrWhiteSpace(imageUrl)
        };
    }

    // Avatar dùng nguyên ảnh X làm logo; chỉ nhờ OpenAI tạo tên, symbol và mô tả.
    public async Task<TokenPreviewDto> CreateWithOriginalImageAsync(string? postText, string? imageUrl,
        DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        return await CreateWithOriginalImageAsync(postText, imageUrl, receivedAt, null, cancellationToken);
    }

    public async Task<TokenPreviewDto> CreateWithOriginalImageAsync(string? postText, string? imageUrl,
        DateTimeOffset receivedAt, string? chain, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new InvalidOperationException("X did not return the new avatar URL.");
        }

        TimeSpan remainingTime = MaxPreparationTime - (DateTimeOffset.UtcNow - receivedAt);
        if (remainingTime <= TimeSpan.Zero)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remainingTime);
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            Task<TokenDraftDto> metadataTask = openAiClient.CreateTokenMetadataAsync(postText, imageUrl,
                GetChainImageStyle(chain), deadline.Token);
            Task<byte[]> imageTask = fluxClient.DownloadSourceImageAsync(imageUrl, deadline.Token);
            await Task.WhenAll(metadataTask, imageTask);
            timer.Stop();
            return new TokenPreviewDto
            {
                Draft = await metadataTask,
                Image = await imageTask,
                ImageUrl = imageUrl,
                OpenAiSeconds = timer.Elapsed.TotalSeconds,
                TotalSeconds = Math.Max(0, (DateTimeOffset.UtcNow - receivedAt).TotalSeconds),
                UsedSourceImage = true
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }
    }

    private static TokenPreviewDto CreateExpiredResult(DateTimeOffset receivedAt, string? imageUrl)
    {
        return new TokenPreviewDto
        {
            IsExpired = true,
            UsedSourceImage = !string.IsNullOrWhiteSpace(imageUrl),
            TotalSeconds = Math.Max(10, (DateTimeOffset.UtcNow - receivedAt).TotalSeconds)
        };
    }

    private async Task<(TokenDraftDto Draft, double Seconds)> CreateMetadataAsync(string? postText,
        string? imageUrl, string? imageStyle, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        TokenDraftDto draft = await openAiClient.CreateTokenMetadataAsync(postText, imageUrl, imageStyle,
            cancellationToken);
        timer.Stop();
        return (draft, timer.Elapsed.TotalSeconds);
    }

    private async Task<(FluxImageDto Image, double Seconds)> CreateImageAsync(string? postText, string? imageUrl,
        string? imageStyle, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        FluxImageDto image = await fluxClient.CreateTokenImageResultAsync(postText, imageUrl, imageStyle,
            cancellationToken);
        timer.Stop();
        return (image, timer.Elapsed.TotalSeconds);
    }

    private async Task<TokenPreviewDto> CreateFromTextAsync(Task<(TokenDraftDto Draft, double Seconds)> metadataTask,
        DateTimeOffset receivedAt, string? imageStyle, CancellationTokenSource deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            (TokenDraftDto draft, double openAiSeconds) = await metadataTask;
            Stopwatch timer = Stopwatch.StartNew();
            FluxImageDto image = await fluxClient.CreateTokenImageFromPromptAsync(draft.ImagePrompt, imageStyle,
                deadline.Token);
            timer.Stop();

            if (DateTimeOffset.UtcNow - receivedAt >= MaxPreparationTime)
            {
                return CreateExpiredResult(receivedAt, null);
            }

            return new TokenPreviewDto
            {
                Draft = draft,
                Image = image.Data,
                ImageUrl = image.Url,
                OpenAiSeconds = openAiSeconds,
                FluxSeconds = timer.Elapsed.TotalSeconds,
                TotalSeconds = Math.Max(0, (DateTimeOffset.UtcNow - receivedAt).TotalSeconds)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, null);
        }
    }

    private static string? GetChainImageStyle(string? chain)
    {
        return chain?.ToLowerInvariant() switch
        {
            "bsc" => "BNB yellow #F0B90B, charcoal black #0B0E11, and white accents.",
            "base" => "Base blue #0052FF, clean white, and deep navy accents.",
            "sol" => "Solana electric purple #9945FF, mint green #14F195, cyan, and black.",
            "robinhood" => "Robinhood vivid green #00C805, black, and white accents.",
            _ => null
        };
    }

    private static (string? Text, string? ImageUrl) ParseInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (input, null);
        }

        int separator = input.LastIndexOf('|');
        if (separator < 0)
        {
            return (input.Trim(), null);
        }

        string text = input[..separator].Trim();
        string imageUrl = input[(separator + 1)..].Trim();

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return (input.Trim(), null);
        }

        return (text, imageUrl);
    }
}
