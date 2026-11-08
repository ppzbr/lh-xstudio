using System.Text;
using HarmonyLib;
using VocalPatch.Core.Jobs;
using VocalPatch.Core.Lyrics;

namespace VocalPatch.Editor;

/// <summary>Runtime options, loaded from <c>vpatch-editor.ini</c> beside the assembly.</summary>
public sealed class EditorRuntimeConfig
{
    public string? Profile { get; set; }
    public bool AutoSave { get; set; } = true;
    public bool PatchSaves { get; set; } = true;
    public int SnapshotIntervalSeconds { get; set; } = 30;

    /// <summary>Log every key the hook sees (<c>hotkeydebug=true</c>). Off by default: it is noisy.</summary>
    public bool HotkeyDebug { get; set; }

    /// <summary>Action id -> Ctrl+Alt+&lt;virtual key&gt;. Override with <c>hotkey.&lt;action&gt;=Q</c>.</summary>
    public Dictionary<string, uint> Hotkeys { get; } = DefaultHotkeys();

    /// <summary>Extra folders to watch, from <c>watch=&lt;dir&gt;</c> lines (separate them with <c>;</c>).</summary>
    public List<string> ExtraDirectories { get; } = new List<string>();

    public static Dictionary<string, uint> DefaultHotkeys() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Every action in EditorActions.ActionIds gets a key, so nothing is
        // reachable only from the command line. Letters follow the action name;
        // keep the values distinct or two actions will fire from one press.
        ["snapshot"] = 0x56,  // V
        ["swing"] = 0x53,     // S
        ["quantize"] = 0x51,  // Q
        ["staccato"] = 0x43,  // C
        ["transpose"] = 0x54, // T
        ["normalize"] = 0x4E, // N
        ["pinyin"] = 0x50,    // P
        ["harmony"] = 0x48,   // H
        ["lyrics"] = 0x4C,    // L
        ["export"] = 0x45,    // E
        ["restore"] = 0x5A,   // Z
    };

    public static EditorRuntimeConfig Load(string path)
    {
        var config = new EditorRuntimeConfig();
        try
        {
            if (!File.Exists(path)) return config;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (key.StartsWith("hotkey.", StringComparison.OrdinalIgnoreCase))
                {
                    string action = key.Substring("hotkey.".Length).Trim();
                    if (action.Length > 0 && TryParseKey(value, out uint vk)) config.Hotkeys[action] = vk;
                    continue;
                }

                switch (key.ToLowerInvariant())
                {
                    case "profile": config.Profile = value; break;
                    case "autosave": config.AutoSave = ParseBool(value, config.AutoSave); break;
                    case "patchsaves": config.PatchSaves = ParseBool(value, config.PatchSaves); break;
                    case "snapshotinterval": if (int.TryParse(value, out int s) && s > 0) config.SnapshotIntervalSeconds = s; break;
                    case "hotkeydebug": config.HotkeyDebug = ParseBool(value, config.HotkeyDebug); break;
                    case "watch":
                    case "dir":
                        foreach (string part in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string dir = part.Trim();
                            if (dir.Length > 0 && !config.ExtraDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                                config.ExtraDirectories.Add(dir);
                        }
                        break;
                }
            }
        }
        catch
        {
            // Bad config: keep defaults.
        }
        return config;
    }

    /// <summary>"Q", "0x51" or "81" -> a virtual key.</summary>
    public static bool TryParseKey(string value, out uint virtualKey)
    {
        virtualKey = 0;
        if (value.Length == 0) return false;
        if (value.Length == 1)
        {
            virtualKey = char.ToUpperInvariant(value[0]);
            return true;
        }
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out virtualKey);
        return uint.TryParse(value, out virtualKey);
    }

    private static bool ParseBool(string value, bool fallback) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Entry point of the injected runtime. Call <see cref="Initialize"/> once from the
