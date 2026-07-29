namespace XPostMonitor.Configuration;

public sealed class AutoTradingOptions
{
    public const string SectionName = "AutoTrading";

    public int NoBuyerTimeoutSeconds { get; set; } = 30;
    public int FirstTakeProfitTimeoutSeconds { get; set; } = 60;
}
