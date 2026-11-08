using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// utaformatix "ufdata" JSON — the interchange format used between UTAU-family apps.
/// </summary>
public sealed class UfdataFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Ufdata;
    public override string Name => "ufdata";
    public override string DisplayName => "utaformatix data (.ufdata.json)";
    public override IReadOnlyList<string> Extensions => new[] { ".ufdata.json", ".ufdata" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    protected override Song ReadText(string text, string? path)
    {
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new FormatException("Invalid ufdata JSON.");
        var project = JsonUtil.Obj(root["project"]) ?? root;

        var song = new Song
        {
            Name = JsonUtil.S(project["name"]) is { Length: > 0 } n ? n : "Untitled",
            Resolution = 480,
            SourcePath = path,
            SourceFormat = FormatKind.Ufdata,
        };

        song.Tempos.Clear();
        if (project["tempos"] is JsonArray tempos)
        {
            foreach (var t in tempos)
            {
                if (t == null) continue;
                double bpm = JsonUtil.D(t["bpm"]);
                song.Tempos.Add(new Tempo(Math.Max(0, JsonUtil.I(t["tickPosition"])), bpm <= 0 ? 120 : bpm));
            }
        }
        if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, 120));

        song.TimeSignatures.Clear();
        if (project["timeSignatures"] is JsonArray sigs)
        {
            foreach (var s in sigs)
            {
                if (s == null) continue;
                song.TimeSignatures.Add(new TimeSignature(
                    Math.Max(0, JsonUtil.I(s["measurePosition"])),
                    Math.Max(1, JsonUtil.I(s["numerator"])),
                    Math.Max(1, JsonUtil.I(s["denominator"]))));
            }
        }
        if (song.TimeSignatures.Count == 0) song.TimeSignatures.Add(new TimeSignature(0, 4, 4));

        if (project["tracks"] is JsonArray tracks)
        {
            foreach (var tr in tracks)
            {
                if (tr is not JsonObject to) continue;
                var track = new Track(JsonUtil.S(to["name"]) is { Length: > 0 } tn ? tn : "Track");
                if (to["notes"] is JsonArray notes)
                {
                    foreach (var nn in notes)
                    {
                        if (nn is not JsonObject no) continue;
                        int on = JsonUtil.I(no["tickOn"]);
                        int off = JsonUtil.I(no["tickOff"]);
                        if (off <= on) continue;
                        string lyric = JsonUtil.S(no["lyric"]);
                        if (lyric == "-") lyric = "+~";
                        if (lyric.Length == 0) lyric = "a";
                        track.Notes.Add(new Note(on, off - on, JsonUtil.I(no["key"]), lyric)
                        {
                            Phoneme = JsonUtil.S(no["phoneme"]) is { Length: > 0 } ph ? ph : null,
                        });
                    }
                }
                song.Tracks.Add(track);
            }
        }

        if (song.Tracks.Count == 0) song.Tracks.Add(new Track("Track1"));
        song.Normalize();
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();

        var tempos = new JsonArray();
        foreach (var t in song.Tempos)
            tempos.Add(new JsonObject { ["tickPosition"] = t.Position, ["bpm"] = t.Bpm });

        var sigs = new JsonArray();
        foreach (var s in song.TimeSignatures)
            sigs.Add(new JsonObject
            {
                ["measurePosition"] = s.BarPosition,
                ["numerator"] = s.Numerator,
                ["denominator"] = s.Denominator,
            });

        var tracks = new JsonArray();
        foreach (var track in song.Tracks)
        {
            var notes = new JsonArray();
            foreach (var nvalue in track.SortedNotes())
            {
                notes.Add(new JsonObject
                {
                    ["key"] = nvalue.Pitch,
                    ["tickOn"] = nvalue.Position,
                    ["tickOff"] = nvalue.End,
                    ["lyric"] = nvalue.Lyric == "+~" ? "-" : (string.IsNullOrEmpty(nvalue.Lyric) ? "a" : nvalue.Lyric),
                    ["phoneme"] = nvalue.Phoneme,
                });
            }
            tracks.Add(new JsonObject
            {
                ["name"] = string.IsNullOrWhiteSpace(track.Name) ? "Track" : track.Name,
                ["notes"] = notes,
            });
        }

        var root = new JsonObject
        {
            ["formatVersion"] = 1,
            ["project"] = new JsonObject
            {
                ["name"] = song.Name,
                ["tracks"] = tracks,
                ["timeSignatures"] = sigs,
                ["tempos"] = tempos,
                ["measurePrefix"] = 0,
            },
        };

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>Expressive Labs "commonnote" clipboard/JSON note interchange.</summary>
public sealed class CommonnoteFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Commonnote;
    public override string Name => "commonnote";
    public override string DisplayName => "commonnote (.json)";
    public override IReadOnlyList<string> Extensions => new[] { ".commonnote.json", ".commonnote" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    protected override Song ReadText(string text, string? path)
    {
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new FormatException("Invalid commonnote JSON.");
        var header = JsonUtil.Obj(root["header"]);
        int resolution = header != null ? JsonUtil.I(header["resolution"]) : 480;
        if (resolution <= 0) resolution = 480;

        var song = new Song
        {
            Resolution = resolution,
            SourcePath = path,
            SourceFormat = FormatKind.Commonnote,
            Name = Path.GetFileNameWithoutExtension(path ?? "Untitled"),
        };
        var track = new Track("Track1");
        if (root["notes"] is JsonArray notes)
        {
            foreach (var nn in notes)
            {
                if (nn is not JsonObject no) continue;
                int start = JsonUtil.I(no["start"]);
                int length = JsonUtil.I(no["length"]);
                if (length <= 0) continue;
                string label = JsonUtil.S(no["label"]);
                track.Notes.Add(new Note(start, length, JsonUtil.I(no["pitch"]), string.IsNullOrEmpty(label) ? "a" : label));
            }
        }
        song.Tracks.Add(track);
        song.Normalize();
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        var notes = new JsonArray();
        foreach (var t in song.Tracks)
            foreach (var n in t.SortedNotes())
                notes.Add(new JsonObject
                {
                    ["start"] = n.Position,
                    ["length"] = n.Duration,
                    ["label"] = n.Lyric,
                    ["pitch"] = n.Pitch,
                });

        var root = new JsonObject
        {
            ["identifier"] = "commonnote",
            ["header"] = new JsonObject { ["resolution"] = song.Resolution, ["origin"] = "vocalpatch" },
            ["notes"] = notes,
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
