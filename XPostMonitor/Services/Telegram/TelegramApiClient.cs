using System.Net.Http.Headers;
using System.Net.Http.Json;
using XPostMonitor.Configuration;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.Telegram;

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

    public async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(GetUrl("getMe"), cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
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
            allowed_updates = new[] { "message", "callback_query" }
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

    // Gửi tin nhắn kèm các nút để người dùng không phải nhớ lệnh dài.
    public async Task SendButtonsAsync(long chatId, string text,
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> buttons, CancellationToken cancellationToken,
        bool useHtml = false)
    {
        object request = useHtml
            ? new { chat_id = chatId, text, parse_mode = "HTML", reply_markup = new { inline_keyboard = buttons } }
            : new { chat_id = chatId, text, reply_markup = new { inline_keyboard = buttons } };

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("sendMessage"), request, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    // Báo Telegram rằng bot đã nhận lần bấm nút để ngừng biểu tượng loading.
    public async Task AnswerCallbackAsync(string callbackId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("answerCallbackQuery"),
            new { callback_query_id = callbackId }, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    // Xóa ngay tin nhắn chứa API key hoặc private key sau khi đã đọc.
    public async Task DeleteMessageAsync(long chatId, long messageId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("deleteMessage"),
            new { chat_id = chatId, message_id = messageId }, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    // Tải ảnh user vừa gửi cho bot để dùng nguyên ảnh đó làm avatar token.
    public async Task<byte[]> DownloadPhotoAsync(string fileId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage fileResponse = await httpClient.PostAsJsonAsync(GetUrl("getFile"),
            new { file_id = fileId }, cancellationToken);
        TelegramFileResponse? file = await fileResponse.Content
            .ReadFromJsonAsync<TelegramFileResponse>(cancellationToken);
        CheckResponse(fileResponse, file);

        string filePath = file!.Result?.FilePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Telegram did not return the image path.");
        }

        string url = "https://api.telegram.org/file/bot" + telegramToken + "/" + filePath;
        return await httpClient.GetByteArrayAsync(url, cancellationToken);
    }

    public async Task SendPhotoAsync(long chatId, byte[] photo, string caption, CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form = new MultipartFormDataContent();
        using ByteArrayContent photoContent = new ByteArrayContent(photo);
        bool isJpeg = photo.Length >= 2 && photo[0] == 0xFF && photo[1] == 0xD8;
        photoContent.Headers.ContentType = new MediaTypeHeaderValue(isJpeg ? "image/jpeg" : "image/png");

        form.Add(new StringContent(chatId.ToString()), "chat_id");
        form.Add(new StringContent(caption), "caption");
        form.Add(photoContent, "photo", isJpeg ? "token.jpg" : "token.png");

        using HttpResponseMessage response = await httpClient.PostAsync(GetUrl("sendPhoto"), form, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    // Gửi ảnh trực tiếp từ URL, dùng cho avatar gốc của X.
    public async Task SendPhotoAsync(long chatId, string photoUrl, string caption, CancellationToken cancellationToken)
    {
        var request = new
        {
            chat_id = chatId,
            photo = photoUrl,
            caption
        };

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("sendPhoto"), request, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    // Gửi Post có HTML, ảnh và nút mở bài viết trên X.
    public async Task SendRichMessageAsync(long chatId, string html, string? photoUrl, string buttonUrl,
        string buttonText, CancellationToken cancellationToken)
    {
        var replyMarkup = new
        {
            inline_keyboard = new[]
            {
                new[] { new { text = buttonText, url = buttonUrl } }
            }
        };

        if (!string.IsNullOrWhiteSpace(photoUrl))
        {
            var photoRequest = new
            {
                chat_id = chatId,
                photo = photoUrl,
                caption = html,
                parse_mode = "HTML",
                show_caption_above_media = true,
                reply_markup = replyMarkup
            };

            using HttpResponseMessage photoResponse = await httpClient.PostAsJsonAsync(GetUrl("sendPhoto"), photoRequest, cancellationToken);
            await CheckResponseAsync(photoResponse, cancellationToken);
            return;
        }

        var textRequest = new
        {
            chat_id = chatId,
            text = html,
            parse_mode = "HTML",
            disable_web_page_preview = true,
            reply_markup = replyMarkup
        };

        using HttpResponseMessage textResponse = await httpClient.PostAsJsonAsync(GetUrl("sendMessage"), textRequest, cancellationToken);
        await CheckResponseAsync(textResponse, cancellationToken);
    }

    // Kiểm tra người gửi lệnh có phải chủ hoặc quản trị viên của Channel hay không.
    public async Task<bool> IsChannelAdminAsync(long channelId, long userId, CancellationToken cancellationToken)
    {
        var request = new
        {
            chat_id = channelId,
            user_id = userId
        };

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(GetUrl("getChatMember"), request, cancellationToken);
        TelegramChatMemberResponse? result = await response.Content.ReadFromJsonAsync<TelegramChatMemberResponse>(cancellationToken);

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
