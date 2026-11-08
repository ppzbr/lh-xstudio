using System.Globalization;
using System.Xml.Linq;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// CeVIO project (.ccs) XML. The public schema is not formally documented, so this
/// codec is deliberately tolerant: on read it harvests any note-like element and maps
/// child names heuristically; on write it emits a clean, conventional CCSProject tree.
/// </summary>
public sealed class CcsFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Ccs;
    public override string Name => "ccs";
    public override string DisplayName => "CeVIO Project (.ccs)";
    public override IReadOnlyList<string> Extensions => new[] { ".ccs" };

    private static readonly string[] PosKeys = { "Clock", "Position", "Start", "StartTime", "Tick", "Pos" };
    private static readonly string[] DurKeys = { "Duration", "Length", "Dur", "Long" };
    private static readonly string[] PitchKeys = { "Pitch", "NoteNumber", "Note", "Scale", "Tone", "NoteNum" };
    private static readonly string[] LyricKeys = { "Lyric", "Lyrics", "Word", "Pronounce", "Text", "Syllable" };

    protected override Song ReadText(string text, string? path)
    {
        var doc = XDocument.Parse(text);
        var root = doc.Root ?? throw new FormatException("Empty CeVIO document.");

        var song = new Song
        {
            Name = root.Attribute("Name")?.Value ?? FindFirst(root, "Title") ?? Path.GetFileNameWithoutExtension(path ?? "Untitled"),
            Resolution = 480,
            SourcePath = path,
            SourceFormat = FormatKind.Ccs,
        };

        song.Tempos.Clear();
        foreach (var tempo in root.Descendants().Where(e => e.Name.LocalName.Equals("Tempo", StringComparison.OrdinalIgnoreCase)))
        {
            double bpm = Num(tempo.Value);
            if (bpm <= 0) bpm = Num(tempo.Attribute("Bpm")?.Value ?? tempo.Attribute("Value")?.Value);
            int pos = (int)Num(tempo.Attribute("Position")?.Value ?? tempo.Attribute("Clock")?.Value);
            if (bpm > 0) song.Tempos.Add(new Tempo(Math.Max(0, pos), bpm));
        }
        if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, 120));

        var track = new Track(FindFirst(root, "TrackName") ?? "Track1");

        foreach (var noteEl in root.Descendants().Where(e => e.Name.LocalName.Equals("Note", StringComparison.OrdinalIgnoreCase) ||
                                                            e.Name.LocalName.Equals("NoteUnit", StringComparison.OrdinalIgnoreCase)))
        {
            int pos = (int)FindNum(noteEl, PosKeys);
            int dur = (int)FindNum(noteEl, DurKeys);
            int pitch = (int)FindNum(noteEl, PitchKeys);
            string lyric = FindString(noteEl, LyricKeys) ?? "a";
            if (dur <= 0 || pitch <= 0 || pitch > 127) continue;
            track.Notes.Add(new Note(pos, dur, pitch, lyric));
        }

        song.Tracks.Add(track);
        song.Normalize();
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        XNamespace ns = "";

        var score = new XElement("Score");
        foreach (var track in song.Tracks)
        {
            foreach (var n in track.SortedNotes())
            {
                score.Add(new XElement("Note",
                    new XElement("Clock", n.Position.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Duration", n.Duration.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Pitch", n.Pitch.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Lyric", n.Lyric)));
            }
        }

        var part = new XElement("Part", new XAttribute("Name", song.Tracks.Count > 0 ? song.Tracks[0].Name : "Part"), score);
        var songTrack = new XElement("SongTrack", new XAttribute("Name", song.Tracks.Count > 0 ? song.Tracks[0].Name : "Track"), part);

        var root = new XElement("CCSProject", new XAttribute("Version", "1.0"), new XAttribute("Name", song.Name));
        foreach (var t in song.Tempos)
            root.Add(new XElement("Tempo", new XAttribute("Clock", t.Position.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("Bpm", t.Bpm.ToString(CultureInfo.InvariantCulture))));
        root.Add(songTrack);

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        _ = ns;
        return doc.ToString();
    }

    private static double FindNum(XElement el, string[] keys)
    {
        foreach (var key in keys)
        {
            var child = el.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (child != null)
            {
                double v = Num(child.Value);
                if (v != 0 || child.Value.Trim() == "0") return v;
            }
            var attr = el.Attribute(key);
            if (attr != null) return Num(attr.Value);
        }
        return 0;
    }

    private static string? FindString(XElement el, string[] keys)
    {
        foreach (var key in keys)
        {
            var child = el.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (child != null && !string.IsNullOrWhiteSpace(child.Value)) return child.Value.Trim();
            var attr = el.Attribute(key);
            if (attr != null && !string.IsNullOrWhiteSpace(attr.Value)) return attr.Value.Trim();
        }
        return null;
    }

    private static string? FindFirst(XElement root, string local) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(local, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static double Num(string? s) =>
        double.TryParse((s ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
