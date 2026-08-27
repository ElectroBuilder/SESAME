using System.Text.RegularExpressions;

namespace VisualSSH.Services;

public static class EnglishText
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","you","your","are","for","this","that","with","have","from","not",
        "but","was","can","will","all","one","out","get","got","has","his","her","she",
        "him","they","them","what","when","where","who","how","why","now","then","here",
        "there","into","over","under","after","before","about","just","like","make",
        "need","find","take","come","back","down","up","off","on","in","to","of","or",
        "if","be","is","it","as","at","by","do","no","so","we","my","me","an","a",
        "please","thank","thanks","welcome","press","start","select","pause","options",
        "sound","music","stereo","mono","controller","memory","card","save","load",
        "game","over","player","level","world","stage","score","lives","time","bonus",
        "nintendo","expansion","pak","accessory","insert","remove","turn","power",
        "mario","luigi","peach","bowser","yoshi","toad","star","castle","princess",
        "donkey","kong","diddy","lanky","tiny","chunky","banana","golden","kremling",
        "cranky","funky","candy","snide","wrinkly","kroc","kloak","kasplat","troff","scoff",
        "japes","aztec","factory","galleon","fungi","caves","castle","isles","hideout",
        "welcome","bonus","stage","melon","coconut","peanut","feather","grape","pineapple",
        "banjo","kazooie","jiggy","jinjo","grunty","mumbo","bottles","tooty",
        "collect","jump","run","fly","swim","shoot","open","close","help","wait",
        "look","use","try","again","ready","go","yes","okay","well","hey","wow",
        "dont","can't","wont","lets","it's","i'm","you're","that's","there's"
    };

    private static readonly Regex JunkSymbol = new(@"[~^`|#{}\\\[\]<>*$=]", RegexOptions.Compiled);
    private static readonly Regex MixedCaseWord = new(@"[a-z][A-Z]|[A-Z]{2,}[a-z][A-Z]", RegexOptions.Compiled);
    private static readonly Regex DigitInWord = new(@"[A-Za-z]\d+[A-Za-z]", RegexOptions.Compiled);

    public static bool LooksLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 6) return false;
        if (JunkSymbol.IsMatch(text)) return false;
        if (DigitInWord.IsMatch(text)) return false;

        var letters = 0;
        var vowels = 0;
        var spaces = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (ch == ' ' || ch == '\n') { spaces++; continue; }
            if (char.IsLetter(ch))
            {
                letters++;
                if ("aeiouAEIOU".Contains(ch)) vowels++;
            }
            else if (ch is '.' or ',' or '!' or '?' or '\'' or '-' or ':' or ';' or '(' or ')' or '&' or '%')
                continue;
            else if (char.IsDigit(ch)) other++;
            else other++;
        }

        if (letters < 6) return false;
        if (letters < text.Replace(" ", "").Length * 0.72) return false;
        if (vowels < 2) return false;
        if (other > letters / 5) return false;
        if (text.Length >= 14 && spaces == 0) return false;
        if (text.Distinct().Count() < 5) return false;

        var tokens = text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        if (tokens.Any(t => MixedCaseWord.IsMatch(t) && t.Length > 3)) return false;
        if (LooksLikeAlphabet(text)) return false;

        var hits = 0;
        var englishish = 0;
        foreach (var token in tokens)
        {
            var clean = token.Trim('.', ',', '!', '?', '\'', '-', ':', ';', '(', ')');
            if (clean.Length < 2) continue;
            if (Words.Contains(clean)) hits++;
            if (HasVowel(clean) && clean.Count(char.IsLetter) == clean.Length)
                englishish++;
        }

        if (tokens.Length >= 4) return hits >= 2 && englishish >= 2;
        if (hits >= 2) return true;
        if (hits >= 1 && englishish >= 1 && tokens.Length <= 3) return true;
        return false;
    }

    private static bool HasVowel(string word) =>
        word.Any(c => "aeiouAEIOU".Contains(c));

    private static bool LooksLikeAlphabet(string text)
    {
        var letters = new string(text.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
        return letters.Length >= 16 && "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Contains(letters, StringComparison.Ordinal);
    }
}
