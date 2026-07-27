namespace XPostMonitor.Configuration;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAi";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5.4-mini";
    public string ImageModel { get; set; } = "gpt-image-1-mini";
}
