using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using VisualSSH.Models;

namespace VisualSSH.Services;

public sealed class QuickAccessStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ObservableCollection<QuickPath> Custom { get; } = new();

    public void Load()
    {
        Custom.Clear();
        var path = FilePath();
        if (!File.Exists(path)) return;
        var loaded = JsonSerializer.Deserialize<List<QuickPath>>(File.ReadAllText(path), JsonOptions);
        if (loaded is null) return;
        foreach (var item in loaded)
            Custom.Add(item);
    }

    public IEnumerable<QuickPath> Combined(IEnumerable<QuickPath> defaults) =>
        defaults.Concat(Custom);

    public bool Contains(string remotePath) =>
        Custom.Any(p => string.Equals(p.Path, remotePath, StringComparison.Ordinal));

    public void Add(string name, string remotePath, string group = "Vastgemaakt")
    {
        if (string.IsNullOrWhiteSpace(remotePath) || Contains(remotePath)) return;
        Custom.Add(new QuickPath
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(remotePath.TrimEnd('/')) : name,
            Path = remotePath,
            Group = string.IsNullOrWhiteSpace(group) ? "Vastgemaakt" : group
        });
        Save();
    }

    public void Remove(string remotePath)
    {
        var match = Custom.FirstOrDefault(p => string.Equals(p.Path, remotePath, StringComparison.Ordinal));
        if (match is null) return;
        Custom.Remove(match);
        Save();
    }

    private void Save()
    {
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Custom.ToList(), JsonOptions));
    }

    private static string FilePath() =>
        AppDataPaths.Combine("quickaccess.json");
}
