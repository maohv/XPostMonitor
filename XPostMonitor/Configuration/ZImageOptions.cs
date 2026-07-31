namespace XPostMonitor.Configuration;

public sealed class ZImageOptions
{
    public const string SectionName = "ZImage";

    // Tạo API key tại fal.ai rồi chỉ lưu trong appsettings.Development.json hoặc VPS.
    public string ApiKey { get; set; } = string.Empty;
    public string ImageSize { get; set; } = "square";
    public int InferenceSteps { get; set; } = 8;
    public string Acceleration { get; set; } = "high";
    public int IdentityInferenceSteps { get; set; } = 20;
    public double IdentityWeight { get; set; } = 1;
}
