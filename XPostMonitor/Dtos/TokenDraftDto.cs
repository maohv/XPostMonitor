using System.Text.Json.Serialization;

namespace XPostMonitor.Dtos;

public sealed class TokenDraftDto
{
    [JsonPropertyName("selected_hook")]
    public string SelectedHook { get; set; } = string.Empty;

    [JsonPropertyName("hook_source")]
    public string HookSource { get; set; } = string.Empty;

    [JsonPropertyName("lock_hook")]
    public bool LockHook { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("image_prompt")]
    public string ImagePrompt { get; set; } = string.Empty;

}
