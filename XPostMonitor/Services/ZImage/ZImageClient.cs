using System.Net.Http.Json;
using System.Text.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Services.ImageGeneration;

namespace XPostMonitor.Services.ZImage;

// Gọi Z-Image Turbo qua fal.ai. Có ảnh nhân vật thì dùng image-to-image, không có thì dùng text-to-image.
public sealed class ZImageClient
{
    private const string ZImageModel = "fal-ai/z-image/turbo";
    private const string IdentityModel = "fal-ai/flux-pulid";
    private const string Style = " Create a centered full-body true chibi mascot with an oversized rounded head, "
        + "a very small compact body, short rounded arms and legs, tiny shoes, and natural hands. Polished playful "
        + "2D editorial-cartoon illustration, clean vector-inspired shapes, thick smooth dark-brown outlines, warm "
        + "pastel colors, flat colors, simple two-tone cel shading, subtle shadows, and gentle paper grain. Use a "
        + "simple warm cream background. Square composition. No text, watermark, photorealism, 3D rendering, "
        + "malformed hands, extra fingers, missing fingers, or duplicated limbs.";
    private const string IdentityStyle = " Create a centered three-quarter-body polished chibi character. Make the "
        + "recognizable face large and clear, occupying roughly 35 to 45 percent of the image. Keep the exact facial "
        + "geometry and distinctive identity from the reference while applying only light cartoon stylization to the "
        + "face. Stylize the body and scene more strongly. Use clean vector-inspired shapes, thick smooth dark-brown "
        + "outlines, warm pastel colors, flat colors, simple two-tone cel shading, subtle shadows, gentle paper grain, "
        + "and a warm cream background. Square composition. No text, watermark, photorealism, 3D rendering, malformed "
        + "hands, extra fingers, missing fingers, or duplicated limbs.";

    private readonly HttpClient httpClient;
    private readonly ZImageOptions options;

    public ZImageClient(HttpClient httpClient, ZImageOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<byte[]> CreateTokenImageAsync(string imagePrompt, string? chainImageStyle,
        string? characterImageBase64, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("ZImage:ApiKey is missing from configuration.");
        }

        bool hasCharacter = !string.IsNullOrWhiteSpace(characterImageBase64);
        string prompt = (hasCharacter
                ? "Create the same recognizable person shown in the reference image. Preserve their facial identity, "
                    + "exact face shape, eyes, eyebrows, nose, mouth, hairstyle, glasses, skin tone, and distinctive "
                    + "facial details. The face must remain immediately recognizable; stylize the body and scene more "
                    + "than the face, and do not replace the face with a generic cartoon face. "
                : string.Empty)
            + "Create this Post-specific scene: " + imagePrompt + ". "
            + (string.IsNullOrWhiteSpace(chainImageStyle) ? string.Empty : chainImageStyle + " ")
            + (hasCharacter ? IdentityStyle : Style);

        if (hasCharacter)
        {
            return await CreateIdentityImageAsync(prompt, characterImageBase64!, cancellationToken);
        }

        await ImageGenerationDiagnosticLog.WriteAsync("Z-Image", ZImageModel, prompt);

        Dictionary<string, object> body = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["image_size"] = options.ImageSize,
            ["num_inference_steps"] = Math.Clamp(options.InferenceSteps, 1, 8),
            ["num_images"] = 1,
            ["enable_safety_checker"] = true,
            ["output_format"] = "jpeg",
            ["acceleration"] = options.Acceleration,
            ["enable_prompt_expansion"] = false
        };

        return await SendRequestAsync(ZImageModel, body, "Z-Image", cancellationToken);
    }

    // Flux PuLID nhận ảnh khuôn mặt riêng và dùng id_weight để giữ đúng người.
    private async Task<byte[]> CreateIdentityImageAsync(string prompt, string characterImageBase64,
        CancellationToken cancellationToken)
    {
        await ImageGenerationDiagnosticLog.WriteAsync("Flux PuLID", IdentityModel, prompt);

        Dictionary<string, object> body = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["reference_image_url"] = CreateDataUrl(characterImageBase64),
            ["image_size"] = options.ImageSize,
            ["num_inference_steps"] = Math.Clamp(options.IdentityInferenceSteps, 4, 30),
            ["guidance_scale"] = 4,
            ["true_cfg"] = 1,
            ["id_weight"] = Math.Clamp(options.IdentityWeight, 0.5, 1),
            ["negative_prompt"] = "different person, generic face, unrecognizable face, text, watermark, "
                + "photorealism, 3D, bad anatomy, malformed hands, extra fingers, missing fingers, duplicated limbs",
            ["enable_safety_checker"] = true,
            ["max_sequence_length"] = "128"
        };

        return await SendRequestAsync(IdentityModel, body, "Flux PuLID", cancellationToken);
    }

    // Gửi yêu cầu tới fal và tải ảnh kết quả về.
    private async Task<byte[]> SendRequestAsync(string endpoint, Dictionary<string, object> body,
        string providerName, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", "Key " + options.ApiKey);
        request.Content = JsonContent.Create(body);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(providerName + " failed: " + Shorten(json));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string imageUrl = document.RootElement.GetProperty("images")[0].GetProperty("url").GetString()
            ?? throw new JsonException(providerName + " returned an empty image URL.");
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new JsonException(providerName + " returned an invalid image URL.");
        }

        return await httpClient.GetByteArrayAsync(uri, cancellationToken);
    }

    private static string CreateDataUrl(string imageBase64)
    {
        byte[] image = Convert.FromBase64String(imageBase64);
        bool isPng = image.Length >= 8
            && image[0] == 137 && image[1] == 80 && image[2] == 78 && image[3] == 71;
        return "data:" + (isPng ? "image/png" : "image/jpeg") + ";base64," + imageBase64;
    }

    private static string Shorten(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "Unknown error" : value.Trim();
        return text.Length <= 500 ? text : text[..500];
    }
}
