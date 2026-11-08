using VocalPatch.Core.Formats;
using VocalPatch.Core.Lyrics;
using VocalPatch.Core.Model;
using VocalPatch.Editor;
using Xunit;

namespace VocalPatch.Tests;

/// <summary>
/// The injected feature set, driven exactly the way the hotkeys drive it. These run
/// in-process against real project files, so they cover the code path an injected
/// editor executes when a hotkey fires.
/// </summary>
public sealed class EditorActionTests
{
    private static Song Sample()
    {
        var song = new Song { Name = "Actions", Resolution = 480 };
        song.Tempos.Clear();
        song.Tempos.Add(new Tempo(0, 120));
        var track = new Track("Lead");
        track.Notes.Add(new Note(0, 240, 60, "你好"));
        track.Notes.Add(new Note(240, 240, 62, "世界"));
        song.Tracks.Add(track);
        song.Normalize();
        return song;
    }

    private static string WriteTemp(string ext, Song? song = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-actions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "song" + ext);
        ProjectIo.Save(song ?? Sample(), path);
        return path;
    }

    [Fact]
    public void ApplyJob_edits_in_place_and_keeps_a_bak()
    {
        string file = WriteTemp(".ust");
        string before = File.ReadAllText(file);

        int changed = EditorActions.ApplyJob(file, "transpose", Core.Jobs.JobOptions.From(("semitones", "12")));

        Assert.Equal(2, changed); // both notes moved
        Assert.True(File.Exists(file + ".bak"));
        Assert.Equal(before, File.ReadAllText(file + ".bak")); // bak is the pre-edit state

        var after = ProjectIo.Load(file);
        Assert.Equal(new[] { 72, 74 }, after.Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());
    }

    [Fact]
    public void RestoreBackUp_undoes_the_last_edit()
    {
        string file = WriteTemp(".ust");
        var original = ProjectIo.Load(file).Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray();

        EditorActions.ApplyJob(file, "transpose", Core.Jobs.JobOptions.From(("semitones", "-5")));
        Assert.Equal(new[] { 55, 57 }, ProjectIo.Load(file).Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());

        Assert.True(EditorActions.RestoreBackUp(file));
        Assert.Equal(original, ProjectIo.Load(file).Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());
    }

    [Fact]
    public void Harmony_adds_a_track_in_place()
    {
        string file = WriteTemp(".svp");
        EditorActions.ApplyJob(file, "harmony",
            Core.Jobs.JobOptions.From(("interval", "thirdUp"), ("root", "C"), ("scale", "major")));

        var song = ProjectIo.Load(file);
        Assert.Equal(2, song.Tracks.Count);
        Assert.Equal(new[] { 64, 65 }, song.Tracks[1].SortedNotes().Select(n => n.Pitch).ToArray());
    }

    [Fact]
    public void Romanize_converts_hanzi_in_place()
    {
        string file = WriteTemp(".ust");
        var options = new RomanizationOptions { Mode = RomanizationMode.Pinyin, Separator = " " };

        int changed = EditorActions.Romanize(file, options);

        Assert.Equal(2, changed);
        var lyrics = ProjectIo.Load(file).Tracks[0].SortedNotes().Select(n => n.Lyric).ToArray();
        Assert.Equal(new[] { "ni hao", "shi jie" }, lyrics);
    }

    [Fact]
    public void ExportInterchange_writes_every_editor_format()
    {
        string file = WriteTemp(".ust");
        string outDir = Path.Combine(Path.GetDirectoryName(file)!, "interchange");

        var written = EditorActions.ExportInterchange(file, outDir);

        Assert.Equal(EditorActions.InterchangeFormats.Length, written.Count);
        foreach (string path in written) Assert.True(File.Exists(path), path);
        Assert.True(File.Exists(Path.Combine(outDir, "song.svp")));
        Assert.True(File.Exists(Path.Combine(outDir, "song.ccs")));
        Assert.True(File.Exists(Path.Combine(outDir, "song.ustx")));
        Assert.True(File.Exists(Path.Combine(outDir, "song.vsqx")));
        Assert.True(File.Exists(Path.Combine(outDir, "song.mid")));
    }

    [Fact]
    public void Snapshot_lands_in_the_autosave_folder()
    {
        string file = WriteTemp(".ust");
        string? snapshot = EditorActions.Snapshot(file);

        Assert.NotNull(snapshot);
        Assert.Contains(".vocalpatch-autosave", snapshot!);
        Assert.True(File.Exists(snapshot));
    }

    [Fact]
    public void Snapshot_never_snapshots_its_own_output()
    {
        // A snapshot keeps the project's extension and lives in a subfolder of the same
        // tree, so a watcher that only filters by extension would snapshot it again and
        // nest: .vocalpatch-autosave\.vocalpatch-autosave\...
        string file = WriteTemp(".ust");
        string? first = EditorActions.Snapshot(file);
        Assert.NotNull(first);

        Assert.True(EditorActions.IsGeneratedPath(first!));
        Assert.Null(EditorActions.Snapshot(first!));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(first!)!, ".vocalpatch-autosave")));
    }

    [Fact]
    public void Two_snapshots_in_the_same_second_both_survive()
    {
        string file = WriteTemp(".ust");

        // Both calls run inside the same second, so they want the same timestamped name.
        string? first = EditorActions.Snapshot(file);
        string? second = EditorActions.Snapshot(file);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Theory]
    [InlineData(@"C:\proj\.vocalpatch-autosave\song.20261107-210802.ust", true)]
    [InlineData(@"C:\proj\.vocalpatch-autosave\.vocalpatch-autosave\song.ust", true)]
    [InlineData(@"C:\proj\vocalpatch-export\song.svp", true)]
    [InlineData(@"C:\proj\song.ust.bak", true)]
    [InlineData(@"C:\proj\song.ust", false)]
    [InlineData(@"C:\proj\backup\song.ust", false)]
    [InlineData(@"C:\vocalpatch-export\song.ust", true)]
    public void IsGeneratedPath_separates_our_own_output_from_projects(string path, bool expected)
    {
        Assert.Equal(expected, EditorActions.IsGeneratedPath(path));
    }

    /// <summary>
    /// Editor scratch files carry project extensions, so they have to be excluded by name
    /// and by folder. Measured on a real machine: the OpenUtau profile watched
    /// <c>Documents\OpenUtau</c>, whose only project file is the editor's own
    /// <c>Backups\Untitled-autosave.ustx</c> (360 KB), and the Synthesizer V profile watched
    /// <c>Documents\Dreamtonics</c>, where every .svp is a crash-recovery copy. Both were
    /// being snapshotted on every cycle.
    /// </summary>
    [Theory]
    [InlineData(@"D:\docs\OpenUtau\Backups\Untitled-autosave.ustx", true)]
    [InlineData(@"D:\docs\Dreamtonics\Synthesizer V Studio\recovery\song.svp", true)]
    [InlineData(@"D:\docs\Dreamtonics\Synthesizer V Studio\recovery\recovery\song.svp", true)]
    [InlineData(@"C:\proj\backup\song.ust", true)]
    [InlineData(@"C:\proj\song-autosave.svp", true)]
    [InlineData(@"C:\proj\~$song.svp", true)]
    [InlineData(@"C:\proj\song.ust", false)]
    [InlineData(@"D:\songs\my song.svp", false)]
    [InlineData(@"D:\songs\backup of my song.svp", false)]
    public void IsEditorScratchPath_skips_the_editors_own_files(string path, bool expected)
    {
        Assert.Equal(expected, EditorActions.IsEditorScratchPath(path));
    }

    [Fact]
    public void Snapshot_refuses_to_copy_an_editor_scratch_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string scratch = Path.Combine(dir, "song-autosave.ust");
        File.WriteAllText(scratch, "x");

        Assert.Null(EditorActions.Snapshot(scratch));
        Assert.False(Directory.Exists(Path.Combine(dir, EditorActions.AutoSaveFolderName)));
    }

    [Fact]
    public void Watcher_ignores_the_snapshots_it_just_wrote()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string project = Path.Combine(dir, "song.ust");
        File.WriteAllText(project, "[#VERSION]\nUST Version1.2\n[#SETTING]\nTempo=120.00\n[#0000]\nLength=480\nLyric=a\nNoteNum=60\n");

        var captured = new List<string>();
        using (var watcher = new ProjectWatcher(new[] { dir }, new[] { ".ust" }, TimeSpan.FromMilliseconds(1)))
        {
            watcher.SnapshotTaken += (_, path) => captured.Add(path);

            // Touch the project (a real change the watcher should capture)...
            File.SetLastWriteTimeUtc(project, DateTime.UtcNow);

            // ...and let it write a snapshot into dir\.vocalpatch-autosave, which must
            // not come back around as another project. Wait long enough for the file
            // system notifications to be delivered more than once.
            WaitFor(() => captured.Count > 0, TimeSpan.FromSeconds(5));
            Thread.Sleep(1200);
        }

        Assert.NotEmpty(captured);

        // Every snapshot the watcher took must be a direct child of dir\.vocalpatch-autosave.
        // A nested .vocalpatch-autosave below it is the signature of the runaway loop:
        // the watcher seeing its own snapshot as a fresh project and capturing that too.
        string autosave = Path.Combine(dir, EditorActions.AutoSaveFolderName) + Path.DirectorySeparatorChar;
        Assert.All(captured, path => Assert.True(
            path.StartsWith(autosave, StringComparison.OrdinalIgnoreCase), path));
        Assert.False(Directory.Exists(Path.Combine(autosave, EditorActions.AutoSaveFolderName)));
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition()) Thread.Sleep(25);
    }

    [Fact]
    public void Profile_lookup_accepts_the_id_the_process_name_and_the_display_name()
    {
        Assert.Equal("synthv", ProfileRegistry.ById("synthv")!.Id);
        Assert.Equal("synthv", ProfileRegistry.ById("synthv-studio")!.Id);
        Assert.Equal("synthv", ProfileRegistry.ById("Synthesizer V Studio")!.Id);
        Assert.Null(ProfileRegistry.ById("nope"));
        Assert.Null(ProfileRegistry.ById(""));
    }

    [Fact]
    public void TryLoad_survives_a_file_that_is_not_a_project()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-actions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string junk = Path.Combine(dir, "broken.ust");
        File.WriteAllText(junk, "not a ust at all");

        // No exception, just null (or a tolerant parse) - never a crash in the host.
        var song = EditorActions.TryLoad(junk);
        Assert.True(song == null || song.NoteCount >= 0);
    }

    [Fact]
    public void ActionIds_and_Bootstrap_agree()
    {
        Assert.Contains("swing", EditorActions.ActionIds);
        Assert.Contains("export", EditorActions.ActionIds);
        Assert.Contains("restore", EditorActions.ActionIds);
        Assert.Equal(11, EditorActions.ActionIds.Length);
    }
}

