using VocalPatch.Core.Model;

namespace VocalPatch.Core.Jobs;

/// <summary>
/// Replace Lyrics: overwrites every note's lyric. "text" is a single syllable applied
/// to all notes; "texts" is a comma-separated list cycled across notes. Rests and
/// continuations are preserved unless "includeRests" is set.
/// </summary>
public sealed class ReplaceLyricsJob : IJob
{
    public string Id => "lyrics";
    public string DisplayName => "Replace Lyrics";

    public void Apply(Song song, JobOptions options)
    {
        song.Normalize();
        string single = options.GetString("text", string.Empty);
        string[] list = options.GetList("texts");
        bool includeRests = options.GetBool("includeRests", false);
        int selectedTrack = options.GetInt("track", -1);

        if (single.Length == 0 && list.Length == 0) return;

        int counter = 0;
        for (int ti = 0; ti < song.Tracks.Count; ti++)
        {
            if (selectedTrack >= 0 && ti != selectedTrack) continue;
            foreach (var note in song.Tracks[ti].SortedNotes())
            {
                if (!includeRests && note.IsRest) continue;
                if (!includeRests && note.Lyric.StartsWith("+")) continue;
                if (list.Length > 0)
                {
                    note.Lyric = list[counter % list.Length];
                    counter++;
                }
                else
                {
                    note.Lyric = single;
                }
            }
        }
    }
}
