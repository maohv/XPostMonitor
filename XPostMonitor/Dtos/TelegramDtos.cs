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
}

public sealed class TelegramMessage
{
    [JsonPropertyName("chat")]
    public TelegramChat Chat { get; set; } = new TelegramChat();

    [JsonPropertyName("from")]
    public TelegramFrom? From { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
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
}
