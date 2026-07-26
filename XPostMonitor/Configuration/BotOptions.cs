namespace XPostMonitor.Configuration;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string TelegramToken { get; set; } = string.Empty;
    public string XBearerToken { get; set; } = string.Empty;
    public long TelegramChannelId { get; set; }
    public bool EnablePersonalBot { get; set; }
    public bool EnableXMonitoring { get; set; } = true;
    public string ChannelLanguage { get; set; } = "en";
}
