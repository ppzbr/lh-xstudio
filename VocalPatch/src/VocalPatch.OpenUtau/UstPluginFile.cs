using System.Text;
using System.Text.RegularExpressions;

namespace VocalPatch.OpenUtau;

/// <summary>What a block means to OpenUtau when it reads the temp file back.</summary>
internal enum UstBlockKind
{
    /// <summary>Not part of the note sequence: <c>[#SETTING]</c>, <c>[#PREV]</c>, <c>[#NEXT]</c>, <c>[#TRACKEND]</c>.</summary>
    Other,

    /// <summary>A numbered note block (<c>[#0000]</c>). Replaces the note at the current index.</summary>
    Note,

    /// <summary>A note the plugin added (<c>[#INSERT]</c>). Inserted at the current index.</summary>
    Insert,

    /// <summary>A note the plugin removed (<c>[#DELETE]</c>). Removes the note at the current index.</summary>
    Delete,
}

/// <summary>
/// One block of a UST plugin temp file, with its raw lines preserved.
///
/// OpenUtau reads the file back in order and pairs each block with the note it
/// originally wrote at that index:
///   numbered block -> replace sequence[index], index++
///   [#INSERT]      -> sequence.Insert(index, note), index++
///   [#DELETE]      -> sequence.RemoveAt(index), index unchanged
/// A numbered block past the end of the original sequence is ignored, so a note
/// the plugin *adds* must be emitted as [#INSERT], never as a numbered block.
/// </summary>
internal sealed class UstBlock
{
    private static readonly Regex MainNoteHeader = new(@"^\[#\d+\]$", RegexOptions.Compiled);
    private static readonly Regex InsertHeader = new(@"^\[#INSERT\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeleteHeader = new(@"^\[#DELETE\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UstBlock(string header)
    {
        Header = header;
        Kind = MainNoteHeader.IsMatch(header) ? UstBlockKind.Note
             : InsertHeader.IsMatch(header) ? UstBlockKind.Insert
             : DeleteHeader.IsMatch(header) ? UstBlockKind.Delete
             : UstBlockKind.Other;
    }

    public string Header { get; set; }

    public UstBlockKind Kind { get; }

    public List<string> Lines { get; } = new();

    /// <summary>True for the numbered note blocks (<c>[#0000]</c>) that make up the main sequence.</summary>
    public bool IsMainNote => Kind == UstBlockKind.Note;

    public string? Get(string key)
    {
        string prefix = key + "=";
        foreach (var line in Lines)
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line.Substring(prefix.Length);
        return null;
    }

    public void Set(string key, string value)
    {
        string prefix = key + "=";
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Lines[i] = prefix + value;
                return;
            }
        }
        Lines.Add(prefix + value);
    }

    /// <summary>A numbered rest block, as OpenUtau itself writes for a gap.</summary>
    public static UstBlock Rest(int length)
    {
        var b = new UstBlock("[#0000]");
        b.Lines.Add("Length=" + length);
        b.Lines.Add("Lyric=R");
        b.Lines.Add("NoteNum=60");
        b.Lines.Add("PreUtterance=");
        return b;
    }

    /// <summary>A rest the plugin adds to the sequence (<c>[#INSERT]</c>).</summary>
    public static UstBlock InsertRest(int length)
    {
        var b = new UstBlock("[#INSERT]");
        b.Lines.Add("Length=" + length);
        b.Lines.Add("Lyric=R");
        b.Lines.Add("NoteNum=60");
        b.Lines.Add("PreUtterance=");
        return b;
    }

    /// <summary>Drops the note at the current index (<c>[#DELETE]</c>).</summary>
    public static UstBlock Delete() => new UstBlock("[#DELETE]");
}

/// <summary>
/// A UST plugin temp file kept as an ordered list of blocks so that every field
/// OpenUtau cares about (pitch points, vibrato, velocity, oto aliases) survives
/// the edit untouched. Only the fields a job actually changes are rewritten, and
/// notes the job adds or removes use OpenUtau's [#INSERT]/[#DELETE] convention.
/// </summary>
internal sealed class UstPluginFile
{
    public List<UstBlock> Blocks { get; set; } = new();

    /// <summary>Lines that precede the first block, if any (some editors write a [Tempo] prelude).</summary>
    public List<string> Prelude { get; } = new();

    public IEnumerable<UstBlock> MainNotes() => Blocks.Where(b => b.IsMainNote);

    public static UstPluginFile Load(string path, Encoding encoding)
    {
        var doc = new UstPluginFile();
        var text = File.ReadAllText(path, encoding);
        UstBlock? current = null;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("[#", StringComparison.Ordinal))
            {
                current = new UstBlock(line.Trim());
                doc.Blocks.Add(current);
                continue;
            }
            if (line.Length == 0) continue;
            if (current == null) doc.Prelude.Add(line);
            else current.Lines.Add(line);
        }
        return doc;
    }

    public void Save(string path, Encoding encoding)
    {
        var sb = new StringBuilder();

        // Keep any prelude (a [Tempo] section, comments) so re-reading by other tools still works.
        foreach (var line in Prelude) sb.Append(line).Append('\n');

        int noteIndex = 0;
        foreach (var block in Blocks)
        {
            // OpenUtau parses the number but ignores it (position comes from the
            // accumulated lengths), and renumbers its own files sequentially too.
            string header = block.Kind switch
            {
                UstBlockKind.Note => "[#" + noteIndex++.ToString("D4") + "]",
                UstBlockKind.Insert => "[#INSERT]",
                UstBlockKind.Delete => "[#DELETE]",
                _ => block.Header,
            };
            sb.Append(header).Append('\n');
            foreach (var line in block.Lines)
                sb.Append(line).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), encoding);
    }

    public static int GetLength(UstBlock b) =>
        int.TryParse(b.Get("Length"), out int v) ? v : 0;

    public static void SetLength(UstBlock b, int v) => b.Set("Length", v.ToString());

    public static int GetPitch(UstBlock b) =>
        int.TryParse(b.Get("NoteNum"), out int v) ? v : 60;

    public static void SetPitch(UstBlock b, int v) => b.Set("NoteNum", v.ToString());

    public static string GetLyric(UstBlock b) => b.Get("Lyric") ?? "a";
}
