using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// VocalPatch's own lossless JSON snapshot of the neutral model. Round-trips
/// everything the toolkit can represent, including per-note velocity and phonemes.
/// </summary>
public sealed class JsonSongFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Json;
    public override string Name => "json";
    public override string DisplayName => "VocalPatch JSON (.vp.json)";
    public override IReadOnlyList<string> Extensions => new[] { ".vp.json", ".vpsong.json" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override Song ReadText(string text, string? path)
    {
        var dto = JsonSerializer.Deserialize<SongDto>(text, Options) ?? new SongDto();
        var song = new Song
        {
            Name = string.IsNullOrWhiteSpace(dto.Name) ? "Untitled" : dto.Name!,
            Comment = dto.Comment ?? string.Empty,
            Resolution = dto.Resolution > 0 ? dto.Resolution : 480,
            SourcePath = path,
            SourceFormat = FormatKind.Json,
        };
        song.Tempos.Clear();
        foreach (var t in dto.Tempos ?? new List<TempoDto>())
            song.Tempos.Add(new Tempo(t.Position, t.Bpm <= 0 ? 120 : t.Bpm));
        song.TimeSignatures.Clear();
        foreach (var s in dto.TimeSignatures ?? new List<TimeSigDto>())
            song.TimeSignatures.Add(new TimeSignature(s.BarPosition, Math.Max(1, s.Numerator), Math.Max(1, s.Denominator)));
        foreach (var t in dto.Tracks ?? new List<TrackDto>())
        {
            var track = new Track(t.Name is { Length: > 0 } tn ? tn : "Track")
            {
                Singer = t.Singer,
                Comment = t.Comment,
                Program = t.Program,
                Muted = t.Muted,
            };
            foreach (var n in t.Notes ?? new List<NoteDto>())
                if (n.Duration > 0)
                    track.Notes.Add(new Note(n.Position, n.Duration, n.Pitch, string.IsNullOrEmpty(n.Lyric) ? "a" : n.Lyric!)
                    {
                        Velocity = n.Velocity > 0 ? n.Velocity : 64,
                        Phoneme = n.Phoneme,
                    });
            song.Tracks.Add(track);
        }
        if (song.Tracks.Count == 0) song.Tracks.Add(new Track("Track1"));
        song.Normalize();
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        var dto = new SongDto
        {
            Name = song.Name,
            Comment = song.Comment,
            Resolution = song.Resolution,
            Tempos = song.Tempos.Select(t => new TempoDto { Position = t.Position, Bpm = t.Bpm }).ToList(),
            TimeSignatures = song.TimeSignatures.Select(s => new TimeSigDto
            {
                BarPosition = s.BarPosition,
                Numerator = s.Numerator,
                Denominator = s.Denominator,
            }).ToList(),
            Tracks = song.Tracks.Select(t => new TrackDto
            {
                Name = t.Name,
                Singer = t.Singer,
                Comment = t.Comment,
                Program = t.Program,
                Muted = t.Muted,
                Notes = t.SortedNotes().Select(n => new NoteDto
                {
                    Position = n.Position,
                    Duration = n.Duration,
                    Pitch = n.Pitch,
                    Lyric = n.Lyric,
                    Velocity = n.Velocity,
                    Phoneme = n.Phoneme,
                }).ToList(),
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    internal sealed class SongDto
    {
        public string? Name { get; set; }
        public string? Comment { get; set; }
        public int Resolution { get; set; } = 480;
        public List<TempoDto>? Tempos { get; set; }
        public List<TimeSigDto>? TimeSignatures { get; set; }
        public List<TrackDto>? Tracks { get; set; }
    }

    internal sealed class TempoDto
    {
        public int Position { get; set; }
        public double Bpm { get; set; }
    }

    internal sealed class TimeSigDto
    {
        public int BarPosition { get; set; }
        public int Numerator { get; set; } = 4;
        public int Denominator { get; set; } = 4;
    }

    internal sealed class TrackDto
    {
        public string? Name { get; set; }
        public string? Singer { get; set; }
        public string? Comment { get; set; }
        public int Program { get; set; } = -1;
        public bool Muted { get; set; }
        public List<NoteDto>? Notes { get; set; }
    }

    internal sealed class NoteDto
    {
        public int Position { get; set; }
        public int Duration { get; set; }
        public int Pitch { get; set; } = 60;
        public string? Lyric { get; set; }
        public int Velocity { get; set; } = 64;
        public string? Phoneme { get; set; }
    }
}
