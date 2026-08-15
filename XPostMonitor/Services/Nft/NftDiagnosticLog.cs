namespace XPostMonitor.Services.Nft;

// Lưu lỗi NFT riêng để dễ kiểm tra mà không làm ảnh hưởng luồng mint.
public static class NftDiagnosticLog
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Logs", "nft.log");

    public static async Task WriteAsync(string message)
    {
        await FileLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
                + " | " + message + Environment.NewLine;
            await File.AppendAllTextAsync(FilePath, line);
        }
        catch (IOException)
        {
            // Lỗi ghi log không được phép làm dừng chức năng NFT.
        }
        catch (UnauthorizedAccessException)
        {
            // Lỗi ghi log không được phép làm dừng chức năng NFT.
        }
        finally
        {
            FileLock.Release();
        }
    }
}
