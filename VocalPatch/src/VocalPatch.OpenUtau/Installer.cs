using System.Text;

namespace VocalPatch.OpenUtau;

/// <summary>
/// Writes an OpenUtau legacy-plugin folder: <c>plugin.txt</c> (the manifest OpenUtau
/// reads), <c>vpatch.json</c> (which jobs to run), and the executable plus its
/// dependencies. Copy the produced folder into OpenUtau's <c>Plugins</c> directory.
/// </summary>
internal static class Installer
{
    public static int Install(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vpatch-utau install <directory> --job <id> [--job <id> ...] [--set key value] [--name Name] [--pinyin]");
            return 1;
        }

        string directory = Path.GetFullPath(args[0]);
        var config = new PluginConfig();

        string? name = null;
        string? shortcut = null;
        JobSpec? currentJob = null;

        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            switch (token.ToLowerInvariant())
            {
                case "--job":
                    if (++i >= args.Length) throw new ArgumentException("--job needs an id.");
                    currentJob = new JobSpec { Id = args[i] };
                    config.Jobs.Add(currentJob);
                    break;
                case "--set":
                    if (currentJob == null) throw new ArgumentException("--set must follow a --job.");
                    if (i + 2 >= args.Length) throw new ArgumentException("--set needs a key and a value.");
                    currentJob.Options[args[++i]] = args[++i];
                    break;
                case "--name":
                    if (++i >= args.Length) throw new ArgumentException("--name needs a value.");
                    name = args[i];
                    break;
                case "--shortcut":
                    if (++i >= args.Length) throw new ArgumentException("--shortcut needs a letter.");
                    shortcut = args[i];
                    break;
                case "--pinyin":
                    config.Pinyin ??= new PinyinSpec();
                    break;
                case "--tones":
                    if (++i >= args.Length) throw new ArgumentException("--tones needs a value.");
                    (config.Pinyin ??= new PinyinSpec()).Tones = args[i];
                    break;
                case "--mode":
                    if (++i >= args.Length) throw new ArgumentException("--mode needs a value.");
                    (config.Pinyin ??= new PinyinSpec()).Mode = args[i];
                    break;
                case "--split":
                    (config.Pinyin ??= new PinyinSpec()).Split = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{token}'.");
            }
        }

        if (config.Jobs.Count == 0 && config.Pinyin == null)
            throw new ArgumentException("Nothing to install: give at least one --job or --pinyin.");

        string displayName = name ?? BuildName(config);
        config.Name = displayName;
        Directory.CreateDirectory(directory);

        CopyRuntime(directory);
        config.Save(PluginConfig.PathFor(directory));
        WritePluginManifest(directory, displayName, shortcut, config.Jobs.Count > 0 ? "all" : "all");

        Console.WriteLine($"Installed OpenUtau plugin '{displayName}' into {directory}");
        Console.WriteLine("Copy this folder into OpenUtau's 'Plugins' directory, then use it from a part's plugin menu.");
        return 0;
    }

    private static string BuildName(PluginConfig config)
    {
        var parts = new List<string>();
        if (config.Pinyin != null) parts.Add("Pinyin");
        foreach (var j in config.Jobs) parts.Add(DisplayJob(j.Id));
        return "VocalPatch: " + (parts.Count > 0 ? string.Join(" + ", parts) : "Edit");
    }

    private static string DisplayJob(string id) => id.ToLowerInvariant() switch
    {
        "quantize" => "Quantize",
        "staccato" => "Staccato",
        "transpose" => "Transpose",
        "normalize" => "Normalize",
        "lyrics" => "Replace Lyrics",
        "swing" => "Swing",
        _ => id,
    };

    private static void WritePluginManifest(string directory, string name, string? shortcut, string notes)
    {
        // OpenUtau reads plugin.txt as Shift-JIS, so keep it ASCII-only.
        var sb = new StringBuilder();
        sb.Append("name=").Append(Ascii(name));
        if (!string.IsNullOrEmpty(shortcut)) sb.Append("(&").Append(Ascii(shortcut)[0]).Append(')');
        sb.Append('\n');
        sb.Append("execute=vpatch-utau.exe\n");
        sb.Append("notes=").Append(notes).Append('\n');
        sb.Append("shell=use\n");
        sb.Append("encoding=utf-8\n");
        File.WriteAllText(Path.Combine(directory, "plugin.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < 128 ? c : '?');
        return sb.ToString();
    }

    private static void CopyRuntime(string directory)
    {
        string source = AppContext.BaseDirectory;
        foreach (var file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (name is "vpatch.json" or "plugin.txt") continue;
            if (ext is not (".exe" or ".dll" or ".json" or ".pdb")) continue;
            File.Copy(file, Path.Combine(directory, name), overwrite: true);
        }
    }
}
