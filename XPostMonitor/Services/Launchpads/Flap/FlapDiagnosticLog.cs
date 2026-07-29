using System.Collections;
using System.Text;
using System.Text.Json;

namespace XPostMonitor.Services.Launchpads.Flap;

public static class FlapDiagnosticLog
{
    private static readonly SemaphoreSlim FileLock = new SemaphoreSlim(1, 1);
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Logs", "flap.log");

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
            // Logging must never stop a Flap transaction.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must never stop a Flap transaction.
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static string Describe(Exception exception)
    {
        StringBuilder result = new StringBuilder(exception.ToString());
        AppendRpcError(result, exception);

        foreach (DictionaryEntry item in exception.Data)
        {
            result.Append(" | ExceptionData[").Append(item.Key).Append("]=").Append(item.Value);
        }
        return result.ToString();
    }

    private static void AppendRpcError(StringBuilder result, Exception exception)
    {
        object? rpcError = exception.GetType().GetProperty("RpcError")?.GetValue(exception);
        if (rpcError == null)
        {
            return;
        }

        result.Append(" | RpcError.Code=").Append(GetProperty(rpcError, "Code"));
        result.Append(" | RpcError.Message=").Append(GetProperty(rpcError, "Message"));
        result.Append(" | RpcError.Data=").Append(ToJson(GetProperty(rpcError, "Data")));
    }

    private static object? GetProperty(object value, string propertyName)
    {
        return value.GetType().GetProperty(propertyName)?.GetValue(value);
    }

    private static string ToJson(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString() ?? "null";
        }
    }
}
