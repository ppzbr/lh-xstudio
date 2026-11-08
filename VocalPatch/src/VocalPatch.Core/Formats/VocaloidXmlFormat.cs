using System.Globalization;
using System.Xml.Linq;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// Shared reader/writer for the VOCALOID XML family — VSQX (V3 = vsq3 namespace,
/// V4 = vsq4), VPR and Piapro's PPSF (both vsq4). Reading accepts either namespace;
/// writing targets the version the concrete format declares.
/// </summary>
public abstract class VocaloidXmlFormat : TextProjectFormat
{
    protected const string Ns3 = "http://www.yamaha.co.jp/vocaloid/schema/vsq3/";
    protected const string Ns4 = "http://www.yamaha.co.jp/vocaloid/schema/vsq4/";

    /// <summary>True to emit VOCALOID4 (vsq4) XML, false to emit VOCALOID3 (vsq3).</summary>
    protected abstract bool WriteV4 { get; }

    protected override Song ReadText(string text, string? path)
    {
        var doc = XDocument.Parse(text, LoadOptions.None);
        var root = doc.Root ?? throw new FormatException("Empty VOCALOID XML document.");
        bool v4 = string.Equals(root.Name.LocalName, "vsq4", StringComparison.OrdinalIgnoreCase);
        string ns = root.Name.NamespaceName;

        var song = new Song
        {
            SourcePath = path,
            SourceFormat = v4 ? FormatKind.Vsq4 : FormatKind.Vsq3,
            Resolution = 480,
        };

        string master = v4 ? "masterTrack" : "masterTrack";
        var masterEl = Child(root, master);
        int preMeasure = 0;
        if (masterEl != null)
        {
            song.Name = ChildValue(masterEl, "seqName") ?? "Untitled";
            song.Comment = ChildValue(masterEl, "comment") ?? string.Empty;
            if (int.TryParse(ChildValue(masterEl, "resolution"), out int res) && res > 0) song.Resolution = res;
            if (int.TryParse(ChildValue(masterEl, "preMeasure"), out int pm)) preMeasure = pm;

            song.Tempos.Clear();
            foreach (var tempo in masterEl.Elements().Where(e => Local(e) == "tempo"))
            {
                if (v4)
                {
                    int pos = ParseInt(ChildValue(tempo, "t"));
                    double bpm = ParseDouble(ChildValue(tempo, "v")) / 100.0;
                    song.Tempos.Add(new Tempo(pos, bpm <= 0 ? 120 : bpm));
                }
                else
                {
                    int pos = ParseInt(ChildValue(tempo, "posTick"));
                    double bpm = ParseDouble(ChildValue(tempo, "bpm"));
                    song.Tempos.Add(new Tempo(pos, bpm <= 0 ? 120 : bpm));
                }
            }
            if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, 120));

