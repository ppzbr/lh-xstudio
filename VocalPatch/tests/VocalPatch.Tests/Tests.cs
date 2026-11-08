using VocalPatch.Core.Autosave;
using VocalPatch.Core.Formats;
using VocalPatch.Core.Jobs;
using VocalPatch.Core.Lyrics;
using VocalPatch.Core.Model;
using Xunit;

namespace VocalPatch.Tests;

public sealed class FormatTests
{
    private static Song SampleSong()
    {
        var song = new Song { Name = "Test Song", Resolution = 480 };
        song.Tempos.Clear();
        song.Tempos.Add(new Tempo(0, 128));
        var track = new Track("Lead") { Singer = "Voice" };
        track.Notes.Add(new Note(0, 480, 60, "ni"));
        track.Notes.Add(new Note(480, 480, 62, "hao"));
        track.Notes.Add(new Note(960, 960, 64, "shi"));
        song.Tracks.Add(track);
        song.Normalize();
        return song;
    }

    private static string Temp(string ext) =>
        Path.Combine(Path.GetTempPath(), "vpatch-tests", Guid.NewGuid().ToString("N") + ext);

    [Fact]
    public void Ust_round_trips_notes()
    {
        string path = Temp(".ust");
        ProjectIo.Save(SampleSong(), path);
        var back = ProjectIo.Load(path);
        Assert.Equal(3, back.NoteCount);
        Assert.Equal(new[] { 60, 62, 64 }, back.Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());
        Assert.Equal(new[] { "ni", "hao", "shi" }, back.Tracks[0].SortedNotes().Select(n => n.Lyric).ToArray());
        Assert.Equal(128, back.Tempos[0].Bpm, 3);
    }

    [Fact]
    public void Svp_round_trips_notes()
    {
        string path = Temp(".svp");
        ProjectIo.Save(SampleSong(), path);
        var back = ProjectIo.Load(path);
        Assert.Equal(3, back.NoteCount);
        Assert.Equal(new[] { 60, 62, 64 }, back.Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());
    }

    [Fact]
    public void Midi_round_trips_notes()
    {
        string path = Temp(".mid");
        ProjectIo.Save(SampleSong(), path);
        var back = ProjectIo.Load(path);
        Assert.Equal(3, back.NoteCount);
        Assert.Equal(60, back.Tracks[0].SortedNotes().First().Pitch);
    }

    [Fact]
    public void Ustx_round_trips_notes()
    {
        string path = Temp(".ustx");
        ProjectIo.Save(SampleSong(), path);
        var back = ProjectIo.Load(path);
        Assert.Equal(3, back.NoteCount);
        Assert.Equal(480, back.Resolution);
    }

    /// <summary>
    /// YamlDotNet keeps a per-type callback cache in a plain dictionary, so two threads
    /// serializing YAML at once used to corrupt it and throw "Operations that change
    /// non-concurrent collections must have exclusive access". That is reachable in the
    /// injected runtime - the export hotkey and the autosave watcher are separate threads -
    /// so the access is serialized instead of left to luck.
    /// </summary>
    [Fact]
    public void Ustx_survives_concurrent_reads_and_writes()
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

        System.Threading.Tasks.Parallel.For(0, 32, _ =>
        {
            try
            {
                for (int pass = 0; pass < 4; pass++)
                {
                    string path = Temp(".ustx");
                    ProjectIo.Save(SampleSong(), path);
                    var back = ProjectIo.Load(path);
                    Assert.Equal(3, back.NoteCount);
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex.GetType().Name + ": " + ex.Message);
            }
        });

        Assert.True(failures.IsEmpty, string.Join(" | ", failures));
    }

    [Fact]
    public void Vpr_round_trips_notes()
    {
        string path = Temp(".vpr");
        ProjectIo.Save(SampleSong(), path);
        var back = ProjectIo.Load(path);
        Assert.Equal(3, back.NoteCount);
        Assert.Equal(new[] { 60, 62, 64 }, back.Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());
    }

    [Fact]
    public void JsonSong_round_trips_losslessly()
    {
        string path = Temp(".vp.json");
        var original = SampleSong();
        original.Tracks[0].Notes[0].Phoneme = "n i";
        ProjectIo.Save(original, path);
        var back = ProjectIo.Load(path);
        Assert.Equal(3, back.NoteCount);
        Assert.Equal("n i", back.Tracks[0].SortedNotes().First().Phoneme);
    }

    [Fact]
    public void Multi_suffix_extensions_resolve()
    {
        Assert.Equal("ufdata", FormatRegistry.Resolve(null, "song.ufdata.json").Name);
        Assert.Equal("json", FormatRegistry.Resolve(null, "song.vp.json").Name);
        Assert.Equal("commonnote", FormatRegistry.Resolve(null, "clip.commonnote.json").Name);
    }

    [Fact]
    public void Ust_preserves_gaps_as_rests()
    {
        var song = SampleSong();
        song.Tracks[0].Notes.Clear();
        song.Tracks[0].Notes.Add(new Note(0, 240, 60, "a"));
        song.Tracks[0].Notes.Add(new Note(960, 240, 62, "b")); // 480 tick gap
        string path = Temp(".ust");
        ProjectIo.Save(song, path);
        var back = ProjectIo.Load(path);
        Assert.Equal(2, back.NoteCount);
        Assert.Equal(960, back.Tracks[0].SortedNotes().Last().Position);
    }
}

