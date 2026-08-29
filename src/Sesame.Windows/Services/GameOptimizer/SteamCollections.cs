using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public static class SteamCollections
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string? Apply(DeckClient client, IReadOnlyList<string> configDirs,
        IReadOnlyList<OptimizerGame> games, IReadOnlyList<SteamShortcut>? shortcuts = null)
    {
        var tabs = BuildTabs(games, shortcuts);
        if (tabs.Count == 0) return null;

        var keep = new HashSet<string>(tabs.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var config in configDirs)
        {
            try { WriteCloud(client, config, tabs, keep); }
            catch (Exception ex) { errors.Add("cloudstorage: " + ex.Message); }
            try { WriteLocalConfig(client, config, tabs, keep); }
            catch (Exception ex) { errors.Add("localconfig: " + ex.Message); }
        }
        OptimizerSettings.RememberSteamTabs(keep);
        return errors.Count == 0 ? null : string.Join(" · ", errors);
    }

    private static List<(string Id, string Name, List<uint> AppIds)> BuildTabs(
        IReadOnlyList<OptimizerGame> games, IReadOnlyList<SteamShortcut>? shortcuts)
    {
        var byApp = games
            .Where(g => g.SteamAppId != 0)
            .GroupBy(g => g.SteamAppId)
            .ToDictionary(g => g.Key, g => g.First());
        var groups = new Dictionary<string, (string Name, HashSet<uint> Ids)>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, uint appId)
        {
            if (appId == 0 || string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (!groups.TryGetValue(name, out var bucket))
            {
                bucket = (name, new HashSet<uint>());
                groups[name] = bucket;
            }
            bucket.Ids.Add(appId);
        }

        if (shortcuts is not null)
        {
            foreach (var shortcut in shortcuts)
            {
                if (!SteamShortcuts.IsOwned(shortcut) || SteamShortcuts.IsSesameLauncher(shortcut))
                    continue;
                var name = byApp.TryGetValue(shortcut.AppId, out var game)
                    ? SteamTabGrouping.TabName(game)
                    : CollectionTag(shortcut);
                Add(name, shortcut.AppId);
            }
        }

        foreach (var game in games.Where(g => g.SteamAppId != 0))
            Add(SteamTabGrouping.TabName(game), game.SteamAppId);

        return groups
            .Select(kv => (Id: SteamTabGrouping.TabId(kv.Value.Name), Name: kv.Value.Name,
                AppIds: kv.Value.Ids.ToList()))
            .Where(t => t.Name.Length > 0 && t.AppIds.Count > 0)
            .ToList();
    }

    private static string CollectionTag(SteamShortcut shortcut)
    {
        var tag = shortcut.Tags.FirstOrDefault(t =>
            !t.Equals(SteamShortcuts.OwnerTag, StringComparison.OrdinalIgnoreCase) &&
            !t.Equals(SteamShortcuts.LegacyOwnerTag, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(tag) ? "Overig" : tag.Trim();
    }

    private static void WriteCloud(DeckClient client, string configDir,
        IReadOnlyList<(string Id, string Name, List<uint> AppIds)> tabs, HashSet<string> keep)
    {
        var dir = DeckClient.Combine(configDir, "cloudstorage");
        client.EnsureDirectory(dir);
        var nsPath = DeckClient.Combine(dir, "cloud-storage-namespaces.json");
        var namespaces = ReadNamespaces(client, nsPath);
        var active = ActiveNamespace(namespaces);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var path = DeckClient.Combine(dir, "cloud-storage-namespace-" + active + ".json");
        JsonArray root;
        if (client.Exists(path))
        {
            var text = Encoding.UTF8.GetString(client.ReadBytes(path));
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text) as JsonArray
                   ?? new JsonArray();
        }
        else
            root = new JsonArray();

        foreach (var tab in tabs)
            UpsertCloudCollection(root, tab.Id, tab.Name, tab.AppIds, timestamp);
        RemoveStaleCloud(root, keep, timestamp);

        client.WriteText(path, root.ToJsonString(CompactJson));

        BumpNamespaceVersion(client, nsPath, namespaces, active);
    }

    private static void WriteLocalConfig(DeckClient client, string configDir,
        IReadOnlyList<(string Id, string Name, List<uint> AppIds)> tabs, HashSet<string> keep)
    {
        var path = DeckClient.Combine(configDir, "localconfig.vdf");
        if (!client.Exists(path)) return;
        var text = Encoding.UTF8.GetString(client.ReadBytes(path));
        if (string.IsNullOrWhiteSpace(text)) return;

        var collections = new JsonObject();
        var match = Regex.Match(text, @"""user-collections""\s+""((?:\\.|[^""])*)""");
        if (match.Success)
        {
            try
            {
                var json = UnescapeVdf(match.Groups[1].Value);
                if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) is JsonObject existing)
                    collections = existing;
            }
            catch
            {
                collections = new JsonObject();
            }
        }

        foreach (var tab in tabs)
            UpsertLocalCollection(collections, tab.Id, tab.Name, tab.AppIds);
        RemoveStaleLocal(collections, keep);

        var escaped = EscapeVdf(collections.ToJsonString(CompactJson));
        var replacement = "\"user-collections\"\t\t\"" + escaped + "\"";
        if (match.Success)
            text = string.Concat(text.AsSpan(0, match.Index), replacement, text.AsSpan(match.Index + match.Length));
        else
        {
            var marker = "\"UserLocalConfigStore\"";
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            var brace = at >= 0 ? text.IndexOf('{', at) : text.IndexOf('{');
            if (brace < 0) return;
            text = text.Insert(brace + 1, "\n\t" + replacement + "\n");
        }

        client.WriteText(path, text);
    }

    private static void UpsertCloudCollection(JsonArray root, string collectionId, string name,
        List<uint> appIds, long timestamp)
    {
        JsonArray? entry = null;
        JsonObject? payload = null;
        var key = "user-collections." + collectionId;

        foreach (var node in root)
        {
            if (node is not JsonArray pair || pair.Count < 2) continue;
            if (!string.Equals(pair[0]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (pair[1] is not JsonObject meta) continue;
            entry = pair;
            var valueText = meta["value"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(valueText))
            {
                try { payload = JsonNode.Parse(valueText) as JsonObject; }
                catch { payload = null; }
            }
            break;
        }

        if (payload is null || entry is null)
        {
            payload = NewPayload(collectionId, name);
            entry = new JsonArray
            {
                key,
                new JsonObject
                {
                    ["key"] = key,
                    ["timestamp"] = timestamp,
                    ["value"] = "",
                    ["version"] = timestamp.ToString(CultureInfo.InvariantCulture),
                    ["conflictResolutionMethod"] = "custom",
                    ["strMethodId"] = "union-collections"
                }
            };
            root.Add(entry);
        }

        MergeIds(payload, collectionId, name, appIds);
        var metaObj = (JsonObject)entry[1]!;
        metaObj["timestamp"] = timestamp;
        metaObj["version"] = timestamp.ToString(CultureInfo.InvariantCulture);
        metaObj["value"] = payload.ToJsonString();
        metaObj.Remove("is_deleted");
    }

    private static void UpsertLocalCollection(JsonObject collections, string collectionId, string name,
        List<uint> appIds)
    {
        JsonObject? payload = null;
        foreach (var kv in collections.ToList())
        {
            if (!string.Equals(kv.Key, collectionId, StringComparison.OrdinalIgnoreCase))
                continue;
            payload = kv.Value as JsonObject;
            break;
        }

        payload ??= NewPayload(collectionId, name);
        MergeIds(payload, collectionId, name, appIds);
        collections[collectionId] = payload;
    }

    private static void RemoveStaleCloud(JsonArray root, HashSet<string> keep, long timestamp)
    {
        foreach (var node in root)
        {
            if (node is not JsonArray pair || pair.Count < 2) continue;
            var key = pair[0]?.GetValue<string>();
            if (key is null || !key.StartsWith("user-collections.", StringComparison.Ordinal)) continue;
            var id = key["user-collections.".Length..];
            if (keep.Contains(id) || !SteamTabGrouping.IsManagedId(id)) continue;
            if (pair[1] is not JsonObject meta) continue;
            meta["is_deleted"] = true;
            meta["timestamp"] = timestamp;
            meta["version"] = timestamp.ToString(CultureInfo.InvariantCulture);
            meta["value"] = "{}";
        }
    }

    private static void RemoveStaleLocal(JsonObject collections, HashSet<string> keep)
    {
        foreach (var kv in collections.ToList())
        {
            if (keep.Contains(kv.Key) || !SteamTabGrouping.IsManagedId(kv.Key))
                continue;
            collections.Remove(kv.Key);
        }
    }

    private static JsonObject NewPayload(string id, string name) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["added"] = new JsonArray(),
        ["removed"] = new JsonArray()
    };

    private static void MergeIds(JsonObject payload, string collectionId, string name, List<uint> appIds)
    {
        if (payload["added"] is not JsonArray added)
        {
            added = new JsonArray();
            payload["added"] = added;
        }
        if (payload["removed"] is not JsonArray removed)
        {
            removed = new JsonArray();
            payload["removed"] = removed;
        }
        payload["name"] = name;
        payload["id"] = collectionId;

        var wanted = appIds.Select(CollectionAppId).Distinct().ToList();
        foreach (var stored in wanted)
        {
            if (!ContainsId(added, stored))
                added.Add(stored);
            RemoveId(removed, stored);
        }

        NormalizeAdded(added);
    }

    /// <summary>
    /// Game Mode matches collection members to shortcut appids from shortcuts.vdf.
    /// Those are signed 32-bit values (high bit set → negative). Unsigned JSON
    /// numbers never match, so the tab looks empty and Steam hides it.
    /// </summary>
    private static long CollectionAppId(uint id) => unchecked((int)id);

    private static void NormalizeAdded(JsonArray added)
    {
        var keep = new List<long>();
        foreach (var node in added)
        {
            if (!TryGetLong(node, out var value)) continue;
            var signed = unchecked((int)value);
            if (!keep.Contains(signed))
                keep.Add(signed);
        }
        added.Clear();
        foreach (var id in keep)
            added.Add(id);
    }

    private static List<(int ns, int ver)> ReadNamespaces(DeckClient client, string nsPath)
    {
        var list = new List<(int ns, int ver)>();
        if (!client.Exists(nsPath))
        {
            client.WriteText(nsPath, "[[1,\"1\"]]");
            return [(1, 1)];
        }
        try
        {
            using var doc = JsonDocument.Parse(client.ReadBytes(nsPath));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
                var ns = item[0].ValueKind == JsonValueKind.Number && item[0].TryGetInt32(out var n) ? n : 1;
                var verText = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : item[1].ToString();
                int.TryParse(verText, out var ver);
                list.Add((ns, ver));
            }
        }
        catch
        {
            list.Add((1, 1));
        }
        if (list.Count == 0) list.Add((1, 1));
        return list;
    }

    private static int ActiveNamespace(List<(int ns, int ver)> namespaces)
    {
        var best = namespaces
            .Where(x => x.ver != 0)
            .OrderByDescending(x => x.ver)
            .FirstOrDefault();
        return best.ns == 0 ? 1 : best.ns;
    }

    private static void BumpNamespaceVersion(DeckClient client, string nsPath,
        List<(int ns, int ver)> namespaces, int active)
    {
        var next = namespaces.Where(x => x.ns == active).Select(x => x.ver).DefaultIfEmpty(0).Max() + 1;
        if (next < 1) next = 1;
        var arr = new JsonArray();
        var seen = false;
        foreach (var (ns, ver) in namespaces)
        {
            var value = ns == active ? next : ver;
            arr.Add(new JsonArray { ns, value.ToString(CultureInfo.InvariantCulture) });
            if (ns == active) seen = true;
        }
        if (!seen)
            arr.Add(new JsonArray { active, next.ToString(CultureInfo.InvariantCulture) });
        client.WriteText(nsPath, arr.ToJsonString(CompactJson));
    }

    private static bool ContainsId(JsonArray arr, long id)
    {
        foreach (var node in arr)
        {
            if (TryGetLong(node, out var value) && SameApp(value, id))
                return true;
        }
        return false;
    }

    private static void RemoveId(JsonArray arr, long id)
    {
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            if (TryGetLong(arr[i], out var value) && SameApp(value, id))
                arr.RemoveAt(i);
        }
    }

    private static bool SameApp(long a, long b) =>
        unchecked((int)a) == unchecked((int)b);

    private static bool TryGetLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<long>(out var l))
        {
            value = l;
            return true;
        }
        if (jv.TryGetValue<ulong>(out var ul))
        {
            value = unchecked((long)ul);
            return true;
        }
        return false;
    }

    private static string EscapeVdf(string json) =>
        json.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string UnescapeVdf(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}
