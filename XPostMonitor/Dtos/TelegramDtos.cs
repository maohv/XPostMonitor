using System.Text.Json.Serialization;

namespace XPostMonitor.Dtos;

// Các DTO này mô tả JSON mà Telegram API gửi về.
public class TelegramBasicResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class TelegramUpdatesResponse : TelegramBasicResponse
{
    [JsonPropertyName("result")]
    public List<TelegramUpdate>? Result { get; set; }
}

public sealed class TelegramChatMemberResponse : TelegramBasicResponse
{
    [JsonPropertyName("result")]
    public TelegramChatMember? Result { get; set; }
}

public sealed class TelegramFileResponse : TelegramBasicResponse
{
    [JsonPropertyName("result")]
    public TelegramFile? Result { get; set; }
}

public sealed class TelegramFile
{
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;
}

public sealed class TelegramChatMember
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; set; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; set; }

    [JsonPropertyName("callback_query")]
    public TelegramCallbackQuery? CallbackQuery { get; set; }
}

public sealed class TelegramMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("chat")]
    public TelegramChat Chat { get; set; } = new TelegramChat();

    [JsonPropertyName("from")]
    public TelegramFrom? From { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("photo")]
    public List<TelegramPhotoSize>? Photo { get; set; }
}

public sealed class TelegramPhotoSize
{
    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; set; }
}

public sealed class TelegramCallbackQuery
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public TelegramFrom From { get; set; } = new TelegramFrom();

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

public sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public sealed class TelegramFrom
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }
}

public sealed class TelegramInlineButton
{
    public TelegramInlineButton(string text, string callbackData)
    {
        Text = text;
        CallbackData = callbackData;
    }

    [JsonPropertyName("text")]
    public string Text { get; }

    [JsonPropertyName("callback_data")]
    public string CallbackData { get; }
}
