using System.Text;

namespace VocalPatch.Core.Lyrics;

/// <summary>
/// Japanese kana to romaji. Handles hiragana, katakana, digraphs (きゃ, しゅ …),
/// sokuon (っ) doubling, and the long-vowel mark (ー). Kanji is left untouched
/// because it needs a reading dictionary, same as hanzi pinyin.
/// </summary>
public static class RomajiConverter
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        // vowels
        ["あ"] = "a", ["い"] = "i", ["う"] = "u", ["え"] = "e", ["お"] = "o",
        ["ア"] = "a", ["イ"] = "i", ["ウ"] = "u", ["エ"] = "e", ["オ"] = "o",
        // k
        ["か"] = "ka", ["き"] = "ki", ["く"] = "ku", ["け"] = "ke", ["こ"] = "ko",
        ["カ"] = "ka", ["キ"] = "ki", ["ク"] = "ku", ["ケ"] = "ke", ["コ"] = "ko",
        ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo",
        ["キャ"] = "kya", ["キュ"] = "kyu", ["キョ"] = "kyo",
        // s
        ["さ"] = "sa", ["し"] = "shi", ["す"] = "su", ["せ"] = "se", ["そ"] = "so",
        ["サ"] = "sa", ["シ"] = "shi", ["ス"] = "su", ["セ"] = "se", ["ソ"] = "so",
        ["しゃ"] = "sha", ["しゅ"] = "shu", ["しょ"] = "sho",
        ["シャ"] = "sha", ["シュ"] = "shu", ["ショ"] = "sho",
        // t
        ["た"] = "ta", ["ち"] = "chi", ["つ"] = "tsu", ["て"] = "te", ["と"] = "to",
        ["タ"] = "ta", ["チ"] = "chi", ["ツ"] = "tsu", ["テ"] = "te", ["ト"] = "to",
        ["ちゃ"] = "cha", ["ちゅ"] = "chu", ["ちょ"] = "cho",
        ["チャ"] = "cha", ["チュ"] = "chu", ["チョ"] = "cho",
        // n
        ["な"] = "na", ["に"] = "ni", ["ぬ"] = "nu", ["ね"] = "ne", ["の"] = "no",
        ["ナ"] = "na", ["ニ"] = "ni", ["ヌ"] = "nu", ["ネ"] = "ne", ["ノ"] = "no",
        ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo",
        ["ニャ"] = "nya", ["ニュ"] = "nyu", ["ニョ"] = "nyo",
        // h
        ["は"] = "ha", ["ひ"] = "hi", ["ふ"] = "fu", ["へ"] = "he", ["ほ"] = "ho",
        ["ハ"] = "ha", ["ヒ"] = "hi", ["フ"] = "fu", ["ヘ"] = "he", ["ホ"] = "ho",
        ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
        ["ヒャ"] = "hya", ["ヒュ"] = "hyu", ["ヒョ"] = "hyo",
        // m
        ["ま"] = "ma", ["み"] = "mi", ["む"] = "mu", ["め"] = "me", ["も"] = "mo",
        ["マ"] = "ma", ["ミ"] = "mi", ["ム"] = "mu", ["メ"] = "me", ["モ"] = "mo",
        ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo",
        ["ミャ"] = "mya", ["ミュ"] = "myu", ["ミョ"] = "myo",
        // y
        ["や"] = "ya", ["ゆ"] = "yu", ["よ"] = "yo",
        ["ヤ"] = "ya", ["ユ"] = "yu", ["ヨ"] = "yo",
        // r
        ["ら"] = "ra", ["り"] = "ri", ["る"] = "ru", ["れ"] = "re", ["ろ"] = "ro",
        ["ラ"] = "ra", ["リ"] = "ri", ["ル"] = "ru", ["レ"] = "re", ["ロ"] = "ro",
        ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
        ["リャ"] = "rya", ["リュ"] = "ryu", ["リョ"] = "ryo",
        // w
        ["わ"] = "wa", ["を"] = "wo", ["ん"] = "n",
        ["ワ"] = "wa", ["ヲ"] = "wo", ["ン"] = "n",
        // g
        ["が"] = "ga", ["ぎ"] = "gi", ["ぐ"] = "gu", ["げ"] = "ge", ["ご"] = "go",
        ["ガ"] = "ga", ["ギ"] = "gi", ["グ"] = "gu", ["ゲ"] = "ge", ["ゴ"] = "go",
        ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
        ["ギャ"] = "gya", ["ギュ"] = "gyu", ["ギョ"] = "gyo",
        // z
        ["ざ"] = "za", ["じ"] = "ji", ["ず"] = "zu", ["ぜ"] = "ze", ["ぞ"] = "zo",
        ["ザ"] = "za", ["ジ"] = "ji", ["ズ"] = "zu", ["ゼ"] = "ze", ["ゾ"] = "zo",
        ["じゃ"] = "ja", ["じゅ"] = "ju", ["じょ"] = "jo",
        ["ジャ"] = "ja", ["ジュ"] = "ju", ["ジョ"] = "jo",
        // d
        ["だ"] = "da", ["ぢ"] = "ji", ["づ"] = "zu", ["で"] = "de", ["ど"] = "do",
        ["ダ"] = "da", ["ヂ"] = "ji", ["ヅ"] = "zu", ["デ"] = "de", ["ド"] = "do",
        // b
        ["ば"] = "ba", ["び"] = "bi", ["ぶ"] = "bu", ["べ"] = "be", ["ぼ"] = "bo",
        ["バ"] = "ba", ["ビ"] = "bi", ["ブ"] = "bu", ["ベ"] = "be", ["ボ"] = "bo",
        ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo",
        ["ビャ"] = "bya", ["ビュ"] = "byu", ["ビョ"] = "byo",
        // p
        ["ぱ"] = "pa", ["ぴ"] = "pi", ["ぷ"] = "pu", ["ぺ"] = "pe", ["ぽ"] = "po",
        ["パ"] = "pa", ["ピ"] = "pi", ["プ"] = "pu", ["ペ"] = "pe", ["ポ"] = "po",
        ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
        ["ピャ"] = "pya", ["ピュ"] = "pyu", ["ピョ"] = "pyo",
        // small kana (standalone)
        ["ぁ"] = "a", ["ぃ"] = "i", ["ぅ"] = "u", ["ぇ"] = "e", ["ぉ"] = "o",
        ["ゃ"] = "ya", ["ゅ"] = "yu", ["ょ"] = "yo", ["ゎ"] = "wa",
        ["ァ"] = "a", ["ィ"] = "i", ["ゥ"] = "u", ["ェ"] = "e", ["ォ"] = "o",
        ["ャ"] = "ya", ["ュ"] = "yu", ["ョ"] = "yo", ["ヮ"] = "wa",
    };

    public static string Convert(string text, RomanizationOptions options)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // sokuon: double the next consonant.
            if (c is 'っ' or 'ッ')
            {
                if (i + 1 < text.Length)
                {
                    string nextSyllable = ConvertAt(text, i + 1, out _);
                    if (nextSyllable.Length > 0) sb.Append(nextSyllable[0]);
                }
                i++;
                continue;
            }

            // long vowel mark: repeat the previous vowel.
            if (c == 'ー')
            {
                if (sb.Length > 0)
                {
                    char last = sb[sb.Length - 1];
                    if ("aeiou".IndexOf(last) >= 0) sb.Append(last);
                }
                i++;
                continue;
            }

            string syllable = ConvertAt(text, i, out int advance);
            if (advance == 0) { sb.Append(c); i++; continue; }
            sb.Append(syllable);
            i += advance;
        }

        string result = sb.ToString();
        if (options.Capitalize && result.Length > 0)
            result = char.ToUpperInvariant(result[0]) + result.Substring(1);
        return result;
    }

    private static string ConvertAt(string text, int i, out int advance)
    {
        if (i + 1 < text.Length)
        {
            string pair = text.Substring(i, 2);
            if (Table.TryGetValue(pair, out var digraph)) { advance = 2; return digraph; }
        }
        if (Table.TryGetValue(text[i].ToString(), out var single)) { advance = 1; return single; }
        advance = 0;
        return string.Empty;
    }
}
