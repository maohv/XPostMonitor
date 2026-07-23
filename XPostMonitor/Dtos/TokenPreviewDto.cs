namespace XPostMonitor.Dtos;

public sealed class TokenPreviewDto
{
    public TokenDraftDto Draft { get; set; } = new TokenDraftDto();
    public byte[] Image { get; set; } = Array.Empty<byte>();
    public string ImageUrl { get; set; } = string.Empty;
    public double OpenAiSeconds { get; set; }
    public double FluxSeconds { get; set; }
    public double TotalSeconds { get; set; }
    public bool UsedSourceImage { get; set; }
    public bool IsExpired { get; set; }
}
