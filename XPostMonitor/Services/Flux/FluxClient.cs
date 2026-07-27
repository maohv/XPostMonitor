using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.Flux;

public sealed class FluxClient
{
    private const string MemeTokenStyle = " Render it as a polished, playful 2D editorial-cartoon meme illustration. Use clean "
        + "rounded line art, smooth confident dark outlines, bright aqua and turquoise foundations, warm coral and golden accents, "
        + "soft cream highlights, gentle cel shading, and subtle paper-like texture. Build a lively layered scene with a clear "
        + "foreground, subject, and simple scenic background; add a few small story details that reward a second look without making "
        + "the composition cluttered. Keep the mood colorful, whimsical, premium, and instantly readable. Show one dominant "
        + "character actively doing something, preferably as a full-body or three-quarter-body scene, with one humorous costume "
        + "or prop and at least two concrete visual details tied directly to the selected hook. Match the character's expression "
        + "to the Post's tone; never default to an angry face. If a real public figure is named, draw a respectful recognizable "
        + "cartoon caricature using well-known visual traits instead of a generic businessperson. If a character would not fit "
        + "the hook, use one dominant object in a clear action instead. "
        + "Use rounded expressive shapes, a clean silhouette, dynamic but balanced composition, and a slightly absurd visual joke. "
        + "Adapt the accent colors and scenery to the actual subject while keeping this consistent bright cartoon identity. Keep the "
        + "composition readable at tiny thumbnail size. A plain headshot against an abstract city "
        + "or gradient background is invalid because it does not tell the story. Do not create an event poster, stage, "
        + "crowd, collage, trading dashboard, circular badge, logo, or detailed cinematic scene unless the selected hook truly "
        + "depends on it. For fictional characters, create an original design; do not copy a known copyrighted character or franchise design. "
        + "The image must be completely text-free. Ignore any earlier request to "
        + "render text. Draw no letters, words, numbers, emoji glyphs, captions, labels, speech bubbles, signs, documents, pages, "
        + "whiteboards, screens, user interfaces, writing, or pseudo-text. Express the idea only through visual subjects, actions, "
        + "objects, expressions, colors, and composition.";

    private readonly HttpClient httpClient;
    private readonly FluxOptions options;

    public FluxClient(HttpClient httpClient, FluxOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "FLUX API key is missing from configuration.";
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "v1/credits");
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return "FLUX connection failed: " + Shorten(json);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string credits = document.RootElement.GetProperty("credits").GetRawText();

