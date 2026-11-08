using VocalPatch.Core.Compat;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Jobs;

/// <summary>Transpose every note by a fixed number of semitones (or octaves).</summary>
public sealed class TransposeJob : IJob
{
    public string Id => "transpose";
    public string DisplayName => "Transpose";

    public void Apply(Song song, JobOptions options)
    {
        song.Normalize();
        int semitones = options.GetInt("semitones", 0);
        int octaves = options.GetInt("octaves", 0);
        int shift = semitones + octaves * 12;
        if (shift == 0) return;
        int selectedTrack = options.GetInt("track", -1);

        for (int ti = 0; ti < song.Tracks.Count; ti++)
        {
            if (selectedTrack >= 0 && ti != selectedTrack) continue;
            foreach (var note in song.Tracks[ti].Notes)
                note.Pitch = Num.Clamp(note.Pitch + shift, 0, 127);
        }
    }
}

/// <summary>Normalize notes: drop empty notes, clamp to valid pitch, remove overlaps (optionally).</summary>
public sealed class NormalizeJob : IJob
{
    public string Id => "normalize";
    public string DisplayName => "Normalize Notes";

    public void Apply(Song song, JobOptions options)
    {
        int minLength = Math.Max(10, options.GetInt("minLength", 10));
        bool fixOverlaps = options.GetBool("overlaps", true);
        bool clampPitch = options.GetBool("clampPitch", true);

        foreach (var track in song.Tracks)
        {
            track.Notes.RemoveAll(n => n.Duration <= 0);
            if (clampPitch)
                foreach (var n in track.Notes)
                    n.Pitch = Num.Clamp(n.Pitch, 0, 127);
            foreach (var n in track.Notes)
                if (n.Duration < minLength) n.Duration = minLength;

            if (fixOverlaps)
            {
                var ordered = track.SortedNotes().ToList();
                for (int i = 0; i < ordered.Count - 1; i++)
                {
                    var cur = ordered[i];
                    var next = ordered[i + 1];
                    if (cur.End > next.Position)
                    {
                        int newDur = next.Position - cur.Position;
                        cur.Duration = newDur < minLength ? minLength : newDur;
                    }
                }
            }
        }
        song.Normalize();
    }
}

/// <summary>Staccato: shortens notes by a strength, optionally adding a gap.</summary>
public sealed class StaccatoJob : IJob
{
    public string Id => "staccato";
    public string DisplayName => "Staccato";

    public void Apply(Song song, JobOptions options)
    {
        song.Normalize();
        double percent = options.Has("percent")
            ? options.GetDouble("percent", 50)
            : options.GetString("strength", "medium") switch
            {
                "weak" => 75,
                "medium" => 50,
                "strong" => 25,
                _ => 50,
            };
        int gap = Math.Max(0, options.GetInt("gap", 0));
        double factor = Num.Clamp(percent / 100.0, 0.05, 1.0);
        int selectedTrack = options.GetInt("track", -1);

        for (int ti = 0; ti < song.Tracks.Count; ti++)
        {
            if (selectedTrack >= 0 && ti != selectedTrack) continue;
            foreach (var note in song.Tracks[ti].Notes)
            {
                int newDur = (int)Math.Round(note.Duration * factor) - gap;
                note.Duration = Math.Max(10, newDur);
            }
        }
    }
}
