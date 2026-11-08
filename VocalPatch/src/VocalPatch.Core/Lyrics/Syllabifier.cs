using System.Text;

namespace VocalPatch.Core.Lyrics;

/// <summary>
/// Splits a lyric line into syllable units so each unit can become one note.
/// Hanzi and kana are one unit per character; Latin text is split at vowel
/// boundaries; separators (spaces, hyphens, dots) are dropped.
/// </summary>
public static class Syllabifier
{
    /// <summary>Split into syllable units.</summary>
    public static IReadOnlyList<string> Split(string lyric)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(lyric)) return result;

        var buffer = new StringBuilder();
        for (int i = 0; i < lyric.Length; i++)
        {
            char c = lyric[i];
            if (c is ' ' or '\t' or '-' or '.' or '/' or ',' or '、' or '。')
            {
                Flush(result, buffer);
                continue;
            }
            if (LanguageDetector.IsHan(c) || LanguageDetector.IsKana(c) || LanguageDetector.IsHangul(c))
            {
                Flush(result, buffer);
                result.Add(c.ToString());
                continue;
            }
            buffer.Append(c);
        }
        Flush(result, buffer);
        return result;
    }

    /// <summary>Split a whole song's lyrics so each note holds at most one syllable.</summary>
    public static int SplitSong(Model.Song song, bool splitIntoNotes, int trackIndex = -1)
    {
        int changed = 0;
        for (int ti = 0; ti < song.Tracks.Count; ti++)
        {
            if (trackIndex >= 0 && ti != trackIndex) continue;
            var track = song.Tracks[ti];
            var rebuilt = new List<Model.Note>();
            foreach (var note in track.SortedNotes())
            {
                if (note.Lyric.StartsWith("+") || note.IsRest)
                {
                    rebuilt.Add(note);
                    continue;
                }
                var parts = Split(note.Lyric);
                if (parts.Count <= 1)
                {
                    if (parts.Count == 1 && parts[0] != note.Lyric) { note.Lyric = parts[0]; changed++; }
                    rebuilt.Add(note);
                    continue;
                }

                if (!splitIntoNotes)
                {
                    note.Lyric = string.Join(" ", parts);
                    changed++;
                    rebuilt.Add(note);
                    continue;
                }

                // Distribute the syllables across the note's duration.
                changed++;
                int step = Math.Max(10, note.Duration / parts.Count);
                int cursor = note.Position;
                for (int p = 0; p < parts.Count; p++)
                {
                    int dur = p == parts.Count - 1 ? Math.Max(10, note.End - cursor) : step;
                    var clone = note.Clone();
                    clone.Position = cursor;
                    clone.Duration = dur;
                    clone.Lyric = parts[p];
                    rebuilt.Add(clone);
                    cursor += step;
                }
            }

            track.Notes.Clear();
            track.Notes.AddRange(rebuilt.OrderBy(n => n.Position));
        }
        return changed;
    }

    private static void Flush(List<string> result, StringBuilder buffer)
    {
        if (buffer.Length == 0) return;
        result.Add(buffer.ToString());
        buffer.Clear();
    }
}
