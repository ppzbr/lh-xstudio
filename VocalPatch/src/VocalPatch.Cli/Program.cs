using System.Text;
using VocalPatch.Core.Autosave;
using VocalPatch.Core.Formats;
using VocalPatch.Core.Jobs;
using VocalPatch.Core.Lyrics;
using VocalPatch.Core.Model;

namespace VocalPatch.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* console may be redirected */ }

        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var parsed = ParsedArgs.Parse(args.Skip(1).ToArray());

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "formats" => CmdFormats(),
                "jobs" => CmdJobs(),
                "info" => CmdInfo(parsed),
                "convert" => CmdConvert(parsed),
                "job" => CmdJob(parsed),
                "pinyin" => CmdPinyin(parsed),
                "harmony" => CmdHarmony(parsed),
                "autosave" => CmdAutosave(parsed),
                "help" or "--help" or "-h" => CmdHelpAndReturn(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static bool IsHelp(string a) => a is "-h" or "--help" or "/?" or "help";

    private static int CmdHelpAndReturn() { PrintHelp(); return 0; }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command '{cmd}'.");
        PrintHelp();
        return 1;
    }

    private static int CmdFormats()
    {
        Console.WriteLine("id".PadRight(14) + "extensions".PadRight(24) + "r/w".PadRight(6) + "name");
        foreach (var f in FormatRegistry.Formats)
        {
            string ext = string.Join(", ", f.Extensions);
            string rw = (f.CanRead ? "r" : "-") + (f.CanWrite ? "w" : "-");
            Console.WriteLine(f.Name.PadRight(14) + ext.PadRight(24) + rw.PadRight(6) + f.DisplayName);
        }
        return 0;
    }

    private static int CmdJobs()
    {
        Console.WriteLine("id".PadRight(14) + "name");
        foreach (var j in JobRegistry.Jobs)
            Console.WriteLine(j.Id.PadRight(14) + j.DisplayName);
        Console.WriteLine();
        Console.WriteLine("Options are passed as --key value (e.g. --division 8 --amount 50).");
        return 0;
    }

    private static int CmdInfo(ParsedArgs a)
    {
        string input = a.RequirePositional(0, "input file");
        var song = ProjectIo.Load(input, a.Get("from"));
        Console.WriteLine($"File:        {Path.GetFullPath(input)}");
        Console.WriteLine($"Format:      {ProjectIo.Detect(input).DisplayName}");
        Console.WriteLine($"Name:        {song.Name}");
        Console.WriteLine($"Resolution:  {song.Resolution} ticks/quarter");
        Console.WriteLine($"Tempo(s):    {string.Join(", ", song.Tempos.Select(t => $"{t.Bpm}@{t.Position}"))}");
        Console.WriteLine($"Time sig(s): {string.Join(", ", song.TimeSignatures.Select(s => $"{s.Numerator}/{s.Denominator}@{s.BarPosition}"))}");
        Console.WriteLine($"Tracks:      {song.Tracks.Count}");
        for (int i = 0; i < song.Tracks.Count; i++)
        {
            var t = song.Tracks[i];
            Console.WriteLine($"  [{i}] {t.Name}  notes={t.Notes.Count}  singer={t.Singer ?? "-"}");
        }
        Console.WriteLine($"Total notes: {song.NoteCount}");
        Console.WriteLine($"End tick:    {song.EndTick}  (~{song.TickToMs(song.EndTick) / 1000.0:F2}s)");
        return 0;
    }

    private static int CmdConvert(ParsedArgs a)
    {
        string input = a.RequirePositional(0, "input file");
        string output = a.RequirePositional(1, "output file");
        var song = ProjectIo.Load(input, a.Get("from"));
        ProjectIo.Save(song, output, a.Get("to"));
        Console.WriteLine($"Converted {Path.GetFileName(input)} -> {Path.GetFileName(output)} ({song.NoteCount} notes).");
        return 0;
    }

    private static int CmdJob(ParsedArgs a)
    {
        string jobId = a.RequirePositional(0, "job id");
        string input = a.RequirePositional(1, "input file");
        string output = a.RequirePositional(2, "output file");
        var job = JobRegistry.ById(jobId) ?? throw new ArgumentException($"Unknown job '{jobId}'. Run 'vpatch jobs'.");
        var song = ProjectIo.Load(input, a.Get("from"));
        job.Apply(song, a.ToJobOptions());
        ProjectIo.Save(song, output, a.Get("to"));
        Console.WriteLine($"Applied '{job.Id}' to {song.NoteCount} notes -> {Path.GetFileName(output)}.");
        return 0;
    }

    private static int CmdPinyin(ParsedArgs a)
    {
        string input = a.RequirePositional(0, "input file");
        string output = a.RequirePositional(1, "output file");
        var options = new RomanizationOptions
        {
            Mode = ParseMode(a.Get("mode")),
            ToneMarks = string.Equals(a.Get("tones"), "marks", StringComparison.OrdinalIgnoreCase),
            KeepToneNumbers = string.Equals(a.Get("tones"), "numbers", StringComparison.OrdinalIgnoreCase),
            Separator = a.Get("separator") ?? " ",
        };
        var song = ProjectIo.Load(input, a.Get("from"));
        bool split = a.HasFlag("split");
        if (split) Syllabifier.SplitSong(song, splitIntoNotes: true, a.GetInt("track", -1));
        int changed = PinyinConverter.ConvertSong(song, options, a.GetInt("track", -1));
        ProjectIo.Save(song, output, a.Get("to"));
        Console.WriteLine($"Romanised {changed} notes -> {Path.GetFileName(output)}.");
        return 0;
    }

    private static int CmdHarmony(ParsedArgs a)
    {
        string input = a.RequirePositional(0, "input file");
        string output = a.RequirePositional(1, "output file");
        var song = ProjectIo.Load(input, a.Get("from"));
        new HarmonyJob().Apply(song, a.ToJobOptions());
        ProjectIo.Save(song, output, a.Get("to"));
        Console.WriteLine($"Generated harmony track ({song.Tracks.Count} tracks) -> {Path.GetFileName(output)}.");
        return 0;
    }

    private static int CmdAutosave(ParsedArgs a)
    {
        string file = a.RequirePositional(0, "project file");
        double seconds = a.GetDouble("interval", 300);
        using var saver = new AutoSaver(file, TimeSpan.FromSeconds(seconds))
        {
            WriteModelSnapshot = a.HasFlag("snapshot"),
            MaxBackups = a.GetInt("keep", 20),
        };
        saver.Saved += (_, e) => Console.WriteLine($"autosave: {e.BackupPath}");
        saver.Failed += (_, ex) => Console.Error.WriteLine("autosave error: " + ex.Message);

        if (a.HasFlag("once"))
        {
            var path = saver.BackupNow();
            Console.WriteLine(path == null ? "Nothing to back up (file missing)." : "Backup written: " + path);
            return path == null ? 1 : 0;
        }

        saver.Start();
        Console.WriteLine($"Watching {Path.GetFullPath(file)} every {seconds:F0}s. Press Ctrl+C to stop.");
        using var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();
        return 0;
    }

    private static RomanizationMode ParseMode(string? s) => (s ?? "auto").ToLowerInvariant() switch
    {
        "pinyin" or "zh" => RomanizationMode.Pinyin,
        "romaji" or "jp" or "ja" => RomanizationMode.Romaji,
        _ => RomanizationMode.Auto,
    };

    private static void PrintHelp()
    {
        Console.WriteLine("vpatch — VocalPatch cross-editor singing-synthesis toolkit");
        Console.WriteLine();
        Console.WriteLine("usage: vpatch <command> [positional...] [--key value]");
        Console.WriteLine();
        Console.WriteLine("commands:");
        Console.WriteLine("  formats                              list supported project/interchange formats");
        Console.WriteLine("  jobs                                 list edit jobs");
        Console.WriteLine("  info <in>                            summarise a project");
        Console.WriteLine("  convert <in> <out> [--from id --to id]");
        Console.WriteLine("  job <job-id> <in> <out> [--opt value ...]");
        Console.WriteLine("  pinyin <in> <out> [--mode auto|pinyin|romaji] [--tones none|numbers|marks] [--split] [--track n]");
        Console.WriteLine("  harmony <in> <out> [--interval thirdUp] [--root C] [--scale major|minor] [--name Harmony]");
        Console.WriteLine("  autosave <file> [--interval secs] [--once] [--snapshot] [--keep n]");
        Console.WriteLine();
        Console.WriteLine("examples:");
        Console.WriteLine("  vpatch convert song.ustx song.svp");
        Console.WriteLine("  vpatch job swing song.vpr out.vpr --division 8 --amount 55");
        Console.WriteLine("  vpatch pinyin song.svp song.pinyin.svp --split");
        Console.WriteLine("  vpatch harmony song.ust out.ust --interval thirdUp --root C");
        Console.WriteLine();
        Console.WriteLine("Format ids come from 'vpatch formats'.");
    }
}

