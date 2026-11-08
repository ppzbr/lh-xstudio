using System.Text;
using System.Text.RegularExpressions;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>LRC lyrics (.lrc). Per-syllable timestamps; metadata tags are preserved as name/artist.</summary>
public sealed class LrcFormat : TextProjectFormat
{
    public override FormatKind Kind => FormatKind.Lrc;
    public override string Name => "lrc";
    public override string DisplayName => "LRC Lyrics (.lrc)";
    public override IReadOnlyList<string> Extensions => new[] { ".lrc" };
    protected override Encoding DefaultEncoding => new UTF8Encoding(false);

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex MetaTag = new(@"^\[(ti|ar|al|by|offset):(.*)\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected override Song ReadText(string text, string? path)
    {
        var song = TimedText.NewSong(path, FormatKind.Lrc);
        var lines = new List<(double startMs, double endMs, string text)>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            var meta = MetaTag.Match(line);
            if (meta.Success)
            {
                if (meta.Groups[1].Value.Equals("ti", StringComparison.OrdinalIgnoreCase)) song.Name = meta.Groups[2].Value.Trim();
                else if (meta.Groups[1].Value.Equals("ar", StringComparison.OrdinalIgnoreCase)) song.Tracks[0].Singer = meta.Groups[2].Value.Trim();
                continue;
            }
            var tags = TimeTag.Matches(line);
            if (tags.Count == 0) continue;
            string lyric = TimeTag.Replace(line, string.Empty).Trim();
            if (lyric.Length == 0) continue;
            foreach (Match t in tags)
            {
                double ms = TimedText.ParseLrcTime(t.Groups[1].Value);
                if (ms >= 0) lines.Add((ms, -1, lyric));
            }
        }
        TimedText.AddLines(song, lines);
        return song;
    }

    protected override string WriteText(Song song)
    {
        song.Normalize();
        var sb = new StringBuilder();
        sb.Append("[ti:").Append(song.Name).Append("]\n");
        if (song.Tracks.Count > 0 && !string.IsNullOrEmpty(song.Tracks[0].Singer))
            sb.Append("[ar:").Append(song.Tracks[0].Singer).Append("]\n");
        foreach (var track in song.Tracks)
            foreach (var n in track.SortedNotes())
                if (!n.IsRest)
                    sb.Append(TimedText.FormatLrc(song.TickToMs(n.Position))).Append(n.Lyric).Append('\n');
        return sb.ToString();
    }
}
