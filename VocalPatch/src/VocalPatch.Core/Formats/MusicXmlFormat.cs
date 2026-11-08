using System.Globalization;
using System.Text;
using System.Xml.Linq;
using VocalPatch.Core.Music;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// MusicXML score-partwise (.musicxml/.xml). A pragmatic subset: one part per track,
/// pitches as step/alter/octave, rests to fill gaps, lyric syllables attached to notes.
/// </summary>
public sealed class MusicXmlFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.MusicXml;
    public override string Name => "musicxml";
    public override string DisplayName => "MusicXML (.musicxml)";
    public override IReadOnlyList<string> Extensions => new[] { ".musicxml", ".xml" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    private static readonly Dictionary<char, int> StepSemitone = new()
    {
        ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5, ['G'] = 7, ['A'] = 9, ['B'] = 11,
    };

    protected override Song ReadText(string text, string? path)
    {
        var doc = XDocument.Parse(text);
        var root = doc.Root ?? throw new FormatException("Empty MusicXML.");
        var song = new Song
        {
            Name = Local(root, "movement-title") ?? Local(root, "work-title") ?? Path.GetFileNameWithoutExtension(path ?? "Untitled"),
            Resolution = 1,
            SourcePath = path,
            SourceFormat = FormatKind.MusicXml,
        };

        var partNames = new Dictionary<string, string>();
        var partList = Descendants(root, "part-list").FirstOrDefault();
        if (partList != null)
        {
            foreach (var sp in Descendants(partList, "score-part"))
            {
                string id = sp.Attribute("id")?.Value ?? string.Empty;
                partNames[id] = Local(sp, "part-name") ?? "Part";
            }
        }

        int divisions = 1;
        foreach (var d in Descendants(root, "divisions"))
        {
            if (int.TryParse(d.Value.Trim(), out int dv) && dv > 0) { divisions = dv; break; }
        }

        foreach (var partEl in root.Elements().Where(e => e.Name.LocalName == "part"))
        {
            string id = partEl.Attribute("id")?.Value ?? string.Empty;
            var track = new Track(partNames.TryGetValue(id, out var pn) ? pn : (id.Length > 0 ? id : "Part"));
            int cursor = 0;
            int lastNoteStart = 0;
            bool lastWasChord = false;

            foreach (var measure in partEl.Elements().Where(e => e.Name.LocalName == "measure"))
            {
                foreach (var child in measure.Elements())
                {
                    switch (child.Name.LocalName)
                    {
                        case "attributes":
                            foreach (var d in child.Elements().Where(e => e.Name.LocalName == "divisions"))
                                if (int.TryParse(d.Value.Trim(), out int dv2) && dv2 > 0) divisions = dv2;
                            break;
                        case "backup":
                            cursor -= (int)ParseFat(child, "duration", divisions);
                            if (cursor < 0) cursor = 0;
                            break;
                        case "forward":
                            cursor += (int)ParseFat(child, "duration", divisions);
                            break;
                        case "note":
                            bool isChord = child.Elements().Any(e => e.Name.LocalName == "chord");
                            bool isRest = child.Elements().Any(e => e.Name.LocalName == "rest");
                            bool isGrace = child.Elements().Any(e => e.Name.LocalName == "grace");
                            int dur = (int)ParseFat(child, "duration", divisions);
                            int start = isChord ? lastNoteStart : cursor;
                            if (!isChord) { cursor += dur; lastNoteStart = start; }
                            lastWasChord = isChord;
                            if (isRest || isGrace) break;
                            var pitchEl = child.Elements().FirstOrDefault(e => e.Name.LocalName == "pitch");
                            if (pitchEl == null) break;
                            int midi = PitchToMidi(pitchEl);
                            if (midi < 0) break;
                            string lyric = Local(child, "text") ?? "a";
                            if (dur <= 0) dur = divisions / 4;
                            track.Notes.Add(new Note(start, dur, midi, lyric));
                            break;
                    }
                }
                _ = lastWasChord;
            }

            song.Tracks.Add(track);
        }

        if (song.Tracks.Count == 0) song.Tracks.Add(new Track("Track1"));
        song.Resolution = divisions > 0 ? divisions : 480;
        song.Normalize();
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        XNamespace ns = "http://www.musicxml.org/ns/musicxml";
        int divisions = song.Resolution > 0 ? song.Resolution : 480;
        var sig = song.TimeSignatures.Count > 0 ? song.TimeSignatures[0] : new TimeSignature(0, 4, 4);
        int ticksPerMeasure = Math.Max(divisions, (int)Math.Round(sig.Numerator * (4.0 / sig.Denominator) * divisions));

        var partListEl = new XElement(ns + "part-list");
        var partEls = new List<XElement>();
        for (int i = 0; i < song.Tracks.Count; i++)
        {
            string id = "P" + (i + 1);
            partListEl.Add(new XElement(ns + "score-part", new XAttribute("id", id),
                new XElement(ns + "part-name", song.Tracks[i].Name ?? "Track")));
            partEls.Add(BuildPart(ns, id, song.Tracks[i], divisions, ticksPerMeasure, sig));
        }

        var root = new XElement(ns + "score-partwise", new XAttribute("version", "4.0"),
            new XElement(ns + "movement-title", song.Name),
            partListEl);
        root.Add(partEls);

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root).ToString();
    }

    private static XElement BuildPart(XNamespace ns, string id, Track track, int divisions, int ticksPerMeasure, TimeSignature sig)
    {
        var part = new XElement(ns + "part", new XAttribute("id", id));
        var notes = track.SortedNotes().ToList();

        int cursor = 0;
        int noteIndex = 0;

        while (noteIndex < notes.Count || cursor == 0)
        {
            var measure = new XElement(ns + "measure", new XAttribute("number", (cursor / ticksPerMeasure + 1).ToString(CultureInfo.InvariantCulture)));
            if (cursor == 0)
            {
                measure.Add(new XElement(ns + "attributes",
                    new XElement(ns + "divisions", divisions.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "time",
                        new XElement(ns + "beats", sig.Numerator.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "beat-type", sig.Denominator.ToString(CultureInfo.InvariantCulture))),
                    new XElement(ns + "clef",
                        new XElement(ns + "sign", "G"),
                        new XElement(ns + "line", "2"))));
            }

            int measureEnd = cursor + ticksPerMeasure;
            int filled = cursor;

            while (noteIndex < notes.Count && notes[noteIndex].Position < measureEnd)
            {
                var n = notes[noteIndex];
                if (n.Position > filled)
                {
                    int gap = Math.Min(n.Position, measureEnd) - filled;
                    if (gap > 0)
                    {
                        measure.Add(Rest(ns, gap));
                        filled += gap;
                    }
                    if (filled >= measureEnd) break;
                }
                if (n.Position < filled)
                {
                    noteIndex++;
                    continue;
                }
                int dur = Math.Min(n.Duration, measureEnd - filled);
                if (dur <= 0) break;
                measure.Add(NoteElement(ns, n.Pitch, dur, n.Lyric));
                filled += dur;
                if (n.Position + n.Duration > measureEnd)
                {
                    n.Position = measureEnd;
                    n.Duration -= dur;
                    // keep note in list for next measure
                }
                else
                {
                    noteIndex++;
                }
            }

            if (filled < measureEnd && measure.Descendants(ns + "note").Any())
                measure.Add(Rest(ns, measureEnd - filled));

            part.Add(measure);
            cursor = measureEnd;

            if (noteIndex >= notes.Count && cursor >= ticksPerMeasure) break;
            if (cursor > tracksEndGuard(notes)) break;
        }

        if (!part.Elements().Any()) part.Add(new XElement(ns + "measure", new XAttribute("number", "1"), new XElement(ns + "attributes", new XElement(ns + "divisions", divisions))));
        return part;
    }

    private static int tracksEndGuard(List<Note> notes) =>
        notes.Count == 0 ? 0 : notes.Max(n => n.Position + n.Duration) + 10_000_000;

    private static XElement Rest(XNamespace ns, int duration) =>
        new(ns + "note",
            new XElement(ns + "rest"),
            new XElement(ns + "duration", duration.ToString(CultureInfo.InvariantCulture)));

    private static XElement NoteElement(XNamespace ns, int midi, int duration, string lyric)
    {
        int semitone = midi % 12;
        int octave = midi / 12 - 1;
        char step;
        int alter = 0;
        switch (semitone)
        {
            case 0: step = 'C'; break;
            case 1: step = 'C'; alter = 1; break;
            case 2: step = 'D'; break;
            case 3: step = 'D'; alter = 1; break;
            case 4: step = 'E'; break;
            case 5: step = 'F'; break;
            case 6: step = 'F'; alter = 1; break;
            case 7: step = 'G'; break;
            case 8: step = 'G'; alter = 1; break;
            case 9: step = 'A'; break;
            case 10: step = 'A'; alter = 1; break;
            default: step = 'B'; break;
        }

        var pitch = new XElement(ns + "pitch",
            new XElement(ns + "step", step.ToString()),
            alter != 0 ? new XElement(ns + "alter", alter.ToString(CultureInfo.InvariantCulture)) : null,
            new XElement(ns + "octave", octave.ToString(CultureInfo.InvariantCulture)));

        var note = new XElement(ns + "note",
            pitch,
            new XElement(ns + "duration", duration.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(lyric) && lyric != "+~")
            note.Add(new XElement(ns + "lyric", new XElement(ns + "syllabic", "single"), new XElement(ns + "text", lyric)));
        return note;
    }

    private static int PitchToMidi(XElement pitchEl)
    {
        string step = Local(pitchEl, "step") ?? "C";
        if (step.Length == 0 || !StepSemitone.TryGetValue(char.ToUpperInvariant(step[0]), out int semitone)) return -1;
        int alter = int.TryParse(Local(pitchEl, "alter"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int a) ? a : 0;
        int octave = int.TryParse(Local(pitchEl, "octave"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) ? o : 4;
        int midi = (octave + 1) * 12 + semitone + alter;
        return midi is >= 0 and <= 127 ? midi : -1;
    }

    private static double ParseFat(XElement el, string child, int divisions)
    {
        string? v = Local(el, child);
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return 0;
        // MusicXML durations are in divisions; neutral model wants ticks == divisions, so scale 1:1.
        _ = divisions;
        return d;
    }

    private static IEnumerable<XElement> Descendants(XElement parent, string local) =>
        parent.Descendants().Where(e => e.Name.LocalName == local);

    private static string? Local(XElement parent, string local) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
}
