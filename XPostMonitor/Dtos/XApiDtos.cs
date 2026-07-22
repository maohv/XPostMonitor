using System.Text.Json.Serialization;

namespace XPostMonitor.Dtos;

// DTO la du lieu X API tra ve. DTO khong phai bang trong database.
public sealed class XUserResponse
{
    [JsonPropertyName("data")]
    public XUser? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<XApiError>? Errors { get; set; }
}

public sealed class XApiError
{
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

public sealed class XUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("protected")]
    public bool Protected { get; set; }
}
