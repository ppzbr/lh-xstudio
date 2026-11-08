using VocalPatch.Core.Formats;
using VocalPatch.Core.Model;

namespace VocalPatch.XStudio;

/// <summary>
/// X Studio project (.svip3), the editor shipped as NetEase Cloud Music XStudio.
///
/// The file is a serialized <c>xstudio.proto.AppModel</c> — Protocol Buffers, proto3, no
/// container, no compression. The schema is not on disk anywhere; it was recovered from
/// the editor's own image, which embeds the compiled <c>.proto</c> as a
/// <c>FileDescriptorProto</c>. <c>xstudio-format.md</c>, in this folder, has the field
/// tables and the method; every number in this file comes from it.
///
/// It lives outside <c>VocalPatch.Core</c> deliberately: it is one engine's format, not part
/// of the neutral model, and it implements <see cref="IProjectFormat"/> so it can be handed
/// to <see cref="FormatRegistry.Register"/> by whoever wants it — see this folder's README
/// for the two lines that gives the toolkit native <c>.svip3</c> support.
///
/// What this codec carries: tracks, patterns flattened to absolute note positions, notes
/// (pitch, position, length, lyric, pinyin), tempo and time signature. One unit note:
/// X Studio counts <b>480 ticks per beat</b>, measured against the template projects
/// shipped with the editor (their backing track's exact length in frames against their own
/// <c>AudioPattern.real_dur</c> gives 480.00 on seven of eight), and the neutral model's
/// <see cref="Song.Resolution"/> defaults to 480 as well — so positions map one to one.
///
/// What it does not carry, stated plainly rather than discovered later:
/// <list type="bullet">
/// <item>per-note vibrato and the piano-roll parameter curves (<c>edited_pitch_line</c> …
/// <c>merge_energy_value_line</c>) — the neutral model has nowhere to put them;</item>
/// <item>audio tracks: an <c>AudioTrack</c> is skipped, the model is notes only — so a
/// project written from here arrives without its backing track;</item>
/// <item>consonant, SP and silence lengths, the per-note tone and the track colour and
/// reverb, which are re-created from the editor's own defaults on write.</item>
/// </list>
/// A file written here is a project the editor can open and sing. It is not a byte-for-byte
/// copy of what the editor saved, and it does not claim to be.
/// </summary>
public sealed class Svip3Format : IProjectFormat
{
    /// <summary>Track type enum, <c>xstudio.proto.TrackType</c>.</summary>
    private const int TrackTypeAudio = 1;
    private const int TrackTypeSinging = 2;

    /// <summary>Pattern type enum, <c>xstudio.proto.PatternType</c>.</summary>
    private const int PatternTypeSinging = 2;

    public const string SingingTrackTypeUrl = "type.googleapis.com/xstudio.proto.SingingTrack";
    public const string AudioTrackTypeUrl = "type.googleapis.com/xstudio.proto.AudioTrack";

    /// <summary>
    /// Ticks per beat, measured — see the class comment. Kept public because a caller
    /// converting between X Studio and a different grid needs it.
    /// </summary>
    public const int TicksPerBeat = 480;

    /// <summary>File-format version written. The newest one measured, from a 3.0.3 project.</summary>
    public const string FormatVersion = "3.0.3";

    /// <summary>
    /// The canvas length the editor writes for a fresh, empty project. It is not the
    /// project's content length and not in the note ticks: measured across the eight
    /// templates, <c>AppModel.duration</c> runs 1.05–2.11× the content end. Since the unit
    /// cannot be derived, this codec reproduces the observed envelope instead — at least
    /// the empty-project floor, otherwise twice the content — rather than inventing a value.
    /// </summary>
    private const int CanvasFloor = 115200;

    /// <summary>Default pattern name ("unnamed"). Escaped so this source file stays ASCII.</summary>
    private const string UnnamedPattern = "\u672a\u547d\u540d";

