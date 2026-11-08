using System.Globalization;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Jobs;

/// <summary>
/// A batch edit applied to a neutral <see cref="Song"/> in place. Options are string
/// key/value pairs so the same job can be driven from the CLI, a plugin, or a UI.
/// </summary>
public interface IJob
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Apply the job to every track (or the tracks the options select).</summary>
    void Apply(Song song, JobOptions options);
}

/// <summary>Convenience accessor over the option dictionary.</summary>
public sealed class JobOptions
{
    private readonly Dictionary<string, string> _values;

    public JobOptions(IDictionary<string, string>? values = null)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values != null)
            foreach (var kv in values) _values[kv.Key] = kv.Value;
    }

    public static JobOptions From(params (string key, string value)[] pairs)
    {
        var o = new JobOptions();
        foreach (var (k, v) in pairs) o._values[k] = v;
        return o;
    }

    public IReadOnlyDictionary<string, string> Raw => _values;

    public bool Has(string key) => _values.ContainsKey(key);

    public string? GetString(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public string GetString(string key, string fallback) => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;

    public double GetDouble(string key, double fallback) =>
        _values.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;

    public bool GetBool(string key, bool fallback)
    {
        if (!_values.TryGetValue(key, out var v)) return fallback;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Comma-separated list option.</summary>
    public string[] GetList(string key) =>
        _values.TryGetValue(key, out var v) && v.Length > 0
            ? v.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
}

/// <summary>All jobs known to the toolkit.</summary>
public static class JobRegistry
{
    private static readonly List<IJob> All = new()
    {
        new SwingJob(),
        new QuantizeLengthJob(),
        new ReplaceLyricsJob(),
        new HarmonyJob(),
        new TransposeJob(),
        new NormalizeJob(),
        new StaccatoJob(),
    };

    public static IReadOnlyList<IJob> Jobs => All;

    public static IJob? ById(string id) =>
        All.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
}
