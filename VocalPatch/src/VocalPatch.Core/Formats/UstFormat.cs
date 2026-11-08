using System.Globalization;
using System.Text;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// UTAU Sequence Text (.ust). INI-like blocks: a [#SETTING] header, one [#nnnn]
/// block per note, and a [#TRACKEND] footer.
/// </summary>
public sealed class UstFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Ust;
    public override string Name => "ust";
    public override string DisplayName => "UTAU Sequence Text (.ust)";
    public override IReadOnlyList<string> Extensions => new[] { ".ust" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    public override Song Read(Stream stream, string? path = null)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }
        var song = ReadBytes(bytes, path);
        return song;
    }

    private static Song ReadBytes(byte[] bytes, string? path)
    {
        var encoding = DetectUstEncoding(bytes);
        string text = encoding.GetString(StripBom(bytes, encoding));
        var song = new Song { SourcePath = path, SourceFormat = FormatKind.Ust };
        song.Tempos.Clear();
        var track = new Track("Track1");
        song.Tracks.Add(track);

        int cursor = 0;
        int lastPos = 0;
        string section = string.Empty;
        var body = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string lyric = string.Empty;
        int noteNum = 60;
        int length = 0;
        int? delta = null, duration = null;
        bool haveNote = false;

        void FlushNote()
        {
            if (!haveNote) return;
            if (delta.HasValue && duration.HasValue)
            {
                int pos = lastPos + delta.Value;
                if (lyric.ToLowerInvariant() != "r")
                    track.Notes.Add(new Note(pos, duration.Value, noteNum, lyric));
                lastPos = pos; cursor = pos + duration.Value;
            }
            else
            {
                int pos = cursor;
                if (lyric.ToLowerInvariant() != "r")
                    track.Notes.Add(new Note(pos, length, noteNum, lyric));
                lastPos = pos; cursor = pos + length;
            }
            body.Clear();
            haveNote = false;
            length = 0;
            delta = null;
            duration = null;
        }

        foreach (var raw in SplitLines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[#", StringComparison.Ordinal))
            {
                FlushNote();
                section = line.EndsWith("]", StringComparison.Ordinal)
                    ? line.Substring(2, line.Length - 3)
                    : line.Substring(2);
                if (section.StartsWith("SETTING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (section.Equals("TRACKEND", StringComparison.OrdinalIgnoreCase) ||
                    section.Equals("VERSION", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                haveNote = true;
                lyric = string.Empty;
                noteNum = 60;
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();

            if (section.StartsWith("SETTING", StringComparison.OrdinalIgnoreCase))
            {
                switch (key.ToLowerInvariant())
                {
                    case "tempo":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm > 0)
                        {
                            if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, bpm));
                            else song.Tempos[0] = new Tempo(song.Tempos[0].Position, bpm);
                        }
                        break;
                    case "projectname":
                        song.Name = value;
                        break;
                    case "voicedir":
                        track.Singer = value;
                        break;
                }
            }
            else if (haveNote)
            {
                switch (key.ToLowerInvariant())
                {
                    case "length":
                        if (int.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out int l)) length = l;
                        break;
                    case "delta":
                        if (int.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out int dl)) delta = dl;
                        break;
                    case "duration":
                        if (int.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out int du)) duration = du;
                        break;
                    case "lyric":
                        lyric = value.StartsWith("?") ? value.Substring(1) : value;
                        break;
                    case "notenum":
                        if (int.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out int nn)) noteNum = nn;
                        break;
                }
            }
        }
        FlushNote();

        if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, 120));
        song.Name = song.Name.Length == 0 ? "Untitled" : song.Name;
        song.Normalize();
        return song;
    }

    protected override Song ReadText(string text, string? path) =>
        ReadBytes(Encoding.UTF8.GetBytes(text), path);

    protected override string WriteText(Song song)
    {
        song.Normalize();
        var sb = new StringBuilder();
        sb.Append("[#SETTING]\n");
        sb.Append("Charset=UTF-8\n");
        sb.Append("Tempo=").Append(song.Tempos.Count > 0 ? song.Tempos[0].Bpm.ToString(CultureInfo.InvariantCulture) : "120").Append('\n');
        sb.Append("Tracks=1\n");
        sb.Append("ProjectName=").Append(song.Name).Append('\n');
        sb.Append("Mode2=True\n");

        int index = 0;
        int cursor = 0;
        foreach (var note in song.Tracks.SelectMany(t => t.SortedNotes()))
        {
            // UST stores notes sequentially; a gap before this note is a rest note.
            if (note.Position > cursor)
            {
                sb.Append("[#").Append(index.ToString("D4", CultureInfo.InvariantCulture)).Append("]\n");
                sb.Append("Length=").Append((note.Position - cursor).ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("Lyric=R\n");
                sb.Append("NoteNum=60\n");
                sb.Append("PreUtterance=\n");
                index++;
            }
            sb.Append("[#").Append(index.ToString("D4", CultureInfo.InvariantCulture)).Append("]\n");
            sb.Append("Length=").Append(note.Duration.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Lyric=").Append(string.IsNullOrEmpty(note.Lyric) ? "a" : note.Lyric).Append('\n');
            sb.Append("NoteNum=").Append(note.Pitch.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("PreUtterance=\n");
            cursor = note.Position > cursor ? note.Position : cursor;
            cursor += note.Duration;
            index++;
        }
        sb.Append("[#TRACKEND]\n");
        return sb.ToString();
    }

    /// <summary>UST historically ships as Shift-JIS. Honour Charset=, else guess from UTF-8 validity.</summary>
    protected override Encoding DetectEncoding(Stream stream) => DefaultEncoding;

    internal static Encoding DetectUstEncoding(byte[] bytes)
    {
        int bomSkip = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bomSkip = 3;
        int scan = Math.Min(bytes.Length, 512);
        string ascii = Encoding.ASCII.GetString(bytes, 0, scan);
        int idx = ascii.IndexOf("Charset=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int end = ascii.IndexOfAny(new[] { '\r', '\n' }, idx);
            if (end < 0) end = ascii.Length;
            string name = ascii.Substring(idx + 8, end - idx - 8).Trim();
            try { return Encoding.GetEncoding(name); } catch { /* fall through */ }
        }
        return IsValidUtf8(bytes, bomSkip) ? new UTF8Encoding(false) : GetShiftJis();
    }

    private static Encoding GetShiftJis()
    {
        try { return Encoding.GetEncoding(932); }
        catch { return new UTF8Encoding(false); }
    }

    private static byte[] StripBom(byte[] bytes, Encoding enc)
    {
        var preamble = enc.GetPreamble();
        if (preamble.Length > 0 && bytes.Length >= preamble.Length)
        {
            bool match = true;
            for (int i = 0; i < preamble.Length; i++)
                if (bytes[i] != preamble[i]) { match = false; break; }
            if (match) return bytes.Skip(preamble.Length).ToArray();
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return bytes.Skip(3).ToArray();
        return bytes;
    }

    private static bool IsValidUtf8(byte[] bytes, int start)
    {
        int i = start;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            int n;
            if (b < 0x80) { i++; continue; }
            else if ((b & 0xE0) == 0xC0) n = 1;
            else if ((b & 0xF0) == 0xE0) n = 2;
            else if ((b & 0xF8) == 0xF0) n = 3;
            else return false;
            if (i + n >= bytes.Length) return false;
            for (int k = 1; k <= n; k++)
                if ((bytes[i + k] & 0xC0) != 0x80) return false;
            i += n + 1;
        }
        return true;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                yield return text.Substring(start, end - start);
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text.Substring(start);
    }
}
