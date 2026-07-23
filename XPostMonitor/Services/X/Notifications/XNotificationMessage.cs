using System.Globalization;
using System.Net;
using System.Text;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.X.Notifications;

// Tạo thẻ thông báo Telegram từ Post và dữ liệu mở rộng của X.
public static class XNotificationMessage
{
    public static XNotificationContent Create(string fallbackUsername, XStreamPostResponse response)
    {
        XPost post = response.Data!;
        XStreamIncludes includes = response.Includes ?? new XStreamIncludes();
        XUser? author = FindUser(includes, post.AuthorId);
        string username = author?.Username ?? fallbackUsername;
        string displayName = author?.Name ?? username;

        XReferencedPost? reference = post.ReferencedPosts?.FirstOrDefault();
        XPost? originalPost = includes.Posts?.FirstOrDefault(item => item.Id == reference?.Id);
        XUser? originalAuthor = FindUser(includes, originalPost?.AuthorId ?? post.InReplyToUserId);

        StringBuilder text = new StringBuilder();
        AppendAuthor(text, displayName, username, author?.PublicMetrics?.FollowersCount, post.CreatedAt);
        AppendActivity(text, reference?.Type, originalAuthor?.Username);

        if (reference?.Type != "retweeted" || originalPost == null)
        {
            text.Append(WebUtility.HtmlEncode(Trim(post.Text, 300)));
        }

        if (originalPost != null)
        {
            AppendOriginalPost(text, originalPost, originalAuthor);
        }

        string postUrl = "https://x.com/" + username + "/status/" + post.Id;
        string? photoUrl = FindPhotoUrl(post, includes) ?? FindPhotoUrl(originalPost, includes);
        return new XNotificationContent(text.ToString(), postUrl, photoUrl);
    }

    private static void AppendAuthor(StringBuilder text, string displayName, string username, long? followers, DateTimeOffset? createdAt)
    {
        string profileUrl = "https://x.com/" + username;
        text.Append("<b>").Append(WebUtility.HtmlEncode(displayName)).Append("</b> | ")
            .Append("<a href=\"").Append(profileUrl).Append("\">@")
            .Append(WebUtility.HtmlEncode(username)).Append("</a>\n");

        if (followers.HasValue)
        {
            text.Append(FormatFollowers(followers.Value)).Append(" followers · ");
        }

        text.Append(FormatTime(createdAt)).Append("\n\n");
    }

    private static void AppendActivity(StringBuilder text, string? type, string? originalUsername)
    {
        if (type == "replied_to")
        {
            text.Append("<b>Replying to @")
                .Append(WebUtility.HtmlEncode(originalUsername ?? "unknown"))
                .Append("</b>\n\n");
            return;
        }

        string title = type switch
        {
            "retweeted" => "Reposted",
            "quoted" => "Quoted post",
            _ => "New post"
        };

        text.Append("<b>").Append(title).Append("</b>\n\n");
    }

    private static void AppendOriginalPost(StringBuilder text, XPost originalPost, XUser? originalAuthor)
    {
        string username = originalAuthor?.Username ?? "unknown";
        string displayName = originalAuthor?.Name ?? username;

        text.Append("\n\n<blockquote><b>")
            .Append(WebUtility.HtmlEncode(displayName)).Append(" | @")
            .Append(WebUtility.HtmlEncode(username)).Append("</b>\n")
            .Append(WebUtility.HtmlEncode(Trim(originalPost.Text, 300)))
            .Append("</blockquote>");
    }

    private static XUser? FindUser(XStreamIncludes includes, string? userId)
    {
        return includes.Users?.FirstOrDefault(user => user.Id == userId);
    }

    private static string? FindPhotoUrl(XPost? post, XStreamIncludes includes)
    {
        if (post?.Attachments?.MediaKeys == null || includes.Media == null)
        {
            return null;
        }

        foreach (string mediaKey in post.Attachments.MediaKeys)
        {
            XMedia? media = includes.Media.FirstOrDefault(item => item.MediaKey == mediaKey);
            string? url = media?.Type == "photo" ? media.Url : media?.PreviewImageUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string FormatFollowers(long followers)
    {
        if (followers >= 1_000_000)
        {
            return (followers / 1_000_000D).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (followers >= 1_000)
        {
            return (followers / 1_000D).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        return followers.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatTime(DateTimeOffset? createdAt)
    {
        if (!createdAt.HasValue)
        {
            return "just now";
        }

        TimeSpan age = DateTimeOffset.UtcNow - createdAt.Value;
        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return (int)age.TotalMinutes + "m ago";
        }

        if (age.TotalDays < 1)
        {
            return (int)age.TotalHours + "h ago";
        }

        return (int)age.TotalDays + "d ago";
    }

    private static string Trim(string text, int maximumLength)
    {
        return text.Length <= maximumLength ? text : text[..maximumLength] + "...";
    }
}

public sealed record XNotificationContent(string Text, string PostUrl, string? PhotoUrl);
