using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace XPostMonitor.Services.Nft;

// Đọc toàn bộ NFT của một ví từ explorer Robinhood Chain, không cần API key.
public sealed class NftPortfolioClient
{
    private readonly HttpClient httpClient;

    public NftPortfolioClient(HttpClient httpClient) => this.httpClient = httpClient;

    public async Task<IReadOnlyList<NftCollectionBalance>> GetCollectionsAsync(string wallet,
        CancellationToken cancellationToken)
    {
        List<NftCollectionBalance> result = [];
        string url = $"api/v2/addresses/{Uri.EscapeDataString(wallet)}/nft/collections"
            + "?type=ERC-721%2CERC-1155";
        while (true)
        {
            NftCollectionsResponse response = await ReadPageAsync(url, cancellationToken);
            result.AddRange(response.Items.Select(item => new NftCollectionBalance(
                string.IsNullOrWhiteSpace(item.Token.Name) ? item.Token.Symbol ?? item.Token.Address : item.Token.Name,
                item.Amount)));
            if (response.NextPage == null) break;
            url = $"api/v2/addresses/{Uri.EscapeDataString(wallet)}/nft/collections"
                + "?type=ERC-721%2CERC-1155"
                + "&token_contract_address_hash=" + Uri.EscapeDataString(response.NextPage.Address)
                + "&token_type=" + Uri.EscapeDataString(response.NextPage.TokenType);
        }
        return result;
    }

    // Blockscout đôi lúc trả lỗi 5xx tạm thời; thử lại một lần trước khi báo lỗi ví đó.
    private async Task<NftCollectionsResponse> ReadPageAsync(string url,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<NftCollectionsResponse>(
                    cancellationToken) ?? throw new InvalidOperationException(
                    "Explorer không trả dữ liệu NFT.");

            if ((int)response.StatusCode < 500 || attempt == 2)
                response.EnsureSuccessStatusCode();
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException("Explorer không trả dữ liệu NFT.");
    }

    private sealed class NftCollectionsResponse
    {
        [JsonPropertyName("items")] public List<NftCollectionItem> Items { get; set; } = [];
        [JsonPropertyName("next_page_params")] public NftNextPage? NextPage { get; set; }
    }

    private sealed class NftCollectionItem
    {
        [JsonPropertyName("amount")] public string Amount { get; set; } = "0";
        [JsonPropertyName("token")] public NftToken Token { get; set; } = new();
    }

    private sealed class NftToken
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        [JsonPropertyName("address_hash")] public string Address { get; set; } = string.Empty;
    }

    private sealed class NftNextPage
    {
        [JsonPropertyName("token_contract_address_hash")] public string Address { get; set; } = string.Empty;
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = string.Empty;
    }
}

public sealed record NftCollectionBalance(string Name, string Quantity);
