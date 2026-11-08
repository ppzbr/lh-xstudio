using System.Text;
using System.Text.Json.Nodes;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// Synthesizer V Studio project (.svp). JSON; positions are "blicks" where one
/// quarter note is 705600000 blicks. Notes are held on groups referenced by tracks.
/// </summary>
public sealed class SvpFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Svp;
    public override string Name => "svp";
    public override string DisplayName => "Synthesizer V Studio Project (.svp)";
    public override IReadOnlyList<string> Extensions => new[] { ".svp" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    private const double BlicksPerQuarter = 705600000.0;
    private const int Resolution = 480;

    protected override Song ReadText(string text, string? path)
    {
        text = text.TrimEnd('\0');
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new FormatException("Invalid SVP JSON.");

        var song = new Song
        {
            Name = Path.GetFileNameWithoutExtension(path ?? "Untitled"),
            Resolution = Resolution,
            SourcePath = path,
            SourceFormat = FormatKind.Svp,
        };
        double blicksPerTick = BlicksPerQuarter / Resolution;

        song.Tempos.Clear();
        if (root["time"]?["tempo"] is JsonArray tempos)
        {
            foreach (var t in tempos)
            {
                if (t == null) continue;
                int pos = (int)Math.Round(GetD(t["position"]) / blicksPerTick);
                double bpm = GetD(t["bpm"]);
                song.Tempos.Add(new Tempo(Math.Max(0, pos), bpm <= 0 ? 120 : bpm));
            }
        }
        if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, 120));

        song.TimeSignatures.Clear();
        if (root["time"]?["meter"] is JsonArray meters)
        {
            foreach (var m in meters)
            {
                if (m == null) continue;
                song.TimeSignatures.Add(new TimeSignature(
                    Math.Max(0, GetI(m["index"])),
                    Math.Max(1, GetI(m["numerator"])),
                    Math.Max(1, GetI(m["denominator"]))));
            }
        }
        if (song.TimeSignatures.Count == 0) song.TimeSignatures.Add(new TimeSignature(0, 4, 4));

        var groups = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        void RegisterGroups(JsonNode? arr)
        {
            if (arr is not JsonArray a) return;
            foreach (var g in a)
            {
                if (g is not JsonObject go) continue;
                var uuid = GetS(go["uuid"]);
                if (!string.IsNullOrEmpty(uuid)) groups[uuid] = go;
            }
        }
        RegisterGroups(root["library"]);
        RegisterGroups(root["groups"]);

        foreach (var trackNode in root["tracks"] as JsonArray ?? new JsonArray())
        {
            if (trackNode is not JsonObject trackObj) continue;
            var track = new Track(GetS(trackObj["name"]) is { Length: > 0 } n ? n : "Track");

            var refs = new List<JsonObject>();
            void AddRef(JsonNode? r)
            {
                if (r is JsonObject ro) refs.Add(ro);
            }
            AddRef(trackObj["mainRef"]);
            AddRef(trackObj["mainGroupSV2"]);
            if (trackObj["groups"] is JsonArray grpRefs)
                foreach (var r in grpRefs) AddRef(r);

            foreach (var reference in refs)
            {
                var groupId = GetS(reference["groupID"]);
                JsonObject? group = null;
                if (!string.IsNullOrEmpty(groupId) && groups.TryGetValue(groupId, out var g1)) group = g1;
                else if (trackObj["mainGroup"] is JsonObject mg) group = mg;
                if (group == null) continue;

                if (string.IsNullOrEmpty(track.Singer) && reference["database"] is JsonObject db)
                    track.Singer = GetS(db["name"]);

                int pitchOffset = GetI(reference["pitchOffset"]);

                if (group["notes"] is JsonArray notes)
                {
                    foreach (var noteNode in notes)
                    {
                        if (noteNode is not JsonObject no) continue;
                        var musicalType = GetS(no["musicalType"]);
                        if (musicalType.Length > 0 && musicalType != "singing" && musicalType != "rap") continue;
                        long onset = (long)GetD(no["onset"]);
                        long duration = (long)GetD(no["duration"]);
                        if (duration <= 0) continue;
                        int pos = (int)Math.Round(onset / blicksPerTick);
                        int dur = (int)Math.Round(duration / blicksPerTick);
                        if (dur <= 0) continue;
                        string lyric = GetS(no["lyrics"]);
                        if (lyric.Length == 0) lyric = "a";
                        if (lyric == "-") lyric = "+~";
                        string phonemes = GetS(no["phonemes"]);
                        track.Notes.Add(new Note(pos, dur, GetI(no["pitch"]) + pitchOffset, lyric)
                        {
                            Phoneme = string.IsNullOrWhiteSpace(phonemes) ? null : phonemes,
                        });
                    }
                }
            }

            song.Tracks.Add(track);
        }

        if (song.Tracks.Count == 0) song.Tracks.Add(new Track("Track1"));
        song.Normalize();
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        double blicksPerTick = BlicksPerQuarter / song.Resolution;

        var tempoArray = new JsonArray();
        foreach (var t in song.Tempos)
            tempoArray.Add(new JsonObject
            {
                ["position"] = (long)Math.Round(t.Position * blicksPerTick),
                ["bpm"] = t.Bpm,
            });

        var meterArray = new JsonArray();
        foreach (var s in song.TimeSignatures)
            meterArray.Add(new JsonObject
            {
                ["index"] = s.BarPosition,
                ["numerator"] = s.Numerator,
                ["denominator"] = s.Denominator,
            });

        var library = new JsonArray();
        var tracks = new JsonArray();

        for (int i = 0; i < song.Tracks.Count; i++)
        {
            var track = song.Tracks[i];
            string uuid = "vp-" + i.ToString("D4");

            var notes = new JsonArray();
            foreach (var n in track.SortedNotes())
            {
                notes.Add(new JsonObject
                {
                    ["onset"] = (long)Math.Round(n.Position * blicksPerTick),
                    ["duration"] = (long)Math.Round(n.Duration * blicksPerTick),
                    ["pitch"] = n.Pitch,
                    ["lyrics"] = string.IsNullOrEmpty(n.Lyric) || n.Lyric == "+~" ? (n.Lyric == "+~" ? "-" : "a") : n.Lyric,
                    ["phonemes"] = n.Phoneme ?? string.Empty,
                    ["attributes"] = new JsonObject(),
                    ["instantMode"] = false,
                });
            }

            library.Add(new JsonObject
            {
                ["uuid"] = uuid,
                ["name"] = string.IsNullOrWhiteSpace(track.Name) ? "Main" : track.Name,
                ["notes"] = notes,
                ["parameters"] = new JsonObject(),
            });

            tracks.Add(new JsonObject
            {
                ["name"] = string.IsNullOrWhiteSpace(track.Name) ? "Track" : track.Name,
                ["mainRef"] = new JsonObject
                {
                    ["groupID"] = uuid,
                    ["blickOffset"] = 0,
                    ["pitchOffset"] = 0,
                    ["database"] = new JsonObject { ["name"] = track.Singer ?? string.Empty },
                },
                ["parameters"] = new JsonObject(),
            });
        }

        var rootObj = new JsonObject
        {
            ["version"] = 1,
            ["time"] = new JsonObject
            {
                ["meter"] = meterArray,
                ["tempo"] = tempoArray,
            },
            ["library"] = library,
            ["tracks"] = tracks,
            ["renderConfig"] = new JsonObject { ["destination"] = "." },
        };

        return rootObj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static double GetD(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        return 0;
    }

    private static int GetI(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        }
        return 0;
    }

    private static string GetS(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
}
