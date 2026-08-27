using System.IO;
using System.Text.Json;
using VisualSSH.Models;

namespace VisualSSH.Services;

public static class RomHackLog
{
    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _cache;

    public static void Remember(string remotePath, string title, string originalFileName,
        string kind = "romhack")
    {
        lock (Gate)
        {
            var map = Load();
            map[Norm(remotePath)] = new Entry(remotePath, title, originalFileName, kind);
            Save(map);
        }
    }

    public static bool TryGet(string remotePath, out string title) =>
        TryGet(remotePath, out title, out _);

    public static bool TryGet(string remotePath, out string title, out string kind)
    {
        lock (Gate)
        {
            var map = Load();
            if (map.TryGetValue(Norm(remotePath), out var entry))
            {
                title = entry.Title;
                kind = InferKind(entry);
                return true;
            }
            title = "";
            kind = "";
            return false;
        }
    }

    private static string InferKind(Entry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Kind))
            return entry.Kind;
        return StoreGame.LooksLikeTranslation(entry.Title) ? "translation" : "romhack";
    }

    private static Dictionary<string, Entry> Load()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var path = FilePath();
        if (!File.Exists(path)) return _cache;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var remote = el.GetProperty("remotePath").GetString() ?? "";
                if (remote.Length == 0) continue;
                _cache[Norm(remote)] = new Entry(remote,
                    el.GetProperty("title").GetString() ?? "",
                    el.TryGetProperty("originalFileName", out var o) ? o.GetString() ?? "" : "",
                    el.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "");
            }
        }
        catch { /* kapot logbestand: opnieuw beginnen */ }
        return _cache;
    }

    private static void Save(Dictionary<string, Entry> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath())!);
        var json = JsonSerializer.Serialize(map.Values.Select(e => new
        {
            remotePath = e.RemotePath,
            title = e.Title,
            originalFileName = e.OriginalFileName,
            kind = e.Kind
        }));
        File.WriteAllText(FilePath(), json);
        _cache = map;
    }

    private static string FilePath() =>
        AppDataPaths.Combine("romhacks.json");

    private static string Norm(string path) => path.Replace('\\', '/').TrimEnd('/');

    private readonly record struct Entry(string RemotePath, string Title, string OriginalFileName, string Kind);
}
