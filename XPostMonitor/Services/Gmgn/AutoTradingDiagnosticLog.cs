namespace XPostMonitor.Services.Gmgn;

public static class AutoTradingDiagnosticLog
{
    private static readonly SemaphoreSlim FileLock = new SemaphoreSlim(1, 1);
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Logs", "auto-trading.log");

    public static async Task WriteAsync(string message)
    {
        await FileLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " | " + message
                + Environment.NewLine;
            await File.AppendAllTextAsync(FilePath, line);
        }
        catch (IOException)
        {
            // Diagnostic logging must never interrupt trading.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostic logging must never interrupt trading.
        }
        finally
        {
            FileLock.Release();
        }
    }
}
