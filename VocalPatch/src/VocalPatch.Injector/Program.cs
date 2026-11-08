using System.Diagnostics;
using System.Text;
using VocalPatch.Core.Autosave;
using VocalPatch.Editor;

namespace VocalPatch.Injector;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected */ }

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "profiles" => CmdProfiles(),
                "targets" => CmdTargets(),
                "inject" => CmdInject(args.Skip(1).ToArray()),
                "autosave" => CmdAutosave(args.Skip(1).ToArray()),
                "actions" => CmdActions(),
                "action" => CmdAction(args.Skip(1).ToArray()),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static int CmdProfiles()
    {
        Console.WriteLine("id".PadRight(12) + "process".PadRight(18) + "project files".PadRight(28) + "folders");
        foreach (var p in ProfileRegistry.All)
            Console.WriteLine(p.Id.PadRight(12) + p.ProcessName.PadRight(18) +
                              string.Join(", ", p.ProjectExtensions).PadRight(28) +
                              string.Join("; ", p.ProjectDirectories));
        return 0;
    }

    private static int CmdTargets()
    {
        var found = 0;
        foreach (var process in Process.GetProcesses())
        {
            string name;
            try { name = process.ProcessName; } catch { continue; }
            var profile = ProfileRegistry.Detect(name);
            if (profile == null) continue;
            found++;
            Console.WriteLine($"pid {process.Id,-7} {name,-20} -> {profile.DisplayName}");
        }
        if (found == 0) Console.WriteLine("No target editors are running.");
        return 0;
    }

    private static int CmdInject(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: vpatch-inject inject <pid|process-name> [native-loader.dll]");
            return 1;
        }

        string target = args[0];
        string loader = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "vocalpatch_loader.dll");

        int pid = ResolvePid(target);
        if (pid <= 0)
        {
            Console.Error.WriteLine($"No running process matches '{target}'.");
            return 1;
        }

        Injector.LoadLibrary(pid, loader);
        Console.WriteLine($"Injected {Path.GetFileName(loader)} into pid {pid}.");
        return 0;
    }

    private static int CmdAutosave(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: vpatch-inject autosave <project file> [--interval seconds] [--snapshot] [--keep n]");
            return 1;
        }

        string file = args[0];
        double interval = 300;
        bool snapshot = false;
        int keep = 20;
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--interval": double.TryParse(args[i + 1], out interval); break;
                case "--keep": int.TryParse(args[i + 1], out keep); break;
                case "--snapshot": snapshot = true; break;
            }
        }

        using var saver = new AutoSaver(file, TimeSpan.FromSeconds(interval))
        {
            WriteModelSnapshot = snapshot,
            MaxBackups = keep,
        };
        saver.Saved += (_, e) => Console.WriteLine("autosave: " + e.BackupPath);
        saver.Start();
        Console.WriteLine($"Autosaving {Path.GetFullPath(file)} every {interval:F0}s -> {saver.BackupDirectory} (Ctrl+C to stop).");
        using var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();
        return 0;
    }

    private static int CmdActions()
    {
        Console.WriteLine("Actions the injected runtime and this command share:");
        foreach (string id in EditorActions.ActionIds)
            Console.WriteLine("  " + id);
        return 0;
    }

    private static int CmdAction(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: vpatch-inject action <project file> <action>");
            Console.Error.WriteLine("       actions: " + string.Join(", ", EditorActions.ActionIds));
            return 1;
        }

        string file = Path.GetFullPath(args[0]);
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"No such file: {file}");
            return 1;
        }

        string result = Bootstrap.RunAction(args[1], file);
        Console.WriteLine(result);

        bool failed = result.StartsWith("unknown action", StringComparison.OrdinalIgnoreCase)
                      || result.Contains("failed", StringComparison.OrdinalIgnoreCase)
                      || result.Contains("no project found", StringComparison.OrdinalIgnoreCase);
        return failed ? 1 : 0;
    }

    private static int ResolvePid(string target)
    {
        if (int.TryParse(target, out int pid))
        {
            try { Process.GetProcessById(pid); return pid; } catch { return -1; }
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (string.Equals(process.ProcessName, target, StringComparison.OrdinalIgnoreCase))
                    return process.Id;
            }
            catch { /* process may have exited */ }
        }
        return -1;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command '{cmd}'.");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("vpatch-inject — load the VocalPatch runtime into an editor, and autosave its projects");
        Console.WriteLine();
        Console.WriteLine("  profiles                                   list target editors and their project files");
        Console.WriteLine("  targets                                    list running target editors");
        Console.WriteLine("  inject <pid|process-name> [loader.dll]     inject a native loader into the editor");
        Console.WriteLine("  autosave <project> [--interval s] [--snapshot] [--keep n]");
        Console.WriteLine("  actions                                    list the shared edit actions");
        Console.WriteLine("  action <project> <action>                  run one action on a project file");
        Console.WriteLine();
        Console.WriteLine("The injected native loader hosts the CLR and calls VocalPatch.Editor.Bootstrap.Initialize().");
        Console.WriteLine("A matching VocalPatch.Editor runtime is built by the VocalPatch.Editor project.");
    }
}
