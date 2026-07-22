using System.Net.Http.Headers;
using System.Net.Http.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.X;

public sealed class XApiClient
{
    private readonly HttpClient httpClient;
    private readonly string bearerToken;

    // Nhận HttpClient và X Bearer Token đã được cấu hình trong Program.cs.
    public XApiClient(HttpClient httpClient, BotOptions options)
    {
        this.httpClient = httpClient;
        bearerToken = options.XBearerToken;
    }

    // Tìm thông tin một tài khoản X theo username.
    public async Task<XUser?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        string url = "2/users/by/username/" + Uri.EscapeDataString(username) + "?user.fields=protected";

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        XUserResponse? result = await response.Content.ReadFromJsonAsync<XUserResponse>(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return result?.Data;
        }

        string errorMessage = response.ReasonPhrase ?? "Unknown error";
        if (result?.Errors != null && result.Errors.Count > 0)
        {
            errorMessage = result.Errors[0].Detail ?? errorMessage;
        }

        throw new HttpRequestException("X API: " + errorMessage, null, response.StatusCode);
    }

    // Lấy toàn bộ Filtered Stream rule hiện có trên X.
    public async Task<List<XStreamRule>> GetStreamRulesAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "2/tweets/search/stream/rules");
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        XStreamRulesResponse? result = await response.Content.ReadFromJsonAsync<XStreamRulesResponse>(cancellationToken);
        return result?.Data ?? new List<XStreamRule>();
    }

    // Thêm các rule mới để X gửi Post của tài khoản đang được theo dõi.
    public async Task AddStreamRulesAsync(List<XStreamRuleDefinition> rules, CancellationToken cancellationToken)
    {
        if (rules.Count == 0)
        {
            return;
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "2/tweets/search/stream/rules");
        request.Content = JsonContent.Create(new { add = rules });
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    // Xóa các rule không còn cần thiết khỏi X.
    public async Task DeleteStreamRulesAsync(List<string> ruleIds, CancellationToken cancellationToken)
    {
        if (ruleIds.Count == 0)
        {
            return;
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "2/tweets/search/stream/rules");
        request.Content = JsonContent.Create(new { delete = new { ids = ruleIds } });
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    // Mở kết nối HTTP lâu dài để nhận Post mới ngay khi X gửi về.
    public async Task<HttpResponseMessage> OpenFilteredStreamAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get,
            "2/tweets/search/stream?tweet.fields=created_at,referenced_tweets");
        HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // Tạo request và gắn Bearer Token vào Authorization header.
    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return request;
    }

    // Giữ nguyên khi request thành công; ném lỗi kèm nội dung X trả về khi thất bại.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException("X API: " + detail, null, response.StatusCode);
    }
}
