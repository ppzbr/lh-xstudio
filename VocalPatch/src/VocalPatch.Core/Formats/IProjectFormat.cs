using System.Text;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>
/// A read/write codec for one project or interchange format. Implementations are
/// stateless and registered in <see cref="FormatRegistry"/>.
/// </summary>
public interface IProjectFormat
{
    FormatKind Kind { get; }

    /// <summary>Stable short id, e.g. "ustx".</summary>
    string Name { get; }

    string DisplayName { get; }

    /// <summary>Lower-case extensions including the dot, e.g. ".ustx".</summary>
    IReadOnlyList<string> Extensions { get; }

    bool CanRead { get; }

    bool CanWrite { get; }

    /// <summary>Read a stream into a neutral <see cref="Song"/>. <paramref name="path"/> is advisory (for relative refs).</summary>
    Song Read(Stream stream, string? path = null);

    /// <summary>Write a neutral <see cref="Song"/> to a stream.</summary>
    void Write(Song song, Stream stream);
}

/// <summary>Convenience base for text formats: handles encoding + stream plumbing.</summary>
public abstract class TextProjectFormat : IProjectFormat
{
    static TextProjectFormat()
    {
        // Shift-JIS et al. are not in the default netstandard provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public abstract FormatKind Kind { get; }
    public abstract string Name { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<string> Extensions { get; }
    public virtual bool CanRead => true;
    public virtual bool CanWrite => true;

    /// <summary>Default encoding when a format does not carry its own.</summary>
    protected virtual Encoding DefaultEncoding { get; } = new UTF8Encoding(false);

    protected abstract Song ReadText(string text, string? path);
    protected abstract string WriteText(Song song);

    public virtual Song Read(Stream stream, string? path = null)
    {
        using var reader = new StreamReader(stream, DetectEncoding(stream), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return ReadText(text, path);
    }

    public virtual void Write(Song song, Stream stream)
    {
        string text = WriteText(song);
        using var writer = new StreamWriter(stream, DefaultEncoding, 1024, leaveOpen: true);
        writer.Write(text);
        writer.Flush();
    }

    /// <summary>Formats that advertise their own charset (UST's Charset= line) may override.</summary>
    protected virtual Encoding DetectEncoding(Stream stream) => DefaultEncoding;
}
