namespace XPostMonitor.Configuration;

public sealed class FluxOptions
{
    public const string SectionName = "Flux";

    public string ApiKey { get; set; } = string.Empty;
    public string ModelEndpoint { get; set; } = "flux-2-klein-9b-preview";
}
