namespace XPostMonitor.Services.Gmgn.Launchpads;

// Launchpad dùng interface này khi bot phải tự theo dõi giá rồi mới gọi GMGN bán.
public interface ILocalTakeProfitHandler : IAutoTradingLaunchpadHandler
{
    string LocalOrderPrefix { get; }

    Task<decimal> GetCurrentPriceAsync(GmgnCredentials credentials, string tokenAddress,
        CancellationToken cancellationToken);
}
