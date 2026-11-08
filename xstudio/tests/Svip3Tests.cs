using VocalPatch.Core.Formats;
using VocalPatch.Core.Model;
using VocalPatch.XStudio;
using Xunit;

namespace VocalPatch.XStudio.Tests;

/// <summary>
/// The X Studio codec. <c>Reads_editor_wire_bytes</c> matters more than the rest: it runs
/// against a fixture built byte by byte from the recovered schema rather than from
/// <see cref="Svip3Format.Write"/>, so the reader is not merely agreeing with its own
/// writer. <c>Reads_past_what_the_model_cannot_hold</c> pins the tolerance for curves,
/// vibrato, audio tracks and unknown fields, which is what keeps a project saved by a
/// newer editor readable.
///
/// The static constructor is also the documentation of the integration: registering the
/// format is the only thing the toolkit needs to know <c>.svip3</c> — see the CLI.
/// </summary>
public sealed class Svip3Tests
{
    static Svip3Tests() => FormatRegistry.Register(new Svip3Format());

    /// <summary>
    /// A hand-built AppModel: project path, version 3.0.3, canvas, 120 BPM, 6/8, one
    /// singing track called "Lead" (muted, with a voice GUID) holding 风/feng, 开 and a
    /// rest, plus an audio track beside it. Inside the notes there is a vibrato
    /// sub-message, an unknown field 99 on a note and field 30 on the AppModel, and the
    /// pattern carries an edited pitch line — all of which have no home in the neutral
    /// model and must be skipped rather than choke the read.
    /// </summary>
    private const string FixtureHex =
        "0A18433A2F70726F6A656374732F73616D706C652E73766970331205332E302E" +
        "3318808407220310E05D2A0612040806100832D2010A2E747970652E676F6F67" +
        "6C65617069732E636F6D2F7873747564696F2E70726F746F2E53696E67696E67" +
        "547261636B129F010D3333333F180122044C6561643207234543384535393802" +
        "425A0A09E69CAAE591BDE5908D1002180020B00930B0094221080010E003183C" +
        "2203E9A38E2A0466656E673060380162050D0000B0409806B960420D08E00310" +
        "E003183E2203E5BC80420808C00710F001183C4A070800150000003F4A246335" +
        "6564353361302D396438342D346661372D626566352D65393930383762303436" +
        "663065000090C132590A2C747970652E676F6F676C65617069732E636F6D2F78" +
        "73747564696F2E70726F746F2E417564696F547261636B122922074261636B69" +
        "6E673801421C0A074261636B696E67100120E48907420B6261636B696E672E6D" +
        "70334808F00107";

    private static byte[] Fixture() => Convert.FromHexString(FixtureHex);

