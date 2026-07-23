namespace XPostMonitor.Dtos;

public sealed class FluxImageDto
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Url { get; set; } = string.Empty;
}
