using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>Loads and saves songs by path, choosing the codec from the extension or an explicit id.</summary>
public static class ProjectIo
{
    /// <summary>Read a song. If <paramref name="formatId"/> is null the format is chosen by extension/sniffing.</summary>
    public static Song Load(string path, string? formatId = null)
    {
        var format = FormatRegistry.Resolve(formatId, path);
        using var stream = File.OpenRead(path);
        var song = format.Read(stream, path);
        if (string.IsNullOrWhiteSpace(song.Name)) song.Name = Path.GetFileNameWithoutExtension(path);
        return song;
    }

    /// <summary>Write a song. If <paramref name="formatId"/> is null the format is chosen by extension.</summary>
    public static void Save(Song song, string path, string? formatId = null)
    {
        var format = FormatRegistry.Resolve(formatId, path);
        if (!format.CanWrite) throw new NotSupportedException($"Format '{format.Name}' cannot be written.");
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var stream = File.Create(path);
        format.Write(song, stream);
    }

    /// <summary>Detect the format of a file without fully parsing it.</summary>
    public static IProjectFormat Detect(string path) => FormatRegistry.Resolve(null, path);
}
