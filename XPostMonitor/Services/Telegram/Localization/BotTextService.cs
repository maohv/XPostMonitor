using System.Globalization;
using System.Text.Json;

namespace XPostMonitor.Services.Telegram.Localization;

// Đọc câu chữ từ 3 file JSON. Nếu thiếu bản dịch thì tự dùng tiếng Anh.
public sealed class BotTextService
{
    private readonly Dictionary<string, Dictionary<string, string>> languages;

    public BotTextService(IHostEnvironment environment)
    {
        languages = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = Load(environment, "en"),
            ["vi"] = Load(environment, "vi"),
            ["zh"] = Load(environment, "zh")
        };
    }

    public string Get(string? language, string key, params object?[] values)
    {
        string code = Normalize(language);
        if (!languages[code].TryGetValue(key, out string? template)
            && !languages["en"].TryGetValue(key, out template))
        {
            return key;
        }

        return values.Length == 0
            ? template
            : string.Format(CultureInfo.InvariantCulture, template, values);
    }

    public static string Normalize(string? language)
    {
        if (language?.StartsWith("vi", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "vi";
        }

        return language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh" : "en";
    }

    private static Dictionary<string, string> Load(IHostEnvironment environment, string language)
    {
        string path = Path.Combine(environment.ContentRootPath, "Localization", language + ".json");
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json");
        }
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new JsonException("Language file is empty: " + path);
    }
}
