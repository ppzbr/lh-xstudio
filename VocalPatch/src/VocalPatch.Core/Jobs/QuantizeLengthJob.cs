using VocalPatch.Core.Compat;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Jobs;

/// <summary>
/// Quantize Length: snaps each note's end to a grid. "strength" (0..100) blends
/// between the original length and the fully quantized length.
/// </summary>
public sealed class QuantizeLengthJob : IJob
{
    public string Id => "quantize";
    public string DisplayName => "Quantize Length";

    public void Apply(Song song, JobOptions options)
    {
        song.Normalize();
        int denominator = options.GetInt("grid", 4);            // 1=whole,2=half,4=quarter,8=eighth,16=sixteenth
        double strength = Num.Clamp(options.GetDouble("strength", 100), 0, 100) / 100.0;
        int minLen = Math.Max(10, options.GetInt("minLength", song.Resolution / 16));
        bool quantizeStart = options.GetBool("start", false);

        int grid = Math.Max(1, song.Resolution * 4 / Math.Max(1, denominator));
        int selectedTrack = options.GetInt("track", -1);

        for (int ti = 0; ti < song.Tracks.Count; ti++)
        {
            if (selectedTrack >= 0 && ti != selectedTrack) continue;
            foreach (var note in song.Tracks[ti].Notes)
            {
                if (quantizeStart)
                    note.Position = RoundBlend(note.Position, grid, strength);

                int end = note.End;
                int snappedEnd = (int)Math.Round(end / (double)grid) * grid;
                int snappedDur = snappedEnd - note.Position;
                if (snappedDur < minLen) snappedDur = minLen;
                note.Duration = (int)Math.Round(note.Duration * (1 - strength) + snappedDur * strength);
                if (note.Duration < minLen) note.Duration = minLen;
            }
        }
    }

    private static int RoundBlend(int value, int grid, double strength)
    {
        int snapped = (int)Math.Round(value / (double)grid) * grid;
        return (int)Math.Round(value * (1 - strength) + snapped * strength);
    }
}
