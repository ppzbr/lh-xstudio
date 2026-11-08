using System.Text;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>SubRip subtitles (.srt) as a timed lyric interchange.</summary>
public sealed class SrtFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Srt;
    public override string Name => "srt";
    public override string DisplayName => "SubRip Subtitles (.srt)";
    public override IReadOnlyList<string> Extensions => new[] { ".srt" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    protected override Song ReadText(string text, string? path)
    {
        var song = TimedText.NewSong(path, FormatKind.Srt);
        var lines = new List<(double, double, string)>();
        var blocks = text.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var rows = block.Split('\n');
            int timeRow = Array.FindIndex(rows, r => r.Contains("-->"));
            if (timeRow < 0) continue;
            var parts = rows[timeRow].Split(new[] { "-->" }, StringSplitOptions.None);
            if (parts.Length < 2) continue;
            double start = TimedText.ParseClock(parts[0]);
            double end = TimedText.ParseClock(parts[1]);
            string lyric = string.Join(" ", rows.Skip(timeRow + 1)).Trim();
            if (lyric.Length > 0) lines.Add((start, end, lyric));
        }
        TimedText.AddLines(song, lines);
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        var sb = new StringBuilder();
        int index = 1;
        foreach (var track in song.Tracks)
        {
            foreach (var n in track.SortedNotes())
            {
                if (n.IsRest) continue;
                sb.Append(index++).Append('\n');
                sb.Append(TimedText.FormatSrt(song.TickToMs(n.Position)))
                  .Append(" --> ")
                  .Append(TimedText.FormatSrt(song.TickToMs(n.End)))
                  .Append('\n');
                sb.Append(n.Lyric).Append("\n\n");
            }
        }
        return sb.ToString();
    }
}

/// <summary>Advanced SubStation Alpha (.ass) dialogue lines as a timed lyric interchange.</summary>
public sealed class AssFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Ass;
    public override string Name => "ass";
    public override string DisplayName => "ASS Subtitles (.ass)";
    public override IReadOnlyList<string> Extensions => new[] { ".ass", ".ssa" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    protected override Song ReadText(string text, string? path)
    {
        var song = TimedText.NewSong(path, FormatKind.Ass);
        var lines = new List<(double, double, string)>();
        bool inEvents = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents) continue;
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            string rest = line.Substring("Dialogue:".Length);
            var fields = rest.Split(new[] { ',' }, 10);
            if (fields.Length < 10) continue;
            double start = TimedText.ParseClock(fields[1]);
            double end = TimedText.ParseClock(fields[2]);
            string lyric = fields[9].Replace("\\N", " ").Replace("\\n", " ").Trim();
            if (lyric.Length > 0) lines.Add((start, end, lyric));
        }
        TimedText.AddLines(song, lines);
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        var sb = new StringBuilder();
        sb.Append("[Script Info]\nScriptType: v4.00+\nTitle: ").Append(song.Name).Append("\n\n");
        sb.Append("[V4+ Styles]\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        sb.Append("Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n\n");
        sb.Append("[Events]\n");
        sb.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var track in song.Tracks)
            foreach (var n in track.SortedNotes())
            {
                if (n.IsRest) continue;
                sb.Append("Dialogue: 0,")
                  .Append(TimedText.FormatAss(song.TickToMs(n.Position))).Append(',')
                  .Append(TimedText.FormatAss(song.TickToMs(n.End))).Append(",Default,,0,0,0,,")
                  .Append(n.Lyric).Append('\n');
            }
        return sb.ToString();
    }
}
