using VocalPatch.Core.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VocalPatch.Core.Formats;

/// <summary>
/// OpenUtau's native YAML project format (.ustx). Resolution is fixed at 480 ticks
/// per quarter by OpenUtau itself. Notes live on voice parts, which we flatten into
/// the neutral track list on read and rebuild one part per track on write.
/// </summary>
public sealed class UstxFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Ustx;
    public override string Name => "ustx";
    public override string DisplayName => "OpenUtau Project (.ustx)";
    public override IReadOnlyList<string> Extensions => new[] { ".ustx" };

    private const int OpenUtauResolution = 480;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    // YamlDotNet's DefaultObjectFactory caches per-type callbacks in a plain Dictionary,
    // which is not thread-safe. Two threads serializing at the same time corrupt that
    // cache and throw "Operations that change non-concurrent collections must have
    // exclusive access" - which in the injected runtime is a real scenario, because the
    // export hotkey and the autosave watcher are different threads. The serializer and
    // deserializer are immutable and reusable, so a lock around their use is enough.
    private static readonly object YamlGate = new();

    protected override Song ReadText(string text, string? path)
    {
        UstxProjectDto dto;
        lock (YamlGate)
        {
            dto = Deserializer.Deserialize<UstxProjectDto>(text) ?? new UstxProjectDto();
        }
        var song = new Song
        {
            Name = string.IsNullOrWhiteSpace(dto.Name) ? "Untitled" : dto.Name!,
            Comment = dto.Comment ?? string.Empty,
            Resolution = OpenUtauResolution,
            SourcePath = path,
            SourceFormat = FormatKind.Ustx,
        };

        song.Tempos.Clear();
        foreach (var t in dto.Tempos ?? new List<UstxTempoDto>())
            song.Tempos.Add(new Tempo(t.Position, t.Bpm <= 0 ? 120 : t.Bpm));
        if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, 120));

        song.TimeSignatures.Clear();
        foreach (var s in dto.TimeSignatures ?? new List<UstxTimeSigDto>())
            song.TimeSignatures.Add(new TimeSignature(s.BarPosition, Math.Max(1, s.BeatPerBar), Math.Max(1, s.BeatUnit)));
        if (song.TimeSignatures.Count == 0) song.TimeSignatures.Add(new TimeSignature(0, 4, 4));

        var tracks = new List<Track>();
        foreach (var t in dto.Tracks ?? new List<UstxTrackDto>())
            tracks.Add(new Track(string.IsNullOrWhiteSpace(t.TrackName) ? "Track" : t.TrackName!)
            {
                Singer = t.Singer,
                Muted = t.Mute,
            });

        foreach (var part in dto.VoiceParts ?? new List<UstxVoicePartDto>())
        {
            while (tracks.Count <= part.TrackNo) tracks.Add(new Track("Track" + (tracks.Count + 1)));
            if (part.TrackNo < 0) continue;
            var track = tracks[part.TrackNo];
            foreach (var n in part.Notes ?? new List<UstxNoteDto>())
            {
                if (n.Duration <= 0) continue;
                track.Notes.Add(new Note(part.Position + n.Position, n.Duration, n.Tone, string.IsNullOrEmpty(n.Lyric) ? "a" : n.Lyric!));
            }
        }

        song.Tracks.AddRange(tracks);
        if (song.Tracks.Count == 0) song.Tracks.Add(new Track("Track1"));
        song.Normalize();
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        var dto = new UstxProjectDto
        {
            Name = song.Name,
            Comment = song.Comment,
            UstxVersion = "0.10",
            Tempos = song.Tempos.Select(t => new UstxTempoDto { Position = t.Position, Bpm = t.Bpm }).ToList(),
            TimeSignatures = song.TimeSignatures.Select(s => new UstxTimeSigDto
            {
                BarPosition = s.BarPosition,
                BeatPerBar = s.Numerator,
                BeatUnit = s.Denominator,
            }).ToList(),
            Tracks = new List<UstxTrackDto>(),
            VoiceParts = new List<UstxVoicePartDto>(),
        };

        for (int i = 0; i < song.Tracks.Count; i++)
        {
            var track = song.Tracks[i];
            dto.Tracks.Add(new UstxTrackDto
            {
                TrackName = track.Name,
                Singer = track.Singer,
                Mute = track.Muted,
                RendererSettings = new UstxRenderSettingsDto(),
            });

            if (track.Notes.Count == 0) continue;

            var part = new UstxVoicePartDto
            {
                Name = string.IsNullOrWhiteSpace(track.Name) ? "Part" : track.Name,
                TrackNo = i,
                Position = 0,
                Duration = track.SortedNotes().Last().End,
                Notes = new List<UstxNoteDto>(),
                Curves = new List<object>(),
            };
            foreach (var n in track.SortedNotes())
            {
                part.Notes.Add(new UstxNoteDto
                {
                    Position = n.Position,
                    Duration = n.Duration,
                    Tone = n.Pitch,
                    Lyric = string.IsNullOrEmpty(n.Lyric) ? "a" : n.Lyric,
                    Pitch = new UstxPitchDto
                    {
                        SnapFirst = true,
                        Data = new List<UstxPitchPointDto>
                        {
                            new() { X = 0, Y = 0, Shape = "io" },
                            new() { X = 0, Y = 0, Shape = "io" },
                        },
                    },
                    Vibrato = new UstxVibratoDto(),
                    PhonemeExpressions = new List<object>(),
                    PhonemeOverrides = new List<object>(),
                });
            }
            dto.VoiceParts.Add(part);
        }

        lock (YamlGate)
        {
            return Serializer.Serialize(dto);
        }
    }

    // ---- DTOs. Property names are chosen so CamelCase naming yields OpenUtau's keys. ----

    internal sealed class UstxProjectDto
    {
        public string? Name { get; set; } = "Untitled";
        public string? Comment { get; set; } = string.Empty;
        public string? OutputDir { get; set; } = "Vocal";
        public string? CacheDir { get; set; } = "UCache";
        public string? UstxVersion { get; set; } = "0.10";
        public Dictionary<string, object> Expressions { get; set; } = new();
        public string[]? ExpSelectors { get; set; } = { "dyn", "pitd", "clr", "eng", "vel", "vol", "atk", "dec", "gen", "bre" };
        public int ExpPrimary { get; set; }
        public int ExpSecondary { get; set; } = 1;
        public int Key { get; set; }
        public List<UstxTimeSigDto>? TimeSignatures { get; set; }
        public List<UstxTempoDto>? Tempos { get; set; }
        public List<UstxTrackDto>? Tracks { get; set; }
        public List<UstxVoicePartDto>? VoiceParts { get; set; }
    }

    internal sealed class UstxTimeSigDto
    {
        public int BarPosition { get; set; }
        public int BeatPerBar { get; set; } = 4;
        public int BeatUnit { get; set; } = 4;
    }

    internal sealed class UstxTempoDto
    {
        public int Position { get; set; }
        public double Bpm { get; set; } = 120;
    }

    internal sealed class UstxTrackDto
    {
        public string? TrackName { get; set; } = "New Track";
        public string? Singer { get; set; }
        public string? Phonemizer { get; set; }
        public string? TrackColor { get; set; } = "Blue";
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public double Volume { get; set; }
        public double Pan { get; set; }
        public UstxRenderSettingsDto? RendererSettings { get; set; } = new();
    }

    internal sealed class UstxRenderSettingsDto
    {
        public string? Renderer { get; set; }
        public string? Resampler { get; set; }
        public string? Wavtool { get; set; }
    }

    internal sealed class UstxVoicePartDto
    {
        public string? Name { get; set; } = "New Part";
        public string? Comment { get; set; } = string.Empty;
        public int TrackNo { get; set; }
        public int Position { get; set; }
        public int Duration { get; set; }
        public List<UstxNoteDto>? Notes { get; set; }
        public List<object>? Curves { get; set; }
    }

    internal sealed class UstxNoteDto
    {
        public int Position { get; set; }
        public int Duration { get; set; }
        public int Tone { get; set; }
        public string? Lyric { get; set; } = "a";
        public UstxPitchDto? Pitch { get; set; }
        public UstxVibratoDto? Vibrato { get; set; }
        public int Tuning { get; set; }
        public List<object>? PhonemeExpressions { get; set; }
        public List<object>? PhonemeOverrides { get; set; }
    }

    internal sealed class UstxPitchDto
    {
        public List<UstxPitchPointDto>? Data { get; set; }
        public bool SnapFirst { get; set; } = true;
    }

    internal sealed class UstxPitchPointDto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public string? Shape { get; set; } = "io";
    }

    internal sealed class UstxVibratoDto
    {
        public float Length { get; set; }
        public float Period { get; set; } = 182;
        public float Depth { get; set; } = 25;
        public float In { get; set; } = 20;
        public float Out { get; set; } = 20;
        public float Shift { get; set; }
        public float Drift { get; set; }
        public float VolLink { get; set; }
    }
}
