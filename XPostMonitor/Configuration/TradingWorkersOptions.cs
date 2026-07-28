namespace XPostMonitor.Configuration;

public sealed class TradingWorkersOptions
{
    public const string SectionName = "TradingWorkers";

    public int MaxWorkers { get; set; } = 3;
}
