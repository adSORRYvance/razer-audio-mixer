using Tomlyn;
using Tomlyn.Model;

namespace RazerMixerBridge.Config;

public static class ConfigLoader
{
    public static BridgeConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Failed to parse config file");

        return new BridgeConfig
        {
            Audio = ParseAudio(model),
        };
    }

    private static AudioConfig ParseAudio(TomlTable t)
    {
        if (!t.TryGetValue("audio", out var raw) || raw is not TomlTable a)
            return new();
        return new AudioConfig
        {
            OutputSink = a.GetStringOrNull("output_sink"),
            SinkDescriptionTemplate = a.GetString("sink_description_template", "Razer-Ch-{n}"),
            Routing = ParseRouting(a),
        };
    }

    private static Dictionary<string, string> ParseRouting(TomlTable audio)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!audio.TryGetValue("routing", out var raw) || raw is not TomlTable r)
            return result;
        foreach (var (key, value) in r)
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
                result[key] = s;
        }
        return result;
    }
}

file static class TomlTableExtensions
{
    public static string? GetStringOrNull(this TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is string s ? s : null;

    public static string GetString(this TomlTable t, string key, string def) =>
        t.TryGetValue(key, out var v) && v is string s ? s : def;
}
