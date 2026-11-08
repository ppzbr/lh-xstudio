using VocalPatch.Core.Compat;
using VocalPatch.Core.Jobs;
using VocalPatch.Core.Lyrics;

namespace VocalPatch.OpenUtau;

/// <summary>
/// The jobs an OpenUtau plugin can apply while keeping the note sequence intact.
/// Position-shifting jobs work by inserting an <c>R</c> rest and trimming the swung
/// note, so the sequence length never drifts. Notes the plugin adds are written as
/// <c>[#INSERT]</c> because OpenUtau ignores numbered blocks past its own sequence.
/// </summary>
internal static class UtauOperations
{
    // OpenUtau fixes its resolution at 480 ticks per quarter.
    private const int Resolution = 480;

    public static void Apply(UstPluginFile doc, string jobId, JobOptions options)
    {
        switch (jobId.ToLowerInvariant())
        {
            case "quantize": Quantize(doc, options); break;
            case "staccato": Staccato(doc, options); break;
            case "transpose": Transpose(doc, options); break;
            case "normalize": Normalize(doc, options); break;
            case "lyrics": ReplaceLyrics(doc, options); break;
            case "swing": Swing(doc, options); break;
            default: throw new ArgumentException($"Job '{jobId}' is not supported by the OpenUtau plugin.");
        }
    }

    public static void Pinyin(UstPluginFile doc, RomanizationOptions options)
    {
        foreach (var note in doc.MainNotes())
        {
            string lyric = UstPluginFile.GetLyric(note);
            if (lyric.StartsWith("+") || lyric.StartsWith("[")) continue;
            string converted = PinyinConverter.Convert(lyric, options);
            if (converted != lyric) note.Set("Lyric", converted);
        }
    }

    private static void Quantize(UstPluginFile doc, JobOptions options)
    {
        int denominator = options.GetInt("grid", 4);
        double strength = Num.Clamp(options.GetDouble("strength", 100), 0, 100) / 100.0;
        int minLen = Math.Max(10, options.GetInt("minLength", Resolution / 16));
        int grid = Math.Max(1, Resolution * 4 / Math.Max(1, denominator));

        int cursor = 0;
        foreach (var note in doc.MainNotes())
        {
            int dur = UstPluginFile.GetLength(note);
            int snappedEnd = (int)Math.Round((cursor + dur) / (double)grid) * grid;
            int snappedDur = Math.Max(minLen, snappedEnd - cursor);
            int blended = (int)Math.Round(dur * (1 - strength) + snappedDur * strength);
            UstPluginFile.SetLength(note, Math.Max(minLen, blended));
            cursor += UstPluginFile.GetLength(note);
        }
    }

    private static void Staccato(UstPluginFile doc, JobOptions options)
    {
        double percent = options.Has("percent")
            ? options.GetDouble("percent", 50)
            : options.GetString("strength", "medium") switch
            {
                "weak" => 75,
                "medium" => 50,
                "strong" => 25,
                _ => 50,
            };
        int gap = Math.Max(0, options.GetInt("gap", 0));
        double factor = Num.Clamp(percent / 100.0, 0.05, 1.0);
        int before = 0, after = 0;
        foreach (var note in doc.MainNotes())
        {
            before += UstPluginFile.GetLength(note);
            int len = (int)Math.Round(UstPluginFile.GetLength(note) * factor) - gap;
            len = Math.Max(10, len);
            UstPluginFile.SetLength(note, len);
            after += len;
        }
        // Keep the sequence length stable with a trailing R rest, so the last
        // sung note stays shortened instead of swallowing the freed time. It is an
        // [#INSERT] because OpenUtau ignores numbered blocks past its own sequence.
        if (before > after)
        {
            int lastMain = -1;
            for (int i = 0; i < doc.Blocks.Count; i++)
                if (doc.Blocks[i].IsMainNote) lastMain = i;
            if (lastMain >= 0)
                doc.Blocks.Insert(lastMain + 1, UstBlock.InsertRest(before - after));
        }
    }

    private static void Transpose(UstPluginFile doc, JobOptions options)
    {
        int shift = options.GetInt("semitones", 0) + options.GetInt("octaves", 0) * 12;
        if (shift == 0) return;
        foreach (var note in doc.MainNotes())
            UstPluginFile.SetPitch(note, Num.Clamp(UstPluginFile.GetPitch(note) + shift, 0, 127));
    }

    private static void Normalize(UstPluginFile doc, JobOptions options)
    {
        int minLen = Math.Max(10, options.GetInt("minLength", 10));
        bool clampPitch = options.GetBool("clampPitch", true);
        foreach (var note in doc.MainNotes())
        {
            if (UstPluginFile.GetLength(note) < minLen) UstPluginFile.SetLength(note, minLen);
            if (clampPitch)
                UstPluginFile.SetPitch(note, Num.Clamp(UstPluginFile.GetPitch(note), 0, 127));
        }
    }

    private static void ReplaceLyrics(UstPluginFile doc, JobOptions options)
    {
        string single = options.GetString("text", string.Empty);
        var list = options.GetList("texts");
        bool includeRests = options.GetBool("includeRests", false);
        if (single.Length == 0 && list.Length == 0) return;

        int counter = 0;
        foreach (var note in doc.MainNotes())
        {
            string lyric = UstPluginFile.GetLyric(note);
            if (!includeRests && (lyric.Equals("R", StringComparison.OrdinalIgnoreCase) || lyric.StartsWith("+"))) continue;
            note.Set("Lyric", list.Length > 0 ? list[counter++ % list.Length] : single);
        }
    }

    private static void Swing(UstPluginFile doc, JobOptions options)
    {
        int division = options.GetInt("division", 8);
        bool shuffle = options.GetBool("shuffle", false);
        double amount = shuffle ? 200.0 / 3.0 : options.GetDouble("amount", 50);
        int grid = division == 16 ? Resolution / 4 : Resolution / 2;
        int delta = (int)Math.Round(grid * Num.Clamp(amount / 100.0, 0, 0.9));
        if (delta <= 0) return;

        var rebuilt = new List<UstBlock>();
        int cursor = 0;
        foreach (var block in doc.Blocks)
        {
            if (!block.IsMainNote)
            {
                rebuilt.Add(block);
                continue;
            }

            int pos = cursor;
            int dur = UstPluginFile.GetLength(block);
            bool offBeat = pos % grid == 0 && (pos / grid) % 2 == 1;
            if (offBeat && dur - delta >= 10)
            {
                // [#INSERT], not a numbered block: OpenUtau ignores numbered blocks
                // past its own sequence, which would silently drop the last note.
                rebuilt.Add(UstBlock.InsertRest(delta));
                UstPluginFile.SetLength(block, dur - delta);
            }
            rebuilt.Add(block);
            cursor += dur;
        }
        doc.Blocks = rebuilt;
    }
}
