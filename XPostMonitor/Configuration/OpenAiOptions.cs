namespace XPostMonitor.Configuration;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAi";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5.4-mini";
    public string ImageModel { get; set; } = "gpt-image-2";
    public string ImageQuality { get; set; } = "low";
    public string ImageSize { get; set; } = "816x816";
}
