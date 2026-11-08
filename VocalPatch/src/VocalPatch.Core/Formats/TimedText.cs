using System.Globalization;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>Shared time parsing/formatting for timed-text lyric formats (LRC/SRT/ASS).</summary>
internal static class TimedText
{
    public static Song NewSong(string? path, FormatKind kind)
    {
        var song = new Song
        {
            Name = Path.GetFileNameWithoutExtension(path ?? "Untitled"),
            Resolution = 480,
            SourcePath = path,
            SourceFormat = kind,
        };
        song.Tracks.Add(new Track("Lyrics"));
        return song;
    }

    /// <summary>Turn (startMs, endMs, text) runs into neutral notes at a fixed 120 BPM tempo.</summary>
    public static void AddLines(Song song, IEnumerable<(double startMs, double endMs, string text)> lines)
    {
        var track = song.Tracks[0];
        var sorted = lines.OrderBy(l => l.startMs).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var (start, end, text) = sorted[i];
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (end <= start)
                end = i + 1 < sorted.Count ? sorted[i + 1].startMs : start + 500;
            int pos = Music.MusicMath.MsToTicks(start, song.Resolution, 120);
            int dur = Math.Max(10, Music.MusicMath.MsToTicks(end - start, song.Resolution, 120));
            track.Notes.Add(new Note(pos, dur, 60, text.Trim()));
        }
        song.Normalize();
    }

    public static double ParseLrcTime(string s)
    {
        // mm:ss.xx or mm:ss:xx
        s = s.Trim();
        var parts = s.Split(':');
        if (parts.Length < 2) return -1;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double min)) return -1;
        string secPart = string.Join(":", parts.Skip(1));
        char sep = secPart.IndexOf('.') >= 0 ? '.' : (secPart.IndexOf(',') >= 0 ? ',' : ':');
        var secs = secPart.Split(sep);
        if (!double.TryParse(secs[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)) return -1;
        double frac = 0;
        if (secs.Length > 1)
        {
            string f = secs[1].PadRight(3, '0').Substring(0, 3);
            if (int.TryParse(f, out int millis)) frac = millis / 1000.0;
        }
        return (min * 60 + sec + frac) * 1000.0;
    }

    /// <summary>Seconds.millis accepted; also H:MM:SS.mmm.</summary>
    public static double ParseClock(string s)
    {
        s = s.Trim().Replace(',', '.');
        var parts = s.Split(':');
        if (!double.TryParse(parts[parts.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double last)) return -1;
        double total = last;
        if (parts.Length >= 2 && int.TryParse(parts[parts.Length - 2], out int minutes)) total += minutes * 60;
        if (parts.Length >= 3 && int.TryParse(parts[parts.Length - 3], out int hours)) total += hours * 3600;
        return total * 1000.0;
    }

    public static string FormatLrc(double ms)
    {
        if (ms < 0) ms = 0;
        int totalSec = (int)(ms / 1000);
        int min = totalSec / 60;
        double sec = ms / 1000.0 - min * 60;
        return string.Format(CultureInfo.InvariantCulture, "[{0:00}:{1:00.00}]", min, sec);
    }

    public static string FormatSrt(double ms)
    {
        if (ms < 0) ms = 0;
        int totalMs = (int)Math.Round(ms);
        int h = totalMs / 3600000;
        int m = totalMs / 60000 % 60;
        int s = totalMs / 1000 % 60;
        int f = totalMs % 1000;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", h, m, s, f);
    }

    public static string FormatAss(double ms)
    {
        if (ms < 0) ms = 0;
        int totalMs = (int)Math.Round(ms);
        int h = totalMs / 3600000;
        int m = totalMs / 60000 % 60;
        int s = totalMs / 1000 % 60;
        int cs = totalMs / 10 % 100;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", h, m, s, cs);
    }
}
