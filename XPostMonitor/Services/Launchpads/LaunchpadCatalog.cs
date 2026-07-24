namespace XPostMonitor.Services.Launchpads;

// Chỉ thêm launchpad vào đây sau khi đã có client tích hợp trực tiếp theo tài liệu chính thức.
public static class LaunchpadCatalog
{
    public static readonly IReadOnlyList<LaunchpadNetwork> All =
    [
        new("bsc", "BSC", "BNB", 0.01m, "bsc", [new("fourmeme", "Four.Meme")]),
        new("stable", "Stable", "USDT0", 0.01m, "stable", [new("dyorswap", "DYOR Swap")])
    ];

    public static LaunchpadNetwork? Find(string? chain)
    {
        return All.FirstOrDefault(item => item.Chain == chain?.ToLowerInvariant());
    }

    public static bool IsValid(string? chain, string? launchpad)
    {
        return Find(chain)?.Launchpads.Any(item => item.Code == launchpad?.ToLowerInvariant()) == true;
    }

    public static string? GetGmgnTokenUrl(string chain, string? tokenAddress)
    {
        LaunchpadNetwork? network = Find(chain);
        return network == null || string.IsNullOrWhiteSpace(tokenAddress)
            ? null
            : "https://gmgn.ai/" + network.GmgnChain + "/token/" + tokenAddress;
    }
}

public sealed record LaunchpadNetwork(string Chain, string DisplayName, string Currency,
    decimal MinimumBuyAmount, string GmgnChain, IReadOnlyList<LaunchpadInfo> Launchpads);

public sealed record LaunchpadInfo(string Code, string DisplayName);
