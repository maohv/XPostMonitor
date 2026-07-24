namespace XPostMonitor.Services.Gmgn;

// Danh sách network và launchpad mà gmgn-cli 1.5.4 hỗ trợ tạo token.
public static class TradingNetworks
{
    public static readonly IReadOnlyList<TradingNetwork> All =
    [
        new("sol", "Solana", "SOL", [new("pump", "Pump.fun"), new("bonk", "Bonk"), new("bags", "Bags")]),
        new("bsc", "BSC", "BNB", [new("fourmeme", "Four.Meme"), new("flap", "Flap")]),
        new("base", "Base", "ETH", [new("klik", "Klik"), new("clanker", "Clanker")]),
        new("robinhood", "Robinhood", "ETH", [new("trench", "Trench"), new("pons", "Pons")])
    ];

    public static TradingNetwork? Find(string? chain)
    {
        return All.FirstOrDefault(item => item.Chain == chain?.ToLowerInvariant());
    }

    public static bool IsValid(string? chain, string? dex)
    {
        TradingNetwork? network = Find(chain);
        return network?.Launchpads.Any(item => item.Dex == dex?.ToLowerInvariant()) == true;
    }
}

public sealed record TradingNetwork(
    string Chain,
    string DisplayName,
    string Currency,
    IReadOnlyList<TradingLaunchpad> Launchpads);

public sealed record TradingLaunchpad(string Dex, string DisplayName);
