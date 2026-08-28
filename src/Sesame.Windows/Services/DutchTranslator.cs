using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sesame.Services.N64;

namespace Sesame.Services;

public sealed class TranslateProgress
{
    public int Total { get; init; }
    public int Done { get; init; }
    public int Remaining => Math.Max(0, Total - Done);
    public int FromCache { get; init; }
    public string Message { get; init; } = "";
}

public static class DutchTranslator
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex LineMark = new(@"<<\s*(\d+)\s*>>\s*(.*?)(?=\s*<<\s*\d+\s*>>|$)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LeftoverIndex = new(@"XBK\d+X|§\s*P\s*\d+\s*§|ZZ[A-Z]{3,}ZZ",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ButtonTag = new(@"\[(?:[ABCRZ]|C\^?)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingPad = new(@"^0+(?=[A-Za-z\[""])", RegexOptions.Compiled);
    private static readonly Regex ControlCode = new(@"\\x[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    private static readonly object CacheLock = new();
    private static Dictionary<string, string>? _liveCache;

    private static readonly string[] LibreEndpoints =
    [
        "https://libretranslate.de/translate",
        "https://translate.fedilab.app/translate",
        "https://trans.zillyhuhn.com/translate"
    ];

    private static readonly string[] Protect =
    [
        "BANJO-KAZOOIE", "BANJO KAZOOIE", "BANJO", "KAZOOIE", "TOOTY",
        "GRUNTILDA", "GRUNTY", "MUMBO JUMBO", "MUMBO", "JUMBO",
        "BOTTLES", "CHEATO", "BRENTILDA", "JINJONATOR",
        "JIGGYWIGGY", "JIGGIES", "JIGGY", "JINJOS", "JINJO",
        "WITCHYWORLD", "GRUNTYLAND", "SPIRAL MOUNTAIN",
        "MUMBO'S MOUNTAIN", "MUMBOS MOUNTAIN",
        "TREASURE TROVE COVE", "CLANKER'S CAVERN", "CLANKERS CAVERN",
        "BUBBLEGLOOP SWAMP", "FREEZEEZY PEAK",
        "GOBI'S VALLEY", "GOBIS VALLEY",
        "MAD MONSTER MANSION", "RUSTY BUCKET BAY", "CLICK CLOCK WOOD",
        "GRUNTY'S LAIR", "GRUNTYS LAIR", "FURNACE FUN",
        "SHOCK SPRING JUMP", "TURBO TALON TROT", "TALON TROT",
        "RAT-A-TAT RAP", "BEAK BARGE", "BEAK BOMB", "BEAK BUSTER",
        "WONDERWING", "STILT STRIDE", "FLAP FLIP", "CLAW SWIPE",
        "BLUBBER", "NIPPER", "CLANKER", "CONGA", "CHIMPY",
        "GOBI", "TIPTUP", "TANKTUP", "NAPPER", "LEAKY", "SNACKER", "RUBEE",
        "TRUNKER", "LOGGO", "EYRIE", "NABNUT", "GNAWTY", "CROCTUS",
        "HONEYCOMBS", "HONEYCOMB", "YUM-YUM", "MR. VILE", "GRABBA", "FLIBBIT",
        "DONKEY KONG", "DONKEY", "DIDDY", "LANKY", "TINY", "CHUNKY",
        "CRANKY", "FUNKY", "CANDY", "SNIDE", "WRINKLY", "K. ROOL", "K.ROOL",
        "KING K. ROOL", "RAMBI", "ENGUARDE", "EXPRESSO", "SQUAWKS", "WRECKING BARREL",
        "JUNGLE JAPES", "ANGRY AZTEC", "FRANTIC FACTORY", "GLOOMY GALLEON",
        "FUNGI FOREST", "CRYSTAL CAVES", "CREEPY CASTLE", "HIDEOUT HELM", "DK ISLES",
        "GOLDEN BANANA", "KREMLING", "KREMLINGS", "KASPLAT", "KLAPTRAP", "KLAPTRAPS",
        "ZINGERS", "ZINGER", "ARMY DILLO", "MARIO", "LUIGI", "PEACH", "BOWSER",
        "YOSHI", "TOAD", "TOADSTOOL", "LAKITU", "KOOPA", "BOB-OMB", "BOB-OMBS",
        "CHAIN CHOMP", "WHOMP", "THWOMP", "POWER STAR", "WING CAP", "METAL CAP",
        "VANISH CAP", "CONTROL STICK"
    ];

    private static readonly Dictionary<string, string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NOTE"] = "NOOT",
        ["NOTES"] = "NOTEN",
        ["HONEYCOMB"] = "HONINGRAAT",
        ["HONEYCOMBS"] = "HONINGRATEN",
        ["WITCH"] = "HEKS",
        ["BEAR"] = "BEER",
        ["BIRD"] = "VOGEL",
        ["MOLE"] = "MOL",
        ["PRESS A"] = "DRUK OP A",
        ["PRESS B"] = "DRUK OP B",
        ["GAME OVER"] = "GAME OVER",
        ["START"] = "START",
        ["OPTIONS"] = "OPTIES",
        ["SOUND"] = "GELUID",
        ["MUSIC"] = "MUZIEK",
        ["COPY"] = "KOPIEER",
        ["ERASE"] = "WIS",
        ["YES"] = "JA",
        ["NO"] = "NEE"
    };

    private static readonly (string Name, string Token)[] NameTokens = BuildNameTokens();
    private static readonly ConcurrentDictionary<string, byte> DeadEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastSaveUtc = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SESAME/0.4");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }

    public static async Task TranslateAsync(
        IList<BkTextLine> lines,
        Action<TranslateProgress>? progress = null,
        CancellationToken ct = default,
        bool useCache = true,
        bool tryOnline = true)
    {
        var total = lines.Count;
        var cache = LoadCache();
        _liveCache = cache;
        var fromCache = 0;

        if (useCache)
        {
            foreach (var line in lines)
            {
                if (line.UserEdited) continue;
                if (!cache.TryGetValue(line.Original, out var hit)) continue;
                hit = CleanupTokens(hit);
                hit = EnsureNames(line.Original, hit);
                if (LooksBroken(hit) || !IsUseful(line.Original, hit) || !NamesIntact(line.Original, hit) ||
                    !LooksSpoken(line.Original, hit))
                    continue;
                line.Translation = SafeFinish(line.Original, hit, line.MaxChars);
                fromCache++;
            }
        }

        Report(progress, total, lines, fromCache, fromCache > 0
            ? $"{fromCache} uit cache. Rest aanvullen…"
            : "Vertalen…");

        if (tryOnline)
        {
            var groups = GroupForContext(lines).Where(g => g.Any(NeedsWork)).ToList();
            if (groups.Count == 0)
            {
                Report(progress, total, lines, fromCache,
                    fromCache > 0
                        ? $"{fromCache} uit cache. Niets meer online te vertalen."
                        : "Niets meer online te vertalen.");
            }
            else
            {
            var doneGroups = new int[1];
            await Parallel.ForEachAsync(groups, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = ct
            }, async (group, token) =>
            {
                var pending = group.Where(l => !l.UserEdited && NeedsWork(l)).ToList();
                if (pending.Count == 0) return;
                try
                {
                    if (pending.Count == group.Count && group.Count > 1)
                        await TranslateGroupAsync(group, cache, token);
                    else
                        await TranslateLinesFallbackAsync(pending, cache, token);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try { await TranslateLinesFallbackAsync(pending, cache, token); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* lokale afronding volgt */ }
                }

                foreach (var line in group)
                {
                    if (line.UserEdited) continue;
                    if (LooksSpoken(line.Original, line.Translation) && IsUseful(line.Original, line.Translation))
                        line.Translation = CleanupTokens(line.Translation);
                    else
                        line.Translation = SafeFinish(line.Original, line.Translation, line.MaxChars, keepPartial: true);
                }

                var n = Interlocked.Increment(ref doneGroups[0]);
                SaveCacheThrottled(cache);
                Report(progress, total, lines, fromCache,
                    $"Online vertalen… groep {n}/{groups.Count}");
            });
            }
        }

        Report(progress, total, lines, fromCache, "Lokale afronding, zonder woord-voor-woord…");
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.UserEdited || !NeedsWork(line)) continue;
            if (DutchGameSpeak.TryExact(line.Original, out var exact))
            {
                line.Translation = SafeFinish(line.Original, exact, line.MaxChars, keepPartial: true);
                if (IsDone(line))
                    lock (CacheLock) cache[line.Original] = line.Translation;
                continue;
            }
            var local = TranslateLocal(line.Original);
            line.Translation = SafeFinish(line.Original, local, line.MaxChars, keepPartial: true);
            if (IsDone(line) && LooksSpoken(line.Original, line.Translation))
                lock (CacheLock) cache[line.Original] = line.Translation;
        }

        SaveCache(cache);
        var pendingLeft = lines.Count(NeedsWork);
        var done = lines.Count(IsSettled);
        Report(progress, total, lines, fromCache, pendingLeft == 0
            ? $"{done} van {total} klaar. Je kunt de ROM maken."
            : $"{done} klaar · {pendingLeft} nog Engels (vaak namen of kreten).");
    }

    public static void Remember(string original, string translation, bool userEdit = false, int max = 254)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translation)) return;
        if (!userEdit && LooksBroken(translation)) return;
        var cache = _liveCache ?? LoadCache();
        var allCaps = BkTextCodec.PreferAllCaps(original);
        cache[original] = userEdit
            ? FitUserText(original, translation, max, allCaps)
            : SafeFinish(original, translation, max);
        _liveCache = cache;
        SaveCache(cache);
    }

    public static void RememberMany(IEnumerable<BkTextLine> lines)
    {
        var cache = _liveCache ?? LoadCache();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Original) || string.IsNullOrWhiteSpace(line.Translation)) continue;
            if (!line.UserEdited && LooksBroken(line.Translation)) continue;
            cache[line.Original] = line.UserEdited
                ? FitUserText(line.Original, line.Translation, line.MaxChars, line.AllCaps, line.NewlineByte)
                : SafeFinish(line.Original, line.Translation, line.MaxChars);
        }
        _liveCache = cache;
        SaveCache(cache);
    }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            _liveCache = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var path = CachePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* cache is optioneel */ }
        }
    }

    public static int CacheCount()
    {
        var cache = _liveCache ?? LoadCache();
        return cache.Count;
    }

    private static void Report(
        Action<TranslateProgress>? progress, int total, IList<BkTextLine> lines, int fromCache, string message)
    {
        var done = lines.Count(IsSettled);
        progress?.Invoke(new TranslateProgress
        {
            Total = total,
            Done = done,
            FromCache = fromCache,
            Message = message
        });
    }

    public static bool IsPending(BkTextLine line) => NeedsWork(line);

    private static bool IsSettled(BkTextLine line) => !NeedsWork(line);

    private static bool IsDone(BkTextLine line) =>
        !NeedsTranslation(line.Original) || (line.Changed && !LooksBroken(line.Translation));

    private static bool NeedsWork(BkTextLine line) =>
        NeedsTranslation(line.Original) && !IsDone(line);

    private static bool LooksBroken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (LeftoverIndex.IsMatch(text)) return true;
        if (text.Contains('§', StringComparison.Ordinal)) return true;
        if (Regex.IsMatch(text, @"XID\d+X", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static List<List<BkTextLine>> GroupForContext(IList<BkTextLine> lines)
    {
        var groups = new List<List<BkTextLine>>();
        var special = lines.Where(l => l.Codec is "sm64" or "dk64");
        foreach (var line in special.Where(l => l.Codec == "sm64"))
            groups.Add([line]);
        foreach (var chunk in special.Where(l => l.Codec == "dk64").GroupBy(l => (l.RomOffset, l.Section)))
        {
            var list = chunk.OrderBy(l => l.Index).ToList();
            for (var i = 0; i < list.Count; i += 4)
                groups.Add(list.GetRange(i, Math.Min(4, list.Count - i)));
        }

        var rest = lines.Where(l => l.Codec is not "sm64" and not "dk64").ToList();
        var raw = rest.Where(l => l.Kind == BkTextKind.Raw).OrderBy(l => l.RomOffset).ToList();
        if (raw.Count > 0)
        {
            var chunk = new List<BkTextLine>();
            foreach (var line in raw)
            {
                if (chunk.Count >= 6 ||
                    (chunk.Count > 0 && line.RomOffset - chunk[^1].RomOffset > 0x80))
                {
                    groups.Add(chunk);
                    chunk = [];
                }
                chunk.Add(line);
            }
            if (chunk.Count > 0) groups.Add(chunk);
        }

        foreach (var asset in rest.Where(l => l.Kind != BkTextKind.Raw).GroupBy(l => l.AssetId))
        {
            var list = asset.ToList();
            var kind = list[0].Kind;
            if (kind != BkTextKind.Dialog || list.Count <= 8)
            {
                groups.Add(list);
                continue;
            }

            for (var i = 0; i < list.Count; i += 6)
                groups.Add(list.GetRange(i, Math.Min(6, list.Count - i)));
        }
        return groups;
    }

    private static async Task TranslateGroupAsync(
        List<BkTextLine> group, Dictionary<string, string> cache, CancellationToken ct)
    {
        var originals = group.Select(l => l.Original).ToList();
        var pack = Pack(group);
        var raw = await TranslateTextAsync(pack, ct) ?? "";
        var parts = Unpack(raw, originals);
        var useful = 0;
        for (var i = 0; i < group.Count; i++)
        {
            if (group[i].UserEdited) continue;
            var nl = SafeFinish(originals[i], parts[i], group[i].MaxChars);
            if (IsUseful(originals[i], nl) && !LooksBroken(nl) && NamesIntact(originals[i], nl) &&
                LooksSpoken(originals[i], nl))
            {
                group[i].Translation = nl;
                lock (CacheLock) cache[originals[i]] = nl;
                useful++;
            }
        }

        if (useful < (group.Count + 1) / 2)
            await TranslateLinesFallbackAsync(group.Where(NeedsWork).ToList(), cache, ct);
    }

    private static async Task TranslateLinesFallbackAsync(
        List<BkTextLine> group, Dictionary<string, string> cache, CancellationToken ct)
    {
        if (group.Count == 0) return;
        var missing = new List<(int Index, string Text)>();
        for (var i = 0; i < group.Count; i++)
        {
            var src = group[i].Original;
            if (cache.TryGetValue(src, out var hit))
            {
                hit = CleanupTokens(hit);
                if (!LooksBroken(hit) && IsUseful(src, hit) && NamesIntact(src, hit) && LooksSpoken(src, hit))
                {
                    group[i].Translation = hit;
                    continue;
                }
            }
            if (NeedsTranslation(src)) missing.Add((i, src));
        }

        for (var i = 0; i < missing.Count; i += 8)
        {
            ct.ThrowIfCancellationRequested();
            var slice = missing.Skip(i).Take(8).ToList();
            var context = BuildContextPrefix(group, slice[0].Index);
            Dictionary<string, string>? got = null;
            try { got = await TranslateBatchLibreAsync(slice.Select(s => s.Text).ToList(), context, ct); }
            catch { got = new Dictionary<string, string>(StringComparer.Ordinal); }

            got ??= new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (index, text) in slice)
            {
                var draft = got.TryGetValue(text, out var dst) && !string.IsNullOrWhiteSpace(dst)
                    ? dst
                    : TranslateLocal(text);
                var finished = SafeFinish(text, draft, group[index].MaxChars);
                group[index].Translation = finished;
                if (!LooksBroken(finished) && IsUseful(text, finished) && NamesIntact(text, finished) &&
                    LooksSpoken(text, finished))
                    lock (CacheLock) cache[text] = finished;
            }
        }
    }

    private static string BuildContextPrefix(List<BkTextLine> group, int index)
    {
        var parts = new List<string>();
        var from = Math.Max(0, index - 2);
        for (var i = from; i < index; i++)
        {
            var bit = ToSentence(group[i].Original);
            if (bit.Length > 80) bit = bit[..80];
            parts.Add(bit);
        }

        if (index >= 0 && index < group.Count && group[index].Kind != BkTextKind.Dialog)
            parts.Insert(0, group[index].Kind == BkTextKind.Quiz
                ? "Keep quiz answers consistent with the question."
                : "Keep the joke, do not translate word for word.");
        return string.Join(" / ", parts);
    }

    private static string Pack(List<BkTextLine> group)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < group.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append("<<").Append(i).Append(">> ");
            sb.Append(ProtectForApi(ToSentence(ForApi(group[i].Original))));
        }
        return sb.ToString();
    }

    private static string[] Unpack(string translated, List<string> originals)
    {
        var result = originals.ToArray();
        if (string.IsNullOrWhiteSpace(translated)) return result;

        var matches = LineMark.Matches(translated);
        if (matches.Count > 0)
        {
            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, out var idx) || idx < 0 || idx >= result.Length)
                    continue;
                result[idx] = m.Groups[2].Value.Trim().Trim('"');
            }
            return result;
        }

        var lines = translated
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Regex.Replace(l, @"^<<\s*\d+\s*>>\s*", "").Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        return lines.Length == originals.Count ? lines : result;
    }

    private static string GameContext(string extra)
    {
        var baseCtx =
            "Nintendo 64 in-game dialogue. Informal spoken Dutch (je/jij). " +
            "Keep character names, place names and item names in English. " +
            "Translate meaning and jokes, not word for word.";
        if (string.IsNullOrWhiteSpace(extra)) return baseCtx;
        return baseCtx + " Nearby lines: " + extra;
    }

    private static async Task<string?> TranslateTextAsync(string packed, CancellationToken ct)
    {
        if (TranslateSettings.HasDeepL)
        {
            try
            {
                var deepl = await DeepLClient.TranslateAsync(packed, GameContext(""), ct);
                if (!string.IsNullOrWhiteSpace(deepl)) return deepl;
            }
            catch { /* Google daarna */ }
        }

        var gtx = await TranslateGtxAsync(packed, ct);
        if (!string.IsNullOrWhiteSpace(gtx)) return gtx;

        foreach (var url in LibreEndpoints)
        {
            if (DeadEndpoints.ContainsKey(url)) continue;
            try
            {
                var map = await PostLibreAsync(url, [packed], ct);
                if (map.TryGetValue(packed, out var hit) && !string.IsNullOrWhiteSpace(hit))
                    return hit;
                DeadEndpoints.TryAdd(url, 0);
            }
            catch
            {
                DeadEndpoints.TryAdd(url, 0);
            }
        }
        return null;
    }

    private static async Task<Dictionary<string, string>> TranslateBatchLibreAsync(
        List<string> texts, string context, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TranslateSettings.HasDeepL)
        {
            try
            {
                var bodies = texts.Select(t => ProtectForApi(ToSentence(ForApi(t)))).ToList();
                var got = await DeepLClient.TranslateManyAsync(bodies, GameContext(context), ct);
                for (var i = 0; i < texts.Count; i++)
                {
                    if (got.TryGetValue(bodies[i], out var hit) && !string.IsNullOrWhiteSpace(hit))
                        map[texts[i]] = hit;
                }
                if (map.Count == texts.Count) return map;
            }
            catch { /* Google voor het restant */ }
        }

        foreach (var text in texts)
        {
            if (map.ContainsKey(text)) continue;
            ct.ThrowIfCancellationRequested();
            var body = ProtectForApi(ToSentence(ForApi(text)));
            var q = string.IsNullOrEmpty(context) ? body : context + " => " + body;
            if (q.Length > 900) q = body;
            var hit = await TranslateGtxAsync(q, ct);
            if (string.IsNullOrWhiteSpace(hit)) continue;
            map[text] = StripContext(hit, context);
        }
        return map;
    }

    private static async Task<string?> TranslateGtxAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var encoded = Uri.EscapeDataString(text);
        if (encoded.Length > 1600)
        {
            var sb = new StringBuilder();
            foreach (var part in SplitForGtx(text))
            {
                var piece = await TranslateGtxOnceAsync(part, ct);
                if (sb.Length > 0 && !sb.ToString().EndsWith('\n') && !part.StartsWith('\n'))
                    sb.Append(part.Contains('\n') ? '\n' : ' ');
                sb.Append(piece ?? part);
            }
            var joined = sb.ToString().Trim();
            return joined.Length == 0 ? null : joined;
        }

        return await TranslateGtxOnceAsync(text, ct);
    }

    private static IEnumerable<string> SplitForGtx(string text)
    {
        var buf = new StringBuilder();
        foreach (var sentence in Regex.Split(text.Trim(), @"(?<=[.!?])\s+"))
        {
            var piece = sentence.Trim();
            if (piece.Length == 0) continue;
            var trial = buf.Length == 0 ? piece : buf + " " + piece;
            if (buf.Length > 0 && Uri.EscapeDataString(trial).Length > 1500)
            {
                yield return buf.ToString();
                buf.Clear();
                buf.Append(piece);
            }
            else
            {
                buf.Clear();
                buf.Append(trial);
            }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    private static async Task<string?> TranslateGtxOnceAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var encoded = Uri.EscapeDataString(text);
        if (encoded.Length > 1800)
            encoded = Uri.EscapeDataString(text[..Math.Min(text.Length, 500)]);

        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=nl&dt=t&q=" + encoded;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(14));
            using var res = await Http.GetAsync(url, linked.Token);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(linked.Token));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var chunks = doc.RootElement[0];
            if (chunks.ValueKind != JsonValueKind.Array) return null;
            var sb = new StringBuilder();
            foreach (var part in chunks.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0)
                    sb.Append(part[0].GetString());
            }
            var result = sb.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, string>> PostLibreAsync(
        string url, List<string> prepared, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            q = prepared.Count == 1 ? (object)prepared[0] : prepared,
            source = "en",
            target = "nl",
            format = "text",
            api_key = ""
        }), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("translatedText", out var translated))
            return map;
        if (translated.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var el in translated.EnumerateArray())
            {
                if (i >= prepared.Count) break;
                var t = el.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                    map[prepared[i]] = t!;
                i++;
            }
        }
        else if (prepared.Count >= 1)
        {
            var t = translated.GetString();
            if (!string.IsNullOrWhiteSpace(t))
                map[prepared[0]] = t!;
        }
        return map;
    }

    private static string StripContext(string text, string context)
    {
        text = text.Trim();
        var idx = text.LastIndexOf("=>", StringComparison.Ordinal);
        if (idx >= 0 && idx + 2 < text.Length) text = text[(idx + 2)..].Trim();
        if (!string.IsNullOrEmpty(context) && text.StartsWith(context, StringComparison.OrdinalIgnoreCase))
            text = text[context.Length..].TrimStart(':', '-', ' ');
        return text;
    }

    private static string TranslateLocal(string text)
    {
        if (DutchGameSpeak.TryExact(text, out var exact))
            return exact;
        var work = DutchGameSpeak.Apply(ToSentence(text));
        work = DutchIdioms.Protect(work);
        work = DutchIdioms.Restore(work);
        var words = Regex.Matches(work, @"[A-Za-z']+").Count;
        if (words <= 4)
            work = DutchLocalLexicon.Apply(work);
        else
            work = DutchLocalLexicon.ApplyPhrases(work);
        foreach (var (en, nl) in Terms)
            work = Regex.Replace(work, $@"\b{Regex.Escape(en)}\b", nl, RegexOptions.IgnoreCase);
        return work;
    }

    private static string ProtectForApi(string text)
    {
        var work = MaskButtons(DutchIdioms.Protect(text));
        work = work.Replace("~", "ZZTILDEZZ");
        work = ControlCode.Replace(work, m => "XCC" + m.Value[2..] + "X");
        foreach (var (name, token) in NameTokens)
            work = Regex.Replace(work, $@"\b{Regex.Escape(name)}\b", token, RegexOptions.IgnoreCase);
        return work;
    }

    private static string CleanupTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = UnmaskButtons(text);
        text = text.Replace("ZZTILDEZZ", "~", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"XCC([0-9A-Fa-f]{2})X", m => "\\x" + m.Groups[1].Value.ToUpperInvariant(),
            RegexOptions.IgnoreCase);
        foreach (var (name, token) in NameTokens.OrderByDescending(t => t.Token.Length))
            text = Regex.Replace(text, Regex.Escape(token), name, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"XBK\d+X", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"§\s*P\s*\d+\s*§", "", RegexOptions.IgnoreCase);
        text = DutchIdioms.Restore(text);
        text = DutchGameSpeak.Apply(text);
        foreach (var (name, _) in NameTokens)
            text = Regex.Replace(text, $@"^{Regex.Escape(name)}\s*:\s+", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        return text;
    }

    private static string EnsureNames(string original, string translated)
    {
        var work = translated ?? "";
        var present = Protect.Where(n => Regex.IsMatch(original, $@"\b{Regex.Escape(n)}\b", RegexOptions.IgnoreCase))
            .OrderByDescending(n => n.Length)
            .ToList();
        foreach (var name in present)
        {
            if (Regex.IsMatch(work, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                continue;
            var swapped = false;
            foreach (var other in Protect)
            {
                if (present.Contains(other, StringComparer.OrdinalIgnoreCase)) continue;
                if (!Regex.IsMatch(work, $@"\b{Regex.Escape(other)}\b", RegexOptions.IgnoreCase)) continue;
                work = Regex.Replace(work, $@"\b{Regex.Escape(other)}\b", name, RegexOptions.IgnoreCase);
                swapped = true;
                break;
            }
            if (!swapped)
                work = name + (work.Length == 0 ? "" : ", " + work.TrimStart(',', ' '));
        }
        return work;
    }

    private static bool NamesIntact(string original, string translated)
    {
        foreach (var name in Protect)
        {
            if (!Regex.IsMatch(original, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                continue;
            if (!Regex.IsMatch(translated ?? "", $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                return false;
        }
        return true;
    }

    private static string FitUserText(string original, string translation, int max, bool allCaps = true, byte newline = 0xFD)
    {
        var game = BkTextCodec.ToGameText(translation, allCaps, newline);
        if (string.IsNullOrWhiteSpace(game))
            game = BkTextCodec.ToGameText(original, allCaps, newline);
        if (BkTextCodec.EncodedLength(game, allCaps, newline) > max)
            game = CutAtWord(game, max, allCaps, newline);
        return game;
    }

    private static string SafeFinish(string original, string? translated, int max, bool keepPartial = false)
    {
        var src = original ?? "";
        var draft = translated ?? "";
        var allCaps = BkTextCodec.PreferAllCaps(src);
        if (!NeedsTranslation(src))
            return BkTextCodec.ToGameText(src, allCaps);

        draft = CleanupTokens(draft);
        draft = EnsureNames(src, draft);
        if (DutchGameSpeak.TryExact(src, out var exact))
            draft = exact;
        else
            draft = DutchGameSpeak.Apply(draft);
        foreach (var (en, nl) in Terms)
            draft = Regex.Replace(draft, $@"\b{Regex.Escape(en)}\b", nl, RegexOptions.IgnoreCase);

        var game = BkTextCodec.ToGameText(draft, allCaps);
        var spoken = LooksSpoken(src, game) || HasDutchGlue(game);
        if (!spoken && (LooksBroken(game) || !IsUseful(src, game)))
            game = BkTextCodec.ToGameText(TranslateLocal(src), allCaps);
        spoken = LooksSpoken(src, game) || HasDutchGlue(game);
        if (!spoken)
        {
            if (!keepPartial || LooksBroken(game) || string.IsNullOrWhiteSpace(game))
                game = BkTextCodec.ToGameText(
                    DutchGameSpeak.TryExact(src, out var spokenExact) ? spokenExact : src, allCaps);
        }

        var lines = SplitLines(src).Length;
        game = FitShape(src, game, max, allCaps, cutTotal: lines < 3);
        if (string.IsNullOrWhiteSpace(game) || BkTextCodec.EncodedLength(game, allCaps) == 0 || LooksBroken(game))
            return BkTextCodec.ToGameText(src, allCaps);
        if (lines < 3 && BkTextCodec.EncodedLength(game, allCaps) > max)
            game = CutAtWord(game, max, allCaps);
        return game;
    }

    private static string FitShape(string original, string dutch, int max, bool allCaps = true, bool cutTotal = true)
    {
        var origLines = SplitLines(original);
        var dutchLines = SplitLines(dutch);
        string shaped;
        if (origLines.Length > 1 && dutchLines.Length == origLines.Length)
        {
            var fitted = new string[origLines.Length];
            for (var i = 0; i < origLines.Length; i++)
                fitted[i] = CutAtWord(dutchLines[i], LineBudget(origLines[i], max), allCaps);
            shaped = string.Join("\n", fitted.Where(s => s.Length > 0));
        }
        else if (origLines.Length > 1)
        {
            shaped = Reflow(dutch, origLines.Length, AverageWidth(origLines));
        }
        else
        {
            shaped = dutch;
        }

        if (cutTotal)
        {
            var soft = SoftLimit(original, max, allCaps);
            if (BkTextCodec.EncodedLength(shaped, allCaps) > soft)
                shaped = CutAtWord(shaped, soft, allCaps);
        }
        return BkTextCodec.ToGameText(shaped, allCaps);
    }

    private static int SoftLimit(string original, int max, bool allCaps = true)
    {
        var n = Math.Max(1, BkTextCodec.EncodedLength(original, allCaps));
        var soft = Math.Max(n + 24, (int)(n * 1.65) + 8);
        return Math.Min(max, Math.Max(soft, 24));
    }

    private static int LineBudget(string origLine, int max)
    {
        var n = Math.Max(8, origLine.Replace("\n", "").Length);
        return Math.Min(max, Math.Max(28, n + Math.Max(10, n / 2)));
    }

    private static int AverageWidth(string[] lines)
    {
        if (lines.Length == 0) return 26;
        return Math.Clamp((int)lines.Average(l => l.Length), 18, 32);
    }

    private static string Reflow(string text, int lineCount, int width)
    {
        var words = text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return text;
        var lines = new List<string>();
        var cur = new StringBuilder();
        foreach (var word in words)
        {
            var next = cur.Length == 0 ? word : cur + " " + word;
            if (cur.Length > 0 && next.Length > width && lines.Count + 1 < lineCount)
            {
                lines.Add(cur.ToString());
                cur.Clear();
                cur.Append(word);
            }
            else
            {
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(word);
            }
        }
        if (cur.Length > 0) lines.Add(cur.ToString());
        return string.Join("\n", lines);
    }

    private static string CutAtWord(string text, int max, bool allCaps = true, byte newline = 0xFD)
    {
        var game = BkTextCodec.ToGameText(text, allCaps, newline);
        if (BkTextCodec.EncodedLength(game, allCaps, newline) <= max) return game;
        game = Regex.Replace(game, @"\b(DE|HET|EEN|WEL|DUS|MAAR|OOK)\b", " ");
        game = BkTextCodec.ToGameText(game, allCaps, newline);
        if (BkTextCodec.EncodedLength(game, allCaps, newline) <= max) return game;

        var kept = new StringBuilder();
        foreach (var ch in game)
        {
            var trial = kept.ToString() + ch;
            if (BkTextCodec.EncodedLength(trial, allCaps, newline) > max) break;
            kept.Append(ch);
        }
        var cut = kept.ToString().TrimEnd();
        var space = cut.LastIndexOf(' ');
        var nl = cut.LastIndexOf('\n');
        var breakAt = Math.Max(space, nl);
        if (breakAt > cut.Length / 2) cut = cut[..breakAt];
        cut = cut.TrimEnd(' ', ',', ';', '-', '\n');
        return cut.Length > 0 ? cut : game[..Math.Min(game.Length, 1)];
    }

    private static string[] SplitLines(string text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static bool NeedsTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var work = text;
        foreach (var name in Protect.OrderByDescending(n => n.Length))
            work = Regex.Replace(work, $@"\b{Regex.Escape(name)}\b", " ", RegexOptions.IgnoreCase);
        work = ControlCode.Replace(work, " ");
        return work.Count(char.IsLetter) >= 3;
    }

    private static bool IsUseful(string original, string? translated)
    {
        if (string.IsNullOrWhiteSpace(translated) || LooksBroken(translated)) return false;
        var a = BkTextCodec.ToGameText(original);
        var b = BkTextCodec.ToGameText(translated);
        if (b.Length == 0) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return false;
        var letters = b.Count(char.IsLetter);
        return letters >= Math.Min(3, a.Count(char.IsLetter));
    }

    private static bool LooksSpoken(string original, string? translated)
    {
        if (string.IsNullOrWhiteSpace(translated)) return false;
        if (HasDutchGlue(translated)) return true;
        var src = StripKnown(original);
        var words = Regex.Matches(src, @"[A-Za-z']{4,}")
            .Select(m => m.Value)
            .Where(w => !Protect.Any(n => n.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (words.Count < 3) return true;
        var leftover = words.Count(w =>
            Regex.IsMatch(translated, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase));
        return leftover <= Math.Max(1, words.Count / 3);
    }

    private static bool HasDutchGlue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var n = 0;
        foreach (var w in new[] { " de ", " het ", " een ", " je ", " ik ", " we ", " van ", " niet ", " voor ", " naar ", " met ", " dat ", " dit ", " en " })
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n >= 2;
    }

    private static string ForApi(string text) =>
        Regex.Replace(LeadingPad.Replace(text ?? "", ""), @"[\r\n]+", " ").Trim();

    private static string StripKnown(string text)
    {
        var work = LeadingPad.Replace(text ?? "", "");
        foreach (var name in Protect.OrderByDescending(n => n.Length))
            work = Regex.Replace(work, $@"\b{Regex.Escape(name)}\b", " ", RegexOptions.IgnoreCase);
        work = ButtonTag.Replace(work, " ");
        return work;
    }

    private static string ToSentence(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var work = LeadingPad.Replace(text, "");
        var lower = work.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        var cap = true;
        foreach (var ch in lower)
        {
            if (cap && char.IsLetter(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
                cap = false;
            }
            else sb.Append(ch);
            if (ch is '.' or '!' or '?') cap = true;
        }
        return sb.ToString();
    }

    private static string MaskButtons(string text) =>
        ButtonTag.Replace(text, m =>
        {
            var inner = m.Value.Trim('[', ']').ToUpperInvariant().Replace("^", "UP");
            return "XBTN" + inner + "X";
        });

    private static string UnmaskButtons(string text) =>
        Regex.Replace(text, @"XBTN([A-Z0-9UP]+)X", m =>
        {
            var inner = m.Groups[1].Value.Replace("UP", "^", StringComparison.OrdinalIgnoreCase);
            return "[" + inner + "]";
        }, RegexOptions.IgnoreCase);

    private static string CachePath() => AppDataPaths.Combine("bk-nl-cache.json");

    private static Dictionary<string, string> LoadCache()
    {
        lock (CacheLock)
        {
            if (_liveCache is not null) return _liveCache;
            var path = CachePath();
            if (!File.Exists(path))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (raw is not null)
                {
                    foreach (var (en, nl) in raw)
                    {
                        if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(nl)) continue;
                        if (LooksBroken(nl) || !LooksSpoken(en, nl)) continue;
                        map[en] = nl;
                    }
                }
                _liveCache = map;
                return _liveCache;
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }

    private static void SaveCache(Dictionary<string, string> cache)
    {
        lock (CacheLock)
        {
            try
            {
                _liveCache = cache;
                var path = CachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch { /* cache is optioneel */ }
        }
    }

    private static void SaveCacheThrottled(Dictionary<string, string> cache)
    {
        lock (CacheLock)
        {
            _liveCache = cache;
            if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < 2) return;
            _lastSaveUtc = DateTime.UtcNow;
        }
        SaveCache(cache);
    }

    private static (string Name, string Token)[] BuildNameTokens()
    {
        var list = new List<(string, string)>();
        foreach (var name in Protect.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
        {
            var token = "ZZ" + Regex.Replace(name.ToUpperInvariant(), @"[^A-Z]", "") + "ZZ";
            list.Add((name, token));
        }
        return list.ToArray();
    }
}
