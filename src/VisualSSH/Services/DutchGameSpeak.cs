using System.Text.RegularExpressions;

namespace VisualSSH.Services;

/// <summary>
/// Spoken N64 lines: keep the joke and the story, never word-for-word.
/// Exact lines first, then phrases. Names stay English.
/// </summary>
public static class DutchGameSpeak
{
    private static readonly Dictionary<string, string> Exact =
        BuildExact();

    private static readonly (string En, string Nl)[] Phrases =
    [
        ("welcome to the bonus stage", "welkom bij de bonusronde"),
        ("hit as many kremlings as you can", "tref zoveel mogelijk kremlings"),
        ("keep the turtles spinning by feeding the snakes melons",
            "houd de schildpadden draaiend door de slangen meloenen te voeren"),
        ("line up four bananas to win the jackpot", "zet vier bananen op een rij voor de jackpot"),
        ("to spin and stop the reels", "om de rollen te laten draaien en te stoppen"),
        ("almost there - keep going", "bijna, ga door"),
        ("almost there", "bijna"),
        ("keep going", "ga door"),
        ("oh, bad luck! you almost made it", "pech! je had het bijna"),
        ("you almost made it", "je had het bijna"),
        ("bad luck", "pech"),
        ("shoot the golden banana", "schiet op de golden banana"),
        ("just don't hit any kongs", "raak alleen geen kongs"),
        ("to fire the melons, and hit the melon to reload",
            "om de meloenen af te vuren, en schiet op de meloen om te herladen"),
        ("to fire a melon, and shoot the melon to reload",
            "om een meloen af te vuren, en schiet op de meloen om te herladen"),
        ("to fire a melon", "om een meloen af te vuren"),
        ("swat the flies", "meper de vliegen"),
        ("to use the swatter", "om de vliegenmepper te gebruiken"),
        ("shoot the klaptraps to clear the fairy's path",
            "schiet de klaptraps weg zodat de fairy erdoor kan"),
        ("find and shoot all the klaptraps", "vind en schiet alle klaptraps"),
        ("herd the beavers into the pit", "drijf de bevers de kuil in"),
        ("to scare them", "om ze te laten schrikken"),
        ("to jump, and", "om te springen, en"),
        ("destroy all the baddies, then head for the checkered flag",
            "versla alle schurken en ga naar de finishvlag"),
        ("sneak around the maze to the checkered flag",
            "sluip door het doolhof naar de finishvlag"),
        ("survive the onslaught", "overleef de aanval"),
        ("to shoot", "om te schieten"),
        ("collect all the coins, then head for the checkered flag",
            "pak alle munten en ga naar de finishvlag"),
        ("collect all the coins, but watch out for the starfish",
            "pak alle munten, maar pas op voor de zeesterren"),
        ("bounce up into the trees and collect all the coins",
            "stuit naar de bomen en pak alle munten"),
        ("avoid the tnt carts", "ontwijk de tnt-karretjes"),
        ("to speed up, press", "sneller: druk op"),
        ("to slow down, press", "langzamer: druk op"),
        ("to change lanes, use", "van baan wisselen: gebruik"),
        ("at any junction", "bij elke splitsing"),
        ("catch me if you can", "vang me maar als je kan"),
        ("come on", "kom op"),
        ("c'mon", "kom op"),
        ("fair and square", "eerlijk verdiend"),
        ("help yourself", "ga je gang"),
        ("bon voyage", "goede reis"),
        ("better luck next time", "volgende keer beter"),
        ("would you like to save", "wil je opslaan"),
        ("do you want to save", "wil je opslaan"),
        ("you bet", "ja hoor"),
        ("not now", "nu niet"),
        ("walk quietly", "loop stil"),
        ("no one's home", "er is niemand thuis"),
        ("don't come back", "kom niet terug"),
        ("this key doesn't fit", "deze sleutel past niet"),
        ("you need a key to open this door", "je hebt een sleutel nodig voor deze deur"),
        ("i win! you lose", "ik win, jij verliest"),
        ("don't try to scam me", "probeer me niet te flessen"),
        ("the whole course", "de hele baan"),
        ("look me up", "zoek me later op"),
        ("for real", "voor echt"),
        ("peace-loving", "vredelievend"),
        ("blast off", "wegschieten"),
        ("human blur", "een flits"),
        ("wing cap", "wing cap"),
        ("metal cap", "metal cap"),
        ("vanish cap", "vanish cap"),
        ("power star", "power star"),
        ("red coin", "rode munt"),
        ("chain chomp", "chain chomp"),
        ("checkered flag", "finishvlag"),
        ("golden banana", "golden banana"),
        ("press a to", "druk op a om te"),
        ("press b to", "druk op b om te"),
        ("press z to", "druk op z om te"),
        ("yours truly", "hartelijke groeten")
    ];

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var key = Norm(text);
        if (Exact.TryGetValue(key, out var hit))
            return CopyShape(text, hit);