    private static string Temp(string ext)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }

    private static Song LoadFixture()
    {
        string path = Temp(".svip3");
        File.WriteAllBytes(path, Fixture());
        return ProjectIo.Load(path);
    }

    [Fact]
    public void Reads_editor_wire_bytes()
    {
        var song = LoadFixture();

        Assert.Equal("sample", song.Name);              // from project_file_path
        Assert.Equal(Svip3Format.TicksPerBeat, song.Resolution);
        Assert.Equal(120.0, song.Tempos[0].Bpm, 3);      // tempo was BPM * 100 on the wire
        Assert.Equal(6, song.TimeSignatures[0].Numerator);
        Assert.Equal(8, song.TimeSignatures[0].Denominator);

        // The audio track beside it is not a note track and is not invented as one.
        Assert.Single(song.Tracks);
        var track = song.Tracks[0];
        Assert.Equal("Lead", track.Name);
        Assert.True(track.Muted);
        Assert.Equal("c5ed53a0-9d84-4fa7-bef5-e99087b046f0", track.Singer);

        Assert.Equal(3, track.Notes.Count);
        var feng = track.Notes[0];
        Assert.Equal(0, feng.Position);
        Assert.Equal(480, feng.Duration);
        Assert.Equal(60, feng.Pitch);
        Assert.Equal("风", feng.Lyric);
        Assert.Equal("feng", feng.Phoneme);              // pronouncing -> Phoneme

        var kai = track.Notes[1];
        Assert.Equal(480, kai.Position);
        Assert.Equal(62, kai.Pitch);
        Assert.Equal("开", kai.Lyric);
        Assert.Null(kai.Phoneme);

        // A note with no lyric is how X Studio holds a gap open.
        var rest = track.Notes[2];
        Assert.Equal(960, rest.Position);
        Assert.Equal(240, rest.Duration);
        Assert.True(rest.IsRest);
    }

    [Fact]
    public void Reads_past_what_the_model_cannot_hold()
    {
        // Same fixture: it carries a vibrato, an edited pitch line, an audio track and two
        // unknown field numbers, and reading it must simply succeed.
        var song = LoadFixture();
        Assert.Equal(3, song.NoteCount);
        Assert.Equal(1200, song.EndTick);                // 960 + 240, the pattern's real_dur
    }

    [Fact]
    public void Round_trips_notes()
    {
        var song = new Song { Name = "Round Trip" };
        song.Tempos.Clear();
        song.Tempos.Add(new Tempo(0, 128));
        var track = new Track("Lead");
        track.Notes.Add(new Note(0, 480, 60, "ni"));
        track.Notes.Add(new Note(480, 480, 62, "hao") { Phoneme = "hao" });
        track.Notes.Add(new Note(960, 960, 64, "shi"));
        song.Tracks.Add(track);
        song.Normalize();

        string path = Temp(".svip3");
        ProjectIo.Save(song, path);
        var back = ProjectIo.Load(path);

        Assert.Equal(3, back.NoteCount);
        Assert.Equal(new[] { 60, 62, 64 }, back.Tracks[0].SortedNotes().Select(n => n.Pitch).ToArray());
        Assert.Equal(new[] { "ni", "hao", "shi" }, back.Tracks[0].SortedNotes().Select(n => n.Lyric).ToArray());
        Assert.Equal(new[] { 0, 480, 960 }, back.Tracks[0].SortedNotes().Select(n => n.Position).ToArray());
        Assert.Equal(128, back.Tempos[0].Bpm, 3);
        Assert.Equal("hao", back.Tracks[0].SortedNotes().ElementAt(1).Phoneme);
    }

    [Fact]
    public void Merges_continuations_on_write()
    {
        var song = new Song { Name = "Held" };
        var track = new Track("Lead");
        track.Notes.Add(new Note(0, 480, 60, "la"));
        track.Notes.Add(new Note(480, 480, 60, "+~"));   // the same syllable, held longer
        song.Tracks.Add(track);

        string path = Temp(".svip3");
        ProjectIo.Save(song, path);
        var back = ProjectIo.Load(path);

        // X Studio would sing the token aloud, so the continuation is merged, not written.
        var note = Assert.Single(back.Tracks[0].Notes);
        Assert.Equal("la", note.Lyric);
        Assert.Equal(960, note.Duration);
    }

    [Fact]
    public void Rests_are_written_the_way_the_editor_marks_them()
    {
        var song = new Song { Name = "Gap" };
        var track = new Track("Lead");
        track.Notes.Add(new Note(0, 480, 60, "R"));
        track.Notes.Add(new Note(480, 480, 62, "do"));
        song.Tracks.Add(track);

        string path = Temp(".svip3");
        ProjectIo.Save(song, path);
        var back = ProjectIo.Load(path);

        // A real project marks a rest with "-" in the lyric. The pronouncing field is not
        // invented: a rest coming from another format had none, so it gets none.
        Assert.Equal(2, back.Tracks[0].Notes.Count);
        var rest = back.Tracks[0].Notes[0];
        Assert.True(rest.IsRest);
        Assert.Equal("-", rest.Lyric);
        Assert.Null(rest.Phoneme);
        Assert.Equal("do", back.Tracks[0].Notes[1].Lyric);

        // ...and writing that back again changes nothing, so the marker does not drift.
        string twice = Temp(".svip3");
        ProjectIo.Save(back, twice);
        var again = ProjectIo.Load(twice);
        Assert.Equal("-", again.Tracks[0].Notes[0].Lyric);
    }

    [Fact]
    public void Registry_learns_the_format_by_id_and_extension()
    {
        Assert.Equal("svip3", FormatRegistry.ById("svip3")!.Name);
        Assert.Equal("svip3", FormatRegistry.ByExtension(".svip3")!.Name);

        // ...and that is all ProjectIo needs to read and write .svip3 by extension.
        var song = new Song { Name = "Registered" };
        var track = new Track("Lead");
        track.Notes.Add(new Note(0, 480, 60, "la"));
        song.Tracks.Add(track);

        string path = Temp(".svip3");
        ProjectIo.Save(song, path);
        Assert.Equal(1, ProjectIo.Load(path).NoteCount);
    }

    [Fact]
    public void Writes_a_format_version_the_editor_has_been_seen_to_write()
    {
        var song = new Song { Name = "Version" };
        song.Tracks.Add(new Track("Lead"));
        string path = Temp(".svip3");
        ProjectIo.Save(song, path);

        // Field 2, length-delimited, five bytes: the file starts with the version string,
        // and the head can still be read as UTF-8 by anything that sniffs for text.
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(0x12, bytes[0]);
        Assert.Equal(5, bytes[1]);
        Assert.Equal(Svip3Format.FormatVersion, System.Text.Encoding.ASCII.GetString(bytes, 2, 5));
    }

    [Fact]
    public void A_truncated_file_is_an_error_rather_than_a_wrong_song()
    {
        string path = Temp(".svip3");
        File.WriteAllBytes(path, Fixture().Take(20).ToArray());
        Assert.Throws<FormatException>(() => ProjectIo.Load(path));
    }

    [Fact]
    public void An_appledouble_sidecar_is_named_as_such()
    {
        // 00 05 16 07 is the AppleDouble magic: the shipped templates carry one of these
        // beside every project, named "._<project>.svip3". Failing with the parser's
        // "field number 0" reads like a bug in this codec; it is not one.
        string path = Temp(".svip3");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x05, 0x16, 0x07, 0x00, 0x02, 0x00, 0x00 });

        var error = Assert.Throws<FormatException>(() => ProjectIo.Load(path));
        Assert.Contains("AppleDouble", error.Message);
    }
}