/// native loader that drops this assembly into the editor process.
/// </summary>
public static class Bootstrap
{
    private static Harmony? _harmony;
    private static ProjectWatcher? _watcher;
    private static HotkeyHook? _hook;

    public static bool Initialized { get; private set; }
    public static EditorProfile? Profile { get; private set; }
    public static EditorRuntimeConfig Config { get; private set; } = new();
    public static int PatchedSaveMethods => SavePathPatcher.InstalledCount;
    public static IReadOnlyList<string> WatchedDirectories => _watcher?.WatchedDirectories ?? Array.Empty<string>();
    public static IReadOnlyCollection<uint> RegisteredHotkeys => _hook?.Keys ?? Array.Empty<uint>();

    public static event EventHandler<string>? Log;

    /// <summary>
    /// Log sink for the injected case, where nobody can subscribe. Lets an injection be
    /// verified without a debugger. Every editor the runtime is injected into appends to
    /// this one file, so each line carries the process id.
    /// </summary>
    public static string LogFilePath { get; } = Path.Combine(Path.GetTempPath(), "vocalpatch-editor.log");

    private static readonly int Pid = System.Diagnostics.Process.GetCurrentProcess().Id;

    /// <summary>Start the runtime. <paramref name="profileId"/> overrides process detection.</summary>
    public static void Initialize(string? profileId = null)
    {
        if (Initialized) return;
        Initialized = true;

        string baseDir = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location) ?? ".";
        Config = EditorRuntimeConfig.Load(Path.Combine(baseDir, "vpatch-editor.ini"));

        Profile = ProfileRegistry.ById(profileId ?? Config.Profile ?? string.Empty)
                  ?? ProfileRegistry.Detect(System.Diagnostics.Process.GetCurrentProcess().ProcessName);
        Emit($"profile: {(Profile != null ? Profile.DisplayName : "unknown (watching all target folders)")}");

        var extensions = ProjectExtensions();
        var directories = ResolveDirectories();

        if (Config.AutoSave && directories.Length > 0)
        {
            _watcher = new ProjectWatcher(directories, extensions, TimeSpan.FromSeconds(Config.SnapshotIntervalSeconds));
            _watcher.SnapshotTaken += (_, path) => Emit("autosave -> " + path);
            Emit($"watching {_watcher.WatchedDirectories.Count} folder(s)");
            foreach (string dir in _watcher.WatchedDirectories.Take(3)) Emit("  . " + dir);
        }
        else if (Config.AutoSave)
        {
            Emit("autosave off: no existing folder to watch (add watch=<dir> to vpatch-editor.ini)");
        }

        if (Config.PatchSaves)
        {
            try
            {
                _harmony = new Harmony("com.vocalpatch.editor");
                int n = SavePathPatcher.Install(_harmony, extensions, TimeSpan.FromSeconds(2), path =>
                {
                    var snapshot = EditorActions.Snapshot(path);
                    if (snapshot != null) Emit("save-hook -> " + snapshot);
                });
                Emit($"patched {n} save method(s)");
                if (n > 0)
                {
                    string? host = SavePathPatcher.HostAssemblyOutsideOurRuntime();
                    if (host == null)
                    {
                        Emit("note: this host's own code is not in this AppDomain (a native host, or " +
                             "one on its own runtime) - the save hook cannot see its saves; the " +
                             "folder watcher and the hotkeys still work");
                    }
                    else
                    {
                        Emit($"host code in this runtime: {Path.GetFileName(host)} - its saves pass " +
                             "through the hook");
                    }
                }
            }
            catch (Exception ex)
            {
                Emit("save-hook failed: " + ex.Message);
            }
        }

