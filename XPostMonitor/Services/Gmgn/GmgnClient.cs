using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XPostMonitor.Configuration;

namespace XPostMonitor.Services.Gmgn;

// Chạy gmgn-cli để đọc dữ liệu thị trường và chuẩn bị cho chức năng trading sau này.
public sealed class GmgnClient
{
    private readonly GmgnOptions options;

    public GmgnClient(GmgnOptions options)
    {
        this.options = options;
    }

    // Kiểm tra API key chung của admin trong appsettings.Development.json.
    public async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "GMGN API key is missing from configuration.";
        }

        try
        {
            string output = await RunAsync(
                ["market", "trending", "--chain", "bsc", "--interval", "1h", "--limit", "1", "--raw"],
                options.ApiKey, null, false, cancellationToken);
            return ReadConnectionResult(output);
        }
        catch (Exception exception)
        {
            return "GMGN connection failed: " + exception.Message;
        }
    }

    // Kiểm tra API key của Premium user và trả về các network đã có ví.
    public async Task<GmgnConnectionResult> CheckUserConnectionAsync(string apiKey, string privateKey,
        CancellationToken cancellationToken)
    {
        try
        {
            List<GmgnWallet> wallets = await GetWalletsAsync(apiKey, cancellationToken);
            if (wallets.Count == 0)
            {
                return new GmgnConnectionResult(false, "No linked wallet");
            }

            GmgnWallet wallet = FindWalletForConnectionCheck(wallets);
            await ValidateCredentialsAsync(apiKey, privateKey, wallet, cancellationToken);
            string chains = string.Join(", ", wallets.Select(item => item.Chain).Distinct().OrderBy(item => item));
            return new GmgnConnectionResult(true, chains);
        }
        catch (Exception exception)
        {
            return new GmgnConnectionResult(false, exception.Message);
        }
    }

    // Tạo cặp khóa Ed25519 ngay trên server. Chỉ public key được gửi cho user.
    public async Task<GmgnSigningKeyPair> GenerateSigningKeyAsync(CancellationToken cancellationToken)
    {
        const string script = """
            const crypto = require('node:crypto');
            const keys = crypto.generateKeyPairSync('ed25519', {
              publicKeyEncoding: { type: 'spki', format: 'pem' },
              privateKeyEncoding: { type: 'pkcs8', format: 'pem' }
            });
            const test = Buffer.from('gmgn-key-check');
            const signature = crypto.sign(null, test, keys.privateKey);
            if (!crypto.verify(null, test, keys.publicKey, signature)) throw new Error('Key check failed');
            process.stdout.write(JSON.stringify(keys));
            """;

        ProcessStartInfo startInfo = CreateNodeStartInfo();
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        string output = await RunProcessAsync(startInfo, TimeSpan.FromSeconds(10), cancellationToken);

        GmgnSigningKeyPair? keys = JsonSerializer.Deserialize<GmgnSigningKeyPair>(output,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return keys is { PublicKey.Length: > 0, PrivateKey.Length: > 0 }
            ? keys
            : throw new JsonException("Node.js did not return an Ed25519 key pair.");
    }

    // Xác nhận API key đã được ghép đúng public key. Lệnh này chỉ đọc số dư token.
    public async Task ValidateCredentialsAsync(string apiKey, string privateKey,
        CancellationToken cancellationToken)
    {
        List<GmgnWallet> wallets = await GetWalletsAsync(apiKey, cancellationToken);
        if (wallets.Count == 0)
        {
            throw new InvalidOperationException("No wallet is linked to this GMGN API key.");
        }

        await ValidateCredentialsAsync(apiKey, privateKey, FindWalletForConnectionCheck(wallets), cancellationToken);
    }

    // Tìm ví đã được liên kết với API key trên một network.
    public async Task<string> GetWalletAddressAsync(string apiKey, string chain, CancellationToken cancellationToken)
    {
        List<GmgnWallet> wallets = await GetWalletsAsync(apiKey, cancellationToken);
        GmgnWallet? wallet = wallets.FirstOrDefault(item =>
            string.Equals(item.Chain, chain, StringComparison.OrdinalIgnoreCase));

        return wallet?.Address
            ?? throw new InvalidOperationException("No " + chain + " wallet is linked to this GMGN API key.");
    }

    private async Task<List<GmgnWallet>> GetWalletsAsync(string apiKey, CancellationToken cancellationToken)
    {
        string output = await RunAsync(["portfolio", "info", "--raw"], apiKey, null, false, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("wallets", out JsonElement wallets) || wallets.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("GMGN did not return linked wallets.");
        }

        List<GmgnWallet> result = [];
        foreach (JsonElement wallet in wallets.EnumerateArray())
        {
            string chain = wallet.TryGetProperty("chain", out JsonElement chainValue)
                ? chainValue.GetString() ?? string.Empty : string.Empty;
            string address = wallet.TryGetProperty("address", out JsonElement addressValue)
                ? addressValue.GetString() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(chain) && !string.IsNullOrWhiteSpace(address))
            {
                result.Add(new GmgnWallet(chain.ToLowerInvariant(), address));
            }
        }

        return result;
    }

    private async Task ValidateCredentialsAsync(string apiKey, string privateKey, GmgnWallet wallet,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            ["portfolio", "holdings", "--chain", wallet.Chain, "--wallet", wallet.Address, "--limit", "1", "--raw"],
            apiKey, privateKey, false, cancellationToken);
    }

    private static GmgnWallet FindWalletForConnectionCheck(List<GmgnWallet> wallets)
    {
        return wallets.FirstOrDefault(item => item.Chain is "sol" or "bsc" or "base" or "eth") ?? wallets[0];
    }

    // Chạy process bằng ArgumentList để dữ liệu bài viết không thể biến thành lệnh shell.
    private async Task<string> RunAsync(IReadOnlyList<string> arguments, string apiKey, string? privateKey,
        bool allowAutomatedTrade, CancellationToken cancellationToken)
    {
        string cliScriptPath = FindCliScriptPath();
        if (string.IsNullOrWhiteSpace(cliScriptPath))
        {
            throw new FileNotFoundException("GMGN CLI was not found. Run: npm install -g gmgn-cli");
        }

        ProcessStartInfo startInfo = CreateNodeStartInfo();
        startInfo.ArgumentList.Add(cliScriptPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GMGN_API_KEY"] = apiKey;
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            startInfo.Environment["GMGN_PRIVATE_KEY"] = privateKey;
        }
        if (allowAutomatedTrade)
        {
            startInfo.Environment["GMGN_ALLOW_AUTOMATED_TRADES"] = "1";
        }

        return await RunProcessAsync(startInfo, TimeSpan.FromSeconds(60), cancellationToken);
    }

    private ProcessStartInfo CreateNodeStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(options.NodePath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    // Đọc song song stdout và stderr để process không bị treo khi output dài.
    private static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan maximumTime,
        CancellationToken cancellationToken)
    {
        using Process process = new Process { StartInfo = startInfo };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumTime);

        try
        {
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(Shorten(string.IsNullOrWhiteSpace(error) ? output : error));
            }

            return output.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            throw new TimeoutException("The process timed out after " + maximumTime.TotalSeconds + " seconds.");
        }
    }

    // Ưu tiên đường dẫn cấu hình; nếu đổi máy thì tự tìm CLI trong npm của user hiện tại.
    private string FindCliScriptPath()
    {
        string configuredPath = Environment.ExpandEnvironmentVariables(options.CliScriptPath);
        if (File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string automaticPath = Path.Combine(appData, "npm", "node_modules", "gmgn-cli", "dist", "index.js");
        return File.Exists(automaticPath) ? automaticPath : string.Empty;
    }

    private static string ReadConnectionResult(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("code", out JsonElement code) && code.GetInt32() != 0)
        {
            return "GMGN connection failed: " + ReadString(root, "message", "Unknown error");
        }

        JsonElement rank = root.GetProperty("data").GetProperty("rank");
        return rank.GetArrayLength() == 0
            ? "GMGN connected successfully. No BSC trending token was returned."
            : "GMGN connected successfully.\nChain: BSC";
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            ? value.GetString() ?? fallback : fallback;
    }

    private static string Shorten(string text)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "Unknown error" : text.Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private sealed record GmgnWallet(string Chain, string Address);
}

public sealed record GmgnConnectionResult(bool Success, string Message);

public sealed record GmgnSigningKeyPair(string PublicKey, string PrivateKey);