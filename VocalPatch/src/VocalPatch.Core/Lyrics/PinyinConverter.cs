using System.Globalization;
using System.Text;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Lyrics;

/// <summary>How a lyric string is romanised.</summary>
public enum RomanizationMode
{
    /// <summary>Chinese hanzi to pinyin (default).</summary>
    Pinyin,

    /// <summary>Japanese kana to romaji.</summary>
    Romaji,

    /// <summary>Auto-detect the script and pick the right romaniser.</summary>
    Auto,
}

/// <summary>Options controlling romanisation of lyrics.</summary>
public sealed class RomanizationOptions
{
    public RomanizationMode Mode { get; set; } = RomanizationMode.Auto;

    /// <summary>Keep tone digits ("ni3") instead of dropping them.</summary>
    public bool KeepToneNumbers { get; set; }

    /// <summary>Render tones as diacritics ("nǐ") instead of digits.</summary>
    public bool ToneMarks { get; set; }

    /// <summary>Uppercase the first letter of each syllable.</summary>
    public bool Capitalize { get; set; }

    /// <summary>Separator placed between syllables of a multi-character lyric.</summary>
    public string Separator { get; set; } = " ";
}

/// <summary>Converts Chinese / Japanese lyrics to a singable romanisation.</summary>
public static class PinyinConverter
{
    /// <summary>Romanise a single note's lyric according to the options.</summary>
    public static string Convert(string lyric, RomanizationOptions options)
    {
        if (string.IsNullOrEmpty(lyric)) return lyric;
        if (lyric.StartsWith("+") || lyric.StartsWith("[")) return lyric; // continuation / phonetic hint
        var mode = options.Mode == RomanizationMode.Auto ? LanguageDetector.GuessRomanizer(lyric) : options.Mode;

        return mode switch
        {
            RomanizationMode.Pinyin => ConvertPinyin(lyric, options),
            RomanizationMode.Romaji => RomajiConverter.Convert(lyric, options),
            _ => lyric,
        };
    }

    /// <summary>Romanise every note in the song, leaving rests and continuations alone.</summary>
    public static int ConvertSong(Song song, RomanizationOptions options, int trackIndex = -1)
    {
        int changed = 0;
        for (int ti = 0; ti < song.Tracks.Count; ti++)
        {
            if (trackIndex >= 0 && ti != trackIndex) continue;
            foreach (var note in song.Tracks[ti].Notes)
            {
                if (note.IsRest || note.Lyric.StartsWith("+") || note.Lyric.StartsWith("[")) continue;
                string converted = Convert(note.Lyric, options);
                if (converted != note.Lyric)
                {
                    note.Lyric = converted;
                    changed++;
                }
            }
        }
        return changed;
    }

    private static string ConvertPinyin(string lyric, RomanizationOptions options)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var raw in Syllabifier.Split(lyric))
        {
            string syllable = raw;
            if (raw.Length == 1 && LanguageDetector.IsHan(raw[0]))
            {
                syllable = ResolveReading(raw[0], options);
            }
            else if (raw.All(LanguageDetector.IsHan))
            {
                // A run of hanzi inside one "syllable" — romanise each character.
                var inner = string.Join(options.Separator, raw.Select(c => ResolveReading(c, options)));
                syllable = inner;
            }

            if (!first && sb.Length > 0 && !sb.ToString().EndsWith(options.Separator, StringComparison.Ordinal))
                sb.Append(options.Separator);
            sb.Append(syllable);
            first = false;
        }
        return sb.ToString();
    }

    private static string ResolveReading(char ch, RomanizationOptions options)
    {
        if (!PinyinDictionary.TryGet(ch, out var readings) || readings.Length == 0)
            return ch.ToString();

        string reading = readings[0];
        if (options.ToneMarks) return ToneMarks.Apply(reading);
        if (options.KeepToneNumbers) return reading;
        return ToneMarks.Strip(reading);
    }
}