        try
        {
            var map = new Dictionary<uint, Action>();
            foreach (var pair in Config.Hotkeys)
            {
                string action = pair.Key;
                if (Array.IndexOf(EditorActions.ActionIds, action) < 0)
                {
                    Emit($"hotkey ignored: unknown action '{action}'");
                    continue;
                }
                map[pair.Value] = () => RunActionLogged(action);
            }

            _hook = new HotkeyHook(map);
            if (Config.HotkeyDebug) _hook.Log = message => Emit("hotkey-debug: " + message);
            _hook.Start();
            Emit(_hook.IsRunning
                ? "hotkey: Ctrl+Alt+" + string.Join(", Ctrl+Alt+", map.Keys.Select(k => ((char)k).ToString()))
                : "hotkey: not installed");
        }
        catch (Exception ex)
        {
            Emit("hotkey failed: " + ex.Message);
        }
    }

    /// <summary>The project file extensions this profile owns (or all, when unknown).</summary>
    public static string[] ProjectExtensions() =>
        (Profile?.ProjectExtensions ?? ProfileRegistry.All.SelectMany(p => p.ProjectExtensions))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// The folders worth watching: the profile's own folders plus anything the config
    /// added, filtered to the ones that exist. When a profile has no folder on this
    /// machine the user's Documents folder is used instead, so autosave still works
    /// for an editor whose projects live somewhere non-standard.
    /// </summary>
    public static string[] ResolveDirectories()
    {
        var wanted = (Profile?.ProjectDirectories ?? ProfileRegistry.All.SelectMany(p => p.ProjectDirectories))
            .Select(ProfileRegistry.Expand)
            .Concat(Config.ExtraDirectories.Select(ProfileRegistry.Expand))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var present = wanted.Where(Directory.Exists).ToList();
        if (present.Count > 0) return present.ToArray();

        if (Config.ExtraDirectories.Count > 0)
        {
            Emit("none of the folders in vpatch-editor.ini exist");
            return Array.Empty<string>();
        }

        string documents = ProfileRegistry.Expand("{Documents}");
        if (Directory.Exists(documents))
        {
            Emit("no project folder for this profile - using " + documents);
            return new[] { documents };
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Breadth-limited, count-limited file walk. A watched folder can be the whole
    /// Documents tree, so this stops at <paramref name="maxDepth"/> levels and
    /// <paramref name="maxFiles"/> files and skips dot-folders (.git, autosave).
    /// </summary>
    private static IEnumerable<string> EnumerateProjectCandidates(string root, int maxDepth, int maxFiles)
    {
        var pending = new Stack<string[]>();
        pending.Push(new[] { root, "0" });
        int seen = 0;

        while (pending.Count > 0 && seen < maxFiles)
        {
            string[] entry = pending.Pop();
            string dir = entry[0];
            int depth = int.Parse(entry[1]);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (string file in files)
            {
                seen++;
                yield return file;
                if (seen >= maxFiles) yield break;
            }

            if (depth >= maxDepth) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (string sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (name.Length == 0 || name[0] == '.') continue;
                pending.Push(new[] { sub, (depth + 1).ToString() });
            }
        }
    }

    /// <summary>Newest project file across the watched folders — the one a hotkey acts on.</summary>
    public static string? FindCurrentProject()
    {
        string? newest = null;
        DateTime newestWrite = DateTime.MinValue;
        var extensions = ProjectExtensions();

        foreach (string dir in WatchedDirectories)
        {
            foreach (string file in EnumerateProjectCandidates(dir, 3, 5000))
            {
                if (!EditorActions.MatchesExtension(file, extensions)) continue;
                if (EditorActions.IsGeneratedPath(file)) continue;
                // Recovery and autosave copies carry a project extension too, and they are
                // usually the newest thing in the tree - they must never win this race.
                if (EditorActions.IsEditorScratchPath(file)) continue;

                DateTime write;
                try { write = File.GetLastWriteTimeUtc(file); }
                catch { continue; }

                if (write > newestWrite) { newestWrite = write; newest = file; }
            }
        }
        return newest;
    }

    /// <summary>Run one action id against the current project (<paramref name="file"/> overrides it).</summary>
    public static string RunAction(string action, string? file = null)
    {
        file ??= FindCurrentProject();
        if (file == null) return "no project found";

        switch (action.ToLowerInvariant())
        {
            case "snapshot":
                return EditorActions.Snapshot(file) ?? "snapshot failed";

            case "swing":
                return Changed(file, "swing", JobOptions.From(("division", "8"), ("amount", "55")));

            case "quantize":
                return Changed(file, "quantize", JobOptions.From(("grid", "16"), ("strength", "100")));

            case "staccato":
                return Changed(file, "staccato", JobOptions.From(("strength", "medium")));

            case "transpose":
                return Changed(file, "transpose", JobOptions.From(("semitones", "12")));

            case "normalize":
                return Changed(file, "normalize", JobOptions.From());

            case "harmony":
                return Changed(file, "harmony", JobOptions.From(("interval", "thirdUp"), ("root", "C"), ("scale", "major")));

            case "lyrics":
            {
                // A hotkey cannot take typed input, so the words come from the
                // clipboard: one syllable per line, cycled across the notes. The
                // old behaviour (write a fixed syllable to every note) would wipe a
                // project's lyrics on a single mis-keypress, so it is gone.
                string[] syllables = ClipboardText.ReadSyllables();
                if (syllables.Length == 0)
                    return "lyrics: clipboard is empty - copy one syllable per line first";
                return Changed(file, "lyrics", JobOptions.From(("texts", string.Join(",", syllables))));
            }

            case "pinyin":
            {
                int changed = EditorActions.Romanize(file, new RomanizationOptions
                {
                    Mode = RomanizationMode.Pinyin,
                    Separator = " ",
                });
                return changed == 0 ? "pinyin: no lyrics needed it" : $"pinyin: {changed} lyric(s) -> {file}";
            }

            case "export":
            {
                var written = EditorActions.ExportInterchange(file);
                return $"export: {written.Count} file(s) -> {Path.GetDirectoryName(written.FirstOrDefault() ?? file)}";
            }

            case "restore":
                return EditorActions.RestoreBackUp(file) ? "restored from .bak -> " + file : "no .bak to restore";

            default:
                return $"unknown action '{action}'";
        }
    }

    /// <summary>
    /// Hotkey path: run an action and always record the outcome. Hotkeys are silent by
    /// nature, so without this the only way to tell a fired hotkey from a dead hook is
    /// to go looking for side effects on disk.
    /// </summary>
    private static void RunActionLogged(string action)
    {
        string result;
        try { result = RunAction(action); }
        catch (Exception ex) { result = action + " failed: " + ex.Message; }
        Emit("hotkey " + action + " -> " + result);
    }

    private static string Changed(string file, string jobId, JobOptions options)
    {
        try
        {
            int changed = EditorActions.ApplyJob(file, jobId, options);
            return changed == 0
                ? $"{jobId}: no change -> {file}"
                : $"{jobId}: {changed} note(s) changed -> {file}";
        }
        catch (Exception ex)
        {
            return $"{jobId} failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Entry point used by the native CLR-hosting loader: it calls
    /// <c>ICLRRuntimeHost::ExecuteInDefaultAppDomain</c> against
    /// <c>VocalPatch.Editor.Bootstrap.Run</c>.
    /// </summary>
    public static int Run(string arg)
    {
        try
        {
            Initialize(string.IsNullOrWhiteSpace(arg) ? null : arg);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    public static void Shutdown()
    {
        _hook?.Dispose();
        _hook = null;
        _watcher?.Dispose();
        _watcher = null;
        _harmony?.UnpatchAll("com.vocalpatch.editor");
        _harmony = null;
        Initialized = false;
    }

    private static void Emit(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + Pid + "]  " + message + Environment.NewLine;

        // Several injected editors share this file. File.AppendAllText opens it for
        // exclusive write, so a concurrent writer makes this throw; retry briefly rather
        // than dropping the line, because the log is the only way to verify an injection.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.AppendAllText(LogFilePath, line);
                break;
            }
            catch
            {
                Thread.Sleep(15);
            }
        }

        Log?.Invoke(null, message);
    }
}
