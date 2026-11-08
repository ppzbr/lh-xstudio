using System.Globalization;
using VocalPatch.Core.Music;

namespace VocalPatch.Core.Model;

/// <summary>A tempo change. Inside the neutral model ticks are absolute song ticks.</summary>
public readonly record struct Tempo(int Position, double Bpm);

/// <summary>A time-signature change anchored at a bar.</summary>
public readonly record struct TimeSignature(int BarPosition, int Numerator, int Denominator);

/// <summary>
/// A single sung note. <see cref="Pitch"/> is a MIDI note number, <see cref="Lyric"/>
/// is the neutral lyric (a syllable, "R"/"r" for a rest, or "+~" for a continuation).
/// </summary>
public sealed class Note
{
    public int Position { get; set; }
    public int Duration { get; set; }
    public int Pitch { get; set; } = 60;
    public string Lyric { get; set; } = "a";
    public int Velocity { get; set; } = 64;

    /// <summary>Optional phoneme string (SVP "phonemes", VSQX "phnms").</summary>
    public string? Phoneme { get; set; }

    public Note() { }

    public Note(int position, int duration, int pitch, string lyric)
    {
        Position = position;
        Duration = duration;
        Pitch = pitch;
        Lyric = lyric;
    }

    public int End => Position + Duration;

    public bool IsRest => Lyric is "R" or "r" or "-" or "";

    public Note Clone() => new()
    {
        Position = Position,
        Duration = Duration,
        Pitch = Pitch,
        Lyric = Lyric,
        Velocity = Velocity,
        Phoneme = Phoneme,
    };

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1}+{2} @t{3}", MusicMath.MidiToName(Pitch), Lyric, Duration, Position);
}

/// <summary>One monophonic track of notes plus its voice metadata.</summary>
public sealed class Track
{
    public string Name { get; set; } = "Track";
    public string? Singer { get; set; }
    public string? Comment { get; set; }
    public int Program { get; set; } = -1;
    public bool Muted { get; set; }

    /// <summary>Notes relative to the start of the song (parts have been flattened).</summary>
    public List<Note> Notes { get; } = new();

    public Track() { }

    public Track(string name) => Name = name;

    public IEnumerable<Note> SortedNotes() => Notes.OrderBy(n => n.Position).ThenBy(n => n.Pitch);

    public Track Clone()
    {
        var t = new Track(Name) { Singer = Singer, Comment = Comment, Program = Program, Muted = Muted };
        foreach (var n in Notes) t.Notes.Add(n.Clone());
        return t;
    }
}

/// <summary>
/// The neutral, app-neutral song model. Every <see cref="Formats.ProjectFormat"/> reads
/// into and writes from this, and every <see cref="Jobs.IJob"/> mutates it in place.
/// </summary>
public sealed class Song
{
    public string Name { get; set; } = "Untitled";
    public string Comment { get; set; } = string.Empty;
    public int Resolution { get; set; } = 480;
    public string? SourcePath { get; set; }
    public FormatKind SourceFormat { get; set; } = FormatKind.Unknown;

    public List<Tempo> Tempos { get; } = new() { new Tempo(0, 120) };
    public List<TimeSignature> TimeSignatures { get; } = new() { new TimeSignature(0, 4, 4) };
    public List<Track> Tracks { get; } = new();

    public int EndTick
    {
        get
        {
            int end = 0;
            foreach (var t in Tracks)
                foreach (var n in t.Notes)
                    if (n.End > end) end = n.End;
            return end;
        }
    }

    public int NoteCount
    {
        get
        {
            int c = 0;
            foreach (var t in Tracks) c += t.Notes.Count;
            return c;
        }
    }

    /// <summary>Tempo in effect at a tick (LAST tempo whose position is &lt;= tick).</summary>
    public double BpmAt(int tick)
    {
        double bpm = Tempos.Count > 0 ? Tempos[0].Bpm : 120;
        foreach (var t in Tempos)
        {
            if (t.Position <= tick) bpm = t.Bpm;
            else break;
        }
        return bpm <= 0 ? 120 : bpm;
    }

    /// <summary>Convert an absolute tick to milliseconds across all tempo changes.</summary>
    public double TickToMs(int tick)
    {
        var tempos = Tempos.OrderBy(t => t.Position).ToList();
        if (tempos.Count == 0) return MusicMath.TicksToMs(tick, Resolution, 120);
        double ms = 0;
        for (int i = 0; i < tempos.Count; i++)
        {
            int segStart = tempos[i].Position;
            if (segStart >= tick) break;
            int segEnd = i + 1 < tempos.Count ? Math.Min(tick, tempos[i + 1].Position) : tick;
            ms += MusicMath.TicksToMs(segEnd - segStart, Resolution, tempos[i].Bpm);
        }
        return ms;
    }

    /// <summary>Ensure canonical invariants: a tempo/signature at tick 0, sorted, positive lengths.</summary>
    public void Normalize()
    {
        Tempos.Sort((a, b) => a.Position.CompareTo(b.Position));
        TimeSignatures.Sort((a, b) => a.BarPosition.CompareTo(b.BarPosition));
        if (Tempos.Count == 0) Tempos.Add(new Tempo(0, 120));
        if (Tempos[0].Position != 0) Tempos.Insert(0, new Tempo(0, Tempos[0].Bpm));
        if (TimeSignatures.Count == 0) TimeSignatures.Add(new TimeSignature(0, 4, 4));
        if (TimeSignatures[0].BarPosition != 0) TimeSignatures.Insert(0, new TimeSignature(0, 4, 4));
        foreach (var t in Tracks)
            foreach (var n in t.Notes)
                if (n.Duration < 1) n.Duration = 1;
    }

    public Song Clone()
    {
        var s = new Song
        {
            Name = Name,
            Comment = Comment,
            Resolution = Resolution,
            SourcePath = SourcePath,
            SourceFormat = SourceFormat,
        };
        s.Tempos.Clear();
        s.Tempos.AddRange(Tempos);
        s.TimeSignatures.Clear();
        s.TimeSignatures.AddRange(TimeSignatures);
        foreach (var t in Tracks) s.Tracks.Add(t.Clone());
        return s;
    }
}
