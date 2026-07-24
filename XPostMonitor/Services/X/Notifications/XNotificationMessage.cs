using System.Globalization;
using System.Net;
using System.Text;
using XPostMonitor.Dtos;
using XPostMonitor.Services.Telegram.Localization;

namespace XPostMonitor.Services.X.Notifications;

// Tạo thẻ thông báo Telegram từ Post và dữ liệu mở rộng của X.
public static class XNotificationMessage
{
    public static XNotificationContent Create(string fallbackUsername, XStreamPostResponse response,
        BotTextService textService, string language)
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
        AppendAuthor(text, displayName, username, author?.PublicMetrics?.FollowersCount, post.CreatedAt,
            textService, language);
        AppendActivity(text, reference?.Type, originalAuthor?.Username, textService, language);

        if (reference?.Type != "retweeted" || originalPost == null)
        {
            text.Append(WebUtility.HtmlEncode(Trim(post.Text, 300)));
        }

        if (originalPost != null)
        {
            AppendOriginalPost(text, originalPost, originalAuthor, textService, language);
        }

        string postUrl = "https://x.com/" + username + "/status/" + post.Id;
        string? photoUrl = FindPhotoUrl(post, includes) ?? FindPhotoUrl(originalPost, includes);
        return new XNotificationContent(text.ToString(), postUrl, photoUrl);
    }

    private static void AppendAuthor(StringBuilder text, string displayName, string username, long? followers,
        DateTimeOffset? createdAt, BotTextService textService, string language)
    {
        string profileUrl = "https://x.com/" + username;
        text.Append("<b>").Append(WebUtility.HtmlEncode(displayName)).Append("</b> | ")
            .Append("<a href=\"").Append(profileUrl).Append("\">@")
            .Append(WebUtility.HtmlEncode(username)).Append("</a>\n");

        if (followers.HasValue)
        {
            text.Append(FormatFollowers(followers.Value)).Append(' ')
                .Append(textService.Get(language, "Followers")).Append(" · ");
        }

        text.Append(FormatTime(createdAt, textService, language)).Append("\n\n");
    }

    private static void AppendActivity(StringBuilder text, string? type, string? originalUsername,
        BotTextService textService, string language)
    {
        if (type == "replied_to")
        {
            text.Append("<b>").Append(textService.Get(language, "ReplyingTo",
                    WebUtility.HtmlEncode(originalUsername ?? textService.Get(language, "Unknown"))))
                .Append("</b>\n\n");
            return;
        }

        string title = type switch
        {
            "retweeted" => textService.Get(language, "Reposted"),
            "quoted" => textService.Get(language, "QuotedPost"),
            _ => textService.Get(language, "NewPost")
        };

        text.Append("<b>").Append(title).Append("</b>\n\n");
    }

    private static void AppendOriginalPost(StringBuilder text, XPost originalPost, XUser? originalAuthor,
        BotTextService textService, string language)
    {
        string username = originalAuthor?.Username ?? textService.Get(language, "Unknown");
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

    private static string FormatTime(DateTimeOffset? createdAt, BotTextService textService, string language)
    {
        if (!createdAt.HasValue)
        {
            return textService.Get(language, "JustNow");
        }

        TimeSpan age = DateTimeOffset.UtcNow - createdAt.Value;
        if (age.TotalMinutes < 1)
        {
            return textService.Get(language, "JustNow");
        }

        if (age.TotalHours < 1)
        {
            return textService.Get(language, "MinutesAgo", (int)age.TotalMinutes);
        }

        if (age.TotalDays < 1)
        {
            return textService.Get(language, "HoursAgo", (int)age.TotalHours);
        }

        return textService.Get(language, "DaysAgo", (int)age.TotalDays);
    }

    private static string Trim(string text, int maximumLength)
    {
        return text.Length <= maximumLength ? text : text[..maximumLength] + "...";
    }
}

public sealed record XNotificationContent(string Text, string PostUrl, string? PhotoUrl);
