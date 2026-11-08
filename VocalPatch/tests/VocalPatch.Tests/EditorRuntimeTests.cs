using VocalPatch.Editor;
using Xunit;

namespace VocalPatch.Tests;

/// <summary>
/// Exercises the exact entry points the native CLR-hosting loader calls inside an
/// injected editor: <c>Bootstrap.Run</c> / <c>Bootstrap.Initialize</c>. Keeping both
/// in one class makes xUnit run them serially, since the runtime is process-global.
/// </summary>
public sealed class EditorRuntimeTests
{
    [Fact]
    public void Bootstrap_initializes_autosave_hotkey_and_log()
    {
        Bootstrap.Initialize("synthv");
        try
        {
            Assert.True(Bootstrap.Initialized);
            Assert.NotNull(Bootstrap.Profile);
            Assert.Equal("synthv", Bootstrap.Profile!.Id);

            // Autosave watcher attached the SynthV project folders.
            Assert.NotEmpty(Bootstrap.WatchedDirectories);

            // The injected case is observable through the log file.
            Assert.True(File.Exists(Bootstrap.LogFilePath));
            string log = File.ReadAllText(Bootstrap.LogFilePath);
            Assert.Contains("profile:", log);
            Assert.Contains("watching", log);
        }
        finally
        {
            Bootstrap.Shutdown();
        }
    }

    [Fact]
    public void Run_is_the_loaders_entry_point_and_returns_zero()
    {
        int exitCode = Bootstrap.Run("openutau");
        try
        {
            Assert.Equal(0, exitCode);
            Assert.True(Bootstrap.Initialized);
            Assert.Equal("openutau", Bootstrap.Profile!.Id);
            Assert.Contains(Bootstrap.Profile.ProcessName, "OpenUtau");
        }
        finally
        {
            Bootstrap.Shutdown();
        }
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        Bootstrap.Initialize("cevio");
        var first = Bootstrap.Profile;
        Bootstrap.Initialize("piapro");
        Assert.Same(first, Bootstrap.Profile);
        Bootstrap.Shutdown();
        Assert.False(Bootstrap.Initialized);
    }

    [Fact]
    public void Every_profile_has_a_process_and_known_extensions()
    {
        Assert.Equal(9, ProfileRegistry.All.Count);
        foreach (var profile in ProfileRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.ProcessName));
            Assert.NotEmpty(profile.ProjectExtensions);
            Assert.NotEmpty(profile.ProjectDirectories);
            Assert.All(profile.ProjectExtensions, ext => Assert.StartsWith(".", ext));
            // Detection round-trips through the process name.
            Assert.Same(profile, ProfileRegistry.Detect(profile.ProcessName));
        }
    }

    /// <summary>
    /// The process names seen on a real machine, not the tidy ones the profile declares.
    /// Equality matching recognised only the exact spelling, so an editor whose executable
    /// carries a suffix was silently treated as "unknown" and watched every folder.
    /// </summary>
    [Theory]
    [InlineData("synthv-studio", "synthv")]
    [InlineData("VoiSona", "voisona")]
    [InlineData("OpenUtau", "openutau")]
    [InlineData("CeVIO", "cevio")]
    [InlineData("CeVIO AI", "cevio")]
    [InlineData("CeVIO AI.orig", "cevio")]       // wrapper around the original exe
    [InlineData("CeVIO.orig", "cevio")]
    [InlineData("Piapro", "piapro")]
    [InlineData("Piapro Studio", "piapro")]
    [InlineData("Piapro Studio NT", "piapro")]
    [InlineData("VoiSonaEditor", "voisona")]
    [InlineData("SynthUTAU", "synthutau")]        // OpenUtau forks
    [InlineData("UtauV", "utauv")]
    [InlineData("ACE Studio", "ace")]
    [InlineData("NetEase Cloud Music XStudio", "xstudio")]
    public void Profile_detection_accepts_the_process_names_editors_really_use(string processName, string expectedId)
    {
        var profile = ProfileRegistry.Detect(processName);

        Assert.NotNull(profile);
        Assert.Equal(expectedId, profile!.Id);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("explorer")]
    [InlineData("vpatch-inject")]
    [InlineData("")]
    public void Profile_detection_leaves_foreign_processes_alone(string processName)
    {
        Assert.Null(ProfileRegistry.Detect(processName));
    }

    /// <summary>
    /// The save hook patches the BCL writers, so it reports the same count in every host.
    /// What differs is whether the host shares our runtime: only then does the patch
    /// intercept the host's own saves, and the log has to say so instead of implying it
    /// always works.
    /// </summary>
    [Fact]
    public void Only_a_host_sharing_our_runtime_counts_as_patchable()
    {
        string ours = @"C:\app\payload\";
        string runtime = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\";
        string gac = @"C:\Windows\Microsoft.Net\assembly\";
        var platform = new[] { runtime, gac };

        // A host on .NET 5+ keeps its own runtime, so all this AppDomain holds is our payload
        // and the platform. System.dll comes out of the GAC, not out of the runtime directory:
        // the first version of this check excluded only the runtime directory, so that looked
        // like a host assembly and every host was reported as sharing our runtime.
        Assert.False(SavePathPatcher.AnyForeignAssembly(ours, platform, new[]
        {
            ours + "VocalPatch.Editor.dll",
            ours + "0Harmony.dll",
            runtime + "mscorlib.dll",
            runtime + "System.Xml.dll",
            gac + @"GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll",
        }));

        // A Framework host loads its own assemblies into the same AppDomain.
        Assert.True(SavePathPatcher.AnyForeignAssembly(ours, platform, new[]
        {
            ours + "VocalPatch.Editor.dll",
            gac + @"GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll",
            @"C:\Program Files\Dreamtonics\synthv-studio.exe",
        }));

        // The finding is named, and an empty location is not evidence of anything.
        Assert.Equal(
            @"C:\Program Files\Dreamtonics\synthv-studio.exe",
            SavePathPatcher.FirstForeignAssembly(ours, platform, new[]
            {
                "",
                ours + "VocalPatch.Editor.dll",
                @"C:\Program Files\Dreamtonics\synthv-studio.exe",
            }));

        Assert.Null(SavePathPatcher.FirstForeignAssembly(ours, platform, new[] { "", string.Empty }));
    }
}
