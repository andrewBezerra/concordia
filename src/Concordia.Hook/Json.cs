using System.Text.Json;
using System.Text.Json.Serialization;
using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Review;

namespace Concordia.Hook;

/// <summary>The JSON object Claude Code writes to a hook's stdin. Unknown fields are ignored.</summary>
internal sealed class HookInput
{
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("hook_event_name")] public string? HookEventName { get; set; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
    [JsonPropertyName("tool_input")] public ToolInput? ToolInput { get; set; }
    [JsonPropertyName("stop_hook_active")] public bool StopHookActive { get; set; }

    public string WorkingDirectory => string.IsNullOrEmpty(Cwd) ? Directory.GetCurrentDirectory() : Cwd;

    public static HookInput Read(TextReader reader)
    {
        var text = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(text)) return new HookInput();
        return JsonSerializer.Deserialize(text, ConcordiaJson.Default.HookInput) ?? new HookInput();
    }
}

internal sealed class ToolInput
{
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
    [JsonPropertyName("notebook_path")] public string? NotebookPath { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }

    [JsonIgnore] public string? TargetPath => FilePath ?? NotebookPath;
}

/// <summary>Common hook output fields understood by Claude Code.</summary>
internal sealed class HookOutput
{
    public string? SystemMessage { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HookInput))]
[JsonSerializable(typeof(HookOutput))]
[JsonSerializable(typeof(ConcordiaConfig))]
[JsonSerializable(typeof(GateState))]
[JsonSerializable(typeof(ReviewPackSummary))]
[JsonSerializable(typeof(ReviewerAnswer))]
internal partial class ConcordiaJson : JsonSerializerContext
{
}