        return "FLUX connected successfully.\n"
            + "Model: " + options.ModelEndpoint + "\n"
            + "Credits: " + credits;
    }

    public async Task<byte[]> CreateTokenImageAsync(string? postText, CancellationToken cancellationToken)
    {
        FluxImageDto result = await CreateTokenImageResultAsync(postText, null, cancellationToken);
        return result.Data;
    }

    public async Task<byte[]> CreateTokenImageAsync(string? postText, string? imageUrl, CancellationToken cancellationToken)
    {
        FluxImageDto result = await CreateTokenImageResultAsync(postText, imageUrl, cancellationToken);
        return result.Data;
    }

    public async Task<byte[]> DownloadSourceImageAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host == "twimg.com" || uri.Host.EndsWith(".twimg.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("X returned an invalid media URL.");
        }

        return await httpClient.GetByteArrayAsync(uri, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageFromPromptAsync(string imagePrompt, CancellationToken cancellationToken)
    {
        return await CreateTokenImageFromPromptAsync(imagePrompt, null, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageFromPromptAsync(string imagePrompt, string? chainImageStyle,
        CancellationToken cancellationToken)
    {
        return await CreateTokenImageFromPromptAsync(imagePrompt, chainImageStyle, null, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageFromPromptAsync(string imagePrompt, string? chainImageStyle,
        string? chainLogoBase64, CancellationToken cancellationToken)
    {
        return await CreateTokenImageFromPromptAsync(imagePrompt, chainImageStyle, null, chainLogoBase64,
            cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageFromPromptAsync(string imagePrompt, string? chainImageStyle,
        string? characterImageBase64, string? chainLogoBase64, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePrompt))
        {
            throw new ArgumentException("AI did not return an image prompt.");
        }

        string style = string.IsNullOrWhiteSpace(chainImageStyle)
            ? string.Empty
            : " Apply this selected launch-chain palette: " + chainImageStyle;
        bool hasCharacterImage = !string.IsNullOrWhiteSpace(characterImageBase64);
        string logoInstruction = GetLogoInstruction(chainLogoBase64, false,
            hasCharacterImage);

        // Khi có ảnh nhân vật, dùng đúng kiểu prompt đã test tốt với FLUX 4B.
        // Cảnh vẫn thay đổi theo từng Post, còn khuôn mặt và dấu hiệu riêng phải được giữ lại.
        string prompt = hasCharacterImage
            ? BuildCharacterReferencePrompt(imagePrompt, style, logoInstruction)
            : imagePrompt + style + MemeTokenStyle + " "
                + logoInstruction
                + " No URLs, token symbols, or watermark. Allow only the small launch-chain emblem requested by the selected chain palette; no other logos or trademarks.";
        return await GenerateAsync(prompt, null, characterImageBase64, chainLogoBase64, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageResultAsync(string? postText, string? imageUrl, CancellationToken cancellationToken)
    {
        return await CreateTokenImageResultAsync(postText, imageUrl, null, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageResultAsync(string? postText, string? imageUrl,
        string? chainImageStyle, CancellationToken cancellationToken)
    {
        return await CreateTokenImageResultAsync(postText, imageUrl, chainImageStyle, null, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageResultAsync(string? postText, string? imageUrl,
        string? chainImageStyle, string? chainLogoBase64, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(postText) && string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new ArgumentException("Usage: /fluximage post text");
        }

        if (postText?.Length > 4000)
        {
            throw new ArgumentException("Post text is too long. Maximum: 4000 characters.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("FLUX API key is missing from configuration.");
        }

        if (!string.IsNullOrWhiteSpace(imageUrl)
            && (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri) || imageUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Image URL must be a valid HTTPS URL.");
        }

        string cleanPostText = Regex.Replace(postText ?? string.Empty, @"https?://\S+", string.Empty).Trim();
        string style = string.IsNullOrWhiteSpace(chainImageStyle)
            ? "Choose colors from the source subject or image. "
            : "Apply this selected launch-chain palette: " + chainImageStyle + " ";
        string logoInstruction = GetLogoInstruction(chainLogoBase64, !string.IsNullOrWhiteSpace(imageUrl), false);
        string prompt = string.IsNullOrWhiteSpace(imageUrl)
            ? "Create an image based directly on this post: " + cleanPostText + ". "
                + "Visualize the post-specific hook with its concrete subjects and action. Do not use a broad generic theme. "
                + style
                + MemeTokenStyle
                + logoInstruction
                + " No URLs, token symbols, coins, currency signs, or watermark. Allow only the small launch-chain emblem requested by the selected chain palette; no other logos or trademarks."
            : "Use the input image as the primary reference for a meme-token illustrated adaptation. "
                + "Preserve its main subjects, action, mood, and recognizable composition. "
                + "Use the post only as context: " + cleanPostText + ". "
                + style
                + MemeTokenStyle
                + logoInstruction
                + " No URLs, coins, currency signs, or watermark. Allow only the small launch-chain emblem requested by the selected chain palette; no other logos, trademarks, or emblems.";

        return await GenerateAsync(prompt, imageUrl, chainLogoBase64, cancellationToken);
    }

    private async Task<FluxImageDto> GenerateAsync(string prompt, string? imageUrl, string? chainLogoBase64,
        CancellationToken cancellationToken)
    {
        return await GenerateAsync(prompt, imageUrl, null, chainLogoBase64, cancellationToken);
    }

    private async Task<FluxImageDto> GenerateAsync(string prompt, string? imageUrl,
        string? characterImageBase64, string? chainLogoBase64, CancellationToken cancellationToken)
    {
        Dictionary<string, object> requestBody = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["width"] = 512,
            ["height"] = 512,
            ["output_format"] = "jpeg",
            ["safety_tolerance"] = 2
        };

        int nextImageNumber = 1;
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            requestBody["input_image"] = imageUrl;
            nextImageNumber = 2;
        }
        if (!string.IsNullOrWhiteSpace(characterImageBase64))
        {
            AddReferenceImage(requestBody, nextImageNumber, characterImageBase64);
            nextImageNumber++;
        }
        if (!string.IsNullOrWhiteSpace(chainLogoBase64))
        {
            AddReferenceImage(requestBody, nextImageNumber, chainLogoBase64);
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "v1/" + options.ModelEndpoint);
        request.Content = JsonContent.Create(requestBody);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("FLUX image failed: " + Shorten(json));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string pollingUrl = document.RootElement.GetProperty("polling_url").GetString()
            ?? throw new JsonException("FLUX did not return a polling URL.");

        string generatedImageUrl = await WaitForImageAsync(pollingUrl, cancellationToken);
        byte[] image = await httpClient.GetByteArrayAsync(generatedImageUrl, cancellationToken);

        return new FluxImageDto
        {
            Data = image,
            Url = generatedImageUrl
        };
    }

    private static string BuildCharacterReferencePrompt(string imagePrompt, string style,
        string logoInstruction)
    {
        return "Image 1 defines the exact identity of the single dominant person. "
            + "Create a recognizable polished 2D editorial-cartoon likeness of that same person, preserving facial proportions, "
            + "eye and eyebrow shape, nose, mouth, smile, hairstyle, skin tone, and every visible mole, beauty mark, freckle, "
            + "or distinctive facial detail in the same location. Frame the person close enough for facial details to remain visible. "
            + "Show that same person in this Post-specific scene: " + imagePrompt + ". "
            + style
            + logoInstruction
            + " Clean rounded dark line art, bright aqua, turquoise, coral and golden palette, gentle cel shading, "
            + "subtle paper texture, text-free composition. No URLs, token symbols, pseudo-text, or watermark.";
    }

    private static string GetLogoInstruction(string? chainLogoBase64, bool hasPostImage,
        bool hasCharacterImage)
    {
        if (string.IsNullOrWhiteSpace(chainLogoBase64))
        {
            return string.Empty;
        }

        string reference = hasPostImage
            ? (hasCharacterImage ? "third input reference image" : "second input reference image")
            : (hasCharacterImage ? "second input reference image" : "input reference image");
        if (hasCharacterImage && !hasPostImage)
        {
            return " Image 2 is the exact launch-chain logo; print it accurately exactly once on one suitable physical object and nowhere else.";
        }

        return " Use the " + reference
            + " as the exact launch-chain logo. Reproduce its geometry accurately once on a physical object in the scene; do not redesign or approximate it.";
    }

    private static void AddReferenceImage(Dictionary<string, object> requestBody, int imageNumber,
        string imageBase64)
    {
        string key = imageNumber == 1 ? "input_image" : "input_image_" + imageNumber;
        requestBody[key] = imageBase64;
    }

    private async Task<string> WaitForImageAsync(string pollingUrl, CancellationToken cancellationToken)
    {
        Uri url = new Uri(pollingUrl);
        if (url.Scheme != Uri.UriSchemeHttps || !(url.Host == "bfl.ai" || url.Host.EndsWith(".bfl.ai", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("FLUX returned an invalid polling URL.");
        }

        for (int attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("FLUX polling failed: " + Shorten(json));
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string status = root.GetProperty("status").GetString() ?? "Unknown";

            if (status == "Ready")
            {
                return root.GetProperty("result").GetProperty("sample").GetString()
                    ?? throw new JsonException("FLUX returned an empty image URL.");
            }

            if (status is "Error" or "Failed" or "Request Moderated" or "Content Moderated")
            {
                throw new InvalidOperationException("FLUX image failed with status: " + status);
            }
        }

        throw new TimeoutException("FLUX image timed out after 20 seconds.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        return CreateRequest(method, new Uri(httpClient.BaseAddress!, url));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri url)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-key", options.ApiKey);
        request.Headers.Add("accept", "application/json");
        return request;
    }

    private static string Shorten(string text)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "Unknown error" : text.Trim();
        return value.Length <= 500 ? value : value[..500];
    }
}
