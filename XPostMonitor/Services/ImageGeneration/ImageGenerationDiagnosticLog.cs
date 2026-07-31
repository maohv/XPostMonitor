namespace XPostMonitor.Services.ImageGeneration;

// Lưu đúng prompt cuối cùng gửi sang model ảnh để dễ kiểm tra khi ảnh tạo ra không sát nội dung.
public static class ImageGenerationDiagnosticLog
{
    private static readonly SemaphoreSlim FileLock = new SemaphoreSlim(1, 1);

    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Logs", "image-generation.log");

    public static async Task WriteAsync(string provider, string model, string prompt)
    {
        await FileLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string entry = "============================================================"
                + Environment.NewLine
                + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
                + " | Provider: " + provider
                + " | Model: " + model
                + Environment.NewLine
                + "PROMPT:"
                + Environment.NewLine
                + prompt
                + Environment.NewLine;
            await File.AppendAllTextAsync(FilePath, entry);
        }
        catch (IOException)
        {
            // Lỗi ghi log không được làm hỏng luồng tạo ảnh.
        }
        catch (UnauthorizedAccessException)
        {
            // Lỗi ghi log không được làm hỏng luồng tạo ảnh.
        }
        finally
        {
            FileLock.Release();
        }
    }
}