    /// <summary>
    /// How the editor marks a rest on a note: both the lyric and the pronouncing field are
    /// a single hyphen. Measured, not chosen — see <see cref="NotesForWrite"/>.
    /// </summary>
    private const string RestMarker = "-";

    // Track defaults, all measured on a fresh project the editor created itself:
    // volume 0.7, colour #4E7985, reverb -18 dB, is_reverb_open left unset.
    private const float DefaultTrackVolume = 0.7f;
    private const string DefaultTrackColor = "#4E7985";
    private const float DefaultReverbDb = -18f;
    private const int DefaultPianoCells = 8;

    /// <summary>
    /// Unknown on purpose: <see cref="FormatKind"/> is VocalPatch.Core's own enum and has no
    /// X Studio member. Nothing dispatches on it — it is a label — so leaving it unknown is
    /// more honest than pretending to be another editor's format.
    /// </summary>
    public FormatKind Kind => FormatKind.Unknown;
    public string Name => "svip3";
    public string DisplayName => "X Studio Project (.svip3)";
    public IReadOnlyList<string> Extensions => new[] { ".svip3" };
    public bool CanRead => true;
    public bool CanWrite => true;

    // ---------------------------------------------------------------- reading

    public Song Read(Stream stream, string? path = null)
    {
        byte[] bytes = ReadAll(stream);
        if (bytes.Length < 2)
            throw new FormatException("Empty file: this is not an X Studio project.");

        // The shipped templates arrive in a macOS zip, so every project is accompanied by a
        // "._name.svip3" AppleDouble sidecar. Those start 00 05 16 07 - a field number of 0,
        // which no protobuf has. Say so, instead of failing deep inside the parser.
        if ((bytes[0] >> 3) == 0)
            throw new FormatException(
                "Not an X Studio project: the file does not start with a protobuf field " +
                "(an AppleDouble sidecar such as \"._name.svip3\" looks like this).");

        var song = new Song
        {
            Resolution = TicksPerBeat,
            SourcePath = path,
        };
        song.Tempos.Clear();
        song.TimeSignatures.Clear();

        var root = new ProtoReader(bytes);
        while (root.TryReadTag(out int field, out int wt))
        {
            switch (field)
            {
                case 1: // project_file_path — absolute on the machine that wrote it
                    string projectPath = Str(root, wt);
                    if (projectPath.Length > 0)
                        song.Name = Path.GetFileNameWithoutExtension(projectPath);
                    break;
                case 4: // repeated SongTempo
                    var tempo = Msg(root, wt);
                    if (tempo != null) ReadTempo(tempo, song);
                    break;
                case 5: // repeated SongBeat
                    var beat = Msg(root, wt);
                    if (beat != null) ReadBeat(beat, song);
                    break;
                case 6: // repeated google.protobuf.Any
                    var any = Msg(root, wt);
                    if (any != null) ReadAny(any, song);
                    break;
                default: // version, canvas, master, tones, marks, flags: nothing to map onto
                    root.SkipField(wt);
                    break;
            }
        }

        song.Normalize();
        if (string.IsNullOrEmpty(song.Name) || song.Name == "Untitled")
            song.Name = Path.GetFileNameWithoutExtension(path ?? string.Empty);
        if (string.IsNullOrEmpty(song.Name)) song.Name = "Untitled";
        return song;
    }

    private static void ReadTempo(ProtoReader r, Song song)
    {
        int position = 0;
        double bpm = 0;
        while (r.TryReadTag(out int f, out int wt))
        {
            switch (f)
            {
                case 1: position = Int(r, wt); break;
                case 2: bpm = Int(r, wt) / 100.0; break; // tempo is BPM * 100
                default: r.SkipField(wt); break;
            }
        }
        if (bpm > 0) song.Tempos.Add(new Tempo(Math.Max(0, position), bpm));
    }

