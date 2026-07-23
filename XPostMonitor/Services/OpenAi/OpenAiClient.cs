using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.OpenAi;

public sealed class OpenAiClient
{
    private readonly HttpClient httpClient;
    private readonly OpenAiOptions options;

    public OpenAiClient(HttpClient httpClient, OpenAiOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "OpenAI API key is missing from configuration.";
        }

        var requestBody = new
        {
            model = options.Model,
            input = "Reply with exactly: OpenAI connected"
        };

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = JsonContent.Create(requestBody);

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return "OpenAI connection failed: " + ReadError(json);
            }

            return "OpenAI connected successfully.\nModel: " + options.Model;
        }
        catch (Exception exception)
        {
            return "OpenAI connection failed: " + exception.Message;
        }
    }

    public async Task<string> CreateTokenDraftAsync(string? postText, CancellationToken cancellationToken)
    {
        try
        {
            TokenDraftDto draft = await CreateTokenMetadataAsync(postText, cancellationToken);
            return "Token draft\n\n"
                + "Name: " + draft.Name + "\n"
                + "Symbol: " + draft.Symbol + "\n"
                + "Description: " + draft.Description;
        }
        catch (Exception exception)
        {
            return "AI draft failed: " + exception.Message;
        }
    }

    public async Task<TokenDraftDto> CreateTokenMetadataAsync(string? postText, CancellationToken cancellationToken)
    {
        return await CreateTokenMetadataAsync(postText, null, cancellationToken);
    }

    public async Task<TokenDraftDto> CreateTokenMetadataAsync(string? postText, string? imageUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(postText) && string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new ArgumentException("Usage: /tokenpreview post text");
        }

        if (postText?.Length > 4000)
        {
            throw new ArgumentException("Post text is too long. Maximum: 4000 characters.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is missing from configuration.");
        }

        object input = postText ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri) || imageUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Image URL must be a valid HTTPS URL.");
            }

            input = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = "Post text: " + (postText ?? string.Empty) + "\nUse the attached image as the primary source." },
                        new { type = "input_image", image_url = imageUrl, detail = "low" }
                    }
                }
            };
        }

        var requestBody = new
        {
            model = options.Model,
            instructions = "Create token metadata from the post. Preserve the strongest exact phrase when it makes a good name. "
                + "When an image is attached, base the token idea primarily on the image and use the post text as context. "
                + "Symbol must directly match the strongest noun or token name. Never append AI, COIN, TOKEN, or unrelated abbreviations. "
                + "Write in English. Do not claim endorsement or call the token official. Name must be 2-20 characters. "
                + "Symbol must be 2-10 uppercase letters or numbers. Description must naturally explain the idea in no more than 160 characters.",
            input,
            reasoning = new { effort = "minimal" },
            max_output_tokens = 300,
            text = new
            {
                verbosity = "low",
                format = new
                {
                    type = "json_schema",
                    name = "token_draft",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string" },
                            symbol = new { type = "string" },
                            description = new { type = "string" }
                        },
                        required = new[] { "name", "symbol", "description" },
                        additionalProperties = false
                    }
                }
            }
        };

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = JsonContent.Create(requestBody);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadError(json));
        }

        return ReadTokenDraft(json);
    }

    public async Task<byte[]> CreateImageAsync(string? prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Usage: /aiimage image prompt");
        }

        if (prompt.Length > 2000)
        {
            throw new ArgumentException("Image prompt is too long. Maximum: 2000 characters.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is missing from configuration.");
        }

        var requestBody = new
        {
            model = options.ImageModel,
            prompt,
            size = "1024x1024",
            quality = "low",
            output_format = "png",
            n = 1
        };

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = JsonContent.Create(requestBody);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("AI image failed: " + ReadError(json));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string base64 = document.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString()
            ?? throw new JsonException("OpenAI returned empty image data.");

        return Convert.FromBase64String(base64);
    }

    private static TokenDraftDto ReadTokenDraft(string responseJson)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("status", out JsonElement status) && status.GetString() != "completed")
        {
            string reason = root.TryGetProperty("incomplete_details", out JsonElement details)
                && details.TryGetProperty("reason", out JsonElement value)
                ? value.GetString() ?? "unknown reason"
                : "unknown reason";

            throw new JsonException("AI response was incomplete: " + reason);
        }

        JsonElement output = root.GetProperty("output");

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content))
            {
                continue;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out JsonElement type) && type.GetString() == "output_text")
                {
                    string text = part.GetProperty("text").GetString() ?? throw new JsonException("AI returned empty text.");
                    return JsonSerializer.Deserialize<TokenDraftDto>(text) ?? throw new JsonException("AI returned an invalid token draft.");
                }
            }
        }

        throw new JsonException("AI did not return a token draft.");
    }

    private static string ReadError(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Unknown error";
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json[..Math.Min(json.Length, 500)];
        }
    }
}
