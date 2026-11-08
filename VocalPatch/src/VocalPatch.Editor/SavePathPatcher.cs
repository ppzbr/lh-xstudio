using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HarmonyLib;

namespace VocalPatch.Editor;

/// <summary>
/// Detects a project save by patching the handful of .NET methods editors actually
/// use to persist files (<see cref="File.WriteAllText(string,string)"/>,
/// <see cref="XmlDocument.Save(string)"/>, <see cref="XDocument.Save(string)"/> …).
/// The patches are postfix-only and guarded, so a host can never fail a save because
/// of us — at worst a snapshot is skipped.
/// </summary>
public static class SavePathPatcher
{
    private static Action<string>? _onFileWritten;
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> LastFired = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static TimeSpan _minInterval = TimeSpan.FromSeconds(5);

    public static int InstalledCount { get; private set; }

    /// <summary>Patch the writers. <paramref name="extensions"/> limits which files matter.</summary>
    public static int Install(Harmony harmony, IEnumerable<string> extensions, TimeSpan minInterval, Action<string> onFileWritten)
    {
        _onFileWritten = onFileWritten;
        _minInterval = minInterval;
        Allowed.Clear();
        foreach (var e in extensions) Allowed.Add(e);

        var postfix = new HarmonyMethod(typeof(SavePathPatcher).GetMethod(nameof(OnWritten), BindingFlags.Static | BindingFlags.NonPublic));

        int count = 0;
        count += TryPatch(harmony, typeof(File), "WriteAllText", new[] { typeof(string), typeof(string) }, postfix);
        count += TryPatch(harmony, typeof(File), "WriteAllText", new[] { typeof(string), typeof(string), typeof(Encoding) }, postfix);
        count += TryPatch(harmony, typeof(File), "WriteAllBytes", new[] { typeof(string), typeof(byte[]) }, postfix);
        count += TryPatch(harmony, typeof(XmlDocument), "Save", new[] { typeof(string) }, postfix);
        count += TryPatch(harmony, typeof(XDocument), "Save", new[] { typeof(string) }, postfix);

        InstalledCount = count;
        return count;
    }

    /// <summary>
    /// True when the host's own managed code lives in the AppDomain this runtime loaded
    /// into — i.e. a .NET Framework host, where the CLR hosting API reuses the runtime the
    /// process already has, so a patch on the BCL's own writers really does intercept the
    /// host's saves. Two kinds of host fail this test: one built on .NET 5+ (CoreCLR), which
    /// has its own runtime and therefore its own AppDomain, and a native host that loads a
    /// managed runtime only for scripting (Synthesizer V Studio does, measured). In both
    /// cases the patch still installs and the count still reads 5, but it can only ever see
    /// our own writes — the host's saves are elsewhere, or are not managed calls at all.
    /// </summary>
    public static bool HostSharesOurRuntime() => HostAssemblyOutsideOurRuntime() != null;

    /// <summary>
    /// The first loaded assembly that belongs to neither our payload nor the base class
    /// library, or null when the AppDomain holds nothing but us. That is the evidence that
    /// the host shares this runtime, and it is named rather than reduced to a boolean so a
    /// surprise can be diagnosed from the log instead of guessed at.
    /// </summary>
    public static string? HostAssemblyOutsideOurRuntime()
    {
        var locations = new List<string>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { locations.Add(assembly.Location); } catch { /* dynamic or unloaded */ }
        }

        string ours = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location) ?? string.Empty;

        return FirstForeignAssembly(ours, PlatformDirectories(), locations);
    }

    /// <summary>
    /// Where the platform's own assemblies come from: the runtime directory, and the global
    /// assembly cache. Both have to be excluded. The first version of this check excluded
    /// only the runtime directory, so <c>System.dll</c> loaded out of the GAC looked like a
    /// host assembly and the "this host runs on its own runtime" note never appeared for
    /// the hosts it was written for.
    /// </summary>
    internal static string[] PlatformDirectories()
    {
        string runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string gac = Path.Combine(windows, "Microsoft.Net", "assembly");
        return new[] { runtime, gac };
    }

    /// <summary>The testable half of <see cref="HostAssemblyOutsideOurRuntime"/>.</summary>
    internal static bool AnyForeignAssembly(string ourDirectory, IEnumerable<string> platformDirectories, IEnumerable<string> assemblyLocations)
        => FirstForeignAssembly(ourDirectory, platformDirectories, assemblyLocations) != null;

    internal static string? FirstForeignAssembly(string ourDirectory, IEnumerable<string> platformDirectories, IEnumerable<string> assemblyLocations)
    {
        foreach (string location in assemblyLocations)
        {
            if (string.IsNullOrEmpty(location)) continue;
            if (Under(location, ourDirectory)) continue;

            bool platform = false;
            foreach (string directory in platformDirectories)
            {
                if (Under(location, directory)) { platform = true; break; }
            }
            if (platform) continue;

            return location;
        }
        return null;
    }

    /// <summary>Directory containment, case-insensitive, tolerant of a missing trailing separator.</summary>
    private static bool Under(string path, string directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        if (directory[directory.Length - 1] != Path.DirectorySeparatorChar) directory += Path.DirectorySeparatorChar;
        return path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }

    private static int TryPatch(Harmony harmony, Type type, string method, Type[] signature, HarmonyMethod postfix)
    {
        try
        {
            var target = type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, null, signature, null);
            if (target == null) return 0;
            harmony.Patch(target, postfix: postfix);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    // Harmony binds the first argument of the original method to __0.
    private static void OnWritten(string __0)
    {
        if (string.IsNullOrEmpty(__0)) return;
        var handler = _onFileWritten;
        if (handler == null) return;

        string ext = Path.GetExtension(__0);
        if (!Allowed.Contains(ext)) return;
        // A save that lands inside our own snapshot/export folders must not be re-captured.
        if (EditorActions.IsGeneratedPath(__0)) return;

        lock (Gate)
        {
            if (LastFired.TryGetValue(__0, out var last) && DateTime.UtcNow - last < _minInterval) return;
            LastFired[__0] = DateTime.UtcNow;
        }

        try { handler(__0); } catch { /* snapshots are best-effort */ }
    }
}