    private static void ReadBeat(ProtoReader r, Song song)
    {
        int bar = 0, numerator = 4, denominator = 4;
        while (r.TryReadTag(out int f, out int wt))
        {
            switch (f)
            {
                // SongBeat.pos is a bar index, unlike SongTempo.pos which is a tick:
                // measured values are 0, 38 and 39 in a project where a bar is ~2.5 s,
                // and a time signature cannot change twice inside one beat.
                case 1: bar = Int(r, wt); break;
                case 2:
                    var size = Msg(r, wt);
                    if (size != null)
                    {
                        while (size.TryReadTag(out int sf, out int swt))
                        {
                            if (sf == 1 && swt == WireType.Varint) numerator = size.ReadInt32();
                            else if (sf == 2 && swt == WireType.Varint) denominator = size.ReadInt32();
                            else size.SkipField(swt);
                        }
                    }
                    break;
                default: r.SkipField(wt); break;
            }
        }
        if (numerator > 0 && denominator > 0)
            song.TimeSignatures.Add(new TimeSignature(Math.Max(0, bar), numerator, denominator));
    }

    /// <summary>A <c>google.protobuf.Any</c>: a type URL and the message it names.</summary>
    private static void ReadAny(ProtoReader r, Song song)
    {
        string? typeUrl = null;
        ProtoReader? value = null;
        while (r.TryReadTag(out int f, out int wt))
        {
            switch (f)
            {
                case 1:
                    if (wt != WireType.LengthDelimited) { r.SkipField(wt); break; }
                    typeUrl = r.ReadString();
                    break;
                case 2:
                    if (wt != WireType.LengthDelimited) { r.SkipField(wt); break; }
                    value = r.ReadMessage();
                    break;
                default: r.SkipField(wt); break;
            }
        }
        if (value == null) return;

        // Unknown payload types are skipped, not guessed at. That keeps a project from a
        // future editor version readable for its notes instead of failing outright.
        if (typeUrl != null && typeUrl.EndsWith("SingingTrack", StringComparison.Ordinal))
            ReadTrack(value, song);
    }

    private static void ReadTrack(ProtoReader r, Song song)
    {
        var track = new Track();
        var patterns = new List<ProtoReader>();
        while (r.TryReadTag(out int f, out int wt))
        {
            switch (f)
            {
                case 3:
                    if (wt != WireType.Varint) { r.SkipField(wt); break; }
                    track.Muted = r.ReadBool();
                    break;
                case 4:
                    string name = Str(r, wt);
                    if (name.Length > 0) track.Name = name;
                    break;
                case 7:
                    if (wt != WireType.Varint) { r.SkipField(wt); break; }
                    if (r.ReadInt32() == TrackTypeAudio) return; // not representable here
                    break;
                case 8:
                    var pattern = Msg(r, wt);
                    if (pattern != null) patterns.Add(pattern);
                    break;
                case 9:
                    // ai_singer_id is a voice GUID, not a name. It is kept in Singer so a
                    // round trip through this codec keeps the chosen voice.
                    string singer = Str(r, wt);
                    if (singer.Length > 0) track.Singer = singer;
                    break;
                default: r.SkipField(wt); break;
            }
        }

        foreach (var body in patterns) ReadPattern(body, track);
        song.Tracks.Add(track);
    }

    private static void ReadPattern(ProtoReader r, Track track)
    {
        int realPos = 0;
        var notes = new List<ProtoReader>();
        while (r.TryReadTag(out int f, out int wt))
        {
            switch (f)
            {
                case 3:
                    if (wt != WireType.Varint) { r.SkipField(wt); break; }
                    realPos = r.ReadInt32();
                    break;
                case 8: // repeated Note
                    var note = Msg(r, wt);
                    if (note != null) notes.Add(note);
                    break;
                default: // name, type, durations, curves, rap_type, sing_method_id
                    r.SkipField(wt);
                    break;
            }
        }

        // Patterns are flattened: the neutral model holds one note list per track with
        // absolute song positions, the same way the USTX codec flattens parts.
        foreach (var noteReader in notes)
        {
            var note = ReadNote(noteReader, realPos);
            if (note != null) track.Notes.Add(note);
        }
    }

