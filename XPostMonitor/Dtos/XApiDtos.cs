using System.Text.Json.Serialization;

namespace XPostMonitor.Dtos;

// DTO mô tả dữ liệu X API trả về; đây không phải bảng trong database.
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

public sealed class XStreamRulesResponse
{
    [JsonPropertyName("data")]
    public List<XStreamRule>? Data { get; set; }
}

public sealed class XStreamRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
}

public sealed class XStreamRuleDefinition
{
    public XStreamRuleDefinition(string value, string tag)
    {
        Value = value;
        Tag = tag;
    }

    [JsonPropertyName("value")]
    public string Value { get; }

    [JsonPropertyName("tag")]
    public string Tag { get; }
}

public sealed class XStreamPostResponse
{
    [JsonPropertyName("data")]
    public XPost? Data { get; set; }

    [JsonPropertyName("matching_rules")]
    public List<XStreamMatch>? MatchingRules { get; set; }
}

public sealed class XPost
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("referenced_tweets")]
    public List<XReferencedPost>? ReferencedPosts { get; set; }
}

public sealed class XReferencedPost
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed class XStreamMatch
{
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
}
