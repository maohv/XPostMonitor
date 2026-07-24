using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        return await CreateTokenMetadataAsync(postText, imageUrl, null, cancellationToken);
    }

    public async Task<TokenDraftDto> CreateTokenMetadataAsync(string? postText, string? imageUrl,
        string? chainImageStyle, CancellationToken cancellationToken)
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
                        new { type = "input_text", text = "Post text: " + (postText ?? string.Empty)
                            + "\nUse the post text as the primary source for name and symbol. Use the attached image only as visual context." },
                        new { type = "input_image", image_url = imageUrl, detail = "low" }
                    }
                }
            };
        }

        string imageStyleInstruction = string.IsNullOrWhiteSpace(chainImageStyle)
            ? "Choose colors from the source subject or image. "
            : "The user selected this launch-chain style: " + chainImageStyle
                + " Always apply this palette to image_prompt while keeping the post subject recognizable. ";

        var requestBody = new
        {
            model = options.Model,
            instructions = "Act as a fast meme-token editor. Find the post-specific hook: its strongest exact phrase, joke, "
                + "concrete action, object, or cause-and-result story. Do not replace a unique hook with a broad theme. "
                + "Post text is always the primary source for name and symbol. An attached image may add visual context but must "
                + "not replace the post's hook or language. If [POST_TYPE=reply] is present, use only the reply text after that "
                + "marker as the token hook and do not use the parent Post as the token idea. "
                + "If input starts with [SOURCE_LANGUAGE=xx], that X API language is authoritative for both name and symbol. "
                + "Ignore languages found only inside an attached image when choosing name and symbol. Otherwise detect the "
                + "source's main language. Use that same language for both name and symbol and never translate them. "
                + "An English source needs an English name and symbol; a Chinese source needs a Chinese name and symbol. "
                + "Write a short memorable name in the source's main language. English names must use normal Title Case words "
                + "with spaces, never CamelCase or concatenated words. For Chinese content, prefer a natural "
                + "2-8 character familiar phrase or idiom with emotional or cultural meaning, not a formal invented noun compound. "
                + "Avoid generic summaries such as Global Kindness. For example, a Chinese post where charity and support letters "
                + "positively affect a judgment should become \u5584\u6709\u5584\u62A5 / \u5584\u6709\u5584\u62A5. "
                + "Symbol must come directly from name. For a name with multiple words, use their initials: Human Games Corp becomes "
                + "HGC and Best Entry Point becomes BEP. For a name without spaces, use the full name. Uppercase where the language "
                + "supports uppercase and keep the original script. Symbol must fit 2-20 characters. Never use pinyin, translation, "
                + "unrelated letters, AI, COIN, "
                + "or TOKEN. Write an English description under 160 characters that links the name to the exact post hook. "
                + "Write image_prompt in English under 500 characters. Tell the same story using 2-3 concrete visual symbols "
                + "from the post and represent every essential phrase. Use one clear central scene, not a collage or generic trading "
                + "dashboard. If the post asks a question or compares choices, show both choices fairly without inventing an answer. "
                + "Avoid traders at screens and candlestick charts unless the post specifically depends on them. Use strong thumbnail "
                + "contrast. " + imageStyleInstruction + "The image must contain no typography, "
                + "words, letters, numbers, URLs, logos, trademarks, token symbols, or watermark. Do not claim endorsement or "
                + "call the token official. Name must be no more than 20 characters.",
            input,
            reasoning = new { effort = "minimal" },
            max_output_tokens = 500,
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
                            description = new { type = "string" },
                            image_prompt = new { type = "string" }
                        },
                        required = new[] { "name", "symbol", "description", "image_prompt" },
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
                    TokenDraftDto draft = JsonSerializer.Deserialize<TokenDraftDto>(text)
                        ?? throw new JsonException("AI returned an invalid token draft.");
                    draft.Name = NormalizeName(draft.Name);
                    draft.Symbol = CreateSymbolFromName(draft.Name, draft.Symbol);
                    return draft;
                }
            }
        }

        throw new JsonException("AI did not return a token draft.");
    }

    private static string NormalizeName(string name)
    {
        string clean = name.Trim();
        return Regex.Replace(clean,
            "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
    }

    private static string CreateSymbolFromName(string name, string aiSymbol)
    {
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string symbol = words.Length > 1
            ? new string(words.Select(word => word.FirstOrDefault(char.IsLetterOrDigit))
                .Where(character => character != char.MinValue).ToArray())
            : new string(name.Where(char.IsLetterOrDigit).ToArray());
        symbol = symbol.ToUpperInvariant();
        if (symbol.Length < 2)
        {
            symbol = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
        if (symbol.Length < 2)
        {
            symbol = new string(aiSymbol.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
        if (symbol.Length < 2)
        {
            throw new JsonException("AI returned a token name that cannot be used as a symbol.");
        }

        return symbol[..Math.Min(symbol.Length, 20)];
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