    private static Note? ReadNote(ProtoReader r, int realPos)
    {
        int position = 0, duration = 0, pitch = 60;
        string lyric = string.Empty;
        string? pronouncing = null;
        while (r.TryReadTag(out int f, out int wt))
        {
            switch (f)
            {
                case 1: position = Int(r, wt); break;   // start_pos
                case 2: duration = Int(r, wt); break;   // width_pos
                case 3: pitch = Int(r, wt); break;      // key_index, a MIDI note number
                case 4: lyric = Str(r, wt); break;
                case 5:
                    string pron = Str(r, wt);
                    if (pron.Length > 0) pronouncing = pron;
                    break;
                default: r.SkipField(wt); break;        // consonant/SP/silence, tone, vibrato
            }
        }

        if (duration <= 0) return null;
        if (pitch < 0 || pitch > 127) return null;

        // A note with no lyric is X Studio's way of holding a gap open; the neutral model
        // calls that a rest.
        if (lyric.Length == 0) lyric = "R";

        return new Note(realPos + position, duration, pitch, lyric) { Phoneme = pronouncing };
    }

    // ---------------------------------------------------------------- writing

    public void Write(Song song, Stream stream)
    {
        song.Normalize();

        var w = new ProtoWriter();
        // project_file_path is deliberately not written: the output path is not known here,
        // and the editor fills it in on its next save.
        w.WriteString(2, FormatVersion);
        w.WriteInt32(3, Canvas(song));

        foreach (var t in song.Tempos.Where(t => t.Bpm > 0))
        {
            var tempo = t;
            w.WriteMessage(4, m =>
            {
                m.WriteInt32(1, Math.Max(0, tempo.Position));
                m.WriteInt32(2, (int)Math.Round(tempo.Bpm * 100)); // BPM * 100, measured
            });
        }

        foreach (var ts in song.TimeSignatures)
        {
            var sig = ts;
            w.WriteMessage(5, m =>
            {
                m.WriteInt32(1, Math.Max(0, sig.BarPosition));
                m.WriteMessage(2, b =>
                {
                    b.WriteInt32(1, sig.Numerator);
                    b.WriteInt32(2, sig.Denominator);
                });
            });
        }

        // The editor's own fresh project always carries one track; a trackless AppModel is
        // a shape it never produces, so an empty song is written as one empty track.
        if (song.Tracks.Count == 0)
        {
            w.WriteMessage(6, any => WriteAny(any, new Track("Track")));
        }
        else
        {
            foreach (var track in song.Tracks)
            {
                var t = track;
                w.WriteMessage(6, any => WriteAny(any, t));
            }
        }

        w.WriteInt32(9, DefaultPianoCells);
        byte[] bytes = w.ToArray();
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>At least the empty-project canvas, otherwise twice the content — see <see cref="CanvasFloor"/>.</summary>
    private static int Canvas(Song song)
    {
        long content = 2L * Math.Max(0, song.EndTick);
        return content > CanvasFloor ? (int)Math.Min(int.MaxValue, content) : CanvasFloor;
    }

    private static void WriteAny(ProtoWriter w, Track track)
    {
        w.WriteString(1, SingingTrackTypeUrl);
        w.WriteMessage(2, inner => WriteTrack(inner, track));
    }

    private static void WriteTrack(ProtoWriter w, Track track)
    {
        w.WriteFloat(1, DefaultTrackVolume);
        w.WriteBool(3, track.Muted);
        w.WriteString(4, string.IsNullOrWhiteSpace(track.Name) ? "Track" : track.Name);
        w.WriteString(6, DefaultTrackColor);
        w.WriteInt32(7, TrackTypeSinging);
        w.WriteMessage(8, p => WritePattern(p, track));
        // A GUID from a project this codec read; a voice name from any other format is not
        // an id, so anything that does not look like one is left out and the editor picks
        // its own default voice for the track.
        if (LooksLikeGuid(track.Singer)) w.WriteString(9, track.Singer);
        w.WriteFloat(12, DefaultReverbDb);
    }

    private static void WritePattern(ProtoWriter w, Track track)
    {
        List<Note> notes = NotesForWrite(track);
        int end = 0;
        foreach (var n in notes) end = Math.Max(end, n.End);

        w.WriteString(1, UnnamedPattern);
        w.WriteInt32(2, PatternTypeSinging);
        w.WriteInt32(4, end); // real_dur; real_pos stays 0, notes carry absolute positions
        w.WriteInt32(6, end); // play_dur mirrors real_dur in every pattern measured
        foreach (var n in notes) w.WriteMessage(8, x => WriteNote(x, n));
    }

    /// <summary>
    /// Notes as X Studio wants them: continuations merged into the note they continue, and
    /// rests marked the way the editor marks them.
    ///
    /// A "+~" note is the neutral model's way of holding the previous syllable longer;
    /// X Studio has no continuation marker and would sing the token aloud, so it is merged
    /// into the note it continues. Its rests are explicit: a real project carries
    /// <c>lyric = "-"</c> and <c>pronouncing = "-"</c> on them, in both cases with a pitch
    /// of their own and a gap before them, which is what separates a rest from a hold —
    /// measured across the template projects, where 14 such notes appear in one file and
    /// every one of them differs in pitch from the note before it.
    /// </summary>
    private static List<Note> NotesForWrite(Track track)
    {
        var notes = new List<Note>();
        foreach (var n in track.SortedNotes())
        {
            bool continuation = n.Lyric.Length > 0 && n.Lyric[0] == '+';
            if (continuation && notes.Count > 0)
            {
                var previous = notes[notes.Count - 1];
                previous.Duration = Math.Max(previous.End, n.End) - previous.Position;
                continue;
            }
            var copy = n.Clone();
            if (copy.Duration < 1) copy.Duration = 1;
            if (copy.Pitch < 0 || copy.Pitch > 127) copy.Pitch = 60;
            if (copy.IsRest)
            {
                // The lyric marker is what makes it a rest. The pronouncing field is left
                // exactly as it came in: real projects are inconsistent here — some carry
                // "-" there too, some leave it out — so neither form is invented.
                copy.Lyric = RestMarker;
            }
            notes.Add(copy);
        }
        return notes;
    }

    private static void WriteNote(ProtoWriter w, Note n)
    {
        w.WriteInt32(1, n.Position);   // a pattern may have been dragged left of tick 0
        w.WriteInt32(2, n.Duration);   // width_pos
        w.WriteInt32(3, n.Pitch);      // key_index
        w.WriteString(4, n.Lyric);
        w.WriteString(5, n.Phoneme);   // pronouncing: for a Chinese project this is pinyin
    }

    private static bool LooksLikeGuid(string? s)
    {
        if (s == null || s.Length != 36) return false;
        for (int i = 0; i < 36; i++)
        {
            char c = s[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-') return false;
            }
            else if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- plumbing

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Read a length-delimited field, or skip it when the wire type disagrees.</summary>
    private static string Str(ProtoReader r, int wt)
    {
        if (wt != WireType.LengthDelimited)
        {
            r.SkipField(wt);
            return string.Empty;
        }
        return r.ReadString();
    }

    private static ProtoReader? Msg(ProtoReader r, int wt)
    {
        if (wt != WireType.LengthDelimited)
        {
            r.SkipField(wt);
            return null;
        }
        return r.ReadMessage();
    }

    private static int Int(ProtoReader r, int wt)
    {
        if (wt != WireType.Varint)
        {
            r.SkipField(wt);
            return 0;
        }
        return r.ReadInt32();
    }
}
