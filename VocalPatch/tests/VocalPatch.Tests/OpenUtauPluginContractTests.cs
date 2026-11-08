using System.Text;
using VocalPatch.Core.Jobs;
using VocalPatch.OpenUtau;
using Xunit;

namespace VocalPatch.Tests;

/// <summary>
/// The OpenUtau legacy-plugin contract, tested the way OpenUtau itself consumes it.
///
/// OpenUtau writes a temp UST, runs the plugin, then reads the file back and pairs
/// each block with the note it originally wrote at that index:
///   numbered block -> replace sequence[index], index++
///   [#INSERT]      -> sequence.Insert(index, note), index++
///   [#DELETE]      -> sequence.RemoveAt(index), index unchanged
///   a numbered block past the end of the sequence is IGNORED
/// These tests replay that algorithm so a plugin output that would silently drop
/// notes fails here instead of in the editor.
/// </summary>
public sealed class OpenUtauPluginContractTests
{
    private const int Resolution = 480;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>A temp file shaped exactly like OpenUtau's Ust.WritePlugin output.</summary>
    private static string WriteOpenUtauTempFile(params (int Length, string Lyric, int Pitch)[] notes)
    {
        var sb = new StringBuilder();
        sb.Append("[#SETTING]\n");
        sb.Append("Tempo=120\n");
        sb.Append("Tracks=1\n");
        sb.Append("Mode2=True\n");
        for (int i = 0; i < notes.Length; i++)
        {
            sb.Append("[#").Append(i.ToString("D4")).Append("]\n");
            sb.Append("Length=").Append(notes[i].Length).Append('\n');
            sb.Append("Lyric=").Append(notes[i].Lyric).Append('\n');
            sb.Append("NoteNum=").Append(notes[i].Pitch).Append('\n');
            sb.Append("PreUtterance=\n");
        }
        sb.Append("[#TRACKEND]\n");

        string dir = Path.Combine(Path.GetTempPath(), "vpatch-utau", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "temp.tmp");
        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
        return path;
    }

    private static void RunPlugin(string path, Action<UstPluginFile> job)
    {
        var doc = UstPluginFile.Load(path, Utf8NoBom);
        job(doc);
        doc.Save(path, Utf8NoBom);
    }

