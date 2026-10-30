using System.Text.Json;

namespace Concordia.Hook.Config;

internal static class Presets
{
    public static string? ReadText(string stack)
    {
        using var stream = typeof(Presets).Assembly.GetManifestResourceStream($"Concordia.Hook.Presets.{stack}.json");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static ConcordiaConfig? Load(string stack)
    {
        var text = ReadText(stack);
        return text is null ? null : JsonSerializer.Deserialize(text, ConcordiaJson.Default.ConcordiaConfig);
    }
}
