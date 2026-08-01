using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace XPostMonitor.Services.BinanceAlpha;

// Chỉ đọc danh sách token Alpha công khai, không cần API key Binance.
public sealed class BinanceAlphaClient
{
    private const string TokenListPath =
        "bapi/defi/v1/public/wallet-direct/buw/wallet/cex/alpha/all/token/list";

    private readonly HttpClient httpClient;

    public BinanceAlphaClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    // Lấy danh sách token hiện đang hoạt động trên Binance Alpha.
    public async Task<List<BinanceAlphaTokenDto>> GetActiveTokensAsync(
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(TokenListPath, cancellationToken);
        response.EnsureSuccessStatusCode();

        BinanceAlphaTokenListResponse? result = await response.Content
            .ReadFromJsonAsync<BinanceAlphaTokenListResponse>(cancellationToken);

        if (result?.Success != true || result.Code != "000000" || result.Data == null)
        {
            throw new HttpRequestException("Binance Alpha Token List returned an invalid response.");
        }

        if (result.Data.Count == 0)
        {
            throw new HttpRequestException("Binance Alpha Token List unexpectedly returned no tokens.");
        }

        return result.Data
            .Where(token => !token.Offline
                && !string.IsNullOrWhiteSpace(token.TokenId)
                && !string.IsNullOrWhiteSpace(token.ChainId)
                && !string.IsNullOrWhiteSpace(token.ContractAddress))
            .ToList();
    }
}

public sealed class BinanceAlphaTokenDto
{
    [JsonPropertyName("tokenId")]
    public string TokenId { get; set; } = string.Empty;

    [JsonPropertyName("chainId")]
    public string ChainId { get; set; } = string.Empty;

    [JsonPropertyName("chainName")]
    public string ChainName { get; set; } = string.Empty;

    [JsonPropertyName("contractAddress")]
    public string ContractAddress { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("alphaId")]
    public string? AlphaId { get; set; }

    [JsonPropertyName("listingTime")]
    public long? ListingTime { get; set; }

    [JsonPropertyName("offline")]
    public bool Offline { get; set; }
}

internal sealed class BinanceAlphaTokenListResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public List<BinanceAlphaTokenDto>? Data { get; set; }
}
