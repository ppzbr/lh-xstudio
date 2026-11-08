using VocalPatch.Core.Model;

namespace VocalPatch.Core.Jobs;

using VocalPatch.Core.Music;
using VocalPatch.Core.Compat;

/// <summary>
/// Generate Harmony: builds a harmonised copy of a track at a scale interval.
/// "interval" is one of thirdUp/thirdDown/fourthUp/fifthUp/sixthUp/octaveUp/octaveDown
/// (or a raw semitone count via "semitones"). "root"/"scale" drive diatonic voicing;
/// without a scale it falls back to chromatic intervals.
/// </summary>
public sealed class HarmonyJob : IJob
{
    public string Id => "harmony";
    public string DisplayName => "Generate Harmony";

    private static readonly int[] Major = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] Minor = { 0, 2, 3, 5, 7, 8, 10 };

    public void Apply(Song song, JobOptions options)
    {
        song.Normalize();
        int source = options.GetInt("track", 0);
        if (source < 0 || source >= song.Tracks.Count) return;

        string interval = options.GetString("interval", "thirdUp");
        string rootName = options.GetString("root", "C");
        string scale = options.GetString("scale", "major");
        bool diatonic = options.GetBool("diatonic", true) && options.Has("root") || options.Has("scale") || !options.Has("semitones");
        string name = options.GetString("name", "Harmony");

        int rootPc = MusicMath.NameToMidi(rootName);
        rootPc = rootPc < 0 ? 0 : rootPc % 12;
        int[] scaleSet = scale.StartsWith("min", StringComparison.OrdinalIgnoreCase) ? Minor : Major;

        int degrees = interval switch
        {
            "thirdUp" => 2,
            "thirdDown" => -2,
            "fourthUp" => 3,
            "fourthDown" => -3,
            "fifthUp" => 4,
            "fifthDown" => -4,
            "sixthUp" => 5,
            "sixthDown" => -5,
            "octaveUp" => 7,
            "octaveDown" => -7,
            _ => int.MinValue,
        };
        int chromatic = ChromaticFallback(interval);
        if (options.Has("semitones"))
        {
            chromatic = options.GetInt("semitones", chromatic);
            diatonic = false;
        }

        var src = song.Tracks[source];
        var target = ResolveTarget(song, options, name);

        target.Notes.Clear();
        target.Singer = src.Singer;
        foreach (var n in src.SortedNotes())
        {
            int pitch = diatonic && degrees != int.MinValue
                ? TransposeDegrees(n.Pitch, rootPc, scaleSet, degrees)
                : n.Pitch + chromatic;
            pitch = Num.Clamp(pitch, 0, 127);
            target.Notes.Add(new Note(n.Position, n.Duration, pitch, n.Lyric)
            {
                Velocity = n.Velocity,
                Phoneme = n.Phoneme,
            });
        }
    }

    private static Track ResolveTarget(Song song, JobOptions options, string name)
    {
        string target = options.GetString("targetTrack", "new");
        if (target.Equals("new", StringComparison.OrdinalIgnoreCase) || target.Length == 0)
        {
            var t = new Track(name);
            song.Tracks.Add(t);
            return t;
        }
        if (int.TryParse(target, out int idx) && idx >= 0 && idx < song.Tracks.Count)
            return song.Tracks[idx];
        var created = new Track(name);
        song.Tracks.Add(created);
        return created;
    }

    private static int ChromaticFallback(string interval) => interval switch
    {
        "thirdUp" => 4,
        "thirdDown" => -4,
        "fourthUp" => 5,
        "fourthDown" => -5,
        "fifthUp" => 7,
        "fifthDown" => -7,
        "sixthUp" => 9,
        "sixthDown" => -9,
        "octaveUp" => 12,
        "octaveDown" => -12,
        _ => 4,
    };

    private static int TransposeDegrees(int midi, int rootPc, int[] scale, int degrees)
    {
        int rel = ((midi - rootPc) % 12 + 12) % 12;
        int degree = 0;
        for (int i = 0; i < scale.Length; i++)
            if (scale[i] <= rel) degree = i;
        int accidental = rel - scale[degree];

        int newDegree = degree + degrees;
        int octave = (int)Math.Floor(newDegree / 7.0);
        int nd = ((newDegree % 7) + 7) % 7;
        int newRel = scale[nd] + octave * 12 + accidental;
        return midi + (newRel - rel);
    }
}
