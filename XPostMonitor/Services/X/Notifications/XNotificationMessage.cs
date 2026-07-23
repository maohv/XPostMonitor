using XPostMonitor.Dtos;

namespace XPostMonitor.Services.X.Notifications;

// Nhận một Post từ X và tạo nội dung thông báo dễ đọc cho Telegram.
public static class XNotificationMessage
{
    // Tạo tiêu đề đúng cho Post, Reply, Repost hoặc Quote rồi gắn link bài viết.
    public static string Create(string username, XPost post)
    {
        string activityName = GetActivityName(post);
        string header = activityName + " from @" + username + ":\n\n";
        string link = "https://x.com/" + username + "/status/" + post.Id;

        // Telegram chỉ cho phép tối đa 4096 ký tự trong một tin nhắn.
        int maximumTextLength = 4096 - header.Length - link.Length - 2;
        string text = post.Text.Length <= maximumTextLength
            ? post.Text
            : post.Text[..maximumTextLength];

        return header + text + "\n\n" + link;
    }

    // X cho biết loại hoạt động trong referenced_tweets.
    private static string GetActivityName(XPost post)
    {
        if (HasReference(post, "retweeted"))
        {
            return "New repost";
        }

        if (HasReference(post, "replied_to"))
        {
            return "New reply";
        }

        if (HasReference(post, "quoted"))
        {
            return "New quote post";
        }

        return "New post";
    }

    // Kiểm tra Post có tham chiếu thuộc loại cần tìm hay không.
    private static bool HasReference(XPost post, string type)
    {
        return post.ReferencedPosts?.Any(item => item.Type == type) == true;
    }
}
