namespace VocalPatch.Core.Music;

/// <summary>
/// Pitch / tick helpers shared by every format codec and job.
/// Ticks are the neutral model's unit; 480 ticks = one quarter note unless a
/// <see cref="Model.Song.Resolution"/> says otherwise.
/// </summary>
public static class MusicMath
{
    public static readonly string[] NoteNames =
    {
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B",
    };

    /// <summary>MIDI note (0..127) to scientific name, e.g. 60 -> "C4", 61 -> "C#4".</summary>
    public static string MidiToName(int midi)
    {
        if (midi < 0) midi = 0;
        if (midi > 127) midi = 127;
        int octave = midi / 12 - 1;
        return NoteNames[midi % 12] + octave.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Scientific name ("C#4", "db3") to MIDI note. Returns -1 when unparseable.</summary>
    public static int NameToMidi(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        name = name.Trim();
        int i = 0;
        char letter = char.ToUpperInvariant(name[i++]);
        int semitone = letter switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => -1,
        };
        if (semitone < 0) return -1;
        while (i < name.Length && (name[i] == '#' || name[i] == 'b' || name[i] == 'B' || name[i] == 's'))
        {
            // 'b' lowers, '#'/'s' raise. Guard against the octave letter 'B' only at position 0.
            if (name[i] == 'b') semitone -= 1;
            else semitone += 1;
            i++;
        }
        if (i >= name.Length) return -1;
        if (!int.TryParse(name.Substring(i), out int octave)) return -1;
        int midi = (octave + 1) * 12 + semitone;
        return midi is >= 0 and <= 127 ? midi : -1;
    }

    /// <summary>Ticks to milliseconds given a single constant tempo.</summary>
    public static double TicksToMs(int ticks, int resolution, double bpm)
    {
        if (resolution <= 0 || bpm <= 0) return 0;
        return ticks / (double)resolution * (60000.0 / bpm);
    }

    /// <summary>Milliseconds to ticks given a single constant tempo.</summary>
    public static int MsToTicks(double ms, int resolution, double bpm)
    {
        if (bpm <= 0) return 0;
        return (int)Math.Round(ms / (60000.0 / bpm) * resolution);
    }

    /// <summary>Frequency of a MIDI note in Hz (A4 = 440).</summary>
    public static double MidiToFrequency(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);
}
