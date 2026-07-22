using System.Net.Http.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.Telegram;

// Chỉ phụ trách gửi HTTP request đến Telegram API.
public sealed class TelegramApiClient
{
    private readonly HttpClient httpClient;
    private readonly string telegramToken;

    // Nhận HttpClient và token đã được cấu hình trong Program.cs.
    public TelegramApiClient(HttpClient httpClient, BotOptions options)
    {
        this.httpClient = httpClient;
        telegramToken = options.TelegramToken;
    }

    // Xóa webhook cũ để bot có thể nhận tin nhắn bằng getUpdates.
    public async Task DeleteWebhookAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("deleteWebhook"), new { }, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    // Chờ Telegram gửi về các tin nhắn mới kể từ update ID đã xử lý.
    public async Task<List<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        var request = new
        {
            offset,
            timeout = 30,
            allowed_updates = new[] { "message" }
        };

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("getUpdates"), request, cancellationToken);
        TelegramUpdatesResponse? result = await response.Content.ReadFromJsonAsync<TelegramUpdatesResponse>(cancellationToken);

        CheckResponse(response, result);
        return result!.Result ?? new List<TelegramUpdate>();
    }

    // Gửi một tin nhắn văn bản đến chat Telegram.
    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var request = new
        {
            chat_id = chatId,
            text
        };

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("sendMessage"), request, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    // Kiểm tra người gửi lệnh có phải chủ hoặc quản trị viên của Channel hay không.
    public async Task<bool> IsChannelAdminAsync(long channelId, long userId,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            chat_id = channelId,
            user_id = userId
        };

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            GetUrl("getChatMember"), request, cancellationToken);
        TelegramChatMemberResponse? result = await response.Content
            .ReadFromJsonAsync<TelegramChatMemberResponse>(cancellationToken);

        CheckResponse(response, result);
        return result!.Result?.Status is "creator" or "administrator";
    }

    // Tạo URL đầy đủ cho từng method của Telegram Bot API.
    private string GetUrl(string method)
    {
        return "https://api.telegram.org/bot" + telegramToken + "/" + method;
    }

    // Đọc JSON response rồi chuyển sang hàm kiểm tra chung bên dưới.
    private static async Task CheckResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        TelegramBasicResponse? result = await response.Content.ReadFromJsonAsync<TelegramBasicResponse>(cancellationToken);
        CheckResponse(response, result);
    }

    // Ném lỗi khi Telegram trả HTTP lỗi hoặc trường ok bằng false.
    private static void CheckResponse(HttpResponseMessage response, TelegramBasicResponse? result)
    {
        if (response.IsSuccessStatusCode && result?.Ok == true)
        {
            return;
        }

        string error = result?.Description ?? response.ReasonPhrase ?? "Unknown error";
        throw new HttpRequestException("Telegram API: " + error, null, response.StatusCode);
    }
}
