using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Dtos;
using XPostMonitor.Models;

namespace XPostMonitor.Services;

public sealed class TelegramBotService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly XApiClient xApiClient;
    private readonly ILogger<TelegramBotService> logger;
    private readonly HttpClient telegramClient;
    private readonly string telegramToken;
    private long nextUpdateId;

    public TelegramBotService(IServiceScopeFactory scopeFactory, XApiClient xApiClient, BotOptions options, ILogger<TelegramBotService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.xApiClient = xApiClient;
        this.logger = logger;
        telegramToken = options.TelegramToken;

        telegramClient = new HttpClient();
        telegramClient.Timeout = TimeSpan.FromSeconds(40);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(telegramToken))
        {
            throw new InvalidOperationException("Chua co Bot:TelegramToken trong User Secrets.");
        }

        await DeleteWebhookAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<TelegramUpdate> updates = await GetUpdatesAsync(stoppingToken);

                foreach (TelegramUpdate update in updates)
                {
                    // Lan goi sau dung ID lon hon de Telegram khong gui lai tin cu.
                    nextUpdateId = update.UpdateId + 1;

                    if (update.Message != null && update.Message.Text != null)
                    {
                        await HandleMessageAsync(update.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Telegram polling failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public override void Dispose()
    {
        telegramClient.Dispose();
        base.Dispose();
    }

    private async Task HandleMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type != "private" || message.From == null)
        {
            await SendMessageAsync(message.Chat.Id, "Bot hien chi ho tro chat rieng.", cancellationToken);
            return;
        }

        BotCommand? command = BotCommand.Parse(message.Text);
        if (command == null)
        {
            return;
        }

        await SaveTelegramUserAsync(message, cancellationToken);

        switch (command.Name)
        {
            case "/start":
            case "/help":
                await SendMessageAsync(message.Chat.Id, HelpText, cancellationToken);
                break;

            case "/add":
                await AddAsync(message.Chat.Id, command.Argument, cancellationToken);
                break;

            case "/remove":
                await RemoveAsync(message.Chat.Id, command.Argument, cancellationToken);
                break;

            case "/list":
                await ListAsync(message.Chat.Id, cancellationToken);
                break;

            default:
                await SendMessageAsync(message.Chat.Id, "Lenh khong hop le. Dung /help.", cancellationToken);
                break;
        }
    }

    private async Task SaveTelegramUserAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        TelegramUser? user = await db.TelegramUsers.FindAsync(new object[] { message.Chat.Id }, cancellationToken);

        DateTime now = DateTime.UtcNow;
        TelegramFrom sender = message.From!;

        if (user == null)
        {
            user = new TelegramUser();
            user.ChatId = message.Chat.Id;
            user.TelegramUserId = sender.Id;
            user.CreatedAtUtc = now;
            db.TelegramUsers.Add(user);
        }

        user.Username = sender.Username;
        user.DisplayName = GetDisplayName(sender);
        user.LastSeenAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AddAsync(long chatId, string? username, CancellationToken cancellationToken)
    {
        if (!IsValidXUsername(username))
        {
            await SendMessageAsync(chatId, "Cu phap: /add username", cancellationToken);
            return;
        }

        try
        {
            XUser? xUser = await xApiClient.GetUserByUsernameAsync(username!, cancellationToken);

            if (xUser == null)
            {
                await SendMessageAsync(chatId, "Khong tim thay tai khoan X.", cancellationToken);
                return;
            }

            if (xUser.Protected)
            {
                await SendMessageAsync(chatId, "Khong the theo doi tai khoan X dang protected.", cancellationToken);
                return;
            }

            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool alreadyWatching = await db.WatchlistEntries.AnyAsync(item => item.ChatId == chatId && item.XUserId == xUser.Id, cancellationToken);

            if (alreadyWatching)
            {
                await SendMessageAsync(chatId, "Ban da theo doi @" + xUser.Username + ".", cancellationToken);
                return;
            }

            DateTime now = DateTime.UtcNow;
            XAccount? account = await db.XAccounts.FindAsync(new object[] { xUser.Id }, cancellationToken);

            if (account == null)
            {
                account = new XAccount();
                account.XUserId = xUser.Id;
                account.CreatedAtUtc = now;
                db.XAccounts.Add(account);
            }

            account.Username = xUser.Username;
            account.DisplayName = xUser.Name;
            account.UpdatedAtUtc = now;

            WatchlistEntry entry = new WatchlistEntry();
            entry.ChatId = chatId;
            entry.XUserId = xUser.Id;
            entry.CreatedAtUtc = now;
            db.WatchlistEntries.Add(entry);

            await db.SaveChangesAsync(cancellationToken);
            await SendMessageAsync(chatId, "Da them @" + xUser.Username + " vao danh sach theo doi.", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Unable to add X account {Username}", username);
            await SendMessageAsync(chatId, "Khong the kiem tra @" + username + ": " + exception.Message, cancellationToken);
        }
    }

    private async Task RemoveAsync(long chatId, string? username, CancellationToken cancellationToken)
    {
        if (!IsValidXUsername(username))
        {
            await SendMessageAsync(chatId, "Cu phap: /remove username", cancellationToken);
            return;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string normalizedUsername = username!.ToLowerInvariant();

        WatchlistEntry? entry = await db.WatchlistEntries.Include(item => item.XAccount)
            .FirstOrDefaultAsync(item => item.ChatId == chatId && item.XAccount.Username.ToLower() == normalizedUsername, cancellationToken);

        if (entry == null)
        {
            await SendMessageAsync(chatId, "Ban chua theo doi @" + username + ".", cancellationToken);
            return;
        }

        db.WatchlistEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
        await SendMessageAsync(chatId, "Da bo theo doi @" + entry.XAccount.Username + ".", cancellationToken);
    }

    private async Task ListAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> usernames = await db.WatchlistEntries.Where(item => item.ChatId == chatId)
            .OrderBy(item => item.XAccount.Username).Select(item => item.XAccount.Username).ToListAsync(cancellationToken);

        string text;
        if (usernames.Count == 0)
        {
            text = "Danh sach theo doi dang trong.";
        }
        else
        {
            text = "Dang theo doi:\n- " + string.Join("\n- ", usernames);
        }

        await SendMessageAsync(chatId, text, cancellationToken);
    }

    private async Task DeleteWebhookAsync(CancellationToken cancellationToken)
    {
        string url = GetTelegramUrl("deleteWebhook");
        using HttpResponseMessage response = await telegramClient.PostAsJsonAsync(url, new { }, cancellationToken);

        TelegramBasicResponse? result = await response.Content.ReadFromJsonAsync<TelegramBasicResponse>(cancellationToken);

        CheckTelegramResponse(response, result);
    }

    private async Task<List<TelegramUpdate>> GetUpdatesAsync(CancellationToken cancellationToken)
    {
        string url = GetTelegramUrl("getUpdates");
        var request = new
        {
            offset = nextUpdateId,
            timeout = 30,
            allowed_updates = new[] { "message" }
        };

        using HttpResponseMessage response = await telegramClient.PostAsJsonAsync(url, request, cancellationToken);

        TelegramUpdatesResponse? result = await response.Content.ReadFromJsonAsync<TelegramUpdatesResponse>(cancellationToken);

        CheckTelegramResponse(response, result);
        return result!.Result ?? new List<TelegramUpdate>();
    }

    private async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        string url = GetTelegramUrl("sendMessage");
        var request = new
        {
            chat_id = chatId,
            text = text
        };

        using HttpResponseMessage response = await telegramClient.PostAsJsonAsync(url, request, cancellationToken);

        TelegramBasicResponse? result = await response.Content.ReadFromJsonAsync<TelegramBasicResponse>(cancellationToken);

        CheckTelegramResponse(response, result);
    }

    private string GetTelegramUrl(string method)
    {
        return "https://api.telegram.org/bot" + telegramToken + "/" + method;
    }

    private static void CheckTelegramResponse(HttpResponseMessage response, TelegramBasicResponse? result)
    {
        if (response.IsSuccessStatusCode && result != null && result.Ok)
        {
            return;
        }

        string error = result?.Description ?? response.ReasonPhrase ?? "Unknown error";
        throw new HttpRequestException("Telegram API: " + error);
    }

    private static string GetDisplayName(TelegramFrom sender)
    {
        if (string.IsNullOrWhiteSpace(sender.LastName))
        {
            return sender.FirstName;
        }

        return sender.FirstName + " " + sender.LastName;
    }

    private static bool IsValidXUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 15)
        {
            return false;
        }

        foreach (char character in username)
        {
            bool isLowercaseLetter = character >= 'a' && character <= 'z';
            bool isUppercaseLetter = character >= 'A' && character <= 'Z';
            bool isNumber = character >= '0' && character <= '9';

            if (!isLowercaseLetter && !isUppercaseLetter && !isNumber && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private const string HelpText =
        "Cac lenh:\n"
        + "/add username - them tai khoan X\n"
        + "/remove username - bo tai khoan X\n"
        + "/list - xem danh sach theo doi";
}