/// <summary>Script detection used to choose a romaniser.</summary>
public static class LanguageDetector
{
    public static bool IsHan(char c) => c >= 0x4E00 && c <= 0x9FFF;
    public static bool IsHiragana(char c) => c >= 0x3040 && c <= 0x309F;
    public static bool IsKatakana(char c) => c >= 0x30A0 && c <= 0x30FF;
    public static bool IsKana(char c) => IsHiragana(c) || IsKatakana(c);
    public static bool IsHangul(char c) => c >= 0xAC00 && c <= 0xD7A3;
    public static bool IsLatin(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>Pick a romaniser for a lyric string.</summary>
    public static RomanizationMode GuessRomanizer(string text)
    {
        int han = 0, kana = 0, hangul = 0, latin = 0;
        foreach (char c in text)
        {
            if (IsHan(c)) han++;
            else if (IsKana(c)) kana++;
            else if (IsHangul(c)) hangul++;
            else if (IsLatin(c)) latin++;
        }
        if (kana > 0) return RomanizationMode.Romaji;
        if (han > 0) return RomanizationMode.Pinyin;
        if (hangul > 0) return RomanizationMode.Romaji; // no hangul romaniser yet; keep hook
        if (latin > 0) return RomanizationMode.Pinyin;   // already latin; no-op
        return RomanizationMode.Pinyin;
    }

    public static string Describe(string text)
    {
        int han = 0, kana = 0, hangul = 0, latin = 0;
        foreach (char c in text)
        {
            if (IsHan(c)) han++;
            else if (IsKana(c)) kana++;
            else if (IsHangul(c)) hangul++;
            else if (IsLatin(c)) latin++;
        }
        if (kana > 0) return "Japanese";
        if (han > 0) return "Chinese";
        if (hangul > 0) return "Korean";
        if (latin > 0) return "Latin";
        return "Other";
    }
}

/// <summary>Pinyin tone helpers: numbered ("ni3"), toneless ("ni"), and diacritic ("nǐ").</summary>
internal static class ToneMarks
{
    private static readonly Dictionary<char, char[]> Marks = new()
    {
        ['a'] = new[] { '\u0101', '\u00E1', '\u01CE', '\u00E0' }, // ā á ǎ à
        ['e'] = new[] { '\u0113', '\u00E9', '\u011B', '\u00E8' }, // ē é ě è
        ['i'] = new[] { '\u012B', '\u00ED', '\u01D0', '\u00EC' }, // ī í ǐ ì
        ['o'] = new[] { '\u014D', '\u00F3', '\u01D2', '\u00F2' }, // ō ó ǒ ò
        ['u'] = new[] { '\u016B', '\u00FA', '\u01D4', '\u00F9' }, // ū ú ǔ ù
        ['v'] = new[] { '\u01D6', '\u01D8', '\u01DA', '\u01DC' }, // ǖ ǘ ǚ ǜ (v is used for ü in tables)
    };

    public static string Strip(string pinyin)
    {
        int end = pinyin.Length;
        while (end > 0 && char.IsDigit(pinyin[end - 1])) end--;
        return pinyin.Substring(0, end).Replace("ü", "v");
    }

    public static string Apply(string pinyin)
    {
        int tone = 0;
        int end = pinyin.Length;
        while (end > 0 && char.IsDigit(pinyin[end - 1]))
        {
            tone = pinyin[end - 1] - '0';
            end--;
        }
        string basePart = pinyin.Substring(0, end);
        if (tone is < 1 or > 4) return basePart;

        int idx = PickVowel(basePart);
        if (idx < 0) return basePart;
        char vowel = char.ToLowerInvariant(basePart[idx]);
        if (!Marks.TryGetValue(vowel, out var marks)) return basePart;
        var sb = new StringBuilder(basePart);
        sb[idx] = marks[tone - 1];
        return sb.ToString();
    }

    private static int PickVowel(string s)
    {
        int a = s.IndexOf('a');
        if (a >= 0) return a;
        int o = s.IndexOf('o');
        int e = s.IndexOf('e');
        if (o >= 0 && (e < 0 || o < e)) return o;
        if (e >= 0) return e;
        int iu = s.LastIndexOfAny(new[] { 'i', 'u', 'v', 'ü' });
        return iu;
    }
}
