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

        Task<(TokenDraftDto Draft, double Seconds)> metadataTask = CreateMetadataAsync(postText, imageUrl, deadline.Token);
        Task<(FluxImageDto Image, double Seconds)> imageTask = CreateImageAsync(postText, imageUrl, deadline.Token);

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

    private static TokenPreviewDto CreateExpiredResult(DateTimeOffset receivedAt, string? imageUrl)
    {
        return new TokenPreviewDto
        {
            IsExpired = true,
            UsedSourceImage = !string.IsNullOrWhiteSpace(imageUrl),
            TotalSeconds = Math.Max(10, (DateTimeOffset.UtcNow - receivedAt).TotalSeconds)
        };
    }

    private async Task<(TokenDraftDto Draft, double Seconds)> CreateMetadataAsync(string? postText, string? imageUrl, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        TokenDraftDto draft = await openAiClient.CreateTokenMetadataAsync(postText, imageUrl, cancellationToken);
        timer.Stop();
        return (draft, timer.Elapsed.TotalSeconds);
    }

    private async Task<(FluxImageDto Image, double Seconds)> CreateImageAsync(string? postText, string? imageUrl, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        FluxImageDto image = await fluxClient.CreateTokenImageResultAsync(postText, imageUrl, cancellationToken);
        timer.Stop();
        return (image, timer.Elapsed.TotalSeconds);
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