/// <summary>Minimal argv parser: positionals plus <c>--key value</c> / <c>--key=value</c> / <c>--flag</c>.</summary>
internal sealed class ParsedArgs
{
    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static ParsedArgs Parse(string[] args)
    {
        var p = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                string body = token.Substring(2);
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    p._options[body.Substring(0, eq)] = body.Substring(eq + 1);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    p._options[body] = args[++i];
                }
                else
                {
                    p._flags.Add(body);
                    p._options[body] = "true";
                }
            }
            else
            {
                p._positionals.Add(token);
            }
        }
        return p;
    }

    public string RequirePositional(int index, string what)
    {
        if (index >= _positionals.Count)
            throw new ArgumentException($"Missing {what}.");
        return _positionals[index];
    }

    public string? Get(string key) => _options.TryGetValue(key, out var v) ? v : null;

    public bool HasFlag(string key) => _flags.Contains(key);

    public int GetInt(string key, int fallback) =>
        _options.TryGetValue(key, out var v) && int.TryParse(v, out int i) ? i : fallback;

    public double GetDouble(string key, double fallback) =>
        _options.TryGetValue(key, out var v) && double.TryParse(v, out double d) ? d : fallback;

    /// <summary>All options except the routing keys, as a job option bag.</summary>
    public JobOptions ToJobOptions()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _options)
            if (kv.Key is not ("from" or "to")) values[kv.Key] = kv.Value;
        return new JobOptions(values);
    }
}
