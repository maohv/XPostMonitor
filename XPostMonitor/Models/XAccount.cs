namespace XPostMonitor.Models;

public sealed class XAccount
{
    public string XUserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<WatchlistEntry> Watchers { get; set; } = new List<WatchlistEntry>();
    public ICollection<XSubscription> Subscriptions { get; set; } = new List<XSubscription>();
}
