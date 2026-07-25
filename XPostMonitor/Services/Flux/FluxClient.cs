using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.Flux;

public sealed class FluxClient
{
    private const string MemeTokenStyle = " Render it in a polished internet-meme illustration style with expressive "
        + "shapes, bold clean outlines, vivid flat colors, soft simple shading, playful energy, and a crisp sticker-like finish. "
        + "Keep the subjects and composition dictated by the post. Do not force a circular badge, mascot, animal, or logo layout. "
        + "Keep strong readability at tiny thumbnail size. Draw no text by default. If the prompt explicitly quotes one short visible "
        + "phrase or number, render only that quoted text and copy every character exactly without adding or changing anything.";

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
        if (string.IsNullOrWhiteSpace(imagePrompt))
        {
            throw new ArgumentException("AI did not return an image prompt.");
        }

        string style = string.IsNullOrWhiteSpace(chainImageStyle)
            ? string.Empty
            : " Apply this selected launch-chain palette: " + chainImageStyle;
        string prompt = imagePrompt + style + MemeTokenStyle + " "
            + "No URLs, logos, trademarks, token symbols, extra text, or watermark.";
        return await GenerateAsync(prompt, null, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageResultAsync(string? postText, string? imageUrl, CancellationToken cancellationToken)
    {
        return await CreateTokenImageResultAsync(postText, imageUrl, null, cancellationToken);
    }

    public async Task<FluxImageDto> CreateTokenImageResultAsync(string? postText, string? imageUrl,
        string? chainImageStyle, CancellationToken cancellationToken)
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
        string prompt = string.IsNullOrWhiteSpace(imageUrl)
            ? "Create an image based directly on this post: " + cleanPostText + ". "
                + "Visualize the post-specific hook with its concrete subjects and action. Do not use a broad generic theme. "
                + style
                + MemeTokenStyle
                + " No URLs, logos, trademarks, token symbols, extra text, coins, currency signs, or watermark."
            : "Use the input image as the primary reference for a meme-token illustrated adaptation. "
                + "Preserve its main subjects, action, mood, and recognizable composition. "
                + "Use the post only as context: " + cleanPostText + ". "
                + style
                + MemeTokenStyle
                + " No URLs, logos, trademarks, extra text, coins, currency signs, or emblems.";

        return await GenerateAsync(prompt, imageUrl, cancellationToken);
    }

    private async Task<FluxImageDto> GenerateAsync(string prompt, string? imageUrl, CancellationToken cancellationToken)
    {

        Dictionary<string, object> requestBody = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["width"] = 512,
            ["height"] = 512,
            ["output_format"] = "jpeg",
            ["safety_tolerance"] = 2
        };

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            requestBody["input_image"] = imageUrl;
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