public sealed class EditorConfigTests
{
    [Theory]
    [InlineData("Q", 0x51u)]
    [InlineData("q", 0x51u)]
    [InlineData("0x53", 0x53u)]
    [InlineData("83", 83u)]
    public void Hotkey_values_parse(string text, uint expected)
    {
        Assert.True(EditorRuntimeConfig.TryParseKey(text, out uint vk));
        Assert.Equal(expected, vk);
    }

    [Fact]
    public void Ini_overrides_defaults_and_ignores_junk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "vpatch-editor.ini");
        File.WriteAllLines(ini, new[]
        {
            "# comment",
            "profile=synthv",
            "autosave=false",
            "patchsaves=true",
            "snapshotinterval=15",
            "hotkey.swing=0x4B",
            "hotkey.bogusaction=nonsense-key",
            "garbage line without equals",
        });

        var config = EditorRuntimeConfig.Load(ini);

        Assert.Equal("synthv", config.Profile);
        Assert.False(config.AutoSave);
        Assert.True(config.PatchSaves);
        Assert.Equal(15, config.SnapshotIntervalSeconds);
        Assert.Equal(0x4Bu, config.Hotkeys["swing"]);
        Assert.Equal(0x56u, config.Hotkeys["snapshot"]); // untouched default
    }

    [Fact]
    public void Missing_ini_falls_back_to_defaults()
    {
        var config = EditorRuntimeConfig.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.ini"));
        Assert.True(config.AutoSave);
        Assert.True(config.PatchSaves);
        Assert.Equal(11, config.Hotkeys.Count);
    }

    /// <summary>
    /// Every action must be reachable from a hotkey, and no two actions may share one.
    /// An unbound action is only reachable from the command line; a duplicate binding
    /// makes one action silently unreachable and fires the wrong action on a keypress.
    /// </summary>
    [Fact]
    public void Default_hotkeys_bind_every_action_exactly_once()
    {
        var hotkeys = EditorRuntimeConfig.DefaultHotkeys();

        Assert.Equal(EditorActions.ActionIds.Length, hotkeys.Count);
        foreach (string action in EditorActions.ActionIds)
        {
            Assert.True(hotkeys.ContainsKey(action), $"action '{action}' has no default hotkey");
        }

        // Distinct virtual keys: 11 actions, 11 different keys.
        Assert.Equal(hotkeys.Count, hotkeys.Values.Distinct().Count());
    }
}
