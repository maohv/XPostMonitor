namespace XPostMonitor.Services.X
{
    // Chỉ lấy Post ID từ link HTTPS chính chủ X/Twitter, không mở URL do user gửi.
    public static class XPostLinkParser
    {
        public static string? ParsePostId(string? value)
        {
            if (!TryParseXUri(value, out Uri uri))
            {
                return null;
            }

            string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int index = 1; index < segments.Length - 1; index++)
            {
                if (segments[index] == "status" && IsPostId(segments[index + 1]))
                {
                    return segments[index + 1];
                }
            }

            return null;
        }

        // Chấp nhận cả link bài đăng và link tài khoản X khi user đã tự nhập tên, mã và ảnh token.
        public static string? ParseXUrl(string? value)
        {
            return TryParseXUri(value, out Uri uri) ? uri.AbsoluteUri : null;
        }

        public static bool IsPostId(string value)
        {
            return value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);
        }

        private static bool TryParseXUri(string? value, out Uri uri)
        {
            if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? parsedUri)
                && parsedUri.Scheme == Uri.UriSchemeHttps
                && IsXHost(parsedUri.Host))
            {
                uri = parsedUri;
                return true;
            }

            uri = null!;
            return false;
        }

        private static bool IsXHost(string host)
        {
            return host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("www.x.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("mobile.x.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("www.twitter.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("mobile.twitter.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
