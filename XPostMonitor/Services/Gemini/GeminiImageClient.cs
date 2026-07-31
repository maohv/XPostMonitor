using System.Net.Http.Json;
using System.Text.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Services.ImageGeneration;

namespace XPostMonitor.Services.Gemini;

public sealed class GeminiImageClient
{
    private readonly HttpClient httpClient;
    private readonly GeminiOptions options;

    public GeminiImageClient(HttpClient httpClient, GeminiOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    // Kiểm tra API key và quyền truy cập model, không tạo ảnh nên không mất phí ảnh.
    public async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "Gemini API key is missing from configuration.";
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get,
            "v1beta/models/" + Uri.EscapeDataString(options.ImageModel));
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Gemini connection failed: " + ReadError(json));
        }

        return "Gemini connected successfully. Model: " + options.ImageModel;
    }

    // Tạo ảnh vuông từ prompt. Ảnh nhân vật và logo chain là hai tham chiếu tùy chọn.
    public async Task<byte[]> CreateTokenImageAsync(string imagePrompt, string? chainImageStyle,
        string? characterImageBase64, string? chainLogoBase64, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Gemini image generation is not configured.");
        }

        string prompt = BuildPrompt(imagePrompt, chainImageStyle, characterImageBase64, chainLogoBase64);
        await ImageGenerationDiagnosticLog.WriteAsync("Gemini", options.ImageModel, prompt);

        List<object> input = new List<object>
        {
            new
            {
                type = "text",
                text = prompt
            }
        };

        if (!string.IsNullOrWhiteSpace(characterImageBase64))
        {
            input.Add(CreateImageInput(characterImageBase64));
        }

        if (!string.IsNullOrWhiteSpace(chainLogoBase64))
        {
            input.Add(CreateImageInput(chainLogoBase64));
        }

        var requestBody = new
        {
            model = options.ImageModel,
            input,
            response_format = new
            {
                type = "image",
                mime_type = "image/jpeg",
                aspect_ratio = "1:1",
                image_size = options.ImageSize
            }
        };

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "v1beta/interactions");
        request.Content = JsonContent.Create(requestBody);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string error = ReadError(json);
            await ImageGenerationDiagnosticLog.WriteAsync("Gemini error", options.ImageModel, error);
            throw new InvalidOperationException("Gemini image failed: " + error);
        }

        return ReadImage(json);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-goog-api-key", options.ApiKey);
        return request;
    }

    private static object CreateImageInput(string imageBase64)
    {
        byte[] image = Convert.FromBase64String(imageBase64);
        bool isPng = image.Length >= 8
            && image[0] == 137 && image[1] == 80 && image[2] == 78 && image[3] == 71
            && image[4] == 13 && image[5] == 10 && image[6] == 26 && image[7] == 10;

        return new
        {
            type = "image",
            mime_type = isPng ? "image/png" : "image/jpeg",
            data = imageBase64
        };
    }

    private static string BuildPrompt(string imagePrompt, string? chainImageStyle,
        string? characterImageBase64, string? chainLogoBase64)
    {
        bool hasCharacter = !string.IsNullOrWhiteSpace(characterImageBase64);
        bool hasLogo = !string.IsNullOrWhiteSpace(chainLogoBase64);

        string referenceInstruction = hasCharacter
            ? "Image 1 is the identity reference. Make the same recognizable person chibi. Keep the face shape, eyes, eyebrows, nose, mouth, "
                + "hair, skin tone, glasses, moles, and facial marks. No generic face or new face-covering accessories. "
            : string.Empty;

        if (hasLogo)
        {
            string logoNumber = hasCharacter ? "Image 2" : "Image 1";
            referenceInstruction += logoNumber
                + " is the chain logo. Reproduce it accurately once on one scene object. ";
        }

        return referenceInstruction
            + "Scene: " + imagePrompt + ". "
            + (string.IsNullOrWhiteSpace(chainImageStyle) ? string.Empty : chainImageStyle + " ")
            + "One centered full-body true chibi: large head, tiny body, fully clothed in a scene-appropriate outfit. "
            + "Use at most two key props and a minimal cream background. Clean 2D vector style, dark-brown outlines, warm pastels, "
            + "flat two-tone shading, subtle grain. No text, watermark, photo, 3D, nudity, or anatomy errors.";
    }

    private static byte[] ReadImage(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement step in steps.EnumerateArray().Reverse())
            {
                if (!step.TryGetProperty("content", out JsonElement content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement block in content.EnumerateArray().Reverse())
                {
                    if (block.TryGetProperty("type", out JsonElement type)
                        && type.GetString() == "image"
                        && block.TryGetProperty("data", out JsonElement data)
                        && !string.IsNullOrWhiteSpace(data.GetString()))
                    {
                        return Convert.FromBase64String(data.GetString()!);
                    }
                }
            }
        }

        // Giữ tương thích nếu Google trả schema cũ trong thời gian API còn ở v1beta.
        if (root.TryGetProperty("outputs", out JsonElement outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement output in outputs.EnumerateArray().Reverse())
            {
                if (output.TryGetProperty("type", out JsonElement type)
                    && type.GetString() == "image"
                    && output.TryGetProperty("data", out JsonElement data)
                    && !string.IsNullOrWhiteSpace(data.GetString()))
                {
                    return Convert.FromBase64String(data.GetString()!);
                }
            }
        }

        throw new JsonException("Gemini returned no image data.");
    }

    private static string ReadError(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString() ?? "Unknown error";
            }
        }
        catch (JsonException)
        {
            // Phản hồi không phải JSON sẽ được rút gọn ở dưới.
        }

        string value = string.IsNullOrWhiteSpace(json) ? "Unknown error" : json.Trim();
        return value.Length <= 500 ? value : value[..500];
    }
}
