using System.Text;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>Locates a format by id, extension, or by sniffing a file's first bytes.</summary>
public static class FormatRegistry
{
    private static readonly List<IProjectFormat> All = new();
    private static readonly Dictionary<string, IProjectFormat> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IProjectFormat> ByExt = new(StringComparer.OrdinalIgnoreCase);

    static FormatRegistry()
    {
        Register(new UstFormat());
        Register(new UstxFormat());
        Register(new VsqxFormat());
        Register(new VprFormat());
        Register(new PpsfFormat());
        Register(new CcsFormat());
        Register(new SvpFormat());
        Register(new MidiFormat());
        Register(new UfdataFormat());
        Register(new CommonnoteFormat());
        Register(new MusicXmlFormat());
        Register(new LrcFormat());
        Register(new SrtFormat());
        Register(new AssFormat());
        Register(new JsonSongFormat());
    }

    public static void Register(IProjectFormat format)
    {
        All.RemoveAll(f => string.Equals(f.Name, format.Name, StringComparison.OrdinalIgnoreCase));
        All.Add(format);
        ByName[format.Name] = format;
        foreach (var ext in format.Extensions) ByExt[ext] = format;
    }

    public static IReadOnlyList<IProjectFormat> Formats => All;

    public static IProjectFormat? ById(string name) =>
        ByName.TryGetValue(name.TrimStart('.'), out var f) ? f : null;

    public static IProjectFormat? ByExtension(string ext)
    {
        if (!ext.StartsWith(".")) ext = "." + ext;
        return ByExt.TryGetValue(ext, out var f) ? f : null;
    }

    /// <summary>Match the longest registered extension that the path ends with (handles ".ufdata.json").</summary>
    public static IProjectFormat? ByLongestExtension(string path)
    {
        IProjectFormat? best = null;
        int bestLen = 0;
        foreach (var f in All)
        {
            foreach (var ext in f.Extensions)
            {
                if (ext.Length > bestLen && path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    best = f;
                    bestLen = ext.Length;
                }
            }
        }
        return best;
    }

    /// <summary>Pick a read format from an explicit id, else the file extension, else sniff the content.</summary>
    public static IProjectFormat Resolve(string? explicitId, string path)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            var f = ById(explicitId!);
            if (f != null) return f;
            throw new NotSupportedException($"Unknown format id '{explicitId}'.");
        }
        var byLongExt = ByLongestExtension(path);
        if (byLongExt != null) return byLongExt;
        var byExt = ByExtension(Path.GetExtension(path));
        if (byExt != null) return byExt;
        var sniffed = Sniff(path);
        if (sniffed != null) return sniffed;
        throw new NotSupportedException($"Cannot determine format for '{path}'.");
    }

    /// <summary>Content sniffing used when the extension is missing or wrong.</summary>
    public static IProjectFormat? Sniff(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] head = new byte[4096];
        int read;
        using (var fs = File.OpenRead(path))
            read = fs.Read(head, 0, head.Length);
        string text = Encoding.UTF8.GetString(head, 0, read);
        if (text.StartsWith("MThd", StringComparison.Ordinal)) return ById("midi");
        if (text.Contains("[#SETTING]")) return ById("ust");
        if (text.Contains("ustxVersion:") || text.Contains("ustx_version:")) return ById("ustx");
        if (text.Contains("http://www.yamaha.co.jp/vocaloid/schema/vsq4/")) return ById("vsqx");
        if (text.Contains("http://www.yamaha.co.jp/vocaloid/schema/vsq3/")) return ById("vsqx");
        if (text.Contains("score-partwise")) return ById("musicxml");
        if (text.Contains("\"mouthOpening\"") || text.Contains("\"database\"") || text.Contains("\"library\"")) return ById("svp");
        if (text.Contains("\"formatVersion\"")) return ById("ufdata");
        if (text.Contains("\"commonnote\"")) return ById("commonnote");
        if (text.Contains("<CCSProject") || text.Contains("xmlns=\"http://cevio.jp/")) return ById("ccs");
        return null;
    }
}
