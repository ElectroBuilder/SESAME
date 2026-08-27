using System.Text.RegularExpressions;
using VisualSSH.Services.N64;

namespace VisualSSH.Services;

public sealed class PhraseHit
{
    public string Kind { get; init; } = "";
    public string English { get; init; } = "";
    public string Dutch { get; init; } = "";
    public int Count { get; init; }
}

public static class DutchIdioms
{
    public static readonly (string En, string Nl)[] All =
    [
        ("kick your butt", "ik maak je in"),
        ("kick yer butt", "ik maak je in"),
        ("kick your ass", "ik maak je in"),
        ("kick your behind", "ik maak je in"),
        ("whoop your ass", "ik maak je in"),
        ("whoop your butt", "ik maak je in"),
        ("eat my dust", "je ziet alleen nog maar stof"),
        ("piece of cake", "een fluitje van een cent"),
        ("it's a piece of cake", "het is een fluitje van een cent"),
        ("raining cats and dogs", "het regent pijpenstelen"),
        ("rain cats and dogs", "pijpenstelen regenen"),
        ("don't judge a book by its cover", "schijn bedriegt"),
        ("you can't judge a book by its cover", "schijn bedriegt"),
        ("judge a book by its cover", "afgaan op het uiterlijk"),
        ("a penny for your thoughts", "waar denk je aan"),
        ("bite the bullet", "door de zure appel heen bijten"),
        ("break a leg", "succes"),
        ("beat around the bush", "eromheen draaien"),
        ("call it a day", "er een punt achter zetten"),
        ("cut somebody some slack", "niet zo moeilijk doen"),
        ("cut someone some slack", "niet zo moeilijk doen"),
        ("cutting corners", "de kantjes eraf lopen"),
        ("easy does it", "rustig aan"),
        ("get out of hand", "uit de hand lopen"),
        ("get your act together", "raap jezelf bij elkaar"),
        ("give someone the benefit of the doubt", "iemand het voordeel van de twijfel geven"),
        ("go back to the drawing board", "terug naar af"),
        ("hang in there", "blijf volhouden"),
        ("hit the sack", "gaan slapen"),
        ("it's not rocket science", "zo ingewikkeld is het niet"),
        ("let someone off the hook", "iemand laten lopen"),
        ("make a long story short", "om een lang verhaal kort te maken"),
        ("miss the boat", "de boot missen"),
        ("no pain, no gain", "zonder wrijving geen glans"),
        ("on the ball", "scherp"),
        ("pull someone's leg", "iemand voor de gek houden"),
        ("pull your leg", "voor de gek houden"),
        ("pull yourself together", "pak jezelf samen"),
        ("so far so good", "tot nu toe gaat het goed"),
        ("speak of the devil", "als je van de duivel spreekt"),
        ("that's the last straw", "nu is de maat vol"),
        ("the last straw", "de druppel"),
        ("the best of both worlds", "het beste van twee werelden"),
        ("time flies when you're having fun", "de tijd vliegt als je het naar je zin hebt"),
        ("bent out of shape", "van streek"),
        ("to make matters worse", "om het nog erger te maken"),
        ("under the weather", "niet in orde"),
        ("we'll cross that bridge when we come to it", "dat zien we dan wel weer"),
        ("cross that bridge when we come to it", "dat zien we dan wel weer"),
        ("you can say that again", "dat kun je wel zeggen"),
        ("your guess is as good as mine", "ik weet het evenmin"),
        ("fair and square", "eerlijk verdiend"),
        ("help yourself", "ga je gang"),
        ("bon voyage", "goede reis"),
        ("better luck next time", "volgende keer beter"),
        ("would you like to save", "wil je opslaan"),
        ("do you want to save", "wil je opslaan"),
        ("catch me if you can", "vang me maar als je kan"),
        ("don't come back", "kom niet terug"),
        ("walk quietly", "loop stil"),
        ("no way", "nee hoor"),
        ("almost there", "bijna klaar"),
        ("keep going", "ga door"),
        ("bad luck", "pech"),
        ("a blessing in disguise", "een geluk bij een ongeluk"),
        ("a dime a dozen", "ze liggen voor het oprapen"),
        ("better late than never", "beter laat dan nooit"),
        ("a bird in the hand is worth two in the bush", "beter een vogel in de hand dan tien in de lucht"),
        ("a penny saved is a penny earned", "een cent gewonnen is een cent verdiend"),
        ("a picture is worth a thousand words", "een beeld zegt meer dan duizend woorden"),
        ("a picture is worth 1000 words", "een beeld zegt meer dan duizend woorden"),
        ("actions speak louder than words", "daden zeggen meer dan woorden"),
        ("add insult to injury", "zout in de wond strooien"),
        ("barking up the wrong tree", "aan het verkeerde adres"),
        ("birds of a feather flock together", "soort zoekt soort"),
        ("bite off more than you can chew", "een te grote broek aantrekken"),
        ("break the ice", "het ijs breken"),
        ("by the skin of your teeth", "op het nippertje"),
        ("comparing apples to oranges", "appels met peren vergelijken"),
        ("costs an arm and a leg", "kost een vermogen"),
        ("cost an arm and a leg", "een vermogen kosten"),
        ("don't count your chickens before they hatch", "verkoop de huid niet voor de beer geschoten is"),
        ("don't cry over spilt milk", "over geronnen melk moet je niet treuren"),
        ("don't put all your eggs in one basket", "zet niet alles op een kaart"),
        ("every cloud has a silver lining", "achter de wolken schijnt de zon"),
        ("give someone the cold shoulder", "iemand de kous op de kop geven"),
        ("hit the nail on the head", "de spijker op de kop slaan"),
        ("ignorance is bliss", "wat niet weet, wat niet deert"),
        ("it ain't over till the fat lady sings", "het is nog niet voorbij"),
        ("it's raining cats and dogs", "het regent pijpenstelen"),
        ("kill two birds with one stone", "twee vliegen in een klap slaan"),
        ("let the cat out of the bag", "de aap uit de mouw laten"),
        ("look before you leap", "bezint eer ge begint"),
        ("once in a blue moon", "eens in de zoveel tijd"),
        ("spill the beans", "uit de school klappen"),
        ("spill the beans,", "uit de school klappen,"),
        ("take it with a grain of salt", "met een korreltje zout nemen"),
        ("the ball is in your court", "de bal ligt bij jou"),
        ("the early bird gets the worm", "de morgenstond heeft goud in de mond"),
        ("the elephant in the room", "de olifant in de kamer"),
        ("there are other fish in the sea", "er zijn nog meer vissen in de zee"),
        ("you can't have your cake and eat it too", "je kunt niet alles hebben"),
        ("out of the frying pan and into the fire", "van de wal in de sloot"),
        ("the pot calling the kettle black", "de pot verwijt de ketel"),
        ("when it rains it pours", "een ongeluk komt nooit alleen"),
        ("well begun is half done", "een goed begin is het halve werk"),
        ("time is money", "tijd is geld"),
        ("haste makes waste", "haastige spoed is zelden goed"),
        ("let sleeping dogs lie", "geen slapende honden wakker maken"),
        ("on cloud nine", "in de zevende hemel"),
        ("fit as a fiddle", "zo gezond als een vis"),
        ("a storm in a teacup", "een storm in een glas water"),
        ("through thick and thin", "door dik en dun"),
        ("see eye to eye", "het eens zijn"),
        ("watch out", "kijk uit"),
        ("look out", "kijk uit"),
        ("well done", "goed gedaan"),
        ("come back later", "kom later terug"),
        ("over here", "hierheen"),
        ("this way", "deze kant op"),
        ("follow me", "volg me"),
        ("thank you very much", "hartelijk dank"),
        ("thank you", "dank je"),
        ("i don't know", "ik weet het niet")
    ];

