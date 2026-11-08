namespace VocalPatch.Editor;

/// <summary>
/// Describes a host editor: how to recognise its process, which project files it
/// owns, and where it keeps them. The in-process runtime uses this to decide what
/// to watch and which hotkey actions make sense once it is injected.
/// </summary>
public sealed class EditorProfile
{
    public EditorProfile(string id, string displayName, string processName, string[] projectExtensions, string[] projectDirectories)
    {
        Id = id;
        DisplayName = displayName;
        ProcessName = processName;
        ProjectExtensions = projectExtensions;
        ProjectDirectories = projectDirectories;
    }

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>Process name without extension, matched case-insensitively.</summary>
    public string ProcessName { get; }

    public IReadOnlyList<string> ProjectExtensions { get; }

    /// <summary>Directory templates; <c>{Documents}</c> and <c>{AppData}</c> are expanded at runtime.</summary>
    public IReadOnlyList<string> ProjectDirectories { get; }

    /// <summary>
    /// True when a process belongs to this editor. Real executables carry suffixes the
    /// profile name does not — "CeVIO AI.orig" (a wrapper around CeVIO AI.exe),
    /// "Piapro Studio NT", "synthv-studio" — so this is a prefix match on the process
    /// name with any extension stripped, not an equality check. Equality left those
    /// processes unrecognised, which silently degraded the runtime to "watch every
    /// target folder instead of this editor's own".
    /// </summary>
    public bool MatchesProcess(string processName)
    {
        string name = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith(ProcessName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The editors the toolkit targets.</summary>
public static class ProfileRegistry
{
    public static IReadOnlyList<EditorProfile> All { get; } = new[]
    {
        new EditorProfile("cevio", "CeVIO Creative Studio / AI", "CeVIO",
            new[] { ".ccs" },
            new[] { @"{Documents}\CeVIO", @"{Documents}\CeVIO Creative Studio" }),

        new EditorProfile("synthv", "Synthesizer V Studio", "synthv-studio",
            new[] { ".svp", ".svip" },
            new[] { @"{Documents}\SynthV Studio", @"{Documents}\Dreamtonics" }),

        new EditorProfile("voisona", "VoiSona", "VoiSona",
            new[] { ".tmpproj", ".vsqx", ".mid" },
            new[] { @"{Documents}\VoiSona", @"{AppData}\VoiSona" }),

        new EditorProfile("openutau", "OpenUtau", "OpenUtau",
            new[] { ".ustx", ".ust" },
            new[] { @"{Documents}\OpenUtau" }),

        new EditorProfile("piapro", "Piapro Studio", "Piapro",
            new[] { ".ppsf", ".vpr", ".vsqx" },
            new[] { @"{Documents}\Piapro Studio", @"{Documents}\VOCALOID4" }),

        // OpenUtau forks. Same project format and editor folders as the parent, but a
        // different process name, so they need their own entry to be recognised.
        new EditorProfile("synthutau", "SynthUTAU", "SynthUTAU",
            new[] { ".ustx", ".ust" },
            new[] { @"{Documents}\SynthUTAU", @"{Documents}\OpenUtau" }),

        new EditorProfile("utauv", "UtauV", "UtauV",
            new[] { ".ustx", ".ust" },
            new[] { @"{Documents}\UtauV", @"{Documents}\OpenUtau" }),

        new EditorProfile("ace", "ACE Studio", "ACE Studio",
            new[] { ".acep" },
            new[] { @"{AppData}\Timedomain\ACE Studio", @"{Documents}\ACE Studio" }),

        new EditorProfile("xstudio", "NetEase Cloud Music XStudio", "NetEase Cloud Music XStudio",
            new[] { ".svip", ".mid" },
            new[] { @"{AppData}\SXSEditor", @"{Documents}\NetEase Cloud Music XStudio" }),
    };

    /// <summary>
    /// Resolve a profile from an id. The process name and the display name are accepted
    /// too: the injector is driven by process name (<c>synthv-studio</c>) while the id is
    /// short (<c>synthv</c>), and <c>profile=</c> in the ini reads better with either.
    /// </summary>
    public static EditorProfile? ById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string wanted = id.Trim();

        return All.FirstOrDefault(p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(p => string.Equals(p.ProcessName, wanted, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(p => string.Equals(p.DisplayName, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Find the profile whose process name matches, or null.</summary>
    public static EditorProfile? Detect(string processName) =>
        All.FirstOrDefault(p => p.MatchesProcess(processName));

    /// <summary>Expand <c>{Documents}</c> / <c>{AppData}</c> in a directory template.</summary>
    public static string Expand(string template)
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return template
            .Replace("{Documents}", docs)
            .Replace("{AppData}", appData);
    }
}
