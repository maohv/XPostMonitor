using System.Diagnostics;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Flux;
using XPostMonitor.Services.Gemini;
using XPostMonitor.Services.OpenAi;
using XPostMonitor.Services.ZImage;

namespace XPostMonitor.Services;

public sealed class TokenPreviewService
{
    private readonly OpenAiClient openAiClient;
    private readonly FluxClient fluxClient;
    private readonly GeminiImageClient geminiImageClient;
    private readonly ZImageClient zImageClient;
    private readonly ImageGenerationOptions imageGenerationOptions;

    public int AutoTimeoutSeconds => Math.Max(0, imageGenerationOptions.AutoCreateTimeoutSeconds);

    public TokenPreviewService(OpenAiClient openAiClient, FluxClient fluxClient,
        GeminiImageClient geminiImageClient, ZImageClient zImageClient,
        ImageGenerationOptions imageGenerationOptions)
    {
        this.openAiClient = openAiClient;
        this.fluxClient = fluxClient;
        this.geminiImageClient = geminiImageClient;
        this.zImageClient = zImageClient;
        this.imageGenerationOptions = imageGenerationOptions;
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
        string? chain, bool useAutoTimeout, CancellationToken cancellationToken)
    {
        return CreateAsync(postText, imageUrl, receivedAt, chain, null, useAutoTimeout,
            cancellationToken);
    }

    public async Task<TokenPreviewDto> CreateAsync(string? postText, string? imageUrl, DateTimeOffset receivedAt,
        string? chain, string? username, bool useAutoTimeout, CancellationToken cancellationToken)
    {
        TimeSpan autoTimeout = GetAutoTimeout();
        bool hasDeadline = useAutoTimeout && autoTimeout > TimeSpan.Zero;
        TimeSpan age = DateTimeOffset.UtcNow - receivedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        TimeSpan remainingTime = autoTimeout - age;
        if (hasDeadline && remainingTime <= TimeSpan.Zero)
        {
            return CreateExpiredResult(receivedAt, imageUrl, autoTimeout);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (hasDeadline)
        {
            deadline.CancelAfter(remainingTime);
        }

        string? imageStyle = GetChainImageStyle(chain);
        string? chainLogoBase64 = imageGenerationOptions.EnableChainLogo
            ? GetChainLogoBase64(chain)
            : null;
        string? characterImageBase64 = string.IsNullOrWhiteSpace(imageUrl)
            ? GetCharacterImageBase64(username)
            : null;
        Task<(TokenDraftDto Draft, double Seconds)> metadataTask = CreateMetadataAsync(postText, imageUrl,
            imageStyle, deadline.Token);

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return await CreateFromTextAsync(metadataTask, receivedAt, imageStyle, characterImageBase64,
                chainLogoBase64, deadline, hasDeadline, autoTimeout, cancellationToken);
        }

        Task<(FluxImageDto Image, double Seconds)> imageTask = CreateImageAsync(postText, imageUrl, imageStyle,
            chainLogoBase64, deadline.Token);

        try
        {
            await Task.WhenAll(metadataTask, imageTask);
        }
        catch (OperationCanceledException) when (hasDeadline && !cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, imageUrl, autoTimeout);
        }
        catch
        {
            deadline.Cancel();
            throw;
        }

        if (hasDeadline && DateTimeOffset.UtcNow - receivedAt >= autoTimeout)
        {
            return CreateExpiredResult(receivedAt, imageUrl, autoTimeout);
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
        DateTimeOffset receivedAt, string? chain, bool useAutoTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new InvalidOperationException("X did not return the new avatar URL.");
        }

