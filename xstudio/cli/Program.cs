using System.Text;
using VocalPatch.Core.Formats;
using VocalPatch.Core.Model;
using VocalPatch.XStudio;

namespace VocalPatch.XStudio.Cli;

/// <summary>
/// <c>svip3</c> — the X Studio side of the toolkit, as its own command.
///
/// The codec lives outside <c>VocalPatch.Core</c>, so this program does the one thing that
/// integrates it: <see cref="FormatRegistry.Register"/>. From that call on, every
/// <c>ProjectIo.Load</c>/<c>Save</c> in the process understands <c>.svip3</c> by extension,
/// which is what makes <c>convert</c> work in both directions with no other changes.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* no console */ }

        // Registering is the whole integration: after this, ".svip3" resolves like any other
        // extension and the neutral model's other codecs handle the other side.
        FormatRegistry.Register(new Svip3Format());

        if (args.Length == 0) return Help(0);
        if (args[0] is "-h" or "--help" or "help") return Help(0);

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "convert" when args.Length >= 3:
                    return Convert(args[1], args[2]);
                case "info" when args.Length >= 2:
                    return Info(args[1]);
                case "notes" when args.Length >= 2:
                    return Notes(args[1]);
                default:
                    Console.Error.WriteLine($"error: unknown or incomplete command '{args[0]}'");
                    return Help(1);
            }
        }
        catch (Exception ex)
        {
            // A format error is the agent's problem, not a crash: report it and leave.
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Convert(string input, string output)
    {
        Song song = Load(input);
        ProjectIo.Save(song, output);
        Console.WriteLine($"{input} -> {output} ({song.NoteCount} notes, {song.Tracks.Count} tracks)");
        return 0;
    }

    private static int Info(string path)
    {
        Song song = Load(path);
        Console.WriteLine($"file        {path}");
        Console.WriteLine($"name        {song.Name}");
        Console.WriteLine($"resolution  {song.Resolution} ticks/beat");
        Console.WriteLine($"tempo       {string.Join(", ", song.Tempos.Select(t => $"{t.Bpm:0.##}@{t.Position}"))}");
        Console.WriteLine($"metre       {string.Join(", ", song.TimeSignatures.Select(s => $"{s.Numerator}/{s.Denominator}@bar{s.BarPosition}"))}");
        Console.WriteLine($"content     0..{song.EndTick} ticks ({song.EndTick * 60.0 / (Tempo(song) * song.Resolution):0.#} s)");
        Console.WriteLine($"tracks      {song.Tracks.Count}   notes {song.NoteCount}");
        foreach (var track in song.Tracks)
        {
            string singer = string.IsNullOrEmpty(track.Singer) ? "-" : track.Singer;
            Console.WriteLine($"  {track.Name,-24} notes {track.Notes.Count,-5} singer {singer}");
        }
        return 0;
    }

    private static int Notes(string path)
    {
        Song song = Load(path);
        foreach (var track in song.Tracks)
        {
            Console.WriteLine($"# {track.Name}");
            foreach (var n in track.SortedNotes())
            {
                string phoneme = string.IsNullOrEmpty(n.Phoneme) ? "-" : n.Phoneme;
                Console.WriteLine($"{n.Position,8} {n.Duration,6} {n.Pitch,4} {n.Lyric,-6} {phoneme}");
            }
        }
        Console.WriteLine($"# {song.NoteCount} notes");
        return 0;
    }

    private static double Tempo(Song song) => song.Tempos.Count > 0 ? song.Tempos[0].Bpm : 120.0;

    /// <summary>
    /// Load through the toolkit; if the extension is unknown but the bytes are X Studio's,
    /// fall back to the codec directly rather than failing on a misnamed file.
    /// </summary>
    private static Song Load(string path)
    {
        try
        {
            return ProjectIo.Load(path);
        }
        catch (Exception) when (LooksLikeSvip3(path))
        {
            using var stream = File.OpenRead(path);
            return new Svip3Format().Read(stream, path);
        }
    }

    /// <summary>X Studio's track list names its own schema in ASCII near the front of the file.</summary>
    private static bool LooksLikeSvip3(string path)
    {
        try
        {
            byte[] head = new byte[4096];
            using var stream = File.OpenRead(path);
            int read = stream.Read(head, 0, head.Length);
            return Encoding.UTF8.GetString(head, 0, read).Contains("xstudio.proto.");
        }
        catch
        {
            return false;
        }
    }

    private static int Help(int exitCode)
    {
        Console.WriteLine("svip3 — X Studio (.svip3) codec for the VocalPatch toolkit");
        Console.WriteLine();
        Console.WriteLine("usage: svip3 <command> [positional...]");
        Console.WriteLine();
        Console.WriteLine("commands:");
        Console.WriteLine("  convert <in> <out>   convert .svip3 to any format the toolkit knows, and back");
        Console.WriteLine("  info <in>            summarise a project");
        Console.WriteLine("  notes <in>           one line per note: position, length, pitch, lyric, pinyin");
        Console.WriteLine();
        Console.WriteLine("examples:");
        Console.WriteLine("  svip3 convert song.svip3 song.svp        notes out");
        Console.WriteLine("  svip3 convert song.vpr   song.svip3       notes in, as a project to open");
        Console.WriteLine("  svip3 info \"%APPDATA%\\XiaoIce\\X Studio3\\auto_backup\\39971138.svip3\"");
        Console.WriteLine();
        Console.WriteLine("what does not survive: parameter curves, per-note vibrato and the audio track.");
        return exitCode;
    }
}
