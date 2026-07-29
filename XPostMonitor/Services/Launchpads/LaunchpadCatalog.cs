namespace XPostMonitor.Services.Launchpads;

// Chỉ thêm launchpad vào đây sau khi đã có client tích hợp trực tiếp theo tài liệu chính thức.
public static class LaunchpadCatalog
{
    public static readonly IReadOnlyList<LaunchpadNetwork> All =
    [
        new("bsc", "BSC", "BNB", 0.005m, "bsc",
            [new("fourmeme", "Four.Meme"), new("flap", "Flap")]),
        new("stable", "Stable", "USDT0", 0.01m, "stable", [new("dyorswap", "DYOR Swap")]),
        new("robinhood", "Robinhood", "ETH", 0.005m, "robinhood",
            [new("long", "Long"), new("pons", "pons")])
    ];

    public static readonly IReadOnlyList<LaunchpadAnchor> LongAnchors =
    [
        new("AAPL", "AAPL - Apple", "0xaf3d76f1834a1d425780943c99ea8a608f8a93f9"),
        new("AMD", "AMD", "0x86923f96303d656e4aa86d9d42d1e57ad2023fdc"),
        new("AMZN", "AMZN - Amazon", "0x12f190a9f9d7d37a250758b26824b97ce941bf54"),
        new("GOOGL", "GOOGL - Alphabet", "0x2e0847e8910a9732eb3fb1bb4b70a580adad4fe3"),
        new("META", "META", "0xc0d6457c16cc70d6790dd43521c899c87ce02f35"),
        new("MSFT", "MSFT - Microsoft", "0xe93237c50d904957cf27e7b1133b510c669c2e74"),
        new("NVDA", "NVDA - Nvidia", "0xd0601ce157db5bdc3162bbac2a2c8af5320d9eec"),
        new("PLTR", "PLTR - Palantir", "0x894e1ec2d74ffe5aef8dc8a9e84686accb964f2a"),
        new("SPCX", "SPCX - SpaceX", "0x4a0e65a3eccec6dbe60ae065f2e7bb85fae35eea"),
        new("TSLA", "TSLA - Tesla", "0x322f0929c4625ed5bad873c95208d54e1c003b2d")
    ];

    public static LaunchpadNetwork? Find(string? chain)
    {
        return All.FirstOrDefault(item => item.Chain == chain?.ToLowerInvariant());
    }

    public static bool IsValid(string? chain, string? launchpad)
    {
        return Find(chain)?.Launchpads.Any(item => item.Code == launchpad?.ToLowerInvariant()) == true;
    }

    public static bool SupportsCreatorTax(string? launchpad)
    {
        return launchpad?.ToLowerInvariant() is "fourmeme" or "flap";
    }

    public static bool IsValidCreatorTax(string? launchpad, int creatorTaxPercent)
    {
        return launchpad?.ToLowerInvariant() switch
        {
            "fourmeme" => creatorTaxPercent is 0 or 1 or 3 or 5 or 10,
            "flap" => creatorTaxPercent is 1 or 3 or 5 or 10,
            _ => creatorTaxPercent == 0
        };
    }

    public static bool IsValidRoute(string? chain, string? launchpad, string? anchor)
    {
        if (!IsValid(chain, launchpad))
        {
            return false;
        }

        return launchpad?.ToLowerInvariant() != "long" || FindLongAnchor(anchor) != null;
    }

    public static LaunchpadAnchor? FindLongAnchor(string? code)
    {
        return LongAnchors.FirstOrDefault(item =>
            string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
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

public sealed record LaunchpadAnchor(string Code, string DisplayName, string TokenAddress);
