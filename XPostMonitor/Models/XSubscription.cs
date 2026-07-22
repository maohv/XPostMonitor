namespace XPostMonitor.Models;

public sealed class XSubscription
{
    public string XUserId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? RemoteSubscriptionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public XAccount XAccount { get; set; } = null!;
}
