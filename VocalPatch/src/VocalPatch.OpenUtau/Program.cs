using System.Text;
using VocalPatch.Core.Jobs;
using VocalPatch.Core.Lyrics;

namespace VocalPatch.OpenUtau;

internal static class Program
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected */ }

        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "install" => Installer.Install(args.Skip(1).ToArray()),
                "run" => RunAdHoc(args.Skip(1).ToArray()),
                "help" or "--help" or "-h" => Help(),
                // OpenUtau invokes the plugin with a single argument: the temp file path.
                _ => RunConfigured(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("vpatch-utau: " + ex.Message);
            return 2;
        }
    }

    /// <summary>Normal OpenUtau invocation: apply the jobs from vpatch.json to the temp file.</summary>
    private static int RunConfigured(string tempFile)
    {
        var config = PluginConfig.LoadBeside(ExecutablePath());
        if (config == null)
        {
            // No config beside the exe: leave the file untouched and succeed.
            return 0;
        }

        var doc = UstPluginFile.Load(tempFile, Utf8NoBom);
        Run(doc, config);
        doc.Save(tempFile, Utf8NoBom);
        return 0;
    }

    /// <summary>Ad-hoc run used for testing: <c>vpatch-utau run file.ust --job swing --set amount 55</c>.</summary>
    private static int RunAdHoc(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vpatch-utau run <temp.ust> [--job id --set k v] [--pinyin --tones none]");
            return 1;
        }

        string file = args[0];
        var config = new PluginConfig();
        JobSpec? current = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--job":
                    current = new JobSpec { Id = args[++i] };
                    config.Jobs.Add(current);
                    break;
                case "--set":
                    if (current == null) throw new ArgumentException("--set must follow a --job.");
                    current.Options[args[i + 1]] = args[i + 2];
                    i += 2;
                    break;
                case "--pinyin":
                    config.Pinyin ??= new PinyinSpec();
                    break;
                case "--tones":
                    (config.Pinyin ??= new PinyinSpec()).Tones = args[++i];
                    break;
                case "--mode":
                    (config.Pinyin ??= new PinyinSpec()).Mode = args[++i];
                    break;
            }
        }

        var doc = UstPluginFile.Load(file, Utf8NoBom);
        Run(doc, config);
        doc.Save(file, Utf8NoBom);
        Console.WriteLine($"Applied {config.Jobs.Count} job(s) to {Path.GetFileName(file)}.");
        return 0;
    }

    private static void Run(UstPluginFile doc, PluginConfig config)
    {
        if (config.Pinyin != null)
        {
            var options = new RomanizationOptions
            {
                Mode = config.Pinyin.Mode.ToLowerInvariant() switch
                {
                    "romaji" => RomanizationMode.Romaji,
                    "auto" => RomanizationMode.Auto,
                    _ => RomanizationMode.Pinyin,
                },
                ToneMarks = config.Pinyin.Tones.Equals("marks", StringComparison.OrdinalIgnoreCase),
                KeepToneNumbers = config.Pinyin.Tones.Equals("numbers", StringComparison.OrdinalIgnoreCase),
            };
            UtauOperations.Pinyin(doc, options);
        }

        foreach (var job in config.Jobs)
            UtauOperations.Apply(doc, job.Id, new JobOptions(job.Options));
    }

    private static string ExecutablePath() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "vpatch-utau.exe");

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("vpatch-utau — OpenUtau plugin for the VocalPatch toolkit");
        Console.WriteLine();
        Console.WriteLine("  invoked by OpenUtau with a temp .ust path, applies the jobs in vpatch.json");
        Console.WriteLine();
        Console.WriteLine("  vpatch-utau install <dir> --job <id> [--job ...] [--set key value] [--name Name] [--pinyin]");
        Console.WriteLine("  vpatch-utau run <file.ust> [--job id --set key value] [--pinyin --tones none|numbers|marks]");
        Console.WriteLine();
        Console.WriteLine("  jobs: quantize, staccato, transpose, normalize, lyrics, swing");
    }
}
