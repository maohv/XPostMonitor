namespace XPostMonitor.Configuration;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    public string ApiKey { get; set; } = string.Empty;
    public string ImageModel { get; set; } = "gemini-3.1-flash-image";
    public string ImageSize { get; set; } = "1K";
}
