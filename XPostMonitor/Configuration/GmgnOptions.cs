namespace XPostMonitor.Configuration;

public sealed class GmgnOptions
{
    public const string SectionName = "Gmgn";

    public string ApiKey { get; set; } = string.Empty;
    public string NodePath { get; set; } = "node";
    public string CliScriptPath { get; set; } = string.Empty;
}
