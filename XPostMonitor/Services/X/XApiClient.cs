using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.X;

public sealed class XApiClient
{
    public const string AvatarEventType = "profile.update.profile_picture";

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

    // Đọc một Post khi Premium user gửi link X để tạo token thủ công.
    public async Task<XStreamPostResponse> GetPostAsync(string postId, CancellationToken cancellationToken)
    {
        string url = "2/tweets/" + Uri.EscapeDataString(postId)
            + "?tweet.fields=created_at,lang,referenced_tweets,author_id,in_reply_to_user_id,attachments"
            + "&expansions=author_id,in_reply_to_user_id,referenced_tweets.id,referenced_tweets.id.author_id,attachments.media_keys,referenced_tweets.id.attachments.media_keys"
            + "&user.fields=name,username,profile_image_url,public_metrics"
            + "&media.fields=media_key,type,url,preview_image_url";

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        XStreamPostResponse? result = await response.Content
            .ReadFromJsonAsync<XStreamPostResponse>(cancellationToken);
        return result?.Data == null
            ? throw new HttpRequestException("X API did not return the Post.")
            : result;
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
        string url = "2/tweets/search/stream"
            + "?tweet.fields=created_at,lang,referenced_tweets,author_id,in_reply_to_user_id,attachments"
            + "&expansions=author_id,in_reply_to_user_id,referenced_tweets.id,referenced_tweets.id.author_id,attachments.media_keys,referenced_tweets.id.attachments.media_keys"
            + "&user.fields=name,username,profile_image_url,public_metrics"
            + "&media.fields=media_key,type,url,preview_image_url";

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
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

    public async Task TerminateFilteredStreamConnectionsAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete,
            "2/connections/filtered_stream");
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    // Đăng ký nhận sự kiện đổi avatar của một X account.
    public async Task<XActivitySubscription> CreateAvatarSubscriptionAsync(string xUserId, string tag, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "2/activity/subscriptions");
        request.Content = JsonContent.Create(new
        {
            event_type = AvatarEventType,
            filter = new { user_id = xUserId },
            tag
        });

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        XActivityCreateResponse? result = await response.Content.ReadFromJsonAsync<XActivityCreateResponse>(cancellationToken);
        return result?.Data?.Subscription ?? throw new HttpRequestException("X API did not return an activity subscription.");
    }

    public async Task<List<XActivitySubscription>> GetActivitySubscriptionsAsync(
        CancellationToken cancellationToken)
    {
        List<XActivitySubscription> subscriptions = [];
        string? nextToken = null;

        do
        {
            string url = "2/activity/subscriptions?max_results=100"
                + (nextToken == null ? string.Empty : "&pagination_token=" + Uri.EscapeDataString(nextToken));
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            XActivityListResponse? result = await response.Content
                .ReadFromJsonAsync<XActivityListResponse>(cancellationToken);
            if (result == null)
            {
                throw new HttpRequestException("X API did not return the activity subscription list.");
            }

            subscriptions.AddRange(result.Data);
            nextToken = result.Meta.NextToken;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return subscriptions;
    }

    // Xóa Activity subscription theo ID mà X đã trả về khi tạo.
    public async Task DeleteActivitySubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, "2/activity/subscriptions/" + subscriptionId);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    // Mở kết nối lâu dài để nhận các Activity event từ X.
    public async Task<HttpResponseMessage> OpenActivityStreamAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "2/activity/stream");
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
        string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || detail.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
        {
            Match title = Regex.Match(detail, "<title>(.*?)</title>", RegexOptions.IgnoreCase);
            detail = title.Success ? title.Groups[1].Value.Trim() : "HTML error page";
        }
        else
        {
            detail = detail.Trim();
            detail = detail.Length <= 500 ? detail : detail[..500];
        }

        throw new HttpRequestException("X API HTTP " + (int)response.StatusCode + " "
            + response.ReasonPhrase + ": " + detail, null, response.StatusCode);
    }
}
