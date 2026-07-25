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

    [JsonPropertyName("profile_image_url")]
    public string? ProfileImageUrl { get; set; }

    [JsonPropertyName("public_metrics")]
    public XUserMetrics? PublicMetrics { get; set; }
}

public sealed class XUserMetrics
{
    [JsonPropertyName("followers_count")]
    public long FollowersCount { get; set; }
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

    [JsonPropertyName("includes")]
    public XStreamIncludes? Includes { get; set; }
}

public sealed class XStreamIncludes
{
    [JsonPropertyName("tweets")]
    public List<XPost>? Posts { get; set; }

    [JsonPropertyName("users")]
    public List<XUser>? Users { get; set; }

    [JsonPropertyName("media")]
    public List<XMedia>? Media { get; set; }
}

public sealed class XPost
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("lang")]
    public string? Language { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("author_id")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("in_reply_to_user_id")]
    public string? InReplyToUserId { get; set; }

    [JsonPropertyName("referenced_tweets")]
    public List<XReferencedPost>? ReferencedPosts { get; set; }

    [JsonPropertyName("attachments")]
    public XAttachments? Attachments { get; set; }
}

public sealed class XAttachments
{
    [JsonPropertyName("media_keys")]
    public List<string>? MediaKeys { get; set; }
}

public sealed class XMedia
{
    [JsonPropertyName("media_key")]
    public string MediaKey { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("preview_image_url")]
    public string? PreviewImageUrl { get; set; }
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

public sealed class XActivityCreateResponse
{
    [JsonPropertyName("data")]
    public XActivityCreateData? Data { get; set; }
}

public sealed class XActivityCreateData
{
    [JsonPropertyName("subscription")]
    public XActivitySubscription? Subscription { get; set; }
}

public sealed class XActivityListResponse
{
    [JsonPropertyName("data")]
    public List<XActivitySubscription> Data { get; set; } = [];

    [JsonPropertyName("meta")]
    public XActivityListMeta Meta { get; set; } = new XActivityListMeta();
}

public sealed class XActivityListMeta
{
    [JsonPropertyName("next_token")]
    public string? NextToken { get; set; }
}

public sealed class XActivitySubscription
{
    [JsonPropertyName("subscription_id")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("filter")]
    public XActivityFilter Filter { get; set; } = new XActivityFilter();

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
}

public sealed class XActivityFilter
{
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = string.Empty;
}

public sealed class XActivityStreamResponse
{
    [JsonPropertyName("data")]
    public XActivityEvent? Data { get; set; }
}

public sealed class XActivityEvent
{
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("filter")]
    public XActivityFilter Filter { get; set; } = new XActivityFilter();

    [JsonPropertyName("payload")]
    public XActivityPayload? Payload { get; set; }
}

public sealed class XActivityPayload
{
    [JsonPropertyName("before")]
    public string? Before { get; set; }

    [JsonPropertyName("after")]
    public string? After { get; set; }
}