        TimeSpan autoTimeout = GetAutoTimeout();
        bool hasDeadline = useAutoTimeout && autoTimeout > TimeSpan.Zero;
        TimeSpan remainingTime = autoTimeout - (DateTimeOffset.UtcNow - receivedAt);
        if (hasDeadline && remainingTime <= TimeSpan.Zero)
        {
            return CreateExpiredResult(receivedAt, imageUrl, autoTimeout);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (hasDeadline)
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
        catch (OperationCanceledException) when (hasDeadline && !cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, imageUrl, autoTimeout);
        }
    }

    // Ảnh user gửi được dùng nguyên bản làm avatar; AI chỉ tạo tên, mã và mô tả từ nội dung X.
    public async Task<TokenPreviewDto> CreateWithUploadedImageAsync(string? postText, byte[] image,
        DateTimeOffset receivedAt, string? chain, CancellationToken cancellationToken)
    {
        bool isJpeg = image.Length >= 3 && image[0] == 255 && image[1] == 216 && image[2] == 255;
        bool isPng = image.Length >= 8
            && image[0] == 137 && image[1] == 80 && image[2] == 78 && image[3] == 71
            && image[4] == 13 && image[5] == 10 && image[6] == 26 && image[7] == 10;
        if ((!isJpeg && !isPng) || image.Length > 10 * 1024 * 1024)
        {
            throw new ArgumentException("The custom token image must be a JPG or PNG up to 10 MB.");
        }

        Stopwatch timer = Stopwatch.StartNew();
        TokenDraftDto draft = await openAiClient.CreateTokenMetadataAsync(postText, null,
            GetChainImageStyle(chain), cancellationToken);
        timer.Stop();

        return new TokenPreviewDto
        {
            Draft = draft,
            Image = image,
            OpenAiSeconds = timer.Elapsed.TotalSeconds,
            TotalSeconds = Math.Max(0, (DateTimeOffset.UtcNow - receivedAt).TotalSeconds),
            UsedSourceImage = true
        };
    }

    private static TokenPreviewDto CreateExpiredResult(DateTimeOffset receivedAt, string? imageUrl,
        TimeSpan autoTimeout)
    {
        return new TokenPreviewDto
        {
            IsExpired = true,
            UsedSourceImage = !string.IsNullOrWhiteSpace(imageUrl),
            TotalSeconds = Math.Max(autoTimeout.TotalSeconds,
                (DateTimeOffset.UtcNow - receivedAt).TotalSeconds)
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
        CancellationTokenSource deadline, bool hasDeadline, TimeSpan autoTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            (TokenDraftDto draft, double openAiSeconds) = await metadataTask;
            Stopwatch timer = Stopwatch.StartNew();
            byte[] imageData;
            string imageUrl = string.Empty;

            // Một lựa chọn duy nhất được dùng chung cho cả Auto Create và tạo thủ công.
            switch (imageGenerationOptions.Provider.Trim().ToLowerInvariant())
            {
                case "openai":
                    imageData = await openAiClient.CreateTokenImageAsync(draft.ImagePrompt, imageStyle,
                        characterImageBase64, chainLogoBase64, deadline.Token);
                    break;

                case "gemini":
                    imageData = await geminiImageClient.CreateTokenImageAsync(draft.ImagePrompt, imageStyle,
                        characterImageBase64, chainLogoBase64, deadline.Token);
                    break;

                case "flux":
                    FluxImageDto image = await fluxClient.CreateTokenImageFromPromptAsync(draft.ImagePrompt,
                        imageStyle, characterImageBase64, chainLogoBase64, deadline.Token);
                    imageData = image.Data;
                    imageUrl = image.Url;
                    break;

                case "zimage":
                    imageData = await zImageClient.CreateTokenImageAsync(draft.ImagePrompt, imageStyle,
                        characterImageBase64, deadline.Token);
                    break;

                default:
                    throw new InvalidOperationException(
                        "ImageGeneration:Provider must be OpenAi, Gemini, Flux, or ZImage.");
            }
            timer.Stop();

            if (hasDeadline && DateTimeOffset.UtcNow - receivedAt >= autoTimeout)
            {
                return CreateExpiredResult(receivedAt, null, autoTimeout);
            }

            return new TokenPreviewDto
            {
                Draft = draft,
                Image = imageData,
                ImageUrl = imageUrl,
                OpenAiSeconds = openAiSeconds,
                FluxSeconds = timer.Elapsed.TotalSeconds,
                TotalSeconds = Math.Max(0, (DateTimeOffset.UtcNow - receivedAt).TotalSeconds)
            };
        }
        catch (OperationCanceledException) when (hasDeadline && !cancellationToken.IsCancellationRequested)
        {
            return CreateExpiredResult(receivedAt, null, autoTimeout);
        }
    }

    private TimeSpan GetAutoTimeout()
    {
        return imageGenerationOptions.AutoCreateTimeoutSeconds <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(imageGenerationOptions.AutoCreateTimeoutSeconds);
    }

    private static string? GetChainImageStyle(string? chain)
    {
        return chain?.ToLowerInvariant() switch
        {
            "bsc" => "Keep natural subject colors dominant with BNB yellow #F0B90B, charcoal black, and white accents.",
            "base" => "Keep natural subject colors dominant. Add subtle Base blue #0052FF, white, and deep navy accents only in lighting, edges, or background details.",
            "sol" => "Keep natural subject colors dominant. Add subtle Solana purple #9945FF, mint green #14F195, and cyan accents only in lighting, edges, or background details.",
            "robinhood" => "Keep natural subject colors dominant with Robinhood neon green, black, and white accents.",
            "stable" => "Keep natural subject colors dominant with Stable dark green, pale mint, white, and graphite accents.",
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