    /// <summary>OpenUtau's ParsePlugin pairing, reduced to the note bodies.</summary>
    private static (List<string> Lyrics, List<int> Lengths, List<int> Pitches) ReadBackAsOpenUtau(
        string path, IReadOnlyList<(int Length, string Lyric, int Pitch)> original)
    {
        var sequence = original.ToList();
        var blocks = UstPluginFile.Load(path, Utf8NoBom).Blocks;

        int index = 0;
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case UstBlockKind.Insert:
                    if (index <= sequence.Count)
                    {
                        sequence.Insert(index, (UstPluginFile.GetLength(block), UstPluginFile.GetLyric(block),
                                                UstPluginFile.GetPitch(block)));
                        index++;
                    }
                    break;

                case UstBlockKind.Delete:
                    if (index < sequence.Count) sequence.RemoveAt(index);
                    break;

                case UstBlockKind.Note:
                    if (index < sequence.Count)
                    {
                        sequence[index] = (UstPluginFile.GetLength(block), UstPluginFile.GetLyric(block),
                                           UstPluginFile.GetPitch(block));
                        index++;
                    }
                    // else: OpenUtau drops this block.
                    break;
            }
        }

        return (sequence.Select(n => n.Lyric).ToList(),
                sequence.Select(n => n.Length).ToList(),
                sequence.Select(n => n.Pitch).ToList());
    }

    [Fact]
    public void Swing_keeps_every_note_and_shifts_the_off_beat_one()
    {
        // Four 8th notes (240 ticks each), so notes at 0, 240, 480, 720.
        var notes = new[]
        {
            (Length: 240, Lyric: "- ni", Pitch: 60), (Length: 240, Lyric: "- hao", Pitch: 62), (Length: 240, Lyric: "- shi", Pitch: 64), (Length: 240, Lyric: "- jie", Pitch: 65),
        };
        string file = WriteOpenUtauTempFile(notes);

        RunPlugin(file, doc => UtauOperations.Apply(doc, "swing", JobOptions.From(("division", "8"), ("amount", "50"))));
        var (lyrics, lengths, _) = ReadBackAsOpenUtau(file, notes);

        // Nothing is lost: OpenUtau wrote 4 notes, the plugin adds a rest before each
        // off-beat note (positions 240 and 720), so OpenUtau must end up with 6.
        Assert.Equal(6, lyrics.Count);
        Assert.Equal(new[] { "- ni", "R", "- hao", "- shi", "R", "- jie" }, lyrics);
        // The off-beat notes are shortened by the same amount that was inserted.
        Assert.Equal(new[] { 240, 120, 120, 240, 120, 120 }, lengths);
        Assert.Equal(lengths.Sum(), notes.Sum(n => n.Length)); // no time leaked
    }

    [Fact]
    public void Staccato_adds_a_trailing_insert_and_keeps_total_time()
    {
        var notes = new[]
        {
            (Length: 240, Lyric: "- ni", Pitch: 60), (Length: 240, Lyric: "- hao", Pitch: 62), (Length: 240, Lyric: "- shi", Pitch: 64),
        };
        string file = WriteOpenUtauTempFile(notes);

        RunPlugin(file, doc => UtauOperations.Apply(doc, "staccato", JobOptions.From(("strength", "medium"))));
        var (lyrics, lengths, _) = ReadBackAsOpenUtau(file, notes);

        Assert.Equal(4, lyrics.Count); // 3 shortened notes + the trailing rest
        Assert.Equal("R", lyrics[3]);
        Assert.Equal(new[] { 120, 120, 120, 360 }, lengths);
        Assert.Equal(notes.Sum(n => n.Length), lengths.Sum());
    }

    [Fact]
    public void Quantize_and_transpose_only_touch_fields_they_own()
    {
        var notes = new[]
        {
            (Length: 250, Lyric: "a", Pitch: 60), (Length: 230, Lyric: "b", Pitch: 62), (Length: 240, Lyric: "c", Pitch: 64),
        };
        string file = WriteOpenUtauTempFile(notes);

        RunPlugin(file, doc =>
        {
            UtauOperations.Apply(doc, "quantize", JobOptions.From(("grid", "4"), ("strength", "100")));
            UtauOperations.Apply(doc, "transpose", JobOptions.From(("semitones", "12")));
        });
        var (lyrics, _, pitches) = ReadBackAsOpenUtau(file, notes);

        Assert.Equal(new[] { "a", "b", "c" }, lyrics); // lyrics untouched
        Assert.Equal(new[] { 72, 74, 76 }, pitches);   // up an octave
        Assert.Equal(notes.Length, lyrics.Count);      // count untouched
    }

    [Fact]
    public void Unknown_fields_and_prelude_survive_the_round_trip()
    {
        string file = WriteOpenUtauTempFile((Length: 240, Lyric: "a", Pitch: 60), (Length: 240, Lyric: "b", Pitch: 62));

        // Simulate a real temp file: a prelude, and editor-only fields on a note.
        string text = File.ReadAllText(file, Utf8NoBom)
            .Replace("[#SETTING]", "[Tempo]\nBPM=120\n[#SETTING]")
            .Replace("Lyric=a\n", "Lyric=a\nVelocity=100\nPBW=0,0,1,0\n");
        File.WriteAllText(file, text, Utf8NoBom);

        RunPlugin(file, doc => UtauOperations.Pinyin(doc, new Core.Lyrics.RomanizationOptions
        {
            Mode = Core.Lyrics.RomanizationMode.Pinyin,
        }));

        string after = File.ReadAllText(file, Utf8NoBom);
        Assert.Contains("[Tempo]", after);      // prelude kept
        Assert.Contains("BPM=120", after);
        Assert.Contains("Velocity=100", after); // per-note fields kept
        Assert.Contains("PBW=0,0,1,0", after);
    }

    [Fact]
    public void Pinyin_converts_hanzi_and_leaves_rests_alone()
    {
        var notes = new[] { (Length: 240, Lyric: "你好", Pitch: 60), (Length: 240, Lyric: "R", Pitch: 62), (Length: 240, Lyric: "世界", Pitch: 64) };
        string file = WriteOpenUtauTempFile(notes);

        RunPlugin(file, doc => UtauOperations.Pinyin(doc, new Core.Lyrics.RomanizationOptions
        {
            Mode = Core.Lyrics.RomanizationMode.Pinyin,
            Separator = " ",
        }));
        var (lyrics, _, _) = ReadBackAsOpenUtau(file, notes);

        Assert.Equal(new[] { "ni hao", "R", "shi jie" }, lyrics);
    }

    [Fact]
    public void Sequence_never_gains_or_loses_time_for_any_supported_job()
    {
        var notes = new[]
        {
            (Length: 480, Lyric: "a", Pitch: 60), (Length: 240, Lyric: "b", Pitch: 62), (Length: 360, Lyric: "c", Pitch: 64), (Length: 480, Lyric: "d", Pitch: 65),
        };
        int total = notes.Sum(n => n.Length);

        // Jobs that shift notes must not leak time and must not drop notes.
        foreach (string job in new[] { "swing", "staccato", "transpose", "normalize" })
        {
            string file = WriteOpenUtauTempFile(notes);
            RunPlugin(file, doc => UtauOperations.Apply(doc, job, JobOptions.From()));
            var (_, lengths, _) = ReadBackAsOpenUtau(file, notes);

            Assert.Equal(total, lengths.Sum());
            Assert.True(lengths.Count >= notes.Length, job + " lost notes");
        }

        // Quantize deliberately resizes notes onto the grid, so its total length may
        // change - but it must still return exactly one note per note.
        {
            string file = WriteOpenUtauTempFile(notes);
            RunPlugin(file, doc => UtauOperations.Apply(doc, "quantize", JobOptions.From()));
            var (_, lengths, _) = ReadBackAsOpenUtau(file, notes);

            Assert.Equal(notes.Length, lengths.Count);
            Assert.All(lengths, l => Assert.True(l >= 10, "quantize produced a degenerate note"));
        }
    }
}
