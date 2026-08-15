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
            [new("long", "Long"), new("pons", "pons"), new("flap", "Flap")])
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

    // Danh sách Crypto/RWA dự phòng khi chưa bấm Update hoặc Flap tạm thời không truy cập được.
    // BNB dùng địa chỉ zero vì đây là native token của BSC.
    private static FlapPaymentToken[] flapBscPaymentTokens =
    [
        new("BNB", "BNB", "0x0000000000000000000000000000000000000000", 18),
        new("USDT", "USDT", "0x55d398326f99059fF775485246999027B3197955", 18),
        new("USD1", "USD1", "0x8d0D000Ee44948FC98c9B98A4FA4921476f08B0d", 18),
        new("U", "U - United Stables", "0xcE24439F2D9C6a2289F741120FE202248B666666", 18),
        new("BTCB", "BTCB", "0x7130d2A12B9BCbFAe4f2634d864A1Ee1Ce3Ead9c", 18),
        new("SPCXB", "SPCXB - SpaceX", "0xbe9D156892E55e7154BcD3cB0FEA677F9D3103E1", 18),
        new("SKHYB", "SKHYB - SK Hynix", "0xCA750eF65f295BBECd685Abf54e82CAf297BDB61", 18),
        new("SPYB", "SPYB - SPY", "0x7138b48df7D98D7e3cc221BfE7192D0a178182D8", 18),
        new("XAUT", "XAUT - Tether Gold", "0x21cAef8A43163Eea865baeE23b9C2E327696A3bf", 6),
        new("QQQB", "QQQB - Invesco QQQ", "0x205812CdBed920aFf76C6580abD681a46D11efc7", 18),
        new("NVDAB", "NVDAB - NVIDIA", "0x02Fca66C1D1aFB4E2A7884261eB00F63598a7436", 18)
    ];

    // Service chỉ thay cả mảng sau khi Update xong, nên menu không đọc phải dữ liệu dở dang.
    public static IReadOnlyList<FlapPaymentToken> FlapBscPaymentTokens =>
        Volatile.Read(ref flapBscPaymentTokens);

    public static void ReplaceFlapBscPaymentTokens(IEnumerable<FlapPaymentToken> paymentTokens)
    {
        FlapPaymentToken[] updated = paymentTokens
            .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (updated.Length > 0)
        {
            Interlocked.Exchange(ref flapBscPaymentTokens, updated);
        }
    }

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

        if (launchpad?.Equals("long", StringComparison.OrdinalIgnoreCase) == true)
        {
            return FindLongAnchor(anchor) != null;
        }

        return !IsFlapBsc(chain, launchpad) || string.IsNullOrWhiteSpace(anchor)
            || FindFlapBscPaymentToken(anchor) != null;
    }

    public static LaunchpadAnchor? FindLongAnchor(string? code)
    {
        return LongAnchors.FirstOrDefault(item =>
            string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsFlapBsc(string? chain, string? launchpad)
    {
        return chain?.Equals("bsc", StringComparison.OrdinalIgnoreCase) == true
            && launchpad?.Equals("flap", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static FlapPaymentToken? FindFlapBscPaymentToken(string? code)
    {
        string normalized = string.IsNullOrWhiteSpace(code) ? "BNB" : code;
        return FlapBscPaymentTokens.FirstOrDefault(item =>
            string.Equals(item.Code, normalized, StringComparison.OrdinalIgnoreCase));
    }

    // Chuẩn hóa tùy chọn phụ trước khi lưu DB. Flap-BNB giữ null để tương thích dữ liệu cũ.
    public static string? NormalizeRouteOption(string? chain, string? launchpad, string? option)
    {
        if (launchpad?.Equals("long", StringComparison.OrdinalIgnoreCase) == true)
        {
            return FindLongAnchor(option)?.Code;
        }
        if (IsFlapBsc(chain, launchpad))
        {
            FlapPaymentToken? paymentToken = FindFlapBscPaymentToken(option);
            return paymentToken?.Code == "BNB" ? null : paymentToken?.Code;
        }
        return null;
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

public sealed record FlapPaymentToken(string Code, string DisplayName, string TokenAddress, int Decimals);
