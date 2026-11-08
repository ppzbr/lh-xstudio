using VocalPatch.Core.Compat;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Jobs;

/// <summary>
/// Swing: delays the off-beat subdivisions (the 2nd/4th 8th, or the odd 16ths)
/// to create a shuffle groove. "amount" is the percentage of a grid step to delay.
/// </summary>
public sealed class SwingJob : IJob
{
    public string Id => "swing";
    public string DisplayName => "Swing";

    public void Apply(Song song, JobOptions options)
    {
        song.Normalize();
        int division = options.GetInt("division", 8);           // 8 or 16
        double amount = options.GetDouble("amount", 50);        // 0..100
        bool keepEnd = options.GetBool("keepEnd", false);       // shorten the swung note instead of moving its end
        bool shuffle = options.GetBool("shuffle", false);       // triplet feel (delay 2/3)

        int gridStep = division == 16 ? Math.Max(1, song.Resolution / 4) : Math.Max(1, song.Resolution / 2);
        if (shuffle) amount = 200.0 / 3.0;                      // 2/3 of the step = triplet
        double factor = Num.Clamp(amount / 100.0, 0, 0.9);
        int delta = (int)Math.Round(gridStep * factor);

        int selectedTrack = options.GetInt("track", -1);
        for (int ti = 0; ti < song.Tracks.Count; ti++)
        {
            if (selectedTrack >= 0 && ti != selectedTrack) continue;
            foreach (var note in song.Tracks[ti].SortedNotes().ToList())
            {
                if (note.Position % gridStep != 0) continue;            // only grid-aligned notes
                int stepIndex = note.Position / gridStep;
                if (stepIndex % 2 == 0) continue;                       // even steps are the downbeats
                int room = note.Duration;
                int shift = Math.Min(delta, Math.Max(0, room - 10));
                if (shift <= 0) continue;
                note.Position += shift;
                if (keepEnd) note.Duration = Math.Max(10, note.Duration - shift);
            }
        }
    }
}
