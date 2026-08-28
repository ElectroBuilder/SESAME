using System.Text.RegularExpressions;

namespace Sesame.Services;

/// <summary>
/// Lightweight in-app English→Dutch for Banjo-style dialogue.
/// Not a neural model: phrases first, then words. Fast and offline.
/// </summary>
public static class DutchLocalLexicon
{
    private static readonly (string En, string Nl)[] Phrases =
    [
        ("press a to", "druk op a om te"),
        ("press b to", "druk op b om te"),
        ("press a", "druk op a"),
        ("press b", "druk op b"),
        ("while underwater", "onder water"),
        ("winged wonder", "gevleugelde wonder"),
        ("bottle boy", "flessenjongen"),
        ("specky", "bril"),
        ("c'mon", "kom op"),
        ("come on", "kom op"),
        ("spill the beans", "uit de school klappen"),
        ("kick your butt", "ik maak je in"),
        ("learn the", "leer de"),
        ("it's time for you to", "het is tijd om"),
        ("it's time to", "het is tijd om"),
        ("i want to", "ik wil"),
        ("i don't know", "ik weet het niet"),
        ("thank you very much", "hartelijk dank"),
        ("thank you", "dank je"),
        ("well done", "goed gedaan"),
        ("watch out", "kijk uit"),
        ("look out", "kijk uit"),
        ("over here", "hierheen"),
        ("this way", "deze kant op"),
        ("follow me", "volg me"),
        ("come back later", "kom later terug"),
        ("game over", "game over"),
        ("get going", "ga verder"),
        ("stand on", "ga op"),
        ("jump on", "spring op"),
        ("swim up", "zwem omhoog"),
        ("fly up", "vlieg omhoog"),
        ("use her wings", "haar vleugels gebruiken"),
        ("kick his legs", "met zijn benen trappen"),
        ("underwater", "onder water"),
        ("of course", "natuurlijk"),
        ("all right", "prima"),
        ("alright", "prima"),
        ("see you", "tot ziens"),
        ("good luck", "veel succes"),
        ("try it", "probeer het"),
        ("just try it", "probeer het gewoon")
    ];

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["the"] = "de", ["a"] = "een", ["an"] = "een", ["and"] = "en", ["or"] = "of",
        ["but"] = "maar", ["if"] = "als", ["then"] = "dan", ["now"] = "nu", ["so"] = "dus",
        ["you"] = "je", ["your"] = "je", ["yours"] = "van jou", ["i"] = "ik", ["i'm"] = "ik ben",
        ["i'll"] = "ik zal", ["i've"] = "ik heb", ["i'd"] = "ik zou", ["we"] = "we",
        ["we'll"] = "we zullen", ["me"] = "me", ["my"] = "mijn", ["our"] = "onze", ["us"] = "ons",
        ["this"] = "dit", ["that"] = "dat", ["these"] = "deze", ["those"] = "die",
        ["here"] = "hier", ["there"] = "daar", ["where"] = "waar", ["when"] = "wanneer",
        ["what"] = "wat", ["who"] = "wie", ["why"] = "waarom", ["how"] = "hoe",
        ["is"] = "is", ["are"] = "zijn", ["was"] = "was", ["were"] = "waren", ["be"] = "zijn",
        ["been"] = "geweest", ["being"] = "zijn", ["can"] = "kan", ["can't"] = "kan niet",
        ["cannot"] = "kan niet", ["will"] = "zal", ["won't"] = "zal niet", ["would"] = "zou",
        ["could"] = "kon", ["should"] = "moet", ["must"] = "moet", ["need"] = "moet",
        ["needs"] = "moet", ["don't"] = "niet", ["doesn't"] = "niet", ["didn't"] = "niet",
        ["not"] = "niet", ["no"] = "nee", ["yes"] = "ja", ["ok"] = "ok", ["okay"] = "ok",
        ["please"] = "alsjeblieft", ["thanks"] = "dank je", ["hello"] = "hallo", ["hey"] = "hee",
        ["hi"] = "hoi", ["bye"] = "doei", ["stop"] = "stop", ["go"] = "ga", ["come"] = "kom",
        ["get"] = "pak", ["take"] = "neem", ["give"] = "geef", ["let"] = "laat", ["lets"] = "laten we",
        ["let's"] = "laten we", ["make"] = "maak", ["made"] = "gemaakt", ["do"] = "doe",
        ["does"] = "doet", ["did"] = "deed", ["done"] = "klaar", ["try"] = "probeer",
        ["use"] = "gebruik", ["used"] = "gebruikt", ["press"] = "druk op", ["stand"] = "staan",
        ["learn"] = "leer", ["learned"] = "geleerd", ["find"] = "vind", ["found"] = "gevonden",
        ["help"] = "help", ["look"] = "kijk", ["see"] = "zie", ["saw"] = "zag", ["seen"] = "gezien",
        ["hear"] = "hoor", ["listen"] = "luister", ["talk"] = "praat", ["say"] = "zeg",
        ["said"] = "zei", ["tell"] = "vertel", ["told"] = "vertelde", ["ask"] = "vraag",
        ["know"] = "weet", ["think"] = "denk", ["want"] = "wil", ["wanted"] = "wilde",
        ["like"] = "graag", ["love"] = "houd van", ["hate"] = "haat", ["wait"] = "wacht",
        ["run"] = "ren", ["jump"] = "spring", ["swim"] = "zwem", ["fly"] = "vlieg",
        ["walk"] = "loop", ["climb"] = "klim", ["open"] = "open", ["close"] = "dicht",
        ["closed"] = "dicht", ["hit"] = "sla", ["attack"] = "aanval", ["move"] = "zet",
        ["moves"] = "zetten", ["collect"] = "verzamel", ["collecting"] = "verzamelen van",
        ["for"] = "voor", ["with"] = "met", ["from"] = "van", ["to"] = "naar", ["on"] = "op",
        ["in"] = "in", ["of"] = "van", ["at"] = "bij", ["by"] = "door", ["into"] = "in",
        ["out"] = "uit", ["up"] = "omhoog", ["down"] = "omlaag", ["over"] = "over",
        ["under"] = "onder", ["off"] = "uit", ["about"] = "over", ["as"] = "als",
        ["than"] = "dan", ["too"] = "ook", ["also"] = "ook", ["very"] = "erg", ["really"] = "echt",
        ["just"] = "gewoon", ["still"] = "nog", ["already"] = "al", ["again"] = "weer",
        ["back"] = "terug", ["away"] = "weg", ["more"] = "meer", ["most"] = "meeste",
        ["all"] = "alle", ["some"] = "wat", ["any"] = "een", ["every"] = "elke",
        ["each"] = "elk", ["both"] = "beide", ["few"] = "paar", ["many"] = "veel",
        ["much"] = "veel", ["little"] = "klein", ["big"] = "grote", ["small"] = "kleine",
        ["new"] = "nieuwe", ["old"] = "oude", ["good"] = "goed", ["bad"] = "slecht",
        ["nice"] = "mooi", ["great"] = "geweldig", ["first"] = "eerste", ["last"] = "laatste",
        ["next"] = "volgende", ["high"] = "hoog", ["higher"] = "hoger", ["low"] = "laag",
        ["long"] = "lange", ["short"] = "korte", ["fast"] = "snel", ["slow"] = "traag",
        ["hot"] = "heet", ["cold"] = "koud", ["right"] = "goed", ["left"] = "links",
        ["wrong"] = "fout", ["true"] = "waar", ["lost"] = "kwijt", ["safe"] = "veilig",
        ["bear"] = "beer", ["bird"] = "vogel", ["witch"] = "heks", ["mole"] = "mol",
        ["treasure"] = "schat", ["gold"] = "goud", ["feather"] = "veer", ["feathers"] = "veren",
        ["egg"] = "ei", ["eggs"] = "eieren", ["note"] = "noot", ["notes"] = "noten",
        ["honeycomb"] = "honingraat", ["honeycombs"] = "honingraten",
        ["water"] = "water", ["air"] = "lucht", ["ground"] = "grond", ["cave"] = "grot",
        ["mountain"] = "berg", ["beach"] = "strand", ["ship"] = "schip", ["door"] = "deur",
        ["bridge"] = "brug", ["world"] = "wereld", ["time"] = "tijd", ["way"] = "weg",
        ["thing"] = "ding", ["things"] = "dingen", ["one"] = "een", ["two"] = "twee",
        ["three"] = "drie", ["four"] = "vier", ["five"] = "vijf", ["six"] = "zes",
        ["seven"] = "zeven", ["eight"] = "acht", ["nine"] = "negen", ["ten"] = "tien",
        ["his"] = "zijn", ["her"] = "haar", ["their"] = "hun", ["them"] = "ze",
        ["he"] = "hij", ["she"] = "zij", ["it"] = "het", ["its"] = "zijn",
        ["they"] = "ze", ["him"] = "hem", ["himself"] = "zichzelf", ["herself"] = "zichzelf",
        ["legs"] = "benen", ["wings"] = "vleugels", ["while"] = "terwijl"
    };

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var work = text;
        foreach (var (en, nl) in Phrases.OrderByDescending(p => p.En.Length))
            work = Regex.Replace(work, $@"\b{Regex.Escape(en)}\b", nl, RegexOptions.IgnoreCase);

        work = Regex.Replace(work, @"[A-Za-z']+", m =>
            Words.TryGetValue(m.Value, out var nl) ? nl : m.Value);
        return work;
    }

    public static string ApplyPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var work = text;
        foreach (var (en, nl) in Phrases.OrderByDescending(p => p.En.Length))
            work = Regex.Replace(work, $@"\b{Regex.Escape(en)}\b", nl, RegexOptions.IgnoreCase);
        return work;
    }
}
