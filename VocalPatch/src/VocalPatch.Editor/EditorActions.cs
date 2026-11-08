using VocalPatch.Core.Formats;
using VocalPatch.Core.Jobs;
using VocalPatch.Core.Lyrics;
using VocalPatch.Core.Model;

namespace VocalPatch.Editor;

/// <summary>
/// The patcher feature set, usable on any project file the host editor writes.
/// Every in-place action keeps a <c>.bak</c> copy so it is undoable, and every
/// action is also reachable from the command line via <c>vpatch-inject action</c>.
/// </summary>
public static class EditorActions
{
    /// <summary>Formats the interchange export writes (one file per editor family).</summary>
    public static readonly string[] InterchangeFormats =
    {
        "svp", "ccs", "vsqx", "ustx", "ust", "midi", "musicxml", "json",
    };

    /// <summary>The action ids the runtime and the CLI expose.</summary>
    public static readonly string[] ActionIds =
    {
        "snapshot", "swing", "quantize", "staccato", "transpose", "normalize",
        "pinyin", "harmony", "lyrics", "export", "restore",
    };

    /// <summary>Copy the project into its <c>.vocalpatch-autosave</c> folder with a timestamp.</summary>
    public static string? Snapshot(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            if (IsGeneratedPath(file)) return null;       // never snapshot our own output
            if (IsEditorScratchPath(file)) return null;   // nor the editor's own scratch files
            string dir = Path.Combine(Path.GetDirectoryName(file) ?? ".", AutoSaveFolderName);
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string stem = Path.GetFileNameWithoutExtension(file);
            string extension = Path.GetExtension(file);
            string target = Path.Combine(dir, $"{stem}.{stamp}{extension}");

            // Two snapshots inside the same second would land on the same name. Suffix
            // them instead of throwing, so a burst of saves never loses a copy silently.
            for (int suffix = 1; File.Exists(target); suffix++)
                target = Path.Combine(dir, $"{stem}.{stamp}-{suffix}{extension}");

            File.Copy(file, target, overwrite: false);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Romanise the lyrics of a project and rewrite it in place (a <c>.bak</c> is kept).</summary>
    public static int Romanize(string file, RomanizationOptions options)
    {
        var song = ProjectIo.Load(file);
        int changed = PinyinConverter.ConvertSong(song, options);
        if (changed == 0) return 0;
        BackUp(file);
        ProjectIo.Save(song, file);
        return changed;
    }

    /// <summary>Convert a project to another format.</summary>
    public static void Convert(string input, string output, string? toFormatId = null)
    {
        var song = ProjectIo.Load(input);
        ProjectIo.Save(song, output, toFormatId);
    }

    /// <summary>
    /// Apply one edit job to the project in place. This is the injected equivalent of
    /// the patcher's jobs: it never touches anything but the note fields the job owns,
    /// and a <c>.bak</c> is written before the file is replaced.
    /// </summary>
    /// <returns>How many notes changed. 0 means the file was left untouched on disk.</returns>
    public static int ApplyJob(string file, string jobId, JobOptions options)
    {
        var job = JobRegistry.ById(jobId)
                  ?? throw new ArgumentException($"Unknown job '{jobId}'.", nameof(jobId));

        var song = ProjectIo.Load(file);
        var before = Notes(song);
        job.Apply(song, options);
        song.Normalize();

        int changed = Difference(before, Notes(song));
        if (changed == 0) return 0;

        BackUp(file);
        ProjectIo.Save(song, file);
        return changed;
    }

    /// <summary>Write the project out in every editor's format, for interchange.</summary>
    public static IReadOnlyList<string> ExportInterchange(
        string file,
        string? outputDirectory = null,
        IEnumerable<string>? formatIds = null)
    {
        var song = ProjectIo.Load(file);
        string root = outputDirectory
                      ?? Path.Combine(Path.GetDirectoryName(file) ?? ".", "vocalpatch-export");
        Directory.CreateDirectory(root);

        string baseName = Path.GetFileNameWithoutExtension(file);
        var written = new List<string>();
        foreach (string id in formatIds ?? InterchangeFormats)
        {
            var format = FormatRegistry.ById(id);
            if (format == null || format.Extensions.Count == 0) continue;
            string target = Path.Combine(root, baseName + format.Extensions[0]);
            ProjectIo.Save(song, target, id);
            written.Add(target);
        }
        return written;
    }

    /// <summary>Replace every lyric from a list (cycled) or a single syllable.</summary>
    public static int ReplaceLyrics(string file, string single, IReadOnlyList<string>? list = null)
    {
        var options = list is { Count: > 0 }
            ? JobOptions.From(("texts", string.Join(",", list)))
            : JobOptions.From(("text", single));
        return ApplyJob(file, "lyrics", options);
    }

    /// <summary>Undo the last in-place edit by restoring the <c>.bak</c> the action kept.</summary>
    public static bool RestoreBackUp(string file)
    {
        string bak = file + ".bak";
        if (!File.Exists(bak)) return false;
        File.Copy(bak, file, overwrite: true);
        return true;
    }

    /// <summary>True if the file's extension is one of <paramref name="extensions"/>.</summary>
    public static bool MatchesExtension(string file, IEnumerable<string> extensions)
    {
        foreach (string ext in extensions)
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Folder names an editor owns rather than the user: crash-recovery and backup folders.
    /// These hold copies of projects with ordinary project extensions - Synthesizer V keeps
    /// <c>recovery\*.svp</c>, OpenUtau keeps <c>Backups\*-autosave.ustx</c> - so without this
    /// the watcher copies the editor's own autosave output. Measured on a real machine: a
    /// 360 KB file per save cycle, growing without bound, and "the newest project file" would
    /// often resolve to a recovery copy instead of the project being edited.
    /// </summary>
    private static readonly HashSet<string> EditorScratchFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "recovery", "backup", "backups",
    };

    /// <summary>True for a path an editor owns rather than the user's project.</summary>
    public static bool IsEditorScratchPath(string path)
    {
        if (path.Length == 0) return false;

        // Synthesizer V names its recovery copies after the project, so the folder is what
        // identifies them; OpenUtau's autosave file is identified by its name.
        string name = Path.GetFileName(path);
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        if (name.IndexOf("autosave", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("auto-save", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        string? dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            string leaf = Path.GetFileName(dir);
            if (leaf.Length == 0) break;
            if (EditorScratchFolders.Contains(leaf)) return true;

            string? parent = Path.GetDirectoryName(dir);
            if (string.Equals(parent, dir, StringComparison.Ordinal)) break;
            dir = parent;
        }
        return false;
    }

    /// <summary>Folder the runtime writes its own snapshots into.</summary>
    public const string AutoSaveFolderName = ".vocalpatch-autosave";

    /// <summary>Folder the export action writes into.</summary>
    public const string ExportFolderName = "vocalpatch-export";

    /// <summary>
    /// True for paths this runtime produces itself: snapshots, exports and <c>.bak</c>
    /// files. Anything that picks "the project the user is working on" has to skip
    /// these. A snapshot keeps the extension of the project it came from, so without
    /// this check an autosave pass sees its own output as a fresh project, snapshots
    /// that, and nests forever (<c>….vocalpatch-autosave\.vocalpatch-autosave\…</c>).
    /// </summary>
    public static bool IsGeneratedPath(string path)
    {
        if (path.Length == 0) return false;
        if (path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return true;

        string? dir = path;
        while (!string.IsNullOrEmpty(dir))
        {
            string leaf = Path.GetFileName(dir);
            if (leaf.Length == 0) break;

            if (leaf.StartsWith(".vocalpatch-", StringComparison.OrdinalIgnoreCase)) return true;
            if (leaf.Equals(ExportFolderName, StringComparison.OrdinalIgnoreCase)) return true;

            string? parent = Path.GetDirectoryName(dir);
            if (string.Equals(parent, dir, StringComparison.Ordinal)) break;
            dir = parent;
        }
        return false;
    }

    /// <summary>Load a project, tolerating a file the host is still flushing.</summary>
    public static Song? TryLoad(string file)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return ProjectIo.Load(file);
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }

    private static void BackUp(string file) => File.Copy(file, file + ".bak", overwrite: true);

    /// <summary>Every note of the song as a comparable <c>position:length:pitch:lyric</c> line.</summary>
    private static List<string> Notes(Song song)
    {
        var lines = new List<string>();
        foreach (var track in song.Tracks)
            foreach (var note in track.SortedNotes())
                lines.Add($"{note.Position}:{note.Duration}:{note.Pitch}:{note.Lyric}");
        return lines;
    }

    /// <summary>How many notes were added, removed or edited between two snapshots.</summary>
    private static int Difference(List<string> before, List<string> after)
    {
        int diff = Math.Abs(after.Count - before.Count);
        int common = Math.Min(before.Count, after.Count);
        for (int i = 0; i < common; i++)
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) diff++;
        return diff;
    }
}
