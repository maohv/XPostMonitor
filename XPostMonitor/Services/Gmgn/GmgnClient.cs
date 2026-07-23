using System.Diagnostics;
using System.Text.Json;
using XPostMonitor.Configuration;

namespace XPostMonitor.Services.Gmgn;

// Chạy lệnh GMGN read-only để kiểm tra kết nối.
public sealed class GmgnClient
{
    private readonly GmgnOptions options;

    public GmgnClient(GmgnOptions options)
    {
        this.options = options;
    }

    public async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "GMGN API key is missing from configuration.";
        }

        if (string.IsNullOrWhiteSpace(options.CliScriptPath) || !File.Exists(options.CliScriptPath))
        {
            return "GMGN CLI script was not found. Check Gmgn:CliScriptPath.";
        }

        ProcessStartInfo startInfo = CreateStartInfo();
        using Process process = new Process { StartInfo = startInfo };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);
            string output = await outputTask;
            string error = await errorTask;

            return process.ExitCode == 0
                ? ReadConnectionResult(output)
                : "GMGN connection failed: " + Shorten(error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            return "GMGN connection timed out after 30 seconds.";
        }
        catch (Exception exception)
        {
            return "GMGN connection failed: " + exception.Message;
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = options.NodePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(options.CliScriptPath);
        startInfo.ArgumentList.Add("market");
        startInfo.ArgumentList.Add("trending");
        startInfo.ArgumentList.Add("--chain");
        startInfo.ArgumentList.Add("bsc");
        startInfo.ArgumentList.Add("--interval");
        startInfo.ArgumentList.Add("1h");
        startInfo.ArgumentList.Add("--limit");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--raw");
        startInfo.Environment["GMGN_API_KEY"] = options.ApiKey;
        return startInfo;
    }

    private static string ReadConnectionResult(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        int code = root.GetProperty("code").GetInt32();

        if (code != 0)
        {
            string message = root.TryGetProperty("message", out JsonElement value)
                ? value.GetString() ?? "Unknown error"
                : "Unknown error";
            return "GMGN connection failed: " + message;
        }

        JsonElement rank = root.GetProperty("data").GetProperty("rank");
        if (rank.GetArrayLength() == 0)
        {
            return "GMGN connected successfully. No BSC trending token was returned.";
        }

        JsonElement token = rank[0];
        string name = token.GetProperty("name").GetString() ?? "Unknown";
        string symbol = token.GetProperty("symbol").GetString() ?? "Unknown";
        string address = token.GetProperty("address").GetString() ?? "Unknown";

        return "GMGN connected successfully.\n"
            + "Chain: BSC\n"
            + "Sample token: " + name + " (" + symbol + ")\n"
            + "Address: " + address;
    }

    private static string Shorten(string text)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "Unknown error" : text.Trim();
        return value.Length <= 500 ? value : value[..500];
    }
}
