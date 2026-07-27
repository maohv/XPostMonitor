using System.Text;
using XPostMonitor.Dtos;

namespace XPostMonitor.Services.Tokens;

// Chuẩn bị đầy đủ câu chuyện của Post để AI chọn đúng tên và symbol.
public static class TokenPostContext
{
    private static readonly HashSet<string> RepliesWithoutStory =
    [
        "yes", "yeah", "yep", "yup", "no", "nope", "ok", "okay",
        "lol", "lmao", "rofl", "haha", "hahaha",
        "thanks", "thank you", "thank u", "thx", "ty",
        "nice", "good", "great", "cool", "wow",
        "true", "so true", "exactly", "agree", "agreed", "same", "this", "based", "love it",
        "gm", "gn",
        "cảm ơn", "cam on", "đúng", "dung", "chuẩn", "chuan", "ừ", "ừm",
        "哈哈", "哈哈哈", "呵呵", "谢谢", "謝謝", "好的", "好", "是", "对", "對"
    ];

    // Reply có ảnh riêng vẫn đáng phân tích vì câu chuyện có thể nằm trong ảnh.
    public static bool IsMeaningfulReply(XStreamPostResponse response, string? ownPhotoUrl)
    {
        XPost post = response.Data!;
        string? referenceType = post.ReferencedPosts?.FirstOrDefault()?.Type;
        if (referenceType != "replied_to" || !string.IsNullOrWhiteSpace(ownPhotoUrl))
        {
            return true;
        }

        string textWithoutMentionsAndLinks = string.Join(' ', post.Text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith('@')
                && !part.StartsWith("http", StringComparison.OrdinalIgnoreCase)));

        string normalized = new string(textWithoutMentionsAndLinks
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
            .ToArray());
        normalized = string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        int meaningfulCharacters = normalized.Count(char.IsLetterOrDigit);
        return meaningfulCharacters >= 2 && !RepliesWithoutStory.Contains(normalized);
    }

    // Reply rõ nghĩa tự làm hook; chỉ Quote mới cần thêm Post gốc.
    public static string BuildAiInput(XStreamPostResponse response)
    {
        XPost post = response.Data!;
        XReferencedPost? reference = post.ReferencedPosts?.FirstOrDefault();
        if (reference?.Type == "replied_to")
        {
            return "[POST_TYPE=reply]\n[REPLY]\nText: " + post.Text.Trim();
        }

        if (reference?.Type != "quoted")
        {
            return post.Text;
        }

        XStreamIncludes includes = response.Includes ?? new XStreamIncludes();
        XPost? originalPost = includes.Posts?.FirstOrDefault(item => item.Id == reference.Id);

        StringBuilder input = new StringBuilder()
            .AppendLine("[POST_TYPE=quote]");

        if (originalPost != null)
        {
            AppendPost(input, "ORIGINAL_POST", originalPost, includes);
        }

        AppendPost(input, "QUOTE_POST", post, includes);
        return input.ToString().Trim();
    }

    private static void AppendPost(StringBuilder input, string marker, XPost post, XStreamIncludes includes)
    {
        string? username = includes.Users?.FirstOrDefault(user => user.Id == post.AuthorId)?.Username;

        input.Append('[').Append(marker).AppendLine("]");
        if (!string.IsNullOrWhiteSpace(username))
        {
            input.Append("Author: @").AppendLine(username);
        }

        input.Append("Text: ").AppendLine(post.Text.Trim());
    }
}