        var work = text;
        foreach (var (en, nl) in Phrases.OrderByDescending(p => p.En.Length))
            work = Regex.Replace(work, $@"\b{Regex.Escape(en)}\b", MatchCase(nl), RegexOptions.IgnoreCase);
        return work;
    }

    public static bool TryExact(string text, out string dutch)
    {
        dutch = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Exact.TryGetValue(Norm(text), out var hit)) return false;
        dutch = CopyShape(text, hit);
        return true;
    }

    private static Dictionary<string, string> BuildExact()
    {
        var pairs = new (string En, string Nl)[]
        {
            ("WELCOME TO THE BONUS STAGE!", "WELKOM BIJ DE BONUSRONDE!"),
            ("HIT AS MANY KREMLINGS AS YOU CAN!", "TREF ZOVEEL MOGELIJK KREMLINGS!"),
            ("TO FIRE A MELON.", "OM EEN MELOEN AF TE VUREN."),
            ("KEEP THE TURTLES SPINNING BY FEEDING THE SNAKES MELONS.",
                "HOUD DE SCHILDPADDEN DRAAIEND DOOR DE SLANGEN MELOENEN TE VOEREN."),
            ("LINE UP FOUR BANANAS TO WIN THE JACKPOT!", "ZET VIER BANANEN OP EEN RIJ VOOR DE JACKPOT!"),
            ("TO SPIN AND STOP THE REELS.", "OM DE ROLLEN TE LATEN DRAAIEN EN TE STOPPEN."),
            ("ALMOST THERE - KEEP GOING!", "BIJNA, GA DOOR!"),
            ("OH, BAD LUCK! YOU ALMOST MADE IT...", "PECH! JE HAD HET BIJNA..."),
            ("SHOOT THE GOLDEN BANANA.", "SCHIET OP DE GOLDEN BANANA."),
            ("JUST DON'T HIT ANY KONGS!", "RAAK ALLEEN GEEN KONGS!"),
            ("SWAT THE FLIES!", "MEPER DE VLIEGEN!"),
            ("TO USE THE SWATTER.", "OM DE VLIEGENMEPPER TE GEBRUIKEN."),
            ("FIND AND SHOOT ALL THE KLAPTRAPS!", "VIND EN SCHIET ALLE KLAPTRAPS!"),
            ("HERD THE BEAVERS INTO THE PIT!", "DRIJF DE BEVERS DE KUIL IN!"),
            ("TO SCARE THEM!", "OM ZE TE LATEN SCHRIKKEN!"),
            ("TO SHOOT.", "OM TE SCHIETEN."),
            ("AVOID THE TNT CARTS!", "ONTWIJK DE TNT-KARRETJES!"),
            ("GOOD LUCK!", "VEEL SUCCES!"),
            ("TO SPEED UP, PRESS", "SNELLER: DRUK OP"),
            ("TO SLOW DOWN, PRESS", "LANGZAMER: DRUK OP"),
            ("TO CHANGE LANES, USE", "VAN BAAN WISSELEN: GEBRUIK"),
            ("AT ANY JUNCTION.", "BIJ ELKE SPLITSING."),
            ("TO JUMP, AND", "OM TE SPRINGEN, EN"),
            ("DESTROY ALL THE BADDIES, THEN HEAD FOR THE CHECKERED FLAG!",
                "VERSLA ALLE SCHURKEN EN GA NAAR DE FINISHVLAG!"),
            ("SNEAK AROUND THE MAZE TO THE CHECKERED FLAG!",
                "SLUIP DOOR HET DOOLHOF NAAR DE FINISHVLAG!"),
            ("COLLECT ALL THE COINS, THEN HEAD FOR THE CHECKERED FLAG!",
                "PAK ALLE MUNTEN EN GA NAAR DE FINISHVLAG!"),
            ("W-WHAT! HOW DID HE DO THAT?!", "W-WAT! HOE DEED HIJ DAT?!"),
            ("KREMLINGS:THERE'S ONE OF THEM KONGS! GET HIM!",
                "KREMLINGS:DAAR IS EEN VAN DIE KONGS! PAK HEM!"),
            ("DIDDY:WHEEEE! CATCH ME IF YOU CAN!", "DIDDY:WIEOE! VANG ME MAAR ALS JE KAN!"),
            ("I'M SURROUNDED BY FOOLS...", "IK ZIT TUSSEN DE SUKKELS..."),
            ("NOTHING CAN STOP ME NOW. THEIR ISLAND IS DOOMED!",
                "NIETS KAN ME NU NOG STOPPEN. HUN EILAND IS VERLOREN!"),
            ("You need a key to open this door.", "Je hebt een sleutel nodig voor deze deur."),
            ("This key doesn't fit! Maybe it's for the basement...",
                "Deze sleutel past niet! Misschien is hij voor de kelder..."),
            ("I'm sleeping because... ...I'm sleepy. I don't like being disturbed. Please walk quietly.",
                "Ik slaap omdat... ik slaperig ben. Stoor me niet. Loop stil."),
            ("Shhh! Please walk quietly in the hallway!", "Sst! Loop stil in de gang!"),
            ("Welcome. No one's home! Now scram-- and don't come back! Gwa ha ha!",
                "Welkom. Er is niemand thuis! Donder op, en kom niet terug! Gwa ha ha!"),
            ("Dear Mario: Please come to the castle. I've baked a cake for you. Yours truly-- Princess Toadstool",
                "Lieve Mario: kom alsjeblieft naar het kasteel. Ik heb een taart voor je gebakken. Groetjes-- Princess Toadstool"),
            ("I win! You lose! Ha ha ha ha! You're no slouch, but I'm a better sledder! Better luck next time!",
                "Ik win, jij verliest! Haha! Je bent niet gek, maar ik slee beter! Volgende keer beter!")
        };
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (en, nl) in pairs)
            map[Norm(en)] = nl;
        return map;
    }

    private static string Norm(string text)
    {
        var t = Regex.Replace(text ?? "", @"^0+", "");
        t = t.Replace('\r', ' ').Replace('\n', ' ');
        t = Regex.Replace(t, @"\s+", " ").Trim().ToUpperInvariant();
        return t;
    }

    private static string CopyShape(string original, string dutch)
    {
        if (original.Any(char.IsLower)) return dutch;
        return dutch.ToUpperInvariant();
    }

    private static MatchEvaluator MatchCase(string nl) =>
        m => m.Value.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))
            ? nl.ToUpperInvariant()
            : nl;
}