            song.TimeSignatures.Clear();
            foreach (var ts in masterEl.Elements().Where(e => Local(e) == "timeSig"))
            {
                if (v4)
                    song.TimeSignatures.Add(new TimeSignature(ParseInt(ChildValue(ts, "m")),
                        Math.Max(1, ParseInt(ChildValue(ts, "nu"))), Math.Max(1, ParseInt(ChildValue(ts, "de")))));
                else
                    song.TimeSignatures.Add(new TimeSignature(ParseInt(ChildValue(ts, "posMes")),
                        Math.Max(1, ParseInt(ChildValue(ts, "nume"))), Math.Max(1, ParseInt(ChildValue(ts, "denomi")))));
            }
            if (song.TimeSignatures.Count == 0) song.TimeSignatures.Add(new TimeSignature(0, 4, 4));
        }

        int shift = TimeSignatureTicks(song, preMeasure);

        foreach (var trackEl in root.Elements().Where(e => Local(e) == "vsTrack"))
        {
            var track = new Track(ChildValue(trackEl, v4 ? "name" : "trackName") ?? "Track");
            track.Comment = ChildValue(trackEl, "comment");
            var vVoice = Child(trackEl, "vVoice");
            if (vVoice != null) track.Singer = ChildValue(vVoice, "vVoiceName") ?? ChildValue(vVoice, "vBS");

            foreach (var partEl in trackEl.Elements().Where(e => Local(e) is "vsPart" or "musicalPart"))
            {
                int partPos = v4 ? ParseInt(ChildValue(partEl, "t")) : ParseInt(ChildValue(partEl, "posTick"));
                foreach (var noteEl in partEl.Elements().Where(e => Local(e) == "note"))
                {
                    int pos = v4 ? ParseInt(ChildValue(noteEl, "t")) : ParseInt(ChildValue(noteEl, "posTick"));
                    int dur = v4 ? ParseInt(ChildValue(noteEl, "dur")) : ParseInt(ChildValue(noteEl, "durTick"));
                    int pitch = v4 ? ParseInt(ChildValue(noteEl, "n")) : ParseInt(ChildValue(noteEl, "noteNum"));
                    string lyric = ChildValue(noteEl, v4 ? "y" : "lyric") ?? "a";
                    if (lyric == "-") lyric = "+~";
                    int vel = ParseInt(ChildValue(noteEl, v4 ? "v" : "velocity"));
                    if (dur <= 0) continue;
                    track.Notes.Add(new Note(partPos + pos + shift, dur, pitch, lyric)
                    {
                        Velocity = vel > 0 ? vel : 64,
                        Phoneme = Empty(ChildValue(noteEl, v4 ? "p" : "phnms")),
                    });
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
        XNamespace ns = WriteV4 ? Ns4 : Ns3;
        string root = WriteV4 ? "vsq4" : "vsq3";

        var master = new XElement(ns + "masterTrack",
            new XElement(ns + "seqName", song.Name),
            new XElement(ns + "comment", song.Comment ?? string.Empty),
            new XElement(ns + "resolution", song.Resolution.ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "preMeasure", "0"));

        foreach (var ts in song.TimeSignatures)
        {
            if (WriteV4)
                master.Add(new XElement(ns + "timeSig",
                    new XElement(ns + "m", ts.BarPosition.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "nu", ts.Numerator.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "de", ts.Denominator.ToString(CultureInfo.InvariantCulture))));
            else
                master.Add(new XElement(ns + "timeSig",
                    new XElement(ns + "posMes", ts.BarPosition.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "nume", ts.Numerator.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "denomi", ts.Denominator.ToString(CultureInfo.InvariantCulture))));
        }

        foreach (var t in song.Tempos)
        {
            if (WriteV4)
                master.Add(new XElement(ns + "tempo",
                    new XElement(ns + "t", t.Position.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "v", Math.Round(t.Bpm * 100).ToString(CultureInfo.InvariantCulture))));
            else
                master.Add(new XElement(ns + "tempo",
                    new XElement(ns + "posTick", t.Position.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "bpm", t.Bpm.ToString(CultureInfo.InvariantCulture))));
        }

        var rootEl = new XElement(ns + root,
            new XElement(ns + "vender",
                new XElement(ns + "venderName", "VocalPatch")),
            master);

        int trackNo = 0;
        foreach (var track in song.Tracks)
        {
            var noteEls = new List<XElement>();
            foreach (var n in track.SortedNotes())
            {
                XElement note = WriteV4
                    ? new XElement(ns + "note",
                        new XElement(ns + "t", n.Position.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "dur", n.Duration.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "n", n.Pitch.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "v", n.Velocity.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "y", string.IsNullOrEmpty(n.Lyric) ? "a" : n.Lyric),
                        new XElement(ns + "p", n.Phoneme ?? string.Empty))
                    : new XElement(ns + "note",
                        new XElement(ns + "posTick", n.Position.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "durTick", n.Duration.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "noteNum", n.Pitch.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "velocity", n.Velocity.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "lyric", string.IsNullOrEmpty(n.Lyric) ? "a" : n.Lyric),
                        new XElement(ns + "phnms", n.Phoneme ?? string.Empty));
                noteEls.Add(note);
            }

            int duration = track.Notes.Count > 0 ? track.SortedNotes().Last().End : song.Resolution;

            var part = WriteV4
                ? new XElement(ns + "vsPart",
                    new XElement(ns + "t", "0"),
                    new XElement(ns + "playTime", duration.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "name", string.IsNullOrWhiteSpace(track.Name) ? "Part" : track.Name),
                    new XElement(ns + "comment", string.Empty),
                    noteEls)
                : new XElement(ns + "musicalPart",
                    new XElement(ns + "posTick", "0"),
                    new XElement(ns + "playTime", duration.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "partName", string.IsNullOrWhiteSpace(track.Name) ? "Part" : track.Name),
                    new XElement(ns + "comment", string.Empty),
                    noteEls);

            var vsTrack = WriteV4
                ? new XElement(ns + "vsTrack",
                    new XElement(ns + "tNo", trackNo.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "name", track.Name ?? "Track"),
                    new XElement(ns + "comment", track.Comment ?? string.Empty),
                    part)
                : new XElement(ns + "vsTrack",
                    new XElement(ns + "vsTrackNo", trackNo.ToString(CultureInfo.InvariantCulture)),
                    new XElement(ns + "trackName", track.Name ?? "Track"),
                    new XElement(ns + "comment", track.Comment ?? string.Empty),
                    part);

            rootEl.Add(vsTrack);
            trackNo++;
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), rootEl).ToString();
    }

    private static int TimeSignatureTicks(Song song, int preMeasure)
    {
        if (preMeasure <= 0) return 0;
        var sig = song.TimeSignatures.Count > 0 ? song.TimeSignatures[0] : new TimeSignature(0, 4, 4);
        return (int)Math.Round(preMeasure * song.Resolution * (sig.Numerator * 4.0 / sig.Denominator));
    }

    private static string Local(XElement e) => e.Name.LocalName;

    private static XElement? Child(XElement parent, string local) =>
        parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, local, StringComparison.OrdinalIgnoreCase));

    private static string? ChildValue(XElement parent, string local)
    {
        var c = Child(parent, local);
        return c?.Value;
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int ParseInt(string? s) =>
        int.TryParse((s ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static double ParseDouble(string? s) =>
        double.TryParse((s ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}

/// <summary>VOCALOID3/4 VSQX. Reads either namespace; writes vsq3.</summary>
public sealed class VsqxFormat : VocaloidXmlFormat
{
    public override FormatKind Kind => FormatKind.Vsq3;
    public override string Name => "vsqx";
    public override string DisplayName => "VOCALOID VSQX (.vsqx)";
    public override IReadOnlyList<string> Extensions => new[] { ".vsqx" };
    protected override bool WriteV4 => false;
}

/// <summary>VOCALOID4 project XML (.vpr). Reads either namespace; writes vsq4.</summary>
public sealed class VprFormat : VocaloidXmlFormat
{
    public override FormatKind Kind => FormatKind.Vpr;
    public override string Name => "vpr";
    public override string DisplayName => "VOCALOID4 Project (.vpr)";
    public override IReadOnlyList<string> Extensions => new[] { ".vpr" };
    protected override bool WriteV4 => true;
}

/// <summary>Piapro Studio project (.ppsf). VOCALOID4 XML family.</summary>
public sealed class PpsfFormat : VocaloidXmlFormat
{
    public override FormatKind Kind => FormatKind.Ppsf;
    public override string Name => "ppsf";
    public override string DisplayName => "Piapro Studio Project (.ppsf)";
    public override IReadOnlyList<string> Extensions => new[] { ".ppsf" };
    protected override bool WriteV4 => true;
}