public sealed class JobTests
{
    private static Song TwoNoteSong()
    {
        var song = new Song { Resolution = 480 };
        var track = new Track("T");
        track.Notes.Add(new Note(0, 240, 60, "a"));
        track.Notes.Add(new Note(240, 240, 62, "b"));
        song.Tracks.Add(track);
        song.Normalize();
        return song;
    }

    [Fact]
    public void Swing_delays_offbeat()
    {
        var song = TwoNoteSong();
        JobRegistry.ById("swing")!.Apply(song, JobOptions.From(("division", "8"), ("amount", "50")));
        var notes = song.Tracks[0].SortedNotes().ToList();
        Assert.Equal(0, notes[0].Position);
        Assert.Equal(360, notes[1].Position);
    }

    [Fact]
    public void Quantize_snaps_duration()
    {
        var song = new Song { Resolution = 480 };
        var track = new Track("T");
        track.Notes.Add(new Note(0, 500, 60, "a"));
        song.Tracks.Add(track);
        JobRegistry.ById("quantize")!.Apply(song, JobOptions.From(("grid", "4"), ("strength", "100")));
        Assert.Equal(480, song.Tracks[0].Notes[0].Duration);
    }

    [Fact]
    public void Harmony_uses_diatonic_third()
    {
        var song = new Song { Resolution = 480 };
        var track = new Track("T");
        track.Notes.Add(new Note(0, 240, 60, "a"));  // C
        track.Notes.Add(new Note(240, 240, 62, "b")); // D
        track.Notes.Add(new Note(480, 240, 64, "c")); // E
        song.Tracks.Add(track);
        JobRegistry.ById("harmony")!.Apply(song, JobOptions.From(("interval", "thirdUp"), ("root", "C"), ("scale", "major")));
        Assert.Equal(2, song.Tracks.Count);
        Assert.Equal(new[] { 64, 65, 67 }, song.Tracks[1].SortedNotes().Select(n => n.Pitch).ToArray());
    }

    [Fact]
    public void Transpose_shifts_by_semitones()
    {
        var song = TwoNoteSong();
        JobRegistry.ById("transpose")!.Apply(song, JobOptions.From(("semitones", "12")));
        Assert.Equal(new[] { 72, 74 }, song.Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());
    }
}

public sealed class LyricTests
{
    [Fact]
    public void Pinyin_converts_hanzi()
    {
        var options = new RomanizationOptions { Mode = RomanizationMode.Pinyin, Separator = " " };
        Assert.Equal("ni hao", PinyinConverter.Convert("你好", options));
    }

    [Fact]
    public void Pinyin_tone_marks()
    {
        var options = new RomanizationOptions { Mode = RomanizationMode.Pinyin, ToneMarks = true, Separator = " " };
        Assert.Equal("nǐ hǎo", PinyinConverter.Convert("你好", options));
    }

    [Fact]
    public void Romaji_converts_kana()
    {
        var options = new RomanizationOptions { Mode = RomanizationMode.Romaji };
        Assert.Equal("kyou", RomajiConverter.Convert("きょう", options));
    }

    [Fact]
    public void Syllabifier_splits_hanzi()
    {
        Assert.Equal(new[] { "你", "好" }, Syllabifier.Split("你好").ToArray());
    }

    [Fact]
    public void Language_detector_guesses()
    {
        Assert.Equal("Chinese", LanguageDetector.Describe("你好"));
        Assert.Equal("Japanese", LanguageDetector.Describe("きょう"));
        Assert.Equal("Latin", LanguageDetector.Describe("hello"));
    }
}

public sealed class AutosaveTests
{
    [Fact]
    public void BackupNow_writes_a_copy()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string project = Path.Combine(dir, "song.ust");
        File.WriteAllText(project, "[#SETTING]\nTempo=120\n[#TRACKEND]\n");

        using var saver = new AutoSaver(project, TimeSpan.FromSeconds(60));
        var backup = saver.BackupNow();

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        Directory.Delete(dir, recursive: true);
    }
}
