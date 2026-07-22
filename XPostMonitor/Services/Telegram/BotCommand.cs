namespace XPostMonitor.Services.Telegram;

public sealed class BotCommand
{
    // Lưu tên lệnh đã tách và tham số đi kèm.
    public BotCommand(string name, string? argument)
    {
        Name = name;
        Argument = argument;
    }

    public string Name { get; }
    public string? Argument { get; }

    // Tách tin nhắn Telegram thành tên lệnh và tham số, ví dụ: "/add binancezh".
    public static BotCommand? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string cleanedText = text.Trim();
        if (!cleanedText.StartsWith('/'))
        {
            return null;
        }

        string[] parts = cleanedText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string commandName = parts[0];

        int botNamePosition = commandName.IndexOf('@');
        if (botNamePosition >= 0)
        {
            commandName = commandName.Substring(0, botNamePosition);
        }

        string? argument = null;
        if (parts.Length == 2)
        {
            argument = parts[1].Trim().TrimStart('@');
        }

        return new BotCommand(commandName.ToLowerInvariant(), argument);
    }

}
