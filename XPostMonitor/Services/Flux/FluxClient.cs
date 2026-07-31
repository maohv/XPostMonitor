using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;
using XPostMonitor.Services.ImageGeneration;

namespace XPostMonitor.Services.Flux;

public sealed class FluxClient
{
    private const string MemeTokenStyle = " Render it as a full-body true chibi mascot in a polished playful 2D "
        + "editorial-cartoon style. Give a person an oversized rounded head occupying approximately 45-50% of the total character "
        + "height, a very small compact body, short rounded arms and legs, tiny shoes, slightly prominent ears, large expressive "
        + "glossy eyes, simplified but recognizable facial features, and a cheerful friendly expression. Preserve recognizable "
        + "facial identity and hairstyle whenever they are known. Include glasses, facial marks, or other signature face details "
        + "only when they are clearly present in the source or reference; never invent them. "
        + "Use a premium mascot character design, clean vector-inspired shapes, thick smooth dark-brown outlines, consistent line "
        + "weight, rounded contours, a warm pastel color palette, flat colors, simple two-tone cel shading, minimal gradients, soft "
        + "highlights, subtle ambient shadows, gentle paper grain texture, and a charming hand-drawn editorial finish. Keep the "
        + "character centered and fully visible from head to toe in a square composition. Use a simple warm minimalist background "
        + "with cream and light beige tones, softly simplified objects, and enough empty space around the character. "
        + "Show one clear Post-specific action with one or two concrete visual details tied directly to the selected hook. Match the "
        + "character's expression to the Post's tone; never default to anger. If a character does not fit the hook, transform the "
        + "dominant object into an adorable polished chibi mascot while keeping the same visual finish. Keep hands natural with the "
        + "correct number of fingers and no duplicated limbs. Keep the image cute, recognizable, crisp, and readable at thumbnail size. "
        + "For fictional characters, create an original design; do not copy a known copyrighted character or franchise design. "
        + "No text, watermark, photorealism, 3D rendering, realistic body proportions, complex cinematic lighting, thin sketchy "
        + "lines, exaggerated anime style, malformed hands, extra fingers, missing fingers, or duplicated limbs. Draw no letters, "
        + "words, numbers, emoji glyphs, captions, labels, speech bubbles, signs, documents, pages, whiteboards, screens, user "
        + "interfaces, writing, or pseudo-text.";

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
        await ImageGenerationDiagnosticLog.WriteAsync("FLUX", options.ModelEndpoint, prompt);

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
        return "Highest priority: preserve the facial identity from Image 1. Image 1 is the exact face reference for the single "
            + "dominant person, not a reference for the new scene. Preserve the person's face shape and proportions, eye and eyebrow "
            + "shape, nose, mouth, smile, hairstyle, hairline, skin tone, and every visible mole, beauty mark, freckle, or signature "
            + "facial detail in the same location. Include glasses only if they are clearly visible in Image 1; never invent glasses "
            + "or facial marks. Do not replace this face with a generic chibi face. The body, clothing, accessories, pose, props, and "
            + "background may change to fit the Post, but facial likeness must not be sacrificed. "
            + "Create a full-body adorable polished true chibi mascot of that same recognizable person with "
            + "an oversized rounded head occupying approximately 45-50% of the total character height, a very small compact body, "
            + "short rounded arms and legs, tiny shoes, slightly prominent ears, expressive glossy eyes, simplified but recognizable "
            + "facial features, and a friendly expression matching the Post. Slightly enlarge the eyes only if the original eye shape "
            + "and facial identity remain recognizable. Show that same person in this Post-specific scene: "
            + imagePrompt + ". "
            + style
            + logoInstruction
            + " Use a polished playful 2D editorial-cartoon illustration, premium mascot character design, clean vector-inspired "
            + "shapes, thick smooth dark-brown outlines, consistent line weight, rounded contours, a warm pastel color palette, flat "
            + "colors, simple two-tone cel shading, minimal gradients, soft highlights, subtle ambient shadows, gentle paper grain "
            + "texture, a charming hand-drawn editorial finish, and crisp clean details. Keep the character centered and fully visible "
            + "from head to toe. Use a simple warm minimalist background inspired by the Post-specific scene, with cream "
            + "and light beige tones, softly simplified objects, and enough empty space around the character. Maintain the reference "
            + "facial identity accurately, including natural hand anatomy and the correct number of fingers. Square composition, cute but "
            + "recognizable, clean professional finish. No text, URLs, token symbols, pseudo-text, watermark, photorealism, 3D "
            + "rendering, realistic body proportions, complex cinematic lighting, thin sketchy lines, exaggerated anime style, "
            + "malformed hands, extra fingers, missing fingers, or duplicated limbs.";
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