    private static readonly (string Bad, string Good)[] LiteralFixes =
    [
        ("schop je kont", "ik maak je in"),
        ("schop jouw kont", "ik maak je in"),
        ("schop je achterste", "ik maak je in"),
        ("schop je billen", "ik maak je in"),
        ("kick your butt", "ik maak je in"),
        ("het regent katten en honden", "het regent pijpenstelen"),
        ("regenen katten en honden", "pijpenstelen regenen"),
        ("help jezelf", "ga je gang"),
        ("eerlijk en vierkant", "eerlijk verdiend"),
        ("goede reis", "goede reis"),
        ("geen manier", "nee hoor"),
        ("slechte luck", "pech"),
        ("slechte geluk", "pech"),
        ("bijt de kogel", "bijt door de zure appel heen"),
        ("een cent voor je gedachten", "waar denk je aan"),
        ("een penny voor je gedachten", "waar denk je aan"),
        ("mors de bonen", "uit de school klappen"),
        ("mors de boon", "uit de school klappen"),
        ("lek de bonen", "uit de school klappen")
    ];

    public static string Protect(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var work = text;
        for (var i = 0; i < All.Length; i++)
            work = ReplacePhrase(work, All[i].En, Token(i));
        return work;
    }

    public static string Restore(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var work = text;
        work = Regex.Replace(work, @"XID(\d+)X", m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out var i) || i < 0 || i >= All.Length)
                return m.Value;
            return All[i].Nl;
        }, RegexOptions.IgnoreCase);
        foreach (var (bad, good) in LiteralFixes)
            work = ReplacePhrase(work, bad, good);
        return work;
    }

    public static IReadOnlyList<PhraseHit> Scan(IEnumerable<BkTextLine> lines)
    {
        var originals = lines.Select(l => l.Original).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var hits = new List<PhraseHit>();
        for (var i = 0; i < All.Length; i++)
        {
            var n = originals.Count(t => ContainsPhrase(t, All[i].En));
            if (n > 0)
                hits.Add(new PhraseHit
                {
                    Kind = "Gezegde",
                    English = All[i].En.ToUpperInvariant(),
                    Dutch = All[i].Nl.ToUpperInvariant(),
                    Count = n
                });
        }

        foreach (var g in originals
                     .GroupBy(t => NormalizeKey(t), StringComparer.Ordinal)
                     .Where(g => g.Count() >= 2 && g.Key.Count(char.IsLetter) >= 8)
                     .OrderByDescending(g => g.Count())
                     .Take(25))
        {
            if (hits.Any(h => h.English == g.Key)) continue;
            hits.Add(new PhraseHit
            {
                Kind = "Vaste zin",
                English = g.Key,
                Dutch = "(wordt uit context vertaald)",
                Count = g.Count()
            });
        }

        var grams = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in originals)
        {
            var words = Regex.Matches(NormalizeKey(text), @"[A-Z']+")
                .Select(m => m.Value)
                .Where(w => w.Length > 1)
                .ToArray();
            for (var n = 3; n <= 5; n++)
            {
                for (var i = 0; i + n <= words.Length; i++)
                {
                    var gram = string.Join(' ', words.Skip(i).Take(n));
                    if (gram.Length < 12) continue;
                    grams[gram] = grams.GetValueOrDefault(gram) + 1;
                }
            }
        }

        foreach (var kv in grams.Where(k => k.Value >= 3).OrderByDescending(k => k.Value).Take(20))
        {
            if (hits.Any(h => h.English.Contains(kv.Key, StringComparison.Ordinal))) continue;
            hits.Add(new PhraseHit
            {
                Kind = "Woordgroep",
                English = kv.Key,
                Dutch = "(niet letterlijk)",
                Count = kv.Value
            });
        }

        return hits
            .OrderBy(h => h.Kind == "Gezegde" ? 0 : h.Kind == "Vaste zin" ? 1 : 2)
            .ThenByDescending(h => h.Count)
            .ToList();
    }

    private static string Token(int i) => $"XID{i:00}X";

    private static bool ContainsPhrase(string text, string phrase) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(phrase)}\b", RegexOptions.IgnoreCase);

    private static string ReplacePhrase(string text, string from, string to) =>
        Regex.Replace(text, $@"\b{Regex.Escape(from)}\b", to, RegexOptions.IgnoreCase);

    private static string NormalizeKey(string text) =>
        BkTextCodec.ToGameText(text).Replace('\n', ' ');
}
