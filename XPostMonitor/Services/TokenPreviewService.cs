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
        return await CreateAsync(postText, imageUrl, receivedAt, chain, true, cancellationToken);
    }

    public Task<TokenPreviewDto> CreateAsync(string? postText, string? imageUrl, DateTimeOffset receivedAt,
        string? chain, bool expiresAfterTenSeconds, CancellationToken cancellationToken)
    {
        return CreateAsync(postText, imageUrl, receivedAt, chain, null, expiresAfterTenSeconds,
            cancellationToken);
    }

    public async Task<TokenPreviewDto> CreateAsync(string? postText, string? imageUrl, DateTimeOffset receivedAt,
        string? chain, string? username, bool expiresAfterTenSeconds, CancellationToken cancellationToken)
    {
        TimeSpan age = DateTimeOffset.UtcNow - receivedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        TimeSpan remainingTime = MaxPreparationTime - age;
        if (expiresAfterTenSeconds && remainingTime <= TimeSpan.Zero)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (expiresAfterTenSeconds)
        {
            deadline.CancelAfter(remainingTime);
        }

        string? imageStyle = GetChainImageStyle(chain);
        string? chainLogoBase64 = GetChainLogoBase64(chain);
        string? characterImageBase64 = string.IsNullOrWhiteSpace(imageUrl)
            ? GetCharacterImageBase64(username)
            : null;
        Task<(TokenDraftDto Draft, double Seconds)> metadataTask = CreateMetadataAsync(postText, imageUrl,
            imageStyle, deadline.Token);

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return await CreateFromTextAsync(metadataTask, receivedAt, imageStyle, characterImageBase64,
                chainLogoBase64, deadline, expiresAfterTenSeconds, cancellationToken);
        }

        Task<(FluxImageDto Image, double Seconds)> imageTask = CreateImageAsync(postText, imageUrl, imageStyle,
            chainLogoBase64, deadline.Token);

        try
        {
            await Task.WhenAll(metadataTask, imageTask);
        }
        catch (OperationCanceledException) when (expiresAfterTenSeconds && !cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }
        catch
        {
            deadline.Cancel();
            throw;
        }

        if (expiresAfterTenSeconds && DateTimeOffset.UtcNow - receivedAt >= MaxPreparationTime)
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
        return await CreateWithOriginalImageAsync(postText, imageUrl, receivedAt, chain, true,
            cancellationToken);
    }

    public async Task<TokenPreviewDto> CreateWithOriginalImageAsync(string? postText, string? imageUrl,
        DateTimeOffset receivedAt, string? chain, bool expiresAfterTenSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new InvalidOperationException("X did not return the new avatar URL.");
        }

        TimeSpan remainingTime = MaxPreparationTime - (DateTimeOffset.UtcNow - receivedAt);
        if (expiresAfterTenSeconds && remainingTime <= TimeSpan.Zero)
        {
            return CreateExpiredResult(receivedAt, imageUrl);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (expiresAfterTenSeconds)
        {
            deadline.CancelAfter(remainingTime);
        }
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
        catch (OperationCanceledException) when (expiresAfterTenSeconds && !cancellationToken.IsCancellationRequested)
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
        string? imageStyle, string? chainLogoBase64, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        FluxImageDto image = await fluxClient.CreateTokenImageResultAsync(postText, imageUrl, imageStyle,
            chainLogoBase64, cancellationToken);
        timer.Stop();
        return (image, timer.Elapsed.TotalSeconds);
    }

    private async Task<TokenPreviewDto> CreateFromTextAsync(Task<(TokenDraftDto Draft, double Seconds)> metadataTask,
        DateTimeOffset receivedAt, string? imageStyle, string? characterImageBase64, string? chainLogoBase64,
        CancellationTokenSource deadline, bool expiresAfterTenSeconds, CancellationToken cancellationToken)
    {
        try
        {
            (TokenDraftDto draft, double openAiSeconds) = await metadataTask;
            Stopwatch timer = Stopwatch.StartNew();
            FluxImageDto image = await fluxClient.CreateTokenImageFromPromptAsync(draft.ImagePrompt, imageStyle,
                characterImageBase64, chainLogoBase64, deadline.Token);
            timer.Stop();

            if (expiresAfterTenSeconds && DateTimeOffset.UtcNow - receivedAt >= MaxPreparationTime)
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
        catch (OperationCanceledException) when (expiresAfterTenSeconds && !cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, null);
        }
    }

    private static string? GetChainImageStyle(string? chain)
    {
        return chain?.ToLowerInvariant() switch
        {
            "bsc" => "Keep natural subject colors dominant with BNB yellow #F0B90B, charcoal black, and white accents. Naturally print one clearly recognizable black BNB Chain geometric logo on a yellow physical object that belongs in the scene, such as a cap, shirt, backpack, phone case, vehicle, or prop. The logo must follow the object's perspective and material, never float separately, never become a watermark, and include no brand text.",
            "base" => "Keep natural subject colors dominant. Add subtle Base blue #0052FF, white, and deep navy accents only in lighting, edges, or background details.",
            "sol" => "Keep natural subject colors dominant. Add subtle Solana purple #9945FF, mint green #14F195, and cyan accents only in lighting, edges, or background details.",
            "robinhood" => "Keep natural subject colors dominant with Robinhood neon green, black, and white accents. Naturally print one clearly recognizable black Robinhood feather logo on a neon-green physical object that belongs in the scene, such as a cap, shirt, backpack, phone case, vehicle, or prop. The logo must follow the object's perspective and material, never float separately, never become a watermark, and include no brand text.",
            "stable" => "Keep natural subject colors dominant with Stable dark green, pale mint, white, and graphite accents. Naturally print one clearly recognizable pale-mint Stable symbol on a dark-green physical object that belongs in the scene, such as a cap, shirt, backpack, phone case, vehicle, or prop. The logo must follow the object's perspective and material, never float separately, never become a watermark, and include no brand text.",
            _ => null
        };
    }

    private static string? GetChainLogoBase64(string? chain)
    {
        // Chọn đúng file logo theo chain. Chain chưa có logo thì giữ nguyên luồng cũ.
        string? logoFileName = chain?.ToLowerInvariant() switch
        {
            "bsc" => "bnb.png",
            "robinhood" => "robinhood.png",
            "stable" => "stable.png",
            _ => null
        };

        if (logoFileName == null)
        {
            return null;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "ChainLogos", logoFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Chain logo reference file was not found: {logoFileName}");
        }

        return Convert.ToBase64String(File.ReadAllBytes(path));
    }

    private static string? GetCharacterImageBase64(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        // Username X chỉ có chữ, số và dấu gạch dưới. Tên lạ sẽ bị bỏ qua để giữ an toàn.
        string fileName = username.Trim().TrimStart('@').ToLowerInvariant();
        if (fileName.Length == 0 || fileName.Any(character => !char.IsLetterOrDigit(character)
            && character != '_'))
        {
            return null;
        }

        string folder = Path.Combine(AppContext.BaseDirectory, "Assets", "CharacterReferences");
        string? path = new[] { ".png", ".jpg", ".jpeg" }
            .Select(extension => Path.Combine(folder, fileName + extension))
            .FirstOrDefault(File.Exists);
        if (path == null)
        {
            return null;
        }

        try
        {
            byte[] image = File.ReadAllBytes(path);
            bool isPng = image.Length >= 8
                && image[0] == 137 && image[1] == 80 && image[2] == 78 && image[3] == 71
                && image[4] == 13 && image[5] == 10 && image[6] == 26 && image[7] == 10;
            bool isJpeg = image.Length >= 3
                && image[0] == 255 && image[1] == 216 && image[2] == 255;
            return isPng || isJpeg ? Convert.ToBase64String(image) : null;
        }
        catch (IOException)
        {
            // Ảnh lỗi hoặc đang bị khóa thì bỏ qua, luồng tạo token vẫn tiếp tục như cũ.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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
