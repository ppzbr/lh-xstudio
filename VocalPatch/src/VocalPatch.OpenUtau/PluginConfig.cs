using System.Text.Json;
using System.Text.Json.Serialization;

namespace VocalPatch.OpenUtau;

/// <summary>One configured batch job, e.g. swing with an amount of 55%.</summary>
internal sealed class JobSpec
{
    public string Id { get; set; } = "quantize";

    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Optional pinyin pass applied before the configured jobs.</summary>
internal sealed class PinyinSpec
{
    public string Mode { get; set; } = "pinyin";
    public string Tones { get; set; } = "none";
    public bool Split { get; set; }
}

/// <summary>What a single <c>vpatch-utau</c> executable does when OpenUtau invokes it.</summary>
internal sealed class PluginConfig
{
    public string Name { get; set; } = "VocalPatch";

    public List<JobSpec> Jobs { get; set; } = new();

    public PinyinSpec? Pinyin { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string directory) => Path.Combine(directory, "vpatch.json");

    public static PluginConfig? LoadBeside(string executablePath)
    {
        string path = PathFor(Path.GetDirectoryName(executablePath) ?? ".");
        return File.Exists(path) ? Load(path) : null;
    }

    public static PluginConfig Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path), Options) ?? new PluginConfig();
        }
        catch
        {
            return new PluginConfig();
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
}